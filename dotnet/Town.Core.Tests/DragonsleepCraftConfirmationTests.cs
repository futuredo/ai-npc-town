using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepCraftConfirmationTests
    {
        const string Lamp = "用蓝晶石织一盏灯";
        static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        // 不自造合法 JSON：使用真实离线规则提供方，工坊默认蓝晶体足以织灯。
        // 固定骰明确隔离随机性；此处不调用 OpenAI，也不读取环境或预算账本。
        sealed class ScriptedProvider : IAiProvider
        {
            readonly RuleProvider rules = new RuleProvider();
            public int Calls;
            public Action BeforeReply;
            public string Name => rules.Name;
            public bool IsSimulated => true;

            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Calls++;
                var result = await rules.CompleteAsync(packet, ct).ConfigureAwait(false);
                BeforeReply?.Invoke();
                return result;
            }
        }

        sealed class CompletionLog
        {
            public readonly List<AiTrace> Traces = new List<AiTrace>();
            public readonly List<string> Records = new List<string>();
            public readonly List<string> Worlds = new List<string>();
            public readonly List<bool> Pending = new List<bool>();

            public CompletionLog(DragonsleepSession session)
            {
                session.TraceCompleted += trace =>
                {
                    Traces.Add(trace);
                    // 必须在通知当刻冻结，不能事后读取同一可变 trace 冒充两次快照。
                    Records.Add(StripMillis(trace));
                    Worlds.Add(session.CaptureState().Json);
                    Pending.Add(session.HasPendingCraft);
                };
            }
        }

        static T Finish<T>(Task<T> task) => task.WaitAsync(Guard).GetAwaiter().GetResult();

        static DragonsleepSession Session(IAiProvider provider)
        {
            var session = new DragonsleepSession(provider, 7, new DsFixedRoll(0.9999),
                requireBoundCraftConfirmation: true);
            session.World.PlayerPlace = "workshop";
            return session;
        }

        static DsCraftProposal Proposal(DragonsleepSession session, AiTrace trace)
        {
            Assert.AreEqual("pending", trace.CraftDecision);
            Assert.AreEqual("awaiting_confirmation", trace.Receipt?.Status);
            AssertNoEffects(trace);
            Assert.IsTrue(session.HasPendingCraft);
            var proposal = session.GetCraftProposal(trace);
            Assert.IsNotNull(proposal);
            Assert.IsNotEmpty(proposal.Description);
            Assert.AreEqual(session.PendingCraftDescription, proposal.Description);
            return proposal;
        }

        static string StripMillis(AiTrace trace)
        {
            var record = trace.ToRecord();
            record.Remove("total_ms");
            foreach (var stage in ((IEnumerable<object>)record["stages"]).Cast<Dictionary<string, object>>())
                stage.Remove("ms");
            return MiniJson.Write(record);
        }

        static string Receipt(AiTrace trace) => MiniJson.Write(trace.Receipt?.ToRecord());

        static void AssertNoEffects(AiTrace trace)
        {
            Assert.IsNotNull(trace.Receipt);
            Assert.IsFalse(trace.Receipt.Applied);
            Assert.AreEqual(0, trace.Receipt.TimeCost);
            Assert.IsEmpty(trace.Receipt.Effects);
            Assert.IsEmpty(trace.Changes);
        }

        static void AssertIgnored(DragonsleepSession session, CompletionLog log,
            DsCraftProposal proposal, bool accept)
        {
            string world = session.CaptureState().Json;
            string[] traces = session.JsonlLines().ToArray();
            int notices = log.Traces.Count;
            bool pending = session.HasPendingCraft;
            string description = session.PendingCraftDescription;

            Assert.IsNull(session.ConfirmCraft(proposal, accept));

            Assert.AreEqual(world, session.CaptureState().Json);
            CollectionAssert.AreEqual(traces, session.JsonlLines(), "无效句柄连旧 trace 也不能改写");
            Assert.AreEqual(notices, log.Traces.Count, "无效句柄不得产生完成通知");
            Assert.AreEqual(pending, session.HasPendingCraft);
            Assert.AreEqual(description, session.PendingCraftDescription);
        }

        static T ShallowClone<T>(T value) where T : class =>
            (T)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(value, null);

        [TestCase("CraftAsync")]
        [TestCase("RunAsync")]
        [TestCase("RunWithProviderAsync")]
        public void 待确认图样阻止三个共用入口覆盖_拒绝发生在提供方之前(string entry)
        {
            var provider = new ScriptedProvider();
            var overridden = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var before = session.CaptureState();
            var first = Finish(session.CraftAsync(Lamp));
            var handle = Proposal(session, first);
            string firstRecord = MiniJson.Write(first.ToRecord());

            AiTrace rejected;
            switch (entry)
            {
                case "CraftAsync": rejected = Finish(session.CraftAsync(Lamp)); break;
                case "RunAsync": rejected = Finish(session.RunAsync(AiTaskKind.Craft, "player", Lamp)); break;
                default:
                    rejected = Finish(session.RunWithProviderAsync(AiTaskKind.Craft, "player", Lamp, overridden));
                    break;
            }

            Assert.AreEqual("precondition", rejected.FailureCode);
            Assert.AreEqual(StageStatus.Fail, rejected.Stage(AiStage.Context).Status);
            Assert.AreEqual(StageStatus.Skipped, rejected.Stage(AiStage.Provider).Status);
            Assert.IsNull(rejected.Provider);
            Assert.IsNull(rejected.Prompt);
            Assert.IsNull(rejected.Receipt);
            Assert.IsNull(session.GetCraftProposal(rejected));
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(0, overridden.Calls);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.AreEqual(firstRecord, MiniJson.Write(first.ToRecord()));
            Assert.AreSame(handle, session.GetCraftProposal(first));
            Assert.AreEqual(2, log.Traces.Count, "第二次请求只完成自己的前置拒绝 trace");

            Assert.AreSame(first, session.ConfirmCraft(handle, true));
            Assert.AreEqual("committed", first.Receipt.Status, "拒绝新请求不能使旧图样失效");
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(0, overridden.Calls);
            Assert.AreEqual(2, session.Traces.Count, "确认不新建 trace");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void A收起后新建B_迟到的A确认或放弃都不消耗B且不通知(bool lateAccept)
        {
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var first = Finish(session.CraftAsync(Lamp));
            var oldHandle = Proposal(session, first);
            Assert.AreSame(first, session.ConfirmCraft(oldHandle, false));
            var second = Finish(session.CraftAsync(Lamp));
            var current = Proposal(session, second);

            Assert.AreNotSame(oldHandle, current);
            Assert.AreEqual(oldHandle.Description, current.Description, "相同图样内容也必须签发新实例");
            Assert.AreNotEqual(first.RequestId, second.RequestId);
            Assert.IsNull(session.GetCraftProposal(first));
            AssertIgnored(session, log, oldHandle, lateAccept);
            Assert.AreSame(current, session.GetCraftProposal(second));
            Assert.AreEqual(2, provider.Calls);

            Assert.AreSame(second, session.ConfirmCraft(current, true));
            Assert.AreEqual("committed", second.Receipt.Status);
            Assert.AreEqual("consumed", session.World.Items["crystal_blue"].Holder);
            Assert.AreEqual(1, session.World.Items.Values.Count(item => item.Crafted));
            CollectionAssert.AreEqual(new[] { first, first, second, second }, log.Traces);
            Assert.AreEqual(2, provider.Calls);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void 句柄不可公开构造且描述只读_空跨会话与同内容伪造无效_有效决定只领取一次(bool accept)
        {
            var type = typeof(DsCraftProposal);
            Assert.IsTrue(type.IsSealed);
            Assert.IsEmpty(type.GetConstructors(), "不能通过公开构造器签发图样");
            Assert.IsEmpty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
            CollectionAssert.AreEqual(new[] { "Description" },
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name));
            Assert.IsNull(type.GetProperty("Description").GetSetMethod(true));

            var provider = new ScriptedProvider();
            var session = Session(provider);
            var foreignProvider = new ScriptedProvider();
            var foreign = Session(foreignProvider);
            var log = new CompletionLog(session);
            var trace = Finish(session.CraftAsync(Lamp));
            var handle = Proposal(session, trace);
            var otherTrace = Finish(foreign.CraftAsync(Lamp));
            var otherHandle = Proposal(foreign, otherTrace);
            // 不依赖内部构造器：浅克隆只用于模拟字段逐字相同、但并非会话签发实例的攻击输入。
            var forged = ShallowClone(handle);

            Assert.AreEqual(trace.RequestId, otherTrace.RequestId);
            Assert.AreEqual(trace.Provider.RawText, otherTrace.Provider.RawText);
            Assert.AreEqual(handle.Description, otherHandle.Description);
            Assert.AreEqual(handle.Description, forged.Description);
            Assert.AreNotSame(handle, forged);
            Assert.IsNull(session.GetCraftProposal(null));
            Assert.IsNull(session.GetCraftProposal(otherTrace));
            Assert.IsNull(session.GetCraftProposal(ShallowClone(trace)), "逐字段相同的 trace 不能冒领句柄");

            foreach (var invalid in new DsCraftProposal[] { null, otherHandle, forged })
            {
                AssertIgnored(session, log, invalid, accept);
                Assert.AreSame(handle, session.GetCraftProposal(trace));
            }

            int terminalNotices = 0;
            session.TraceCompleted += completed =>
            {
                if (completed.CraftDecision == "pending") return;
                terminalNotices++;
                Assert.IsFalse(session.HasPendingCraft, "终态通知之前必须先领取并清槽");
                Assert.IsNull(session.GetCraftProposal(completed));
                Assert.IsNull(session.ConfirmCraft(handle, !accept), "通知重入不能再次领取");
            };
            Assert.AreSame(trace, session.ConfirmCraft(handle, accept));
            Assert.AreEqual(accept ? "confirmed" : "declined", trace.CraftDecision);
            Assert.AreEqual(1, terminalNotices);
            AssertIgnored(session, log, handle, true);
            AssertIgnored(session, log, handle, false);
            Assert.AreEqual(2, log.Traces.Count);
            Assert.AreSame(trace, log.Traces[0]);
            Assert.AreSame(trace, log.Traces[1]);
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(1, foreignProvider.Calls);
            Assert.AreSame(otherHandle, foreign.GetCraftProposal(otherTrace));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void 严格模式拒绝裸布尔但不消费候选_原绑定句柄仍能决定(bool accept)
        {
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var trace = Finish(session.CraftAsync(Lamp));
            var handle = Proposal(session, trace);
            string world = session.CaptureState().Json;
            string record = MiniJson.Write(trace.ToRecord());

            Assert.IsNull(session.ConfirmCraft(accept));
            Assert.IsNull(session.ConfirmCraft(!accept));
            Assert.AreSame(handle, session.GetCraftProposal(trace));
            Assert.AreEqual(world, session.CaptureState().Json);
            Assert.AreEqual(record, MiniJson.Write(trace.ToRecord()));
            Assert.AreEqual(1, log.Traces.Count);

            Assert.AreSame(trace, session.ConfirmCraft(handle, accept));
            Assert.AreEqual(accept ? "committed" : "declined", trace.Receipt.Status);
            Assert.AreEqual(accept ? 2 : 0, trace.Receipt.TimeCost);
            Assert.AreEqual(2, log.Traces.Count);
            Assert.AreEqual(1, provider.Calls);
        }

        [TestCase(false, true)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(true, false)]
        public void 提交阶段取消不入待槽_准备完成后取消使确认和放弃均无权提交(bool cancelAtPrepareBoundary, bool accept)
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var before = session.CaptureState();
            if (cancelAtPrepareBoundary)
                session.StageChanged += (current, stage) =>
                {
                    // Commit 的开始通知在 Prepare 之后、保存 pending 之前，不借助线程竞争。
                    if (stage == AiStage.Commit && current.Stage(stage).Status == StageStatus.Pending)
                        cancellation.Cancel();
                };

            var trace = Finish(session.CraftAsync(Lamp, cancellation.Token));
            if (cancelAtPrepareBoundary)
            {
                Assert.AreEqual("cancelled", trace.FailureCode);
                Assert.IsNull(trace.CraftDecision, "保存 pending 之前已取消，不产生可领取的图样");
                Assert.IsNull(trace.Receipt);
                Assert.IsNull(session.GetCraftProposal(trace));
                Assert.IsFalse(session.HasPendingCraft);
                Assert.IsNull(session.ConfirmCraft(accept));
                Assert.AreEqual(before.Json, session.CaptureState().Json);
                Assert.AreEqual(1, log.Traces.Count);
                Assert.AreEqual(1, provider.Calls);
                return;
            }
            var handle = Proposal(session, trace);
            string description = handle.Description;
            string pendingRecord = MiniJson.Write(trace.ToRecord());
            if (!cancelAtPrepareBoundary) cancellation.Cancel();

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(session.HasPendingCraft, "取消本身不在后台领取或改写候选");
            Assert.AreEqual(1, log.Traces.Count);
            Assert.AreEqual(pendingRecord, MiniJson.Write(trace.ToRecord()));
            Assert.AreEqual(before.Json, session.CaptureState().Json);

            Assert.AreSame(trace, session.ConfirmCraft(handle, accept));
            Assert.AreEqual("cancelled", trace.CraftDecision, "取消不能被标为玩家 declined");
            Assert.AreEqual("cancelled", trace.FailureCode);
            Assert.AreEqual("cancelled", trace.Receipt.Status);
            Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Commit).Status);
            Assert.AreEqual(VerdictKind.Rejected, trace.Verdict.Kind);
            AssertNoEffects(trace);
            Assert.AreEqual(before.Hash, trace.Receipt.BeforeHash);
            Assert.AreEqual(before.Hash, trace.Receipt.AfterHash);
            Assert.AreEqual(before.Json, session.CaptureState().Json, "没有进入实际世界提交、扣料或耗时路径");
            Assert.IsFalse(session.HasPendingCraft);
            Assert.IsNull(session.GetCraftProposal(trace));
            Assert.AreEqual(description, handle.Description);
            CollectionAssert.AreEqual(new[] { true, false }, log.Pending);
            Assert.AreEqual(2, log.Traces.Count);
            AssertIgnored(session, log, handle, !accept);
            Assert.AreEqual(1, provider.Calls);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void 请求前或提供方返回时取消_不生成可领取图样且世界无变化(bool cancelBeforeRequest)
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var before = session.CaptureState();
            if (cancelBeforeRequest)
            {
                cancellation.Cancel();
                Assert.Catch<OperationCanceledException>(() => Finish(session.CraftAsync(Lamp, cancellation.Token)));
                Assert.IsEmpty(session.Traces);
                Assert.IsEmpty(log.Traces);
                Assert.AreEqual(0, provider.Calls);
            }
            else
            {
                provider.BeforeReply = cancellation.Cancel;
                var trace = Finish(session.CraftAsync(Lamp, cancellation.Token));
                Assert.AreEqual("cancelled", trace.FailureCode);
                Assert.IsNull(trace.CraftDecision);
                Assert.IsNull(trace.Receipt);
                Assert.IsNull(session.GetCraftProposal(trace));
                Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Validate).Status);
                Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Commit).Status);
                Assert.IsEmpty(trace.Changes);
                Assert.AreEqual(1, log.Traces.Count);
                Assert.AreEqual(1, provider.Calls);
            }
            Assert.IsFalse(session.HasPendingCraft);
            Assert.IsFalse(session.Busy);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
        }

        [Test]
        public void 同步提交完成后令牌才取消_已确认材料时间与回执不回滚()
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var before = session.CaptureState();
            var trace = Finish(session.CraftAsync(Lamp, cancellation.Token));
            var handle = Proposal(session, trace);
            string committedWorld = null;
            session.TraceCompleted += completed =>
            {
                if (completed.CraftDecision != "confirmed") return;
                Assert.IsTrue(completed.Receipt.Applied);
                Assert.IsFalse(session.HasPendingCraft);
                committedWorld = session.CaptureState().Json;
                cancellation.Cancel();
            };

            Assert.IsFalse(cancellation.IsCancellationRequested);
            Assert.AreSame(trace, session.ConfirmCraft(handle, true));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual("confirmed", trace.CraftDecision);
            Assert.AreEqual("committed", trace.Receipt.Status);
            Assert.IsNull(trace.FailureCode);
            Assert.IsTrue(trace.Receipt.Applied);
            Assert.AreEqual(2, trace.Receipt.TimeCost);
            Assert.IsNotEmpty(trace.Receipt.Effects);
            Assert.AreEqual("consumed", session.World.Items["crystal_blue"].Holder);
            Assert.AreEqual(1, session.World.Items.Values.Count(item => item.Crafted));
            Assert.AreEqual(before.Spent + 2, session.CaptureState().Spent);
            Assert.AreEqual(committedWorld, session.CaptureState().Json);
            Assert.AreNotEqual(before.Hash, session.CaptureState().Hash);
            AssertIgnored(session, log, handle, false);
            Assert.AreEqual(2, log.Traces.Count);
            Assert.AreEqual(1, provider.Calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 恢复或重启清除候选_恢复同编号同输出也不能复用旧句柄(bool restartFirst)
        {
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var before = session.CaptureState();
            var first = Finish(session.CraftAsync(Lamp));
            var oldHandle = Proposal(session, first);
            string firstRecord = MiniJson.Write(first.ToRecord());

            if (restartFirst) session.Restart();
            else session.RestoreState(before);

            Assert.IsFalse(session.HasPendingCraft);
            Assert.IsEmpty(session.PendingCraftDescription);
            Assert.IsEmpty(session.Traces);
            Assert.IsNull(session.GetCraftProposal(first));
            AssertIgnored(session, log, oldHandle, true);
            AssertIgnored(session, log, oldHandle, false);
            Assert.AreEqual(1, log.Traces.Count, "重启或恢复不为已清槽图样补发终态通知");

            // Restart 自身改变 SessionGen；再恢复原快照，主动制造编号、世界和输出全部相同的 ABA。
            if (restartFirst) session.RestoreState(before);
            var second = Finish(session.CraftAsync(Lamp));
            var current = Proposal(session, second);
            Assert.AreEqual(first.RequestId, second.RequestId);
            Assert.AreEqual(first.Provider.RawText, second.Provider.RawText);
            Assert.AreEqual(oldHandle.Description, current.Description);
            Assert.AreEqual(StripMillis(first), StripMillis(second));
            Assert.AreNotSame(oldHandle, current);
            Assert.IsNull(session.GetCraftProposal(first));
            AssertIgnored(session, log, oldHandle, true);
            AssertIgnored(session, log, oldHandle, false);
            Assert.AreSame(current, session.GetCraftProposal(second));
            Assert.AreEqual(firstRecord, MiniJson.Write(first.ToRecord()));

            Assert.AreSame(second, session.ConfirmCraft(current, true));
            Assert.AreEqual("committed", second.Receipt.Status);
            Assert.AreEqual(2, provider.Calls);
            Assert.AreEqual(3, log.Traces.Count);
        }

        [TestCase("material")]
        [TestCase("place")]
        [TestCase("budget")]
        [TestCase("unrelated")]
        public void 不增加Version的状态变化仍以完整哈希拒绝旧图样_且保留外部变化(string change)
        {
            var provider = new ScriptedProvider();
            var session = Session(provider);
            var log = new CompletionLog(session);
            var trace = Finish(session.CraftAsync(Lamp));
            var handle = Proposal(session, trace);
            var prepared = session.CaptureState();
            int version = session.World.Version;
            string description = handle.Description;
            switch (change)
            {
                case "material": session.World.Items["crystal_blue"].Holder = "consumed"; break;
                case "place": session.World.PlayerPlace = "market"; break;
                case "budget": session.PhaseBudget++; break;
                default: session.World.Coins--; break;
            }
            var changed = session.CaptureState();
            Assert.AreEqual(version, session.World.Version);
            Assert.AreNotEqual(prepared.Hash, changed.Hash);
            Assert.AreEqual(description, handle.Description, "描述必须是冻结值而非实时世界视图");

            Assert.AreSame(trace, session.ConfirmCraft(handle, true));

            Assert.AreEqual("confirmed", trace.CraftDecision);
            Assert.AreEqual("stale_state", trace.FailureCode);
            Assert.AreEqual("stale_state", trace.Receipt.Status);
            Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Commit).Status);
            AssertNoEffects(trace);
            Assert.AreEqual(prepared.Hash, trace.Receipt.BeforeHash);
            Assert.AreEqual(changed.Hash, trace.Receipt.AfterHash);
            Assert.AreEqual(changed.Json, session.CaptureState().Json);
            Assert.AreEqual(version, session.World.Version);
            Assert.IsFalse(session.HasPendingCraft);
            AssertIgnored(session, log, handle, false);
            Assert.AreEqual(2, log.Traces.Count);
            Assert.AreEqual(1, provider.Calls);
        }

        [TestCase("pending")]
        [TestCase("confirmed")]
        [TestCase("declined")]
        public void 默认裸布尔兼容绑定入口_世界回执轨迹逐字一致且三态严格回放仍通过(string decision)
        {
            var legacyProvider = new ScriptedProvider();
            var boundProvider = new ScriptedProvider();
            // 故意省略末尾开关：默认 headless bool 兼容是被测合同，不由辅助方法指定 false。
            var legacy = new DragonsleepSession(legacyProvider, 7, new DsFixedRoll(0.9999));
            legacy.World.PlayerPlace = "workshop";
            var bound = Session(boundProvider);
            var legacyLog = new CompletionLog(legacy);
            var boundLog = new CompletionLog(bound);
            var before = legacy.CaptureState();
            Assert.AreEqual(before.Json, bound.CaptureState().Json);
            var oldTrace = Finish(legacy.CraftAsync(Lamp));
            var newTrace = Finish(bound.CraftAsync(Lamp));
            var oldHandle = Proposal(legacy, oldTrace);
            var newHandle = Proposal(bound, newTrace);
            Assert.AreEqual(oldHandle.Description, newHandle.Description);
            Assert.AreEqual(before.Json, legacy.CaptureState().Json);
            Assert.AreEqual(before.Json, bound.CaptureState().Json);
            Assert.AreEqual(StripMillis(oldTrace), StripMillis(newTrace));
            Assert.AreEqual(Receipt(oldTrace), Receipt(newTrace));

            if (decision != "pending")
            {
                bool accept = decision == "confirmed";
                Assert.AreSame(oldTrace, legacy.ConfirmCraft(accept));
                Assert.AreSame(newTrace, bound.ConfirmCraft(newHandle, accept));
                Assert.AreEqual(accept ? "committed" : "declined", newTrace.Receipt.Status);
                Assert.AreEqual(accept, newTrace.Receipt.Applied);
                Assert.AreEqual(accept ? 2 : 0, newTrace.Receipt.TimeCost);
                if (!accept) AssertNoEffects(newTrace);
            }

            Assert.AreEqual(decision, oldTrace.CraftDecision);
            Assert.AreEqual(decision, newTrace.CraftDecision);
            Assert.AreEqual(legacy.CaptureState().Json, bound.CaptureState().Json);
            Assert.AreEqual(Receipt(oldTrace), Receipt(newTrace), "回执不排除任何字段");
            Assert.AreEqual(StripMillis(oldTrace), StripMillis(newTrace), "仅剔除 total_ms 与阶段 ms");
            CollectionAssert.AreEqual(legacyLog.Records, boundLog.Records);
            CollectionAssert.AreEqual(legacyLog.Worlds, boundLog.Worlds);
            CollectionAssert.AreEqual(legacyLog.Pending, boundLog.Pending);
            int expectedNotices = decision == "pending" ? 1 : 2;
            Assert.AreEqual(expectedNotices, legacyLog.Traces.Count);
            Assert.AreEqual(expectedNotices, boundLog.Traces.Count);
            CollectionAssert.AreEqual(decision == "pending" ? new[] { true } : new[] { true, false }, boundLog.Pending);
            Assert.AreEqual("pending", ((Dictionary<string, object>)MiniJson.Parse(boundLog.Records[0])).Str("craft_decision"));
            Assert.AreEqual(decision, ((Dictionary<string, object>)MiniJson.Parse(boundLog.Records.Last())).Str("craft_decision"));
            Assert.IsTrue(legacyLog.Traces.All(trace => ReferenceEquals(trace, oldTrace)));
            Assert.IsTrue(boundLog.Traces.All(trace => ReferenceEquals(trace, newTrace)));
            Assert.AreEqual(1, legacy.Traces.Count);
            Assert.AreEqual(1, bound.Traces.Count);
            Assert.AreEqual(1, legacyProvider.Calls);
            Assert.AreEqual(1, boundProvider.Calls);
            if (decision != "confirmed")
                Assert.AreEqual(before.Json, bound.CaptureState().Json, "待确认或放弃不能留下实际世界变化");

            var oldFrame = DsReplayFrame.Capture(before, legacy.CaptureState(), oldTrace);
            var newFrame = DsReplayFrame.Capture(before, bound.CaptureState(), newTrace);
            Assert.AreEqual(MiniJson.Write(oldFrame.ToRecord()), MiniJson.Write(newFrame.ToRecord()));
            var roundtrip = DsReplayFrame.Parse(MiniJson.Write(newFrame.ToRecord()));
            Assert.AreEqual(decision, roundtrip.CraftDecision);
            Assert.IsTrue(Finish(roundtrip.VerifyAsync()), "严格回放必须保持提示、世界和呈现一致：" + decision);
        }
    }
}
