using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.PieGame;

namespace Town.Core.Tests
{
    /// <summary>真实 Kernel 的确定性离线记忆边界；不发 HTTP、不读取密钥、环境变量或费用账本。</summary>
    public sealed class DragonsleepActionMemoryTests
    {
        const string Input = "固定离线行动";
        const string Canary = "FORGED_MEMORY_CANARY";

        [Test]
        public void 成功喂鱼后历史保留已消耗ID但当前库存不恢复鱼()
        {
            using var h = new Harness(FeedWorld());
            var fed = h.Run(Raw("feed", "whelp", "smoked_fish"));
            var packet = h.Read();
            var memory = Memory(packet);
            var entry = (JObject)memory.Single();

            Assert.AreEqual("committed", fed.KernelCode);
            Assert.AreEqual(VerdictKind.Accepted, fed.Verdict.Kind);
            Assert.AreEqual("consumed", h.Session.World.Items["smoked_fish"].Holder);
            Assert.AreEqual(fed.RequestId, (string)entry["request_id"]);
            Assert.AreEqual(fed.Clock, (string)entry["at"]);
            Assert.AreEqual("feed", (string)entry["verb"]);
            Assert.AreEqual("whelp", (string)entry["target"]);
            Assert.AreEqual("smoked_fish", (string)entry["item"]);
            Assert.AreEqual("一串熏鱼", (string)entry["item_name"]);
            Assert.AreEqual("Accepted", (string)entry["result"]);
            Assert.AreEqual("consumed_by_this_action", (string)entry["item_outcome"]);
            CollectionAssert.AreEquivalent(new[]
            {
                "request_id", "at", "verb", "target", "item", "item_name", "result", "item_outcome"
            }, entry.Properties().Select(property => property.Name));
            var view = JObject.Parse(packet.TaskSnapshot.Json);
            Assert.IsFalse(view["inventory"].Values<string>().Any(text => text.StartsWith("smoked_fish：")));
            Assert.That((string)view["memory_scope"], Does.Contain("不是当前持有"));
            Assert.That(packet.Instructions, Does.Contain("历史记录不证明现在仍持有"));
        }

        [Test]
        public void 零变化拒绝仍记录且掷骰失败始终标为Rejected而不是成功()
        {
            var world = DsContent.NewWorld(7);
            world.Items["smoked_fish"].Holder = "player";
            using var h = new Harness(world);
            string before = h.Session.CaptureState().Json;
            var refused = h.Run(Raw("buy", "marta", "smoked_fish"));
            Assert.AreEqual("committed", refused.KernelCode);
            Assert.AreEqual("seller_absent", refused.Verdict.ReasonCode.Code);
            Assert.IsFalse(refused.Receipt.Applied);
            Assert.AreEqual(before, h.Session.CaptureState().Json);

            var failed = h.Run(Raw("sneak", difficulty: 0.6));
            Assert.AreEqual("committed", failed.KernelCode);
            Assert.AreEqual("roll_failed", failed.Verdict.ReasonCode.Code);
            Assert.AreEqual(0.3548, failed.Verdict.Roll);
            Assert.IsTrue(failed.Receipt.Applied, "失败掷骰仍耗时，不能按 Applied 推导成功");
            var memory = Memory(h.Read());
            CollectionAssert.AreEqual(new[] { refused.RequestId, failed.RequestId },
                memory.Select(entry => (string)entry["request_id"]));
            CollectionAssert.AreEqual(new[] { "Rejected", "Rejected" },
                memory.Select(entry => (string)entry["result"]));
            Assert.IsTrue(memory.All(entry => (string)entry["item_outcome"] == "no_transfer_observed"));
        }

        [Test]
        public void 可变Trace提示提案历史列表及玩家模型伪造文本均不能污染记忆()
        {
            var world = DsContent.NewWorld(7);
            world.Items["smoked_fish"].Name = new string('鱼', 64);
            using var h = new Harness(world);
            h.Session.StageChanged += (current, stage) =>
            {
                if (stage != AiStage.Commit || current.Stage(stage).Status != StageStatus.Pending)
                    return;
                current.Proposal["item"] = Canary;
                current.Proposal["target"] = Canary;
                current.Verdict.Kind = VerdictKind.Rejected;
                current.Verdict.Reason = Canary;
                current.Presented = Canary;
            };
            var trace = h.Run(Raw("buy", "marta", "smoked_fish", narration: Canary), Canary);
            string request = trace.RequestId;
            trace.RequestId = Canary;
            trace.Clock = Canary;
            trace.Provider.RawText = Raw("give", Canary, Canary, narration: Canary);
            trace.Prompt.Instructions = Canary;
            foreach (var section in trace.Prompt.Sections)
                section.Text = Canary;
            trace.Perception["inventory"] = new List<object> { Canary };
            trace.Changes.Clear();
            h.Session.Traces.Clear();
            h.Session.Traces.Add(new AiTrace { RequestId = Canary, Presented = Canary });

            var packet = h.Read();
            var memory = Memory(packet);
            var entry = memory.Single();
            Assert.AreEqual(request, (string)entry["request_id"]);
            Assert.AreEqual("buy", (string)entry["verb"]);
            Assert.AreEqual("marta", (string)entry["target"]);
            Assert.AreEqual("smoked_fish", (string)entry["item"]);
            Assert.AreEqual(new string('鱼', 48), (string)entry["item_name"], "只保留冻结视角中的物件名且最多 48 字");
            Assert.AreEqual("Accepted", (string)entry["result"]);
            Assert.AreEqual("acquired_by_this_action", (string)entry["item_outcome"], "购买只记获得，不序列化原持有者");
            Assert.That(memory.ToString(Formatting.None), Does.Not.Contain(Canary));
            var detached = packet.TaskSnapshot.ToDictionary();
            ((List<object>)detached["recent_player_actions"]).Clear();
            Assert.AreEqual(1, Memory(packet).Count, "ToDictionary 不得暴露可修改的内部记忆");
            Assert.AreEqual(request, (string)Memory(h.Read())[0]["request_id"], "修改快照副本也不能清除 Session 记忆");
        }

        [Test]
        public void 出示交付只记可见后果而私有信任异地物权认知变化不改变载荷()
        {
            var baseline = new List<string>();
            for (int variant = 0; variant < 2; variant++)
            {
                var world = DsContent.NewWorld(7);
                world.Items["pact_page"].Holder = "player";
                world.Actors["maren"].Place = "market";
                if (variant == 1)
                {
                    world.Actors["maren"].Trust = 0.07f;
                    world.Actors["pip"].Trust = 0.9876f;
                    world.Actors["pip"].Place = "skull_hill";
                    world.Items["rope"].Holder = "pip";
                    world.Items["rope"].Name = Canary;
                    world.Beliefs.First(belief => belief.Owner == "pip").Text = Canary;
                    world.Chronicle.Add(Canary);
                }
                using var h = new Harness(world);
                var shown = h.Run(Raw("show", "maren", "pact_page"));
                var given = h.Run(Raw("give", "maren", "pact_page"));
                Assert.AreEqual(VerdictKind.Accepted, shown.Verdict.Kind);
                Assert.AreEqual(VerdictKind.Accepted, given.Verdict.Kind);
                Assert.IsTrue(shown.Changes.Any(change => change.Path == "maren.trust"));
                Assert.AreEqual("maren", h.Session.World.Items["pact_page"].Holder);
                var packet = h.Read();
                var memory = Memory(packet);
                CollectionAssert.AreEqual(new[] { "no_transfer_observed", "handed_over_by_this_action" },
                    memory.Select(entry => (string)entry["item_outcome"]));
                Assert.That(memory.ToString(Formatting.None), Does.Not.Contain("rope"));
                string payload = Payload(packet);
                foreach (string forbidden in new[] { Canary, "maren.trust", "trust_player", "holder", "your_beliefs" })
                    Assert.That(payload, Does.Not.Contain(forbidden));
                baseline.Add(payload);
            }
            Assert.AreEqual(baseline[0], baseline[1], "只改变隐藏状态的有限差分必须严格相等");
        }

        [Test]
        public void 最近四条按FIFO淘汰且过去记忆不能递归补当前未识别物件与目标()
        {
            using var h = new Harness(FeedWorld());
            var first = h.Run(Raw("feed", "whelp", "smoked_fish"));
            h.Session.World.Whelp.Place = "skull_hill";
            var repeated = h.Run(Raw("feed", "whelp", "smoked_fish"));
            Assert.AreEqual("item_missing", repeated.Verdict.ReasonCode.Code);
            var observedTrace = h.Run(Raw("unsupported"));
            var observed = observedTrace.Prompt;
            Assert.AreEqual("smoked_fish", (string)Memory(observed)[0]["item"]);
            Assert.AreEqual("", (string)Memory(observed)[1]["item"]);
            Assert.AreEqual("", (string)Memory(observed)[1]["item_name"]);
            Assert.AreEqual("", (string)Memory(observed)[1]["target"]);

            var fourth = h.Run(Raw("unsupported"));
            var fifth = h.Run(Raw("unsupported"));
            var memory = Memory(h.Read());
            CollectionAssert.AreEqual(new[] { repeated.RequestId, observedTrace.RequestId, fourth.RequestId, fifth.RequestId },
                memory.Select(entry => (string)entry["request_id"]));
            Assert.AreEqual(4, memory.Count);
            Assert.IsFalse(memory.Any(entry => (string)entry["request_id"] == first.RequestId));
            Assert.That(memory.ToString(Formatting.None), Does.Not.Contain("smoked_fish"));
        }

        [Test]
        public void 重开还原ABA迟到Ready与提交完成通知重开均清空且冻结记忆不随世界变化()
        {
            using var h = new Harness(FeedWorld());
            h.Run(Raw("feed", "whelp", "smoked_fish"));
            var saved = h.Session.CaptureState();
            var frozen = h.Read().TaskSnapshot;
            string frozenJson = frozen.Json;
            h.Session.World.Items["smoked_fish"].Name = Canary;
            h.Session.World.Items["smoked_fish"].Holder = "player";
            Assert.AreEqual(frozenJson, frozen.Json);
            Assert.AreEqual("一串熏鱼", (string)JObject.Parse(frozen.Json)["recent_player_actions"][0]["item_name"]);
            h.Session.RestoreState(saved);
            h.Session.RestoreState(h.Session.CaptureState());
            Assert.AreEqual(saved.Json, h.Session.CaptureState().Json, "同 JSON 的 Restore 仍必须更换记忆时期");
            var fresh = h.Run(Raw("unsupported"));
            Assert.AreEqual("S1-1", fresh.RequestId, "同局代次和编号复用仍是新的记忆时期");
            Assert.IsEmpty(Memory(fresh.Prompt));

            h.Source.HoldNext();
            var pending = h.Act(Raw("observe"));
            h.Owner.Pump(h.Source.Entered.Task);
            h.Source.Release();
            Assert.AreEqual("ready", h.Owner.Pump(h.Kernel.Prepared.Task).Receipt.Code);
            Assert.IsFalse(pending.IsCompleted);
            h.Session.Restart();
            Assert.AreEqual("stale_session", h.Owner.Pump(pending).KernelCode);
            Assert.IsEmpty(Memory(h.Read()), "迟到 Ready 不得向新局回灌");

            bool restarted = false;
            h.Session.StageChanged += (trace, stage) =>
            {
                if (restarted || stage != AiStage.Commit || trace.KernelCode != "committed")
                    return;
                restarted = true;
                h.Session.Restart();
            };
            Assert.AreEqual("committed", h.Run(Raw("observe")).KernelCode);
            Assert.IsTrue(restarted);
            Assert.IsEmpty(Memory(h.Read()), "Commit 完成通知内 Restart 后不能在通知返回时补回旧记忆");
        }

        [Test]
        public void 安全载荷重建保留冻结记忆且真实Kernel同版回放成功跨感知版本拒绝()
        {
            string feed = Raw("feed", "whelp", "smoked_fish");
            using var source = new Harness(FeedWorld());
            source.Run(feed);
            var before = source.Session.CaptureState();
            var trace = source.Run(Raw("unsupported"));
            var record = source.Transaction.PrivateRecordList.Last();
            string expected = Payload(trace.Prompt);
            var copied = trace.Prompt.FreezeCopy();
            trace.Prompt.Instructions = Canary;
            foreach (var section in trace.Prompt.Sections)
                section.Text = Canary;
            trace.Prompt.Sections.Add(new PromptSection { Name = "world_truth", Text = Canary });
            source.Session.World.Chronicle.Add(Canary);
            Assert.AreEqual(expected, Payload(trace.Prompt));
            Assert.That(expected, Does.Contain("recent_player_actions"));
            Assert.That(expected, Does.Contain("smoked_fish"));
            Assert.That(expected, Does.Not.Contain(Canary));
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(expected), 12288);
            Assert.AreEqual(DsActionPerception.Current, copied.ActionPerceptionVersion);
            Assert.AreEqual(ActionPromptCodec.Freeze(copied).Fingerprint, record.PromptFingerprint);

            foreach (string version in new[] { DsActionPerception.Current, DsActionPerception.Legacy })
            {
                using var replay = new Harness(FeedWorld());
                replay.Run(feed);
                Assert.AreEqual(before.Json, replay.Session.CaptureState().Json);
                replay.Kernel.Replay = record;
                replay.Actions = new ReplayTransaction(replay.Transaction, before, record);
                var result = replay.Run(Raw("must_not_call_provider"), perception: version);
                Assert.AreEqual(1, replay.Source.Calls, "回放只能使用 Kernel 的录制提供方");
                if (version == DsActionPerception.Current)
                {
                    Assert.AreEqual("ready", replay.Kernel.LastPreparation.Receipt.Code);
                    Assert.AreEqual("committed", result.KernelCode);
                    Assert.AreEqual(record.AfterDigest, replay.Session.CaptureState().Hash);
                }
                else
                {
                    Assert.AreEqual("replay_mismatch", result.KernelCode);
                    Assert.AreEqual(before.Json, replay.Session.CaptureState().Json);
                }
            }
        }

        [Test]
        public void 默认v1及v2提示配旧感知仍无记忆字段且旧编码和绑定逐字节兼容()
        {
            var world = DsContent.NewWorld(7);
            foreach (string prompt in new[] { DsActionPrompt.Legacy, DsActionPrompt.Current })
            {
                var view = TaskPerceptionView.Capture(world, AiTaskKind.Adjudicate, "player", 4, null, prompt);
                var packet = ContextBuilder.BuildTask(view, Input);
                var explicitLegacy = ContextBuilder.BuildTask(TaskPerceptionView.Capture(world,
                    AiTaskKind.Adjudicate, "player", 4, null, prompt, DsActionPerception.Legacy), Input);
                Assert.AreEqual(DsActionPerception.Legacy, view.ActionPerceptionVersion);
                Assert.AreEqual(DsActionPerception.Legacy, packet.FreezeCopy().ActionPerceptionVersion);
                Assert.IsNull(JObject.Parse(view.Json)["recent_player_actions"]);
                Assert.IsNull(JObject.Parse(view.Json)["memory_scope"]);
                Assert.That(packet.Instructions, Does.Not.Contain("recent_player_actions"));
                CollectionAssert.AreEqual(LegacyEncoding(packet), ActionPromptCodec.Encode(packet));
                CollectionAssert.AreEqual(ActionPromptCodec.Encode(explicitLegacy), ActionPromptCodec.Encode(packet));
                Assert.AreEqual(Payload(explicitLegacy), Payload(packet));
                Assert.IsNull(JObject.Parse(Encoding.UTF8.GetString(ActionPromptCodec.Encode(packet)))["action_perception_version"]);
                string binding = ActionPromptCodec.Binding;
                if (prompt == DsActionPrompt.Current)
                    binding += "|" + prompt;
                Assert.AreEqual(binding, ActionPromptCodec.BindingFor(packet));
            }
            using var h = new Harness();
            h.Owner.Pump(h.Session.ActAsync(Input));
            Assert.AreEqual(DsActionPerception.Legacy, h.Source.Packet.ActionPerceptionVersion);
            Assert.IsEmpty(Memory(h.Read()), "未显式经过 Kernel 的 headless 行动不进入记忆");
            h.Session.Restart();
            h.Source.RawText = Raw("buy", "marta", "smoked_fish");
            var legacyAction = h.Owner.Pump(h.Session.ActAsync(Input, default, h.Transaction));
            Assert.AreEqual("committed", legacyAction.KernelCode);
            Assert.AreEqual(DsActionPerception.Legacy, legacyAction.Prompt.ActionPerceptionVersion);
            Assert.AreEqual(legacyAction.RequestId, (string)Memory(h.Read())[0]["request_id"],
                "所有显式 Kernel 提交都积累记忆，只有显式 v2 感知才发送");
            var empty = PublicPacket(world, DsActionPerception.Current);
            Assert.IsEmpty(Memory(empty), "公开 Capture 只能创建空记忆，不能注入任意历史");
            var encoded = JObject.Parse(Encoding.UTF8.GetString(ActionPromptCodec.Encode(empty)));
            Assert.AreEqual(DsActionPerception.Current, (string)encoded["action_perception_version"]);
            Assert.AreEqual(ActionPromptCodec.Binding + "|" + DsActionPrompt.Current + "|" + DsActionPerception.Current,
                ActionPromptCodec.BindingFor(empty));
            Assert.AreNotEqual(DsReplayFrame.Fingerprint(PublicPacket(world, DsActionPerception.Legacy)),
                DsReplayFrame.Fingerprint(empty));
        }

        [Test]
        public void 错误版本缺事务与其他任务不能启用或继承行动记忆()
        {
            using var h = new Harness();
            string before = h.Session.CaptureState().Json;
            foreach (string invalid in new[] { null, "", "ds-action-perception-v3", "DS-ACTION-PERCEPTION-V2" })
            {
                Assert.Throws<ArgumentException>(() => TaskPerceptionView.Capture(h.Session.World,
                    AiTaskKind.Adjudicate, "player", 4, null, DsActionPrompt.Current, invalid));
                Assert.Throws<ArgumentException>(() => h.Session.ActAsync(Input, default, h.Transaction,
                    DsActionPrompt.Current, invalid));
            }
            Assert.Throws<ArgumentException>(() => h.Session.ActAsync(Input, default, null,
                DsActionPrompt.Current, DsActionPerception.Current));
            Assert.Throws<ArgumentException>(() => h.Session.ActAsync(Input, default, h.Transaction,
                DsActionPrompt.Legacy, DsActionPerception.Current));
            var oldPrompt = Assert.Throws<ArgumentException>(() => TaskPerceptionView.Capture(h.Session.World,
                AiTaskKind.Adjudicate, "player", 4, null, DsActionPrompt.Legacy, DsActionPerception.Current));
            Assert.AreEqual("action_memory_requires_current_prompt", oldPrompt.Message);
            Assert.AreEqual(0, h.Source.Calls);
            Assert.IsEmpty(h.Session.Traces);
            Assert.IsFalse(h.Session.Busy);
            Assert.AreEqual(before, h.Session.CaptureState().Json);
            var action = h.Run(Raw("buy", "marta", "smoked_fish"));
            foreach (AiTaskKind kind in Enum.GetValues(typeof(AiTaskKind)))
            {
                if (kind == AiTaskKind.Adjudicate)
                    continue;
                string actor = kind == AiTaskKind.Direct ? "gm"
                    : kind == AiTaskKind.Dialogue || kind == AiTaskKind.AgentPlan ? "marta" : "player";
                var view = TaskPerceptionView.Capture(h.Session.World, kind, actor);
                Assert.IsNull(JObject.Parse(view.Json)["recent_player_actions"], kind.ToString());
                Assert.IsNull(JObject.Parse(view.Json)["memory_scope"], kind.ToString());
                Assert.Throws<ArgumentException>(() => TaskPerceptionView.Capture(h.Session.World,
                    kind, actor, 4, null, DsActionPrompt.Legacy, DsActionPerception.Current));
            }
            var recaptured = PublicPacket(h.Session.World, DsActionPerception.Current);
            Assert.IsEmpty(Memory(recaptured), "公开快照不能从世界或 Session 日志推导历史");
            h.Source.RawText = "{\"say\":\"普通回应\",\"cites\":[],\"reveal\":[],\"trust_delta\":0,"
                + "\"persuaded_by\":[],\"action\":\"none\"}";
            var dialogue = h.Owner.Pump(h.Session.SpeakAsync("marta", Input));
            Assert.AreEqual(AiTaskKind.Dialogue, dialogue.Kind);
            Assert.IsNull(dialogue.Prompt.TaskSnapshot);
            Assert.That(Payload(dialogue.Prompt), Does.Not.Contain("recent_player_actions"));
            Assert.AreEqual(action.RequestId, (string)Memory(h.Read()).Single()["request_id"],
                "其他任务既不读取行动记忆，也不追加行动记忆");
        }

        [Test]
        public void 提供方解析校验取消与过期失败都不录记忆且冻结前后世界变化不改旧条目()
        {
            foreach (string failure in new[] { "provider_error", "parse_failed", "host_rejected", "cancelled", "stale_state" })
            {
                using var h = new Harness();
                var seed = h.Run(Raw("buy", "marta", "smoked_fish"));
                using var cancellation = new CancellationTokenSource();
                bool intervene = true;
                h.Session.StageChanged += (trace, stage) =>
                {
                    if (!intervene || stage != AiStage.Commit || trace.Stage(stage).Status != StageStatus.Pending)
                        return;
                    if (failure == "stale_state")
                        h.Session.World.Coins--;
                    if (failure == "cancelled")
                        cancellation.Cancel();
                };
                h.Source.Error = failure == "provider_error" ? "offline_failure" : null;
                string raw = failure == "parse_failed" ? "not json"
                    : failure == "host_rejected" ? "{}" : Raw("observe");
                var rejected = h.Run(raw, ct: cancellation.Token);
                Assert.AreEqual(failure, rejected.KernelCode);
                intervene = false;
                h.Source.Error = null;
                h.Session.World.Items["smoked_fish"].Name = Canary;
                var packet = h.Read();
                var memory = Memory(packet);
                Assert.AreEqual(1, memory.Count, failure);
                Assert.AreEqual(seed.RequestId, (string)memory[0]["request_id"], failure);
                Assert.AreEqual("一串熏鱼", (string)memory[0]["item_name"], failure);
                string frozen = packet.TaskSnapshot.Json;
                h.Session.World.Items["smoked_fish"].Holder = "consumed";
                Assert.AreEqual(frozen, packet.TaskSnapshot.Json);
                Assert.That(memory.ToString(Formatting.None), Does.Not.Contain(Canary));
            }
        }

        static DsWorld FeedWorld()
        {
            var world = DsContent.NewWorld(7);
            world.PlayerPlace = "docks";
            world.Whelp.TrustPlayer = 0.6f;
            world.Items["smoked_fish"].Holder = "player";
            return world;
        }

        static string Raw(string verb, string target = "", string item = "", double difficulty = 0.1, string narration = "尝试") =>
            new JObject
            {
                ["verb"] = verb, ["target"] = target, ["item"] = item, ["place"] = "",
                ["laws"] = new JArray(), ["difficulty"] = difficulty, ["narration"] = narration
            }.ToString(Formatting.None);

        static PromptPacket PublicPacket(DsWorld world, string version) => ContextBuilder.BuildTask(
            TaskPerceptionView.Capture(world, AiTaskKind.Adjudicate, "player", 4, null, DsActionPrompt.Current, version), Input);

        static JArray Memory(PromptPacket packet) => (JArray)JObject.Parse(packet.TaskSnapshot.Json)["recent_player_actions"];

        static string Payload(PromptPacket packet) => OpenAiDragonsleepProvider.BuildPayload(
            packet, OpenAiRunProfile.SixMechanismResearch).ToString(Formatting.None);

        // 独立保留感知版本出现前的字段集合与排序，不用新版 Encode 计算期望字节。
        static byte[] LegacyEncoding(PromptPacket packet)
        {
            var record = new Dictionary<string, object>
            {
                ["actor"] = packet.ActorId, ["instructions"] = packet.Instructions, ["kind"] = packet.Kind.ToString(),
                ["original_input"] = packet.OriginalPlayerInput, ["schema"] = packet.SchemaName,
                ["sections"] = packet.Sections.Select(section => (object)new Dictionary<string, object>
                {
                    ["name"] = section.Name, ["text"] = section.Text, ["untrusted"] = section.Untrusted
                }).ToList(),
                ["task_snapshot_json"] = MiniJson.Write(Canonical(MiniJson.Parse(packet.TaskSnapshot.Json)))
            };
            if (packet.ActionPromptVersion != DsActionPrompt.Legacy)
                record["action_prompt_version"] = packet.ActionPromptVersion;
            return new UTF8Encoding(false, true).GetBytes(MiniJson.Write(Canonical(record)));
        }

        static object Canonical(object value)
        {
            if (value is Dictionary<string, object> map)
                return new SortedDictionary<string, object>(map.ToDictionary(pair => pair.Key,
                    pair => Canonical(pair.Value)), StringComparer.Ordinal);
            return value is List<object> list ? list.Select(Canonical).ToList() : value;
        }

        static TaskCompletionSource<T> Signal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        sealed class Source : IAiProvider
        {
            internal string RawText = Raw("observe"), Error;
            internal PromptPacket Packet;
            internal int Calls;
            bool _hold;
            ProviderResult _pending;
            internal TaskCompletionSource<bool> Entered = Signal<bool>();
            TaskCompletionSource<ProviderResult> _reply = Signal<ProviderResult>();
            public string Name => "action-memory-offline";
            public bool IsSimulated => true;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                Packet = packet;
                _pending = new ProviderResult { ProviderName = Name, RawText = RawText, Error = Error, Simulated = true };
                Entered.TrySetResult(true);
                // 故意不协作取消，迟到响应的隔离必须由真实 Kernel 保证。
                return _hold ? _reply.Task : Task.FromResult(_pending);
            }

            internal void HoldNext()
            {
                _hold = true;
                Entered = Signal<bool>();
                _reply = Signal<ProviderResult>();
            }

            internal void Release()
            {
                _hold = false;
                _reply.TrySetResult(_pending);
            }
        }

        sealed class OwnerQueue : IDsOwnerThreadDispatcher
        {
            readonly int _thread = Thread.CurrentThread.ManagedThreadId;
            readonly object _gate = new object();
            readonly Queue<Action> _jobs = new Queue<Action>();
            TaskCompletionSource<bool> _next = Signal<bool>();

            internal void Check() => Assert.AreEqual(_thread, Thread.CurrentThread.ManagedThreadId);

            public Task<T> InvokeAsync<T>(Func<T> block)
            {
                var result = Signal<T>();
                lock (_gate)
                {
                    _jobs.Enqueue(() =>
                    {
                        try { result.TrySetResult(block()); }
                        catch (Exception error) { result.TrySetException(error); }
                    });
                    _next.TrySetResult(true);
                }
                return result.Task;
            }

            internal T Pump<T>(Task<T> task)
            {
                Check();
                while (!task.IsCompleted)
                {
                    Task next;
                    lock (_gate)
                        next = _jobs.Count > 0 ? Task.CompletedTask : _next.Task;
                    Task.WhenAny(task, next).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    if (task.IsCompleted || !next.IsCompleted)
                        continue;
                    Action job;
                    lock (_gate)
                    {
                        job = _jobs.Dequeue();
                        if (_jobs.Count == 0)
                            _next = Signal<bool>();
                    }
                    job();
                }
                return task.GetAwaiter().GetResult();
            }
        }

        /// <summary>只转发真实 Adapter，额外暴露 Ready 边界与 Kernel 内置回放入口。</summary>
        sealed class RecordingKernel : IPieGameActionKernel
        {
            readonly ActionKernelAdapter _adapter;
            readonly OwnerQueue _owner;
            internal ReplayFrame Replay;
            internal Preparation LastPreparation;
            internal TaskCompletionSource<Preparation> Prepared = Signal<Preparation>();

            internal RecordingKernel(ActionKernelAdapter adapter, OwnerQueue owner)
            {
                _adapter = adapter;
                _owner = owner;
            }

            public void Register(string kind, ITaskHandler handler)
            {
                _owner.Check();
                _adapter.Register(kind, handler);
            }

            public Request Begin(string id, string kind, FrozenPayload payload)
            {
                _owner.Check();
                Prepared = Signal<Preparation>();
                return _adapter.Begin(id, kind, payload);
            }

            public async Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken ct = default)
            {
                _owner.Check();
                var prepared = Replay == null
                    ? await _adapter.PrepareAsync(request, provider, ct).ConfigureAwait(false)
                    : await _adapter.PrepareReplayAsync(request, Replay, Replay.Digest, ct).ConfigureAwait(false);
                LastPreparation = prepared;
                Prepared.TrySetResult(prepared);
                return prepared;
            }

            public Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken ct = default)
            {
                _owner.Check();
                return _adapter.Commit(preparation, write, ct);
            }
        }

        /// <summary>回放不经过 IAiProvider；仅补同一 raw 的离线评估旁路，写能力仍由 Session 签发。</summary>
        sealed class ReplayTransaction : IDsActionTransaction
        {
            readonly KernelActionTransaction _inner;
            readonly DsWorldSnapshot _before;
            readonly ReplayFrame _frame;
            IDsActionPreparation _original, _projection;

            internal ReplayTransaction(KernelActionTransaction inner, DsWorldSnapshot before, ReplayFrame frame)
            {
                _inner = inner;
                _before = before;
                _frame = frame;
            }

            public IDsActionRequest Begin(string id, PromptPacket packet) => _inner.Begin(id, packet);

            public async Task<IDsActionPreparation> PrepareAsync(IDsActionRequest request, IAiProvider provider,
                CancellationToken ct = default)
            {
                _original = await _inner.PrepareAsync(request, provider, ct).ConfigureAwait(false);
                if (_original.Code != "ready")
                    return _original;
                var plan = new ActionTaskHandler().Evaluate(_before.Json, _frame.Raw);
                var result = new ProviderResult { ProviderName = "kernel-recorded-offline", RawText = _frame.Raw, Simulated = true };
                return _projection = new DsActionPreparation(request, "ready", null, plan.EvaluationSnapshot, result);
            }

            public DsActionCommit Commit(IDsActionPreparation prepared, DsActionWrite write, CancellationToken ct = default) =>
                _inner.Commit(ReferenceEquals(prepared, _projection) ? _original : prepared, write, ct);
        }

        sealed class Harness : IDisposable
        {
            internal readonly OwnerQueue Owner = new OwnerQueue();
            internal readonly Source Source = new Source();
            internal readonly DragonsleepSession Session;
            internal readonly RecordingKernel Kernel;
            internal readonly KernelActionTransaction Transaction;
            internal IDsActionTransaction Actions;
            readonly List<Task<AiTrace>> _runs = new List<Task<AiTrace>>();

            internal Harness(DsWorld world = null)
            {
                Session = new DragonsleepSession(Source, 7, ownerDispatcher: Owner);
                Session.RestoreState(DsWorldSnapshot.Capture(world ?? DsContent.NewWorld(7), 0, 64));
                var adapter = new ActionKernelAdapter(new DsKernelStatePort(() =>
                {
                    Owner.Check();
                    return Session.CaptureState();
                }));
                Kernel = new RecordingKernel(adapter, Owner);
                Actions = Transaction = new KernelActionTransaction(Kernel);
                Session.StateReplaced += () => adapter.Reset(Session.CaptureState().Json);
                Session.StageChanged += (_, _) => Owner.Check();
                Session.TraceCompleted += _ => Owner.Check();
            }

            internal Task<AiTrace> Act(string raw = null, string input = Input,
                string perception = DsActionPerception.Current, CancellationToken ct = default)
            {
                Source.RawText = raw ?? Raw("observe");
                var task = Session.ActAsync(input, ct, Actions, DsActionPrompt.Current, perception);
                _runs.Add(task);
                return task;
            }

            internal AiTrace Run(string raw = null, string input = Input,
                string perception = DsActionPerception.Current, CancellationToken ct = default) =>
                Owner.Pump(Act(raw, input, perception, ct));

            internal PromptPacket Read() => Run(Raw("unsupported")).Prompt;

            public void Dispose()
            {
                Source.Release();
                foreach (var task in _runs)
                {
                    try { Owner.Pump(task); }
                    catch (Exception) { /* 仅收尾已发起请求，不覆盖主体断言。 */ }
                }
            }
        }
    }
}
