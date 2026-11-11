using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.PieGame;

namespace Town.Core.Tests
{
    /// <summary>仅离线 Session 行动；真实 Kernel、所有者队列和 Session 签发的写能力，不构造假回执。</summary>
    public sealed class DragonsleepActionSessionTests
    {
        const string Observe = "观察四周";
        const string Buy = "{\"verb\":\"buy\",\"target\":\"marta\",\"item\":\"smoked_fish\",\"place\":\"market\","
            + "\"laws\":[],\"difficulty\":0.1,\"narration\":\"买鱼\"}";
        const string Sneak = "{\"verb\":\"sneak\",\"target\":\"\",\"item\":\"\",\"place\":\"market\","
            + "\"laws\":[],\"difficulty\":0.6,\"narration\":\"潜行\"}";
        const string Unsupported = "{\"verb\":\"unsupported\",\"target\":\"\",\"item\":\"\",\"place\":\"\","
            + "\"laws\":[],\"difficulty\":0.1,\"narration\":\"尝试\"}";
        static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void 相同快照的旧入口与内核入口完整世界回执及仅去耗时的Trace一致()
        {
            foreach (string scenario in new[] { "buy", "poor", "roll_failed", "unsupported" })
            {
                var world = DsContent.NewWorld(7);
                if (scenario == "poor") world.Coins = 0;
                var before = DsWorldSnapshot.Capture(world, 0, 4);
                string raw = scenario == "roll_failed" ? Sneak : scenario == "unsupported" ? Unsupported : Buy;
                using var h = new Harness(before, new ScriptProvider { Result = Response(raw) });
                var owner = new OwnerQueue();
                var legacy = new DragonsleepSession(new ScriptProvider { Result = Response(raw) }, 7, ownerDispatcher: owner);
                legacy.RestoreState(before);
                var events = Watch(legacy, owner);

                var expected = owner.Pump(legacy.ActAsync("固定离线行动", CancellationToken.None, null));
                var identity = h.Session.World;
                var actual = h.Run("固定离线行动");

                Assert.AreEqual(legacy.CaptureState().Json, h.Session.CaptureState().Json, scenario);
                Assert.AreEqual(Receipt(expected), Receipt(actual), scenario);
                Assert.AreEqual(StableTrace(expected), StableTrace(actual), scenario);
                Assert.AreEqual(MiniJson.Write(expected.Perception), MiniJson.Write(actual.Perception), scenario);
                Assert.AreEqual(MiniJson.Write(expected.Proposal), MiniJson.Write(actual.Proposal), scenario);
                CollectionAssert.AreEqual(ActionPromptCodec.Encode(expected.Prompt), ActionPromptCodec.Encode(actual.Prompt));
                CollectionAssert.AreEqual(events, h.Events, scenario);
                Assert.AreSame(identity, h.Session.World);
                Assert.AreEqual("committed", actual.KernelCode);
                Assert.IsNull(actual.FailureCode);
                Assert.AreEqual(1, h.Kernel.Applies);
                Assert.AreEqual(1, h.Kernel.Registers);
                Assert.AreEqual(1, h.Kernel.Begins);
                Assert.AreEqual(1, h.Kernel.Commits);
                Assert.AreEqual(1, h.Transaction.PrivateRecordList.Count, "规则拒绝但 Ready 的零变化候选也应私有录制");
                Assert.AreEqual(1, h.Completed);
                Assert.IsFalse(h.Session.Busy);
                Assert.IsFalse(h.Session.KernelActionInFlight);
                Assert.IsNull(expected.KernelCode);
                Assert.IsFalse(expected.ToRecord().ContainsKey("kernel_code"));
                if (scenario == "roll_failed")
                {
                    Assert.AreEqual(0.3548, actual.Verdict.Roll);
                    Assert.AreEqual("roll_failed", actual.Verdict.ReasonCode.Code);
                    Assert.AreEqual(1, h.Session.World.RollCounter);
                    Assert.AreEqual(2, actual.Receipt.TimeCost);
                }
            }
        }

        [Test]
        public void 已花两点再观察跨正午换岗_裸露捕获与斗篷避开巡逻均同旧路径且只写一次()
        {
            foreach (string escort in new[] { "none", "exposed", "cloaked" })
            {
                var world = DsContent.NewWorld(7);
                world.Whelp.Place = world.PlayerPlace;
                world.Whelp.Following = escort != "none";
                world.Whelp.Hidden = escort == "cloaked";
                if (escort == "cloaked") world.Flags["whelp_hidden_by_player"] = "离线斗篷";
                var before = DsWorldSnapshot.Capture(world, 2, 4);
                using var h = new Harness(before);
                h.Kernel.CheckDuplicate = true;
                var owner = new OwnerQueue();
                var legacy = new DragonsleepSession(new RuleProvider(), 7, ownerDispatcher: owner);
                legacy.RestoreState(before);

                var expected = owner.Pump(legacy.ActAsync(Observe));
                var actual = h.Run();

                Assert.AreEqual(legacy.CaptureState().Json, h.Session.CaptureState().Json, escort);
                Assert.AreEqual(Receipt(expected), Receipt(actual), escort);
                Assert.AreEqual(StableTrace(expected), StableTrace(actual), escort);
                Assert.AreEqual(h.Transaction.PrivateRecordList.Single().AfterDigest, h.Session.CaptureState().Hash);
                Assert.AreEqual(DayPhase.Noon, h.Session.World.Phase);
                Assert.AreEqual(0, h.Session.CaptureState().Spent);
                Assert.AreEqual(2, actual.Receipt.TimeCost);
                Assert.AreEqual(before.Restore().Version + 1, h.Session.World.Version);
                Assert.AreEqual(escort == "none" ? 0 : 1, h.Session.World.RollCounter);
                Assert.AreEqual(escort == "exposed" ? "osric" : null, h.Session.World.Whelp.CapturedBy);
                Assert.AreEqual(escort == "cloaked", h.Session.World.Whelp.Following);
                Assert.AreEqual(escort == "exposed" ? "castle" : "market", h.Session.World.Whelp.Place);
                Assert.AreEqual("committed", h.Kernel.LastReceipt.Code);
                Assert.AreEqual(before.Hash, h.Kernel.LastReceipt.BeforeDigest);
                Assert.AreEqual(h.Session.CaptureState().Hash, h.Kernel.LastReceipt.AfterDigest);
                Assert.AreEqual("duplicate", h.Kernel.DuplicateReceipt.Code);
                Assert.AreEqual(h.Kernel.LastReceipt.AfterDigest, h.Kernel.DuplicateReceipt.AfterDigest);
                Assert.AreEqual(h.Kernel.DuplicateBefore, h.Kernel.DuplicateAfter);
                Assert.AreEqual(1, h.Kernel.Applies);
                Assert.AreEqual(1, h.Completed);
                Assert.IsEmpty(h.Session.RuleEvents, "跨时段巡逻属于同一个行动候选，不另开世界规则事务");
                if (escort != "none")
                {
                    Assert.AreEqual(0.3548, DsSeededRoll.Instance.Next(7, 1));
                    Assert.That(actual.Verdict.Notes.Any(note => note.StartsWith("换岗：")), Is.True);
                }
            }
        }

        [Test]
        public void 提供方失败原码状态及Usage透传_缺失Usage不把费用写成零()
        {
            string[] errors =
            {
                "incomplete_response", "output_incomplete", "refusal", "http_429", "http_500", "timeout", "network_error",
                "invalid_response", "response_too_large", "missing_output", "task_not_authorized", "unverified_perception",
                "busy", "input_too_large", "budget_exhausted", "cancelled", "empty_response", "provider_error",
                "frozen_payload_mismatch", "custom_provider_error", null
            };
            for (int i = 0; i < errors.Length; i++)
            {
                var response = Response(errors[i] == null ? "" : "partial:" + errors[i]);
                response.Error = errors[i];
                response.ResponseStatus = "incomplete";
                response.IncompleteReason = "max_output_tokens";
                if (i % 4 == 1) response.EstimatedUsd = null;
                if (i % 4 == 2) response.OutputTokens = null;
                if (i % 4 == 3)
                {
                    response.InputTokens = response.OutputTokens = null;
                    response.EstimatedUsd = 0m;
                }
                decimal? originalFee = response.EstimatedUsd;
                decimal? expectedFee = response.InputTokens.HasValue && response.OutputTokens.HasValue ? originalFee : null;
                using var h = new Harness(provider: new ScriptProvider { Result = response });
                string before = h.Session.CaptureState().Json;

                var trace = h.Run();

                AssertFailure(h, trace, "provider_error", errors[i] ?? "empty_response", before);
                Assert.AreEqual(response.Error, trace.Provider.Error);
                Assert.AreEqual(response.RawText, trace.Provider.RawText);
                Assert.AreEqual(response.ProviderName, trace.Provider.ProviderName);
                Assert.AreEqual(response.ResponseStatus, trace.Provider.ResponseStatus);
                Assert.AreEqual(response.IncompleteReason, trace.Provider.IncompleteReason);
                Assert.AreEqual(response.InputTokens, trace.Provider.InputTokens);
                Assert.AreEqual(response.OutputTokens, trace.Provider.OutputTokens);
                Assert.AreEqual(expectedFee, trace.Provider.EstimatedUsd);
                Assert.AreEqual(expectedFee, trace.ToRecord()["estimated_usd"]);
                Assert.AreEqual(expectedFee, h.Session.Metrics()["estimated_usd"]);
                Assert.AreEqual(originalFee, response.EstimatedUsd, "副本归一化不得改写提供方原结果");
                Assert.IsTrue(trace.Provider.Simulated);
                Assert.IsNull(trace.Proposal);
                Assert.IsNull(trace.Receipt);
                Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Provider).Status);
                Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Parse).Status);
                Assert.IsEmpty(h.Transaction.PrivateRecordList);
            }
        }

        [Test]
        public void 解析与规则失败及真实状态过期分别保留Kernel原码和宿主映射码()
        {
            var cases = new[]
            {
                (Raw: "not json", Kernel: "parse_failed", Failure: "parse_error"),
                (Raw: "[]", Kernel: "parse_failed", Failure: "parse_error"),
                (Raw: "{}", Kernel: "host_rejected", Failure: "schema_invalid"),
                (Raw: Buy.Replace("smoked_fish", "unknown_item"), Kernel: "host_rejected", Failure: "unknown_entity"),
                (Raw: Buy.Replace("buy", "forbidden_verb"), Kernel: "host_rejected", Failure: "verb_not_allowed")
            };
            foreach (var item in cases)
            {
                using var h = new Harness(provider: new ScriptProvider { Result = Response(item.Raw) });
                string before = h.Session.CaptureState().Json;
                var trace = h.Run();
                AssertFailure(h, trace, item.Kernel, item.Failure, before);
                Assert.AreEqual(item.Raw, trace.Provider.RawText);
                Assert.IsNull(trace.Provider.Error);
                Assert.AreEqual(StageStatus.Ok, trace.Stage(AiStage.Provider).Status);
                Assert.IsEmpty(h.Transaction.PrivateRecordList);
                if (item.Kernel == "host_rejected")
                {
                    Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Validate).Status);
                    Assert.IsNotEmpty(trace.ValidationIssues);
                    Assert.AreEqual("host_rejected", trace.Receipt.Status);
                    Assert.AreEqual(before, h.Adapter.Capture());
                }
                else Assert.IsNull(trace.Receipt);
            }

            using var stale = new Harness();
            string changed = null;
            stale.Session.StageChanged += (trace, stage) =>
            {
                if (stage != AiStage.Commit || trace.Stage(stage).Status != StageStatus.Pending) return;
                stale.Owner.Check();
                stale.Session.World.Coins--;
                changed = stale.Session.CaptureState().Json;
            };
            var rejected = stale.Run();
            AssertFailure(stale, rejected, "stale_state", "stale_state", changed);
            Assert.AreEqual("ready", stale.Kernel.LastPreparation.Receipt.Code);
            Assert.AreEqual("stale_state", rejected.Receipt.Status);
            Assert.AreNotEqual("stale_context", rejected.FailureCode);
            Assert.AreEqual(1, stale.Transaction.PrivateRecordList.Count);
        }

        [Test]
        public void Pending拒移动日间推进及Restore_Ready后Restart由真实Kernel拒旧局且不写()
        {
            var provider = new ScriptProvider { Hold = true };
            using var h = new Harness(provider: provider);
            var before = h.Session.CaptureState();
            var identity = h.Session.World;
            var pending = h.Act();
            var call = h.Owner.Pump(provider.Entered.Task);
            Assert.IsTrue(h.Session.Busy);
            Assert.IsTrue(h.Session.KernelActionInFlight);
            Assert.IsNull(h.Session.EnterPlace("workshop"));
            Assert.IsNull(h.Session.EndDayPhase(h));
            Assert.AreEqual("busy", Assert.Throws<InvalidOperationException>(() => h.Session.RestoreState(before)).Message);
            Assert.AreEqual(0, h.DayCommits);
            Assert.IsEmpty(h.Session.RuleEvents);
            Assert.AreEqual(before.Json, h.Session.CaptureState().Json);

            call.Release();
            Assert.AreEqual("ready", h.Owner.Pump(h.Kernel.Prepared.Task).Receipt.Code);
            Assert.IsFalse(pending.IsCompleted, "Kernel 已完成准备，但所有者尚未消费结果");
            Assert.AreEqual(before.Json, h.Session.CaptureState().Json);
            h.Session.Restart();
            var fresh = h.Session.CaptureState();
            Assert.AreNotSame(identity, h.Session.World);
            Assert.AreEqual(1, h.Resets);
            Assert.AreEqual(0, h.Adapter.RetainedRequestCount);

            var trace = h.Owner.Pump(pending);

            AssertFailure(h, trace, "stale_session", "stale_session", fresh.Json);
            Assert.AreEqual("ready", h.Kernel.LastPreparation.Receipt.Code);
            Assert.AreNotEqual("stale_context", trace.FailureCode);
            Assert.AreEqual("stale_session", trace.Receipt.Status);
            Assert.AreEqual(fresh.Hash, trace.Receipt.AfterHash);
            Assert.IsEmpty(h.Session.Traces);
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(1, h.Transaction.PrivateRecordList.Count);
        }

        [Test]
        public void 空闲Restore相同JSON及同编号ABA仍使旧Ready真正返回StaleSession()
        {
            using var h = new Harness();
            var before = h.Session.CaptureState();
            var packet = ContextBuilder.BuildTask(
                TaskPerceptionView.Capture(h.Session.World, AiTaskKind.Adjudicate, "player"), Observe);
            var request = h.Transaction.Begin("S1-1", packet);
            var prepared = h.Owner.Pump(h.Transaction.PrepareAsync(request, h.Source));
            Assert.AreEqual("ready", prepared.Code);
            Assert.IsFalse(h.Session.Busy);
            Assert.AreEqual(before.Json, h.Session.CaptureState().Json);
            var identity = h.Session.World;

            h.Session.RestoreState(before);

            Assert.AreNotSame(identity, h.Session.World);
            Assert.AreEqual(before.Json, h.Session.CaptureState().Json);
            Assert.AreEqual(1, h.Resets);
            Assert.AreEqual(0, h.Adapter.RetainedRequestCount);
            Assert.AreEqual(0, h.Adapter.RetainedBeforeUtf8Bytes);
            // 旧 Ready 不可能获得提交资格，故传 null；写能力仍只由真正的 Session 行动签发。
            var stale = h.Transaction.Commit(prepared, null);
            Assert.AreEqual("stale_session", stale.KernelCode);
            Assert.AreEqual("stale_session", stale.FailureCode);
            Assert.IsFalse(stale.Applied);
            Assert.AreEqual("stale_session", h.Kernel.LastReceipt.Code);
            Assert.AreEqual(before.Json, h.Session.CaptureState().Json);
            Assert.AreEqual(0, h.Kernel.Applies);

            var fresh = h.Run();
            Assert.AreEqual(request.RequestId, fresh.RequestId, "Restore 后有意复用同一请求编号，覆盖 ABA");
            Assert.AreEqual("committed", fresh.KernelCode);
            Assert.IsNull(fresh.FailureCode);
            string after = h.Session.CaptureState().Json;
            Assert.AreEqual("stale_session", h.Transaction.Commit(prepared, null).KernelCode);
            Assert.AreEqual(after, h.Session.CaptureState().Json);
            Assert.AreEqual(1, h.Kernel.Applies);
            Assert.AreEqual(1, h.Completed);
            Assert.AreEqual(2, h.Source.Calls);
        }

        [Test]
        public void Commit阶段同步取消由真实Kernel裁决_不写世界且不伪装宿主取消()
        {
            using var cancellation = new CancellationTokenSource();
            using var h = new Harness();
            string before = h.Session.CaptureState().Json;
            int cancellations = 0;
            h.Session.StageChanged += (trace, stage) =>
            {
                if (stage != AiStage.Commit || trace.Stage(stage).Status != StageStatus.Pending || cancellations != 0) return;
                h.Owner.Check();
                Assert.AreEqual("ready", h.Kernel.LastPreparation.Receipt.Code);
                Assert.AreEqual(before, h.Session.CaptureState().Json);
                cancellations++;
                cancellation.Cancel();
            };

            var trace = h.Run(ct: cancellation.Token);

            AssertFailure(h, trace, "cancelled", "cancelled", before);
            Assert.AreEqual(1, cancellations);
            Assert.AreEqual("ready", h.Kernel.LastPreparation.Receipt.Code);
            Assert.IsNull(trace.Provider.Error);
            Assert.AreEqual("cancelled", trace.Receipt.Status);
            Assert.AreEqual(1, h.Kernel.Commits, "取消仍必须抵达 Kernel.Commit，不能被 Session 前置短路");
            Assert.AreEqual(1, h.Transaction.PrivateRecordList.Count);
            Assert.IsFalse(h.Kernel.Writes.Single().Applied);
            Assert.AreEqual("action_write_not_active", Assert.Throws<InvalidOperationException>(
                () => h.Kernel.Writes.Single().Apply(before)).Message);
            Assert.AreEqual("committed", h.Run().KernelCode, "取消后门闩释放，后续独立请求仍能提交");
            Assert.AreEqual(2, h.Source.Calls);
            Assert.AreEqual(1, h.Kernel.Applies);
        }

        [Test]
        public void 失败准备后Reset仍保留失败原码_不被伪造为StaleSession()
        {
            var cases = new[]
            {
                (Raw: "partial", Error: "incomplete_response", Kernel: "provider_error", Failure: "incomplete_response"),
                (Raw: "not json", Error: (string)null, Kernel: "parse_failed", Failure: "parse_error"),
                (Raw: "{}", Error: (string)null, Kernel: "host_rejected", Failure: "schema_invalid")
            };
            foreach (var item in cases)
            {
                var response = Response(item.Raw);
                response.Error = item.Error;
                response.EstimatedUsd = null;
                var provider = new ScriptProvider { Hold = true, Result = response };
                using var h = new Harness(provider: provider);
                var pending = h.Act();
                var call = h.Owner.Pump(provider.Entered.Task);
                call.Release();
                Assert.AreEqual(item.Kernel, h.Owner.Pump(h.Kernel.Prepared.Task).Receipt.Code);
                Assert.IsFalse(pending.IsCompleted);
                h.Session.Restart();
                string fresh = h.Session.CaptureState().Json;

                var trace = h.Owner.Pump(pending);

                AssertFailure(h, trace, item.Kernel, item.Failure, fresh);
                Assert.AreNotEqual("stale_session", trace.KernelCode);
                Assert.AreNotEqual("stale_context", trace.FailureCode);
                Assert.AreEqual(response.Error, trace.Provider.Error);
                Assert.AreEqual(response.InputTokens, trace.Provider.InputTokens);
                Assert.AreEqual(response.OutputTokens, trace.Provider.OutputTokens);
                Assert.IsNull(trace.Provider.EstimatedUsd);
                Assert.AreEqual(1, h.Resets);
                Assert.AreEqual(1, provider.Calls);
                Assert.IsEmpty(h.Transaction.PrivateRecordList);
                Assert.IsEmpty(h.Session.Traces);
            }
        }

        [Test]
        public void 每次Ready仅增加私有记录_失败不录制且正常禁用入口无Kernel字段()
        {
            var world = DsContent.NewWorld(7);
            const string secret = "PRIVATE_PIP_BELIEF_NOT_FOR_TRACE";
            world.Beliefs.First(belief => belief.Owner == "pip").Text = secret;
            using var h = new Harness(DsWorldSnapshot.Capture(world, 0, 4));
            var first = h.Run();
            var snapshot = h.Transaction.PrivateRecordList;
            var second = h.Run();
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(2, h.Transaction.PrivateRecordList.Count);
            for (int i = 0; i < 2; i++)
            {
                var trace = i == 0 ? first : second;
                var frame = h.Transaction.PrivateRecordList[i];
                Assert.AreEqual(trace.RequestId, frame.RequestId);
                Assert.AreEqual(trace.Provider.RawText, frame.Raw);
                Assert.AreEqual(trace.Receipt.BeforeHash, frame.BeforeDigest);
                Assert.AreEqual(trace.Receipt.AfterHash, frame.AfterDigest);
                Assert.AreEqual(Protocol.Version, frame.KernelVersion);
                Assert.AreEqual(DsExecutor.RulesVersion, frame.HostRulesVersion);
                string publicJson = MiniJson.Write(trace.ToRecord());
                foreach (string forbidden in new[]
                {
                    secret, DsWorldSnapshot.Format, ReplayFrame.CurrentFormat, frame.Digest, frame.PromptFingerprint,
                    frame.CandidateIdentity, "private_records", "before_json", "after_json", "prompt_fingerprint"
                })
                    Assert.That(publicJson, Does.Not.Contain(forbidden));
                string prompt = Encoding.UTF8.GetString(ActionPromptCodec.Encode(trace.Prompt));
                Assert.That(prompt, Does.Not.Contain(secret));
                Assert.That(prompt, Does.Not.Contain(DsWorldSnapshot.Format));
            }
            Assert.Throws<NotSupportedException>(() => ((IList<ReplayFrame>)snapshot).Add(snapshot[0]));

            h.Source.Result = Response("{}");
            Assert.AreEqual("host_rejected", h.Run().KernelCode);
            Assert.AreEqual(2, h.Transaction.PrivateRecordList.Count);
            int begins = h.Kernel.Begins, commits = h.Kernel.Commits;
            h.Source.Result = null;
            var ordinary = h.Owner.Pump(h.Act(useKernel: false));
            Assert.IsNull(ordinary.KernelCode);
            Assert.IsFalse(ordinary.ToRecord().ContainsKey("kernel_code"));
            Assert.IsTrue(ordinary.Receipt.Applied);
            Assert.AreEqual(begins, h.Kernel.Begins);
            Assert.AreEqual(commits, h.Kernel.Commits);
            Assert.AreEqual(2, h.Transaction.PrivateRecordList.Count);
            Assert.IsFalse(h.Session.KernelActionInFlight);
        }

        [Test]
        public void 三十二次真实Session观察累计Begin快照UTF8保留量并输出日志证据()
        {
            const int count = 32;
            // 扩大本地时段预算，让 32 次观察均实际消耗时间，而不是在深夜反复执行零耗时动作。
            var before = DsWorldSnapshot.Capture(DsContent.NewWorld(7), 0, count * 2);
            using var h = new Harness(before);
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                string current = h.Session.CaptureState().Json;
                long bytes = Encoding.UTF8.GetByteCount(current);
                total += bytes;
                var trace = h.Run();
                Assert.AreEqual(AiTaskKind.Adjudicate, trace.Kind);
                Assert.AreEqual("observe", trace.Proposal["verb"]);
                Assert.AreEqual("committed", trace.KernelCode);
                Assert.AreEqual(2, trace.Receipt.TimeCost);
                Assert.IsTrue(trace.Receipt.Applied);
                Assert.AreEqual(bytes, h.Kernel.RequestBeforeUtf8Bytes[i]);
                Assert.AreEqual(total, h.Adapter.RetainedBeforeUtf8Bytes);
            }
            Assert.AreEqual(count, h.Adapter.RetainedRequestCount);
            Assert.AreEqual(count, h.Transaction.PrivateRecordList.Count);
            Assert.AreEqual(count, h.Session.Traces.Count);
            Assert.AreEqual(count, h.Completed);
            Assert.AreEqual(count, h.Source.Calls);
            Assert.AreEqual(count, h.Kernel.Applies);
            Assert.AreEqual(DayPhase.Noon, h.Session.World.Phase);
            Assert.AreEqual(0, h.Session.CaptureState().Spent);
            Assert.Greater(total, before.Json.Length * (long)count);
            TestContext.WriteLine($"真实离线 Session observe={count}；RequestBeforeUtf8Bytes 累计={total}；"
                + $"最小={h.Kernel.RequestBeforeUtf8Bytes.Min()}；最大={h.Kernel.RequestBeforeUtf8Bytes.Max()}；"
                + $"RetainedRequestCount={h.Adapter.RetainedRequestCount}");
        }

        static void AssertFailure(Harness h, AiTrace trace, string kernel, string failure, string unchanged)
        {
            Assert.AreEqual(kernel, h.Kernel.LastReceipt.Code);
            Assert.AreEqual(kernel, trace.KernelCode);
            Assert.AreEqual(failure, trace.FailureCode);
            Assert.AreEqual(kernel, trace.ToRecord()["kernel_code"]);
            Assert.AreEqual(failure, trace.ToRecord()["failure"]);
            Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Commit).Status);
            Assert.AreEqual(VerdictKind.Rejected, trace.Verdict.Kind);
            Assert.AreEqual(unchanged, h.Session.CaptureState().Json);
            Assert.IsEmpty(trace.Changes);
            if (trace.Receipt != null)
            {
                Assert.IsFalse(trace.Receipt.Applied);
                Assert.AreEqual(0, trace.Receipt.TimeCost);
                Assert.IsEmpty(trace.Receipt.Effects);
            }
            Assert.AreEqual(0, h.Kernel.Applies);
            Assert.AreEqual(1, h.Completed);
            Assert.IsFalse(h.Session.Busy);
            Assert.IsFalse(h.Session.KernelActionInFlight);
        }

        static ProviderResult Response(string raw) => new ProviderResult
        {
            ProviderName = "action-session-offline", RawText = raw, Simulated = true,
            InputTokens = 11, OutputTokens = 7, EstimatedUsd = 0.00013m
        };

        static string Receipt(AiTrace trace) => MiniJson.Write(trace.Receipt?.ToRecord());

        static string StableTrace(AiTrace trace)
        {
            var record = trace.ToRecord();
            record.Remove("total_ms");
            record.Remove("kernel_code");
            foreach (var stage in ((List<object>)record["stages"]).Cast<Dictionary<string, object>>()) stage.Remove("ms");
            return MiniJson.Write(record);
        }

        static List<string> Watch(DragonsleepSession session, OwnerQueue owner)
        {
            var events = new List<string>();
            session.StageChanged += (trace, stage) =>
            {
                owner.Check();
                Assert.IsTrue(session.Busy);
                events.Add(trace.RequestId + ":" + stage + ":" + trace.Stage(stage).Status);
            };
            session.TraceCompleted += trace =>
            {
                owner.Check();
                Assert.IsTrue(session.Busy);
                events.Add("done:" + trace.RequestId);
            };
            session.RuleEventCompleted += rule =>
            {
                owner.Check();
                events.Add("rule:" + rule.RequestId);
            };
            return events;
        }

        static TaskCompletionSource<T> Signal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        sealed class OwnerQueue : IDsOwnerThreadDispatcher
        {
            readonly int _owner = Thread.CurrentThread.ManagedThreadId;
            readonly object _gate = new object();
            readonly Queue<Action> _jobs = new Queue<Action>();
            TaskCompletionSource<bool> _next = Signal<bool>();

            internal void Check() => Assert.AreEqual(_owner, Thread.CurrentThread.ManagedThreadId, "只能由创建会话的所有者操作");

            Task Next
            {
                get { lock (_gate) return _jobs.Count > 0 ? Task.CompletedTask : _next.Task; }
            }

            public Task<T> InvokeAsync<T>(Func<T> synchronousBlock)
            {
                var result = Signal<T>();
                lock (_gate)
                {
                    _jobs.Enqueue(() =>
                    {
                        try { result.TrySetResult(synchronousBlock()); }
                        catch (Exception error) { result.TrySetException(error); }
                    });
                    _next.TrySetResult(true);
                }
                return result.Task;
            }

            void Drain()
            {
                Check();
                Action job;
                lock (_gate)
                {
                    job = _jobs.Dequeue();
                    if (_jobs.Count == 0) _next = Signal<bool>();
                }
                job();
            }

            internal T Pump<T>(Task<T> task)
            {
                Check();
                while (!task.IsCompleted)
                {
                    Task next = Next;
                    Task.WhenAny(task, next).WaitAsync(Guard).GetAwaiter().GetResult();
                    // 目标和队列同时就绪时优先停止，便于精确停在 Ready、尚未消费的边界。
                    if (!task.IsCompleted && next.IsCompleted) Drain();
                }
                return task.GetAwaiter().GetResult();
            }
        }

        sealed class ProviderCall
        {
            readonly ProviderResult _value;
            internal readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();
            internal ProviderCall(ProviderResult value) { _value = value; }
            internal void Release() => Reply.TrySetResult(_value);
        }

        sealed class ScriptProvider : IAiProvider
        {
            readonly object _gate = new object();
            readonly List<ProviderCall> _pending = new List<ProviderCall>();
            int _calls;
            bool _released;
            internal bool Hold;
            internal ProviderResult Result;
            internal readonly TaskCompletionSource<ProviderCall> Entered = Signal<ProviderCall>();
            internal int Calls => Volatile.Read(ref _calls);
            public string Name => "action-session-offline";
            public bool IsSimulated => true;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                var value = Result ?? new RuleProvider().CompleteAsync(packet, ct).GetAwaiter().GetResult();
                var call = new ProviderCall(value);
                lock (_gate)
                {
                    _pending.Add(call);
                    if (!Hold || _released) call.Release();
                }
                Entered.TrySetResult(call);
                // 故意不协作取消：Restart/Reset 的代际隔离必须靠真实 Kernel，不能靠取消回包。
                return call.Reply.Task;
            }

            internal void ReleaseAll()
            {
                lock (_gate)
                {
                    _released = true;
                    foreach (var call in _pending) call.Release();
                }
            }
        }

        /// <summary>只观察和转发真实 Adapter 调用；不生成准备结果、回执或写能力。</summary>
        sealed class RecordingKernel : IPieGameActionKernel
        {
            readonly ActionKernelAdapter _adapter;
            readonly OwnerQueue _owner;
            internal int Registers, Begins, Commits;
            internal bool CheckDuplicate;
            internal Preparation LastPreparation;
            internal Receipt LastReceipt, DuplicateReceipt;
            internal string DuplicateBefore, DuplicateAfter;
            internal readonly TaskCompletionSource<Preparation> Prepared = Signal<Preparation>();
            internal readonly List<DsActionWrite> Writes = new List<DsActionWrite>();
            internal readonly List<long> RequestBeforeUtf8Bytes = new List<long>();
            internal int Applies => Writes.Count(write => write.Applied);

            internal RecordingKernel(ActionKernelAdapter adapter, OwnerQueue owner) { _adapter = adapter; _owner = owner; }

            public void Register(string kind, ITaskHandler handler)
            {
                _owner.Check();
                _adapter.Register(kind, handler);
                Registers++;
            }

            public Request Begin(string id, string kind, FrozenPayload payload)
            {
                _owner.Check();
                long before = _adapter.RetainedBeforeUtf8Bytes;
                var request = _adapter.Begin(id, kind, payload);
                RequestBeforeUtf8Bytes.Add(_adapter.RetainedBeforeUtf8Bytes - before);
                Begins++;
                return request;
            }

            public async Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default)
            {
                _owner.Check();
                var prepared = await _adapter.PrepareAsync(request, provider, cancellation).ConfigureAwait(false);
                LastPreparation = prepared;
                Prepared.TrySetResult(prepared);
                return prepared;
            }

            public Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken cancellation = default)
            {
                _owner.Check();
                Commits++;
                if (write != null) Writes.Add(write);
                var receipt = LastReceipt = _adapter.Commit(preparation, write, cancellation);
                if (CheckDuplicate && receipt.Code == "committed")
                {
                    DuplicateBefore = _adapter.Capture();
                    DuplicateReceipt = _adapter.Commit(preparation, write, cancellation);
                    DuplicateAfter = _adapter.Capture();
                }
                return receipt;
            }
        }

        sealed class Harness : IDisposable, IDsDayPhaseTransaction
        {
            internal readonly OwnerQueue Owner = new OwnerQueue();
            internal readonly ScriptProvider Source;
            internal readonly DragonsleepSession Session;
            internal readonly DsKernelStatePort Port;
            internal readonly ActionKernelAdapter Adapter;
            internal readonly RecordingKernel Kernel;
            internal readonly KernelActionTransaction Transaction;
            internal readonly List<string> Events;
            readonly List<Task<AiTrace>> _runs = new List<Task<AiTrace>>();
            internal int Resets, Completed, DayCommits;

            internal Harness(DsWorldSnapshot snapshot = null, ScriptProvider provider = null)
            {
                Source = provider ?? new ScriptProvider();
                Session = new DragonsleepSession(Source, 7, ownerDispatcher: Owner);
                if (snapshot != null) Session.RestoreState(snapshot);
                Port = new DsKernelStatePort(() => { Owner.Check(); return Session.CaptureState(); });
                Adapter = new ActionKernelAdapter(Port);
                Kernel = new RecordingKernel(Adapter, Owner);
                Transaction = new KernelActionTransaction(Kernel);
                Session.StateReplaced += () =>
                {
                    Owner.Check();
                    // Adapter.Reset 自开 port.OpenReset；不能在这里再套一个 scope。
                    Adapter.Reset(Session.CaptureState().Json);
                    Resets++;
                };
                Events = Watch(Session, Owner);
                Session.TraceCompleted += _ => { Owner.Check(); Completed++; };
            }

            internal Task<AiTrace> Act(string text = Observe, CancellationToken ct = default, bool useKernel = true)
            {
                var task = Session.ActAsync(text, ct, useKernel ? Transaction : null);
                _runs.Add(task);
                return task;
            }

            internal AiTrace Run(string text = Observe, CancellationToken ct = default) => Owner.Pump(Act(text, ct));

            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsDayPhaseWrite write)
            {
                Owner.Check();
                DayCommits++;
                return Adapter.TransactDay(id, Adapter.CaptureState(), after.Json, write).Code;
            }

            public void Dispose()
            {
                Source.ReleaseAll();
                foreach (var task in _runs)
                {
                    try { Owner.Pump(task); }
                    catch (Exception) { /* 只收尾已发起请求，不覆盖测试主体的失败。 */ }
                }
            }
        }
    }
}
