using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>真实 Session + PlanRun；仅提供方和所有者队列可控，全部离线且不借助 NUnit 上下文。</summary>
    public sealed class DragonsleepPlanOwnerDispatchTests
    {
        static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
        const string Goal = "逐步离线验证";

        static TaskCompletionSource<T> Signal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        static T Finish<T>(Task<T> task) => task.WaitAsync(Guard).GetAwaiter().GetResult();
        static string Record(DsPlanRun run) => MiniJson.Write(run.ToRecord());
        static string Receipt(AiTrace trace) => MiniJson.Write(trace.Receipt?.ToRecord());

        static Task<T> Worker<T>(Func<Task<T>> call)
        {
            int owner = Thread.CurrentThread.ManagedThreadId;
            var issued = Signal<Task<T>>();
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    Assert.AreNotEqual(owner, Thread.CurrentThread.ManagedThreadId);
                    issued.SetResult(call());
                }
                catch (Exception error) { issued.SetException(error); }
            }) { IsBackground = true };
            thread.Start();
            Assert.IsTrue(thread.Join(Guard), "worker 只发起调用，不能等待所有者或泵送 NUnit 上下文");
            return Finish(issued.Task);
        }

        static void OnWorker(Action action) => Finish(Worker(() =>
        {
            action();
            return Task.FromResult(true);
        }));

        sealed class OwnerClosedFault : InvalidOperationException { }

        sealed class OwnerQueue : IDsOwnerThreadDispatcher
        {
            readonly object sync = new object();
            readonly Queue<(Action Run, Action Reject)> jobs = new Queue<(Action, Action)>();
            readonly int owner = Thread.CurrentThread.ManagedThreadId;
            TaskCompletionSource<bool> available = Signal<bool>();
            bool closed, cancelOnClose;

            public int Count { get { lock (sync) return jobs.Count; } }
            public int Pumped { get; private set; }
            // 仅测试使用：同步块已完成、结果尚未唤醒 worker，可确定性地在 Session 取得 gate 前换世界。
            public Action BeforeReply { get; set; }
            public bool ThrowAfterClose { get; set; }
            public Task<bool> Next
            {
                get { lock (sync) return jobs.Count > 0 ? Task.FromResult(true) : available.Task; }
            }

            public Task<T> InvokeAsync<T>(Func<T> synchronousBlock)
            {
                var completion = Signal<T>();
                Action reject = () =>
                {
                    if (cancelOnClose) completion.TrySetCanceled();
                    else completion.TrySetException(new OwnerClosedFault());
                };
                lock (sync)
                {
                    if (closed && ThrowAfterClose) throw new ObjectDisposedException(nameof(OwnerQueue));
                    if (closed) reject();
                    else
                    {
                        jobs.Enqueue((() =>
                        {
                            try
                            {
                                var result = synchronousBlock();
                                BeforeReply?.Invoke();
                                completion.TrySetResult(result);
                            }
                            catch (Exception error) { completion.TrySetException(error); }
                        }, reject));
                        available.TrySetResult(true);
                    }
                }
                return completion.Task;
            }

            public void One()
            {
                Assert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId, "只有测试所有者可 pump");
                Finish(Next);
                (Action Run, Action Reject) job;
                lock (sync)
                {
                    job = jobs.Dequeue();
                    if (jobs.Count == 0) available = Signal<bool>();
                }
                Pumped++;
                job.Run();
            }

            public T Pump<T>(Task<T> task)
            {
                for (int bound = 0; !task.IsCompleted; bound++)
                {
                    Assert.Less(bound, 512, "有限场景不应无限派发");
                    var queued = Next;
                    if (Finish(Task.WhenAny(task, queued)) == queued) One();
                }
                return Finish(task);
            }

            public void Until(Func<bool> reached)
            {
                for (int bound = 0; !reached(); bound++)
                {
                    Assert.Less(bound, 512, "观测边界必须在有限泵送内出现");
                    One();
                }
            }

            public void Close(bool cancel = false)
            {
                lock (sync)
                {
                    closed = true;
                    cancelOnClose = cancel;
                    foreach (var job in jobs) job.Reject();
                    jobs.Clear();
                }
            }
        }

        sealed class Call
        {
            public PromptPacket Packet;
            public string Raw;
            public readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();

            public void Answer() => OnWorker(() => Reply.SetResult(Response(Packet, Raw)));
        }

        static ProviderResult Response(PromptPacket packet, string raw) => new ProviderResult
        {
            ProviderName = "plan-owner-dispatch-offline", RawText = raw, Simulated = true,
            InputTokens = packet.TotalChars / 2, OutputTokens = raw.Length / 2, EstimatedUsd = 0m,
        };

        sealed class Provider : IAiProvider
        {
            readonly Func<PromptPacket, int, string> script;
            readonly bool delayed;
            readonly object sync = new object();
            readonly List<Call> calls = new List<Call>();
            bool stopped;
            int count;
            public readonly TaskCompletionSource<Call>[] Entered = Enumerable.Range(0, 16).Select(_ => Signal<Call>()).ToArray();
            public int Calls => Volatile.Read(ref count);
            public string Name => "plan-owner-dispatch-offline";
            public bool IsSimulated => true;

            public Provider(Func<PromptPacket, int, string> script, bool delayed = true)
            { this.script = script; this.delayed = delayed; }

            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                int index = Interlocked.Increment(ref count) - 1;
                var call = new Call { Packet = packet, Raw = script(packet, index) };
                if (!delayed) return Response(packet, call.Raw);
                using (ct.Register(() => call.Reply.TrySetCanceled(ct)))
                {
                    lock (sync)
                    {
                        calls.Add(call);
                        if (stopped) call.Reply.TrySetCanceled();
                    }
                    Entered[index].SetResult(call);
                    return await call.Reply.Task.ConfigureAwait(false);
                }
            }

            public void Stop()
            {
                lock (sync)
                {
                    stopped = true;
                    foreach (var call in calls) call.Reply.TrySetCanceled();
                }
            }
        }

        static List<string> Watch(DragonsleepSession session, Action check)
        {
            var events = new List<string>();
            session.StageChanged += (trace, stage) =>
            {
                check();
                events.Add(trace.RequestId + ":" + stage + ":" + trace.Stage(stage).Status);
            };
            session.TraceCompleted += trace => { check(); events.Add("done:" + trace.RequestId); };
            session.RuleEventCompleted += rule => { check(); events.Add("rule:" + rule.RequestId); };
            return events;
        }

        sealed class Harness : IDisposable
        {
            readonly Func<PromptPacket, int, string> script;
            readonly Action<DsWorld> setup;
            readonly double roll;
            readonly DsPlanConstraints constraints;
            readonly List<Task> started = new List<Task>();
            public readonly int OwnerThread = Thread.CurrentThread.ManagedThreadId;
            public readonly OwnerQueue Owner = new OwnerQueue();
            public readonly Provider Provider;
            public readonly DragonsleepSession Session;
            public readonly DsPlanRun Run;
            public readonly List<string> Events;

            public Harness(Func<PromptPacket, int, string> script, Action<DsWorld> setup = null,
                double roll = .9999, DsPlanConstraints constraints = null)
            {
                this.script = script; this.setup = setup; this.roll = roll; this.constraints = constraints;
                Provider = new Provider(script);
                Session = new DragonsleepSession(Provider, 7, new DsFixedRoll(roll), ownerDispatcher: Owner);
                setup?.Invoke(Session.World);
                Run = new DsPlanRun(Session, Goal, constraints: constraints);
                Events = Watch(Session, () => Assert.AreEqual(OwnerThread, Thread.CurrentThread.ManagedThreadId,
                    "StageChanged / TraceCompleted / RuleEventCompleted 必须来自所有者"));
            }

            public Task<T> Start<T>(Func<Task<T>> call)
            {
                var task = Worker(call);
                started.Add(task);
                return task;
            }

            public Call Enter(int index) => Owner.Pump(Provider.Entered[index].Task);

            public AiTrace Propose()
            {
                var task = Start(() => Run.ProposeAsync());
                Enter(0).Answer();
                return Owner.Pump(task);
            }

            public DsPlanRun Reference()
            {
                // 显式省略 dispatcher：同样的真实规则与固定骰，作为原有 null 路径基准。
                var session = new DragonsleepSession(new Provider(script, false), 7, new DsFixedRoll(roll));
                setup?.Invoke(session.World);
                return new DsPlanRun(session, Goal, constraints: constraints);
            }

            public void Dispose()
            {
                Provider.Stop();
                Owner.Close();
                foreach (var task in started)
                {
                    try { task.WaitAsync(Guard).GetAwaiter().GetResult(); }
                    catch (Exception) { /* 保留主体断言，只保证失败测试不遗留等待中的提供方。 */ }
                }
            }
        }

        static DsPlanStep Act(string id, string verb = "observe", string place = "", string target = "", string item = "") =>
            new DsPlanStep { Id = id, Op = "act", Verb = verb, Place = place, Target = target, Item = item };

        static string Plan(params DsPlanStep[] steps)
        {
            var plan = new DsPlan { Goal = Goal, Summary = "固定离线步骤" };
            plan.Steps.AddRange(steps);
            return MiniJson.Write(plan.ToRecord());
        }

        static string Quiet(PromptPacket packet)
        {
            if (packet.Kind == AiTaskKind.Direct) return "{\"event\":\"none\",\"intensity\":0,\"reason\":\"安静一夜\"}";
            var view = (Dictionary<string, object>)MiniJson.Parse(packet.Sections.Single(s => s.Name == "perception").Text);
            return MiniJson.Write(new Dictionary<string, object>
            {
                ["verb"] = "wait", ["place"] = view.Str("where"), ["target"] = "",
                ["share"] = new List<object>(), ["reason"] = "居民留守，避免无关世界变化",
            });
        }

        static void ReadyWhelp(DsWorld world)
        {
            world.PlayerPlace = "docks";
            world.Whelp.Place = "docks";
            world.Whelp.Hidden = false;
            world.Whelp.TrustPlayer = .6f;
            world.Whelp.Fear = .3f;
            world.PlayerKnows.Add("b_pip_whelp_place");
        }

        static void Caged(DsWorld world)
        {
            world.Day = 2; world.Phase = DayPhase.Noon;
            world.Whelp.CapturedBy = "osric"; world.Whelp.Place = "castle";
            world.Whelp.Hidden = false; world.Whelp.TrustPlayer = .6f;
            world.Flags["seen_by_guards"] = "第1天·黄昏";
            world.PlayerKnows.Add("b_pip_whelp_place");
            world.PlayerKnows.Add("law:law_guards_dusk");
        }

        static void Equivalent(DsPlanRun expected, DsPlanRun actual)
        {
            Assert.AreEqual(Record(expected), Record(actual), "完整计划记录的 JSON 字节及键序");
            Assert.AreEqual(expected.NextIndex, actual.NextIndex);
            Assert.AreEqual(expected.LastFailure, actual.LastFailure);
            Assert.AreEqual(expected.LastReason, actual.LastReason);
            Assert.AreEqual(expected.LastReasonCode.Code, actual.LastReasonCode.Code);
            Assert.AreEqual(expected.Session.CaptureState().Json, actual.Session.CaptureState().Json);
            Assert.AreEqual(expected.Session.CaptureState().Hash, actual.Session.CaptureState().Hash);
            CollectionAssert.AreEqual(expected.AcceptedRequestIds, actual.AcceptedRequestIds);
            CollectionAssert.AreEqual(expected.Session.Traces.Select(Receipt), actual.Session.Traces.Select(Receipt));
            CollectionAssert.AreEqual(expected.Session.RuleEvents.Select(e => MiniJson.Write(e.ToRecord())),
                actual.Session.RuleEvents.Select(e => MiniJson.Write(e.ToRecord())));
        }

        [Test]
        public void worker提案冻结和晚回采纳均须泵送_确认与放弃仍由所有者同步调用()
        {
            string plan = Plan(Act("s1"));
            using var h = new Harness((_, __) => plan);
            string world = h.Session.CaptureState().Json, record = Record(h.Run);
            var task = h.Start(() => h.Run.ProposeAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual(1, h.Owner.Count);
            Assert.AreEqual(0, h.Owner.Pumped);
            Assert.IsEmpty(h.Session.Traces);
            Assert.IsEmpty(h.Events);
            Assert.AreEqual(record, Record(h.Run));
            Assert.AreEqual(world, h.Session.CaptureState().Json);

            var call = h.Enter(0);
            Assert.AreEqual(AiTaskKind.Plan, call.Packet.Kind);
            Assert.IsNull(h.Run.LatestPlanTrace);
            call.Answer();
            Finish(h.Owner.Next);
            Assert.IsNull(h.Session.Traces.Single().Provider, "回包尚未经过 Session 的所有者后半段");
            Assert.AreEqual(record, Record(h.Run));

            h.Owner.Until(() => h.Session.Traces.Single().Receipt != null);
            Finish(h.Owner.Next);
            Assert.IsFalse(task.IsCompleted);
            Assert.IsNull(h.Run.LatestPlanTrace, "Session 已结束也不能在 worker 上 Adopt");
            Assert.IsEmpty(h.Run.PlanTraces);
            Assert.AreEqual("planning", h.Run.Status);
            var trace = h.Owner.Pump(task);
            Assert.AreSame(trace, h.Run.LatestPlanTrace);
            Assert.AreEqual("awaiting_confirmation", h.Run.Status);
            Assert.AreEqual(world, h.Session.CaptureState().Json);

            var old = h.Reference();
            Finish(old.ProposeAsync());
            Equivalent(old, h.Run);
            int pumped = h.Owner.Pumped;
            Assert.AreEqual(h.OwnerThread, Thread.CurrentThread.ManagedThreadId);
            h.Run.Accept(); old.Accept();
            Assert.AreEqual(pumped, h.Owner.Pumped, "Accept 不暗自异步派发；调用者负责 owner");
            Assert.AreEqual("running", h.Run.Status);
            Equivalent(old, h.Run);
            h.Run.Abandon(); old.Abandon();
            Assert.AreEqual("abandoned", h.Run.Status);
            Assert.AreEqual(pumped, h.Owner.Pumped);
            Equivalent(old, h.Run);
        }

        [Test]
        public void worker逐步执行的移动行动及结果均在泵送后_一次只提交一步且完整记录等同null路径()
        {
            string plan = Plan(Act("s1", place: "workshop"), new DsPlanStep { Id = "s2", Op = "go", Place = "cathedral" });
            using var h = new Harness((_, __) => plan);
            h.Propose();
            var old = h.Reference(); Finish(old.ProposeAsync());
            var events = Watch(old.Session, () => { });
            h.Run.Accept(); old.Accept();
            int count = h.Events.Count;
            string before = h.Session.CaptureState().Json;
            var task = h.Start(() => h.Run.StepAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual(before, h.Session.CaptureState().Json);
            Assert.AreEqual("market", h.Session.World.PlayerPlace);
            Assert.AreEqual(count, h.Events.Count);
            Assert.IsEmpty(h.Run.Results);
            Assert.AreEqual(0, h.Run.NextIndex);

            h.Owner.Until(() => h.Session.Traces.Count == 2 && h.Session.Traces[1].Receipt != null);
            Finish(h.Owner.Next);
            Assert.AreEqual("workshop", h.Session.World.PlayerPlace);
            Assert.AreEqual(1, h.Session.RuleEvents.Count);
            Assert.AreEqual(2, h.Session.TimeLeftInPhase, "只执行第一步观察");
            Assert.IsEmpty(h.Run.Results, "Session 行动结束不等于 PlanRun 结果已归所有者采纳");
            Assert.AreEqual(0, h.Run.NextIndex);
            Assert.AreEqual("running", h.Run.Status);
            var first = h.Owner.Pump(task);
            var expected = Finish(old.StepAsync());
            Assert.AreEqual(MiniJson.Write(expected.ToRecord()), MiniJson.Write(first.ToRecord()));
            Assert.AreEqual(1, h.Run.NextIndex);
            Assert.AreEqual(1, h.Run.Results.Count);
            Assert.AreEqual("running", h.Run.Status);
            Assert.AreEqual(1, h.Provider.Calls, "结构化行动不再次调用模型提供方");
            Equivalent(old, h.Run);

            var second = h.Start(() => h.Run.StepAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual("workshop", h.Session.World.PlayerPlace);
            Assert.AreEqual(1, h.Run.Results.Count);
            h.Owner.Pump(second); Finish(old.StepAsync());
            Assert.AreEqual("cathedral", h.Session.World.PlayerPlace);
            Assert.AreEqual(2, h.Run.NextIndex);
            Assert.AreEqual("completed", h.Run.Status);
            Equivalent(old, h.Run);
            CollectionAssert.AreEqual(events, h.Events.Skip(count));
        }

        [Test]
        public void wait跨夜逐居民及GM晚回不偷改世界或计划_规则顺序回执与世界hash保持一致()
        {
            string plan = Plan(new DsPlanStep { Id = "s1", Op = "wait", Place = "workshop", Until = "第2天·清晨" }, Act("s2"));
            using var h = new Harness((packet, _) => packet.Kind == AiTaskKind.Plan ? plan : Quiet(packet),
                world => { world.PlayerPlace = "workshop"; world.Phase = DayPhase.Dusk; });
            h.Propose();
            var old = h.Reference(); Finish(old.ProposeAsync());
            var events = Watch(old.Session, () => { });
            h.Run.Accept(); old.Accept();
            int count = h.Events.Count;
            var task = h.Start(() => h.Run.StepAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual(DayPhase.Dusk, h.Session.World.Phase);
            Assert.IsEmpty(h.Run.Results);
            string[] actors = h.Session.World.Actors.Keys.OrderBy(id => id, StringComparer.Ordinal).Concat(new[] { "gm" }).ToArray();
            for (int i = 0; i < actors.Length; i++)
            {
                var call = h.Enter(i + 1);
                Assert.AreEqual(actors[i], call.Packet.ActorId);
                Assert.AreEqual(i == actors.Length - 1 ? AiTaskKind.Direct : AiTaskKind.AgentPlan, call.Packet.Kind);
                Assert.AreEqual(i + 2, h.Provider.Calls, "仅在上一居民回包采纳后才请求下一居民");
                string frozen = h.Session.CaptureState().Json;
                int notices = h.Events.Count;
                call.Answer();
                Finish(h.Owner.Next);
                Assert.AreEqual(frozen, h.Session.CaptureState().Json);
                Assert.AreEqual(notices, h.Events.Count);
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.AreEqual("running", h.Run.Status);
            }

            h.Owner.Until(() => h.Session.World.Day == 2 && h.Session.World.Phase == DayPhase.Dawn);
            Finish(h.Owner.Next);
            Assert.IsEmpty(h.Run.Results, "清晨规则已提交，wait 结果仍须 PlanRun 的所有者后半段");
            Assert.AreEqual(0, h.Run.NextIndex);
            var result = h.Owner.Pump(task);
            var expected = Finish(old.StepAsync());
            Assert.AreEqual("success", result.Status);
            Assert.AreEqual(MiniJson.Write(expected.ToRecord()), MiniJson.Write(result.ToRecord()));
            Assert.AreEqual(1, h.Run.Results.Count);
            Assert.AreEqual("running", h.Run.Status, "wait 不顺手执行后续观察");
            CollectionAssert.AreEqual(events, h.Events.Skip(count));
            Equivalent(old, h.Run);
        }

        [Test]
        public void 前置失败骰失败及世界变化保持原分类_失败与剩余作废均由所有者采纳()
        {
            foreach (var failure in new[] { DsPlanFailure.Precondition, DsPlanFailure.Dice, DsPlanFailure.Stale })
            {
                string plan = failure == DsPlanFailure.Precondition
                    ? Plan(Act("s1", "rescue", "castle", "whelp"), Act("s2", place: "workshop"))
                    : failure == DsPlanFailure.Dice
                        ? Plan(Act("s1", "lead", "docks", "whelp"), Act("s2"))
                        : Plan(Act("s1"), Act("s2"));
                Action<DsWorld> setup = failure == DsPlanFailure.Precondition ? Caged :
                    failure == DsPlanFailure.Dice ? ReadyWhelp : world => { world.Day = 2; world.PlayerPlace = "workshop"; };
                using var h = new Harness((_, __) => plan, setup, failure == DsPlanFailure.Dice ? 0 : .9999);
                Assert.IsTrue(h.Propose().PlanReport.Executable, failure.ToString());
                var old = h.Reference(); Finish(old.ProposeAsync());
                h.Run.Accept(); old.Accept();
                if (failure == DsPlanFailure.Stale)
                {
                    var sweep = new Provider((_, __) => "{\"event\":\"guard_sweep\",\"intensity\":0.8,\"reason\":\"离线扰动\"}", false);
                    var gm = h.Start(() => h.Session.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, sweep));
                    Assert.IsTrue(h.Owner.Pump(gm).Receipt.Applied);
                    Finish(old.Session.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, sweep));
                }
                string before = h.Session.CaptureState().Json;
                var task = h.Start(() => h.Run.StepAsync());
                Finish(h.Owner.Next);
                Assert.AreEqual(before, h.Session.CaptureState().Json, failure.ToString());
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(DsPlanFailure.None, h.Run.LastFailure);
                Assert.AreEqual("running", h.Run.Status);
                var actual = h.Owner.Pump(task);
                var expected = Finish(old.StepAsync());
                Assert.AreEqual(failure, actual.Failure);
                Assert.AreEqual(expected.ReasonCode.Code, actual.ReasonCode.Code);
                Assert.AreEqual(failure == DsPlanFailure.Stale ? "void" : "failed", actual.Status);
                Assert.AreEqual("needs_replan", h.Run.Status);
                Assert.AreEqual(2, h.Run.Results.Count);
                Assert.AreEqual("void", h.Run.Results[1].Status);
                Assert.AreEqual(0, h.Run.NextIndex);
                if (failure == DsPlanFailure.Precondition) Assert.AreEqual(4, h.Session.TimeLeftInPhase);
                if (failure == DsPlanFailure.Dice) Assert.IsTrue(actual.Receipts.Any(r => r.Applied));
                if (failure == DsPlanFailure.Stale)
                {
                    Assert.IsEmpty(actual.Receipts);
                    Assert.AreEqual(before, h.Session.CaptureState().Json);
                }
                Equivalent(old, h.Run);
            }
        }

        [Test]
        public void workerRepair的拒绝反馈及v2约束在所有者准备_晚回只提案不自动确认()
        {
            string rejected = Plan(Act("s1"));
            string repaired = Plan(Act("s2", "confide", "docks", "pip"));
            using var h = new Harness((_, index) => index == 0 ? rejected : repaired, ReadyWhelp,
                constraints: DsPlanConstraints.WithHelper("pip"));
            h.Propose();
            Assert.AreEqual("planning", h.Run.Status, "可执行的独行草稿不满足明确合作条件");
            var previous = h.Run.LatestPlanTrace;
            string before = Record(h.Run), world = h.Session.CaptureState().Json;
            var task = h.Start(() => h.Run.RepairProposalAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual(before, Record(h.Run), "Repair 的 Status/LastReason 也不能在 worker 先写");
            Assert.AreEqual("", h.Run.LastReason);
            var call = h.Enter(1);
            Assert.AreEqual("needs_replan", h.Run.Status);
            var perception = (Dictionary<string, object>)MiniJson.Parse(call.Packet.Sections.Single(s => s.Name == "perception").Text);
            string context = MiniJson.Write(perception["replan"]);
            StringAssert.Contains("player_constraint", context);
            StringAssert.Contains(DsPlanConstraints.CooperationVersion, context);
            StringAssert.Contains("pip", context);
            StringAssert.DoesNotContain("done_steps", context);
            call.Answer();
            h.Owner.Until(() => h.Session.Traces.Last().Receipt != null);
            Finish(h.Owner.Next);
            Assert.AreSame(previous, h.Run.LatestPlanTrace);
            Assert.IsEmpty(h.Run.Replans);
            Assert.AreEqual(1, h.Run.PlanTraces.Count);
            Assert.IsEmpty(h.Run.AcceptedRequestIds);
            var trace = h.Owner.Pump(task);
            Assert.AreEqual("awaiting_confirmation", h.Run.Status);
            Assert.AreSame(trace, h.Run.LatestPlanTrace);
            Assert.AreEqual("pending", h.Run.Replans.Single().Decision);
            Assert.AreEqual(world, h.Session.CaptureState().Json);
            Assert.IsFalse(h.Session.World.Flags.ContainsKey("ally:pip"));

            var old = h.Reference(); Finish(old.ProposeAsync()); Finish(old.RepairProposalAsync());
            Equivalent(old, h.Run);
            h.Run.Accept(); old.Accept();
            Assert.AreEqual("accepted", h.Run.Replans.Single().Decision);
            Equivalent(old, h.Run);
        }

        [Test]
        public void workerReplan保留实际失败上下文及v2合作条件_采纳和放弃只改变计划记录()
        {
            string plan = Plan(Act("s1", "confide", "docks", "pip"));
            using var h = new Harness((_, __) => plan, ReadyWhelp, constraints: DsPlanConstraints.WithHelper("pip"));
            h.Propose();
            var old = h.Reference(); Finish(old.ProposeAsync());
            h.Run.Accept(); old.Accept();
            var sweep = new Provider((_, __) => "{\"event\":\"guard_sweep\",\"intensity\":0.8,\"reason\":\"公开搜城\"}", false);
            h.Owner.Pump(h.Start(() => h.Session.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, sweep)));
            Finish(old.Session.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, sweep));
            Assert.AreEqual(DsPlanFailure.Stale, h.Owner.Pump(h.Start(() => h.Run.StepAsync())).Failure);
            Finish(old.StepAsync());
            var previous = h.Run.LatestPlanTrace;
            string record = Record(h.Run), world = h.Session.CaptureState().Json;
            int traces = h.Session.Traces.Count;
            var task = h.Start(() => h.Run.ReplanAsync());
            Finish(h.Owner.Next);
            Assert.AreEqual(record, Record(h.Run));
            Assert.AreEqual(traces, h.Session.Traces.Count);
            var call = h.Enter(1);
            var perception = (Dictionary<string, object>)MiniJson.Parse(call.Packet.Sections.Single(s => s.Name == "perception").Text);
            string context = MiniJson.Write(perception["replan"]);
            StringAssert.Contains("世界已变", context);
            StringAssert.Contains("s1", context);
            StringAssert.Contains(DsPlanConstraints.CooperationVersion, context);
            StringAssert.Contains("required_helper", context);
            StringAssert.DoesNotContain("done_steps", context);
            call.Answer();
            h.Owner.Until(() => h.Session.Traces.Last().Receipt != null);
            Finish(h.Owner.Next);
            Assert.AreSame(previous, h.Run.LatestPlanTrace);
            Assert.IsEmpty(h.Run.Replans);
            Assert.AreEqual(record, Record(h.Run));
            h.Owner.Pump(task); Finish(old.ReplanAsync());
            Assert.AreEqual("awaiting_confirmation", h.Run.Status);
            Assert.AreEqual(DsPlanFailure.Stale, h.Run.Replans.Single().Failure);
            Assert.AreEqual(world, h.Session.CaptureState().Json);
            Equivalent(old, h.Run);
            h.Run.Abandon(); old.Abandon();
            Assert.AreEqual("abandoned", h.Run.Replans.Single().Decision);
            Assert.AreEqual(world, h.Session.CaptureState().Json);
            Equivalent(old, h.Run);
        }

        static void Closed(Task task)
        {
            var error = Assert.Catch(() => task.WaitAsync(Guard).GetAwaiter().GetResult());
            Assert.That(error is OwnerClosedFault || error is OperationCanceledException, Is.True,
                "关闭必须终止为 ClosedFault 或 Canceled，不能超时或回退执行：" + error);
        }

        [Test]
        public void 关闭冻结前回包后或采纳前队列都不回退_不补移动或结果且Session门闩释放()
        {
            foreach (string point in new[] { "propose_freeze", "provider_reply", "plan_adopt", "step_freeze", "step_consume" })
            {
                string plan = Plan(Act("s1", place: "workshop"));
                using var h = new Harness((_, __) => plan);
                bool step = point.StartsWith("step_", StringComparison.Ordinal);
                if (step) { h.Propose(); h.Run.Accept(); }
                string world = h.Session.CaptureState().Json, record = Record(h.Run);
                Task task = step ? (Task)h.Start(() => h.Run.StepAsync()) : h.Start(() => h.Run.ProposeAsync());
                Finish(h.Owner.Next);
                if (point == "provider_reply" || point == "plan_adopt")
                {
                    h.Enter(0).Answer();
                    Finish(h.Owner.Next);
                    if (point == "plan_adopt")
                    {
                        h.Owner.Until(() => h.Session.Traces.Single().Receipt != null);
                        Finish(h.Owner.Next);
                    }
                }
                if (point == "step_consume")
                {
                    h.Owner.Until(() => h.Session.Traces.Count == 2 && h.Session.Traces[1].Receipt != null);
                    Finish(h.Owner.Next);
                    // 行动自身已提交；关闭不能再回退到 worker 把它记为计划已完成。
                    world = h.Session.CaptureState().Json;
                    Assert.AreEqual("workshop", h.Session.World.PlayerPlace);
                    Assert.IsEmpty(h.Run.Results);
                }
                int events = h.Events.Count, calls = h.Provider.Calls, rules = h.Session.RuleEvents.Count;
                h.Owner.Close(point == "provider_reply" || step);
                Closed(task);
                Assert.AreEqual(record, Record(h.Run), point);
                Assert.AreEqual(world, h.Session.CaptureState().Json, point);
                Assert.AreEqual(events, h.Events.Count, "关闭后不能在 worker 补发通知");
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.AreEqual(rules, h.Session.RuleEvents.Count);
                Assert.IsFalse(h.Session.Busy, point);
                Closed(h.Start(() => h.Session.PlanAsync("验证会话门闩已释放")));
                Assert.IsFalse(h.Session.Busy);
                Assert.AreEqual(calls, h.Provider.Calls);
                Assert.AreEqual(events, h.Events.Count);
                Assert.AreEqual(0, h.Owner.Count);
            }
        }

        [Test]
        public void 冻结前等待回包及结果采纳前取消均不额外移动或追加结果_不撤销既有提交()
        {
            foreach (string point in new[] { "propose_freeze", "provider_wait", "provider_reply", "step_freeze", "step_consume" })
            {
                string plan = Plan(Act("s1", place: "workshop"));
                using var h = new Harness((_, __) => plan);
                bool step = point.StartsWith("step_", StringComparison.Ordinal);
                if (step) { h.Propose(); h.Run.Accept(); }
                using var cancellation = new CancellationTokenSource();
                string world = h.Session.CaptureState().Json, record = Record(h.Run);
                Task task = step ? (Task)h.Start(() => h.Run.StepAsync(cancellation.Token))
                    : h.Start(() => h.Run.ProposeAsync(cancellation.Token));
                Finish(h.Owner.Next);
                Call call = point == "provider_wait" || point == "provider_reply" ? h.Enter(0) : null;
                if (point == "provider_reply")
                {
                    call.Answer();
                    Finish(h.Owner.Next);
                    Assert.IsNull(h.Session.Traces.Single().Provider, "先取消已经回包、尚未由所有者消费的回复");
                }
                if (point == "step_consume")
                {
                    h.Owner.Until(() => h.Session.Traces.Count == 2 && h.Session.Traces[1].Receipt != null);
                    Finish(h.Owner.Next);
                    world = h.Session.CaptureState().Json;
                    Assert.IsEmpty(h.Run.Results);
                }
                int rules = h.Session.RuleEvents.Count;
                cancellation.Cancel();
                Assert.Catch<OperationCanceledException>(() => h.Owner.Pump(AsResult(task)));
                Assert.AreEqual(record, Record(h.Run), point);
                Assert.AreEqual(world, h.Session.CaptureState().Json, point);
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.AreEqual(rules, h.Session.RuleEvents.Count);
                Assert.IsFalse(h.Session.Busy);
                int events = h.Events.Count;
                if (call != null)
                {
                    Assert.AreEqual("cancelled", h.Session.Traces.Single().FailureCode);
                    OnWorker(() => Assert.IsFalse(call.Reply.TrySetResult(Response(call.Packet, call.Raw)),
                        "已取消提供方不能再次完成并触发额外采纳"));
                }
                else Assert.AreEqual(step ? 1 : 0, h.Provider.Calls);
                Assert.AreEqual(events, h.Events.Count);
                Assert.AreEqual(record, Record(h.Run));
                Assert.AreEqual(0, h.Owner.Count);
            }
        }

        [Test]
        public void Prepare完成而Session所有者块尚未执行时重启或同快照恢复_旧提案和等待均不能绑定新世界()
        {
            foreach (string entry in new[] { "propose", "wait_phase" })
            foreach (bool restore in new[] { false, true })
            {
                string scenario = entry + (restore ? ":restore_same_snapshot" : ":restart");
                string plan = Plan(new DsPlanStep { Id = "s1", Op = "wait", Place = "workshop", Until = "第2天·清晨" });
                using var h = new Harness((packet, _) => packet.Kind == AiTaskKind.Plan ? plan : Quiet(packet),
                    world => { world.PlayerPlace = "workshop"; world.Phase = DayPhase.Dusk; });
                if (entry == "wait_phase") { h.Propose(); h.Run.Accept(); }
                var originalWorld = h.Session.World;
                var snapshot = h.Session.CaptureState();
                string planBefore = Record(h.Run), replacementJson = null;
                int calls = h.Provider.Calls, events = h.Events.Count, pumped = h.Owner.Pumped, replacements = 0;
                Task task = entry == "propose" ? (Task)h.Start(() => h.Run.ProposeAsync())
                    : h.Start(() => h.Run.StepAsync());
                Finish(h.Owner.Next);
                Assert.AreEqual(pumped, h.Owner.Pumped);
                h.Owner.BeforeReply = () =>
                {
                    h.Owner.BeforeReply = null;
                    Assert.AreEqual(h.OwnerThread, Thread.CurrentThread.ManagedThreadId);
                    Assert.IsFalse(h.Session.Busy, "Prepare 已返回，Session 尚未取得 gate：" + scenario);
                    Assert.AreEqual(snapshot.Json, h.Session.CaptureState().Json);
                    Assert.AreEqual(entry == "propose" ? 0 : 1, h.Session.Traces.Count);
                    Assert.AreEqual(calls, h.Provider.Calls);
                    if (restore) h.Session.RestoreState(snapshot);
                    else h.Session.Restart();
                    replacements++;
                    Assert.AreNotSame(originalWorld, h.Session.World);
                    replacementJson = h.Session.CaptureState().Json;
                    if (restore) Assert.AreEqual(snapshot.Json, replacementJson,
                        "内容与 SessionGen 都相同，仍必须根据世界实例身份拒绝旧操作");
                };

                h.Owner.One(); // 恰好只完成 PlanRun.Prepare；下一个 Session Owner 块仍留在队列中。
                Finish(h.Owner.Next);
                Assert.AreEqual(1, replacements);
                Assert.AreEqual(pumped + 1, h.Owner.Pumped);
                Assert.AreEqual(1, h.Owner.Count);
                Assert.IsTrue(h.Session.Busy, "Session 已取得 gate，但 BeginCall/phase 初始块尚未 pump");
                Assert.IsFalse(task.IsCompleted);
                Assert.IsEmpty(h.Session.Traces);
                Assert.IsEmpty(h.Session.RuleEvents);
                Assert.AreEqual(calls, h.Provider.Calls);
                Assert.AreEqual(replacementJson, h.Session.CaptureState().Json);

                Assert.Catch<OperationCanceledException>(() => h.Owner.Pump(AsResult(task)), scenario);
                Assert.AreEqual(replacementJson, h.Session.CaptureState().Json, scenario + " 的新世界完整 JSON 不变");
                Assert.IsEmpty(h.Session.Traces, "失效检查必须早于新 trace 编号和冻结感知");
                Assert.IsEmpty(h.Session.RuleEvents, "旧 wait 不得在新世界推进时段");
                Assert.AreEqual(calls, h.Provider.Calls, "不得让旧操作发起新的居民或规划请求");
                Assert.AreEqual(events, h.Events.Count);
                Assert.AreEqual(planBefore, Record(h.Run));
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.IsFalse(h.Session.Busy, scenario + " 的 gate 已释放");
                Assert.AreEqual(0, h.Owner.Count);
            }
        }

        [Test]
        public void 同步移动规则回调放弃后不发行动_提交阶段回调放弃后不写候选_既有移动不回滚()
        {
            foreach (string boundary in new[] { "move_rule", "commit_stage" })
            {
                string plan = Plan(Act("s1", place: "workshop"), Act("s2", place: "cathedral"));
                using var h = new Harness((_, __) => plan,
                    world => world.PlayerPlace = boundary == "move_rule" ? "market" : "workshop");
                var proposed = h.Propose();
                h.Run.Accept();
                string before = h.Session.CaptureState().Json, retainedWorld = null, abandonedRecord = null;
                int traces = h.Session.Traces.Count, calls = h.Provider.Calls, callbacks = 0;
                AiTrace candidate = null;
                DsRuleEvent movement = null;
                if (boundary == "move_rule")
                    h.Session.RuleEventCompleted += rule =>
                    {
                        Assert.AreEqual(h.OwnerThread, Thread.CurrentThread.ManagedThreadId);
                        Assert.AreEqual("enter:workshop", rule.Label);
                        callbacks++;
                        movement = rule;
                        Assert.IsTrue(rule.Receipt.Applied, "回调发生在移动已经实际提交之后");
                        Assert.AreEqual("workshop", h.Session.World.PlayerPlace);
                        h.Run.Abandon();
                        retainedWorld = h.Session.CaptureState().Json;
                        abandonedRecord = Record(h.Run);
                    };
                else
                    h.Session.StageChanged += (trace, stage) =>
                    {
                        if (trace.Kind != AiTaskKind.Adjudicate || stage != AiStage.Commit) return;
                        Assert.AreEqual(h.OwnerThread, Thread.CurrentThread.ManagedThreadId);
                        Assert.AreEqual(StageStatus.Pending, trace.Stage(stage).Status, "仅在 Begin(Commit) 回调中放弃");
                        callbacks++;
                        candidate = trace;
                        Assert.AreEqual(VerdictKind.Accepted, trace.Verdict.Kind, "规则已准备好可执行候选");
                        Assert.IsNotNull(trace.Proposal);
                        Assert.IsNull(trace.Receipt);
                        Assert.AreEqual(before, h.Session.CaptureState().Json, "候选尚未写入真实世界");
                        h.Run.Abandon();
                        retainedWorld = h.Session.CaptureState().Json;
                        abandonedRecord = Record(h.Run);
                    };

                var task = h.Start(() => h.Run.StepAsync());
                Finish(h.Owner.Next);
                Assert.AreEqual(0, callbacks);
                Assert.AreEqual(before, h.Session.CaptureState().Json);
                Assert.Catch<OperationCanceledException>(() => h.Owner.Pump(task), boundary);
                Assert.AreEqual(1, callbacks, "同步放弃只触发一次，不继续下一行动或步骤");
                Assert.AreEqual("abandoned", h.Run.Status);
                Assert.AreEqual(abandonedRecord, Record(h.Run));
                Assert.AreEqual(retainedWorld, h.Session.CaptureState().Json);
                Assert.AreSame(proposed, h.Run.LatestPlanTrace);
                Assert.AreEqual(1, h.Run.PlanTraces.Count);
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.AreEqual(calls, h.Provider.Calls);
                Assert.AreEqual(4, h.Session.TimeLeftInPhase, "未提交行动不能消耗时间");
                if (boundary == "move_rule")
                {
                    Assert.AreEqual(traces, h.Session.Traces.Count, "移动回调放弃后连首个行动 trace 都不能创建");
                    Assert.AreSame(movement, h.Session.RuleEvents.Single());
                    Assert.AreNotEqual(before, retainedWorld, "已提交的移动必须保留，不做取消回滚");
                    Assert.AreEqual("workshop", h.Session.World.PlayerPlace);
                }
                else
                {
                    Assert.AreEqual(traces + 1, h.Session.Traces.Count);
                    Assert.AreSame(candidate, h.Session.Traces.Last());
                    Assert.IsNull(candidate.Receipt, "Commit 回调后失效检查应先于执行器写入和回执分配");
                    Assert.IsEmpty(candidate.Changes);
                    Assert.AreEqual(before, retainedWorld);
                    Assert.IsEmpty(h.Session.RuleEvents);
                    Assert.AreEqual(1, h.Events.Count(e => e == "done:" + candidate.RequestId));
                }
                Assert.IsFalse(h.Session.Busy);
                Assert.AreEqual(0, h.Owner.Count);
            }
        }

        [Test]
        public void 实际提供方开始前或提交通知中取消_不调用提供方或提交候选()
        {
            foreach (AiStage boundary in new[] { AiStage.Provider, AiStage.Commit })
            {
                using var h = new Harness((_, __) => Plan(Act("s1")));
                h.Propose();
                h.Run.Accept();
                using var cancellation = new CancellationTokenSource();
                string before = h.Session.CaptureState().Json;
                int calls = h.Provider.Calls, notices = 0;
                AiTrace candidate = null;
                h.Session.StageChanged += (trace, stage) =>
                {
                    if (trace.Kind != AiTaskKind.Adjudicate || stage != boundary || trace.Stage(stage).Status != StageStatus.Pending) return;
                    candidate = trace;
                    cancellation.Cancel();
                };
                h.Session.TraceCompleted += trace => { if (trace.Kind == AiTaskKind.Adjudicate) notices++; };
                var task = h.Start(() => h.Run.StepAsync(cancellation.Token));
                Assert.Catch<OperationCanceledException>(() => h.Owner.Pump(task));
                Assert.IsNotNull(candidate);
                Assert.AreEqual("cancelled", candidate.FailureCode);
                Assert.IsNull(candidate.Receipt);
                Assert.AreEqual(before, h.Session.CaptureState().Json);
                Assert.AreEqual(4, h.Session.TimeLeftInPhase);
                Assert.AreEqual(calls, h.Provider.Calls);
                Assert.IsEmpty(h.Run.Results);
                Assert.AreEqual(0, h.Run.NextIndex);
                Assert.AreEqual(1, notices);
                Assert.IsFalse(h.Session.Busy);
            }
        }

        [Test]
        public void 前后同步块已有主异常时完成监听异常不能覆盖_无主异常才传播监听错误()
        {
            foreach (string boundary in new[] { "begin", "commit_abandon", "normal_complete" })
            {
                using var h = new Harness((_, __) => Plan(Act("s1")));
                h.Propose();
                h.Run.Accept();
                string before = h.Session.CaptureState().Json;
                var primary = new InvalidOperationException("primary-stage");
                var cleanup = new ApplicationException("completion-observer");
                int notices = 0;
                h.Session.StageChanged += (trace, stage) =>
                {
                    if (trace.Kind != AiTaskKind.Adjudicate || trace.Stage(stage).Status != StageStatus.Pending) return;
                    if (boundary == "begin" && stage == AiStage.Context) throw primary;
                    if (boundary == "commit_abandon" && stage == AiStage.Commit) h.Run.Abandon();
                };
                h.Session.TraceCompleted += trace =>
                {
                    if (trace.Kind != AiTaskKind.Adjudicate) return;
                    notices++;
                    throw cleanup;
                };
                var task = h.Start(() => h.Run.StepAsync());
                var error = Assert.Catch(() => h.Owner.Pump(task));
                if (boundary == "begin") Assert.AreSame(primary, error);
                else if (boundary == "commit_abandon") Assert.IsInstanceOf<OperationCanceledException>(error);
                else Assert.AreSame(cleanup, error);
                if (boundary != "normal_complete") Assert.AreEqual(before, h.Session.CaptureState().Json);
                else Assert.IsTrue(h.Session.Traces.Last().Receipt.Applied, "监听异常不回滚实际已提交后果");
                Assert.AreEqual(1, notices);
                Assert.IsFalse(h.Session.Busy);
            }
        }

        [Test]
        public void 已排队后半块被关闭取消_后续同步清理抛错不能覆盖原取消()
        {
            using var h = new Harness((_, __) => Plan(Act("s1")));
            var task = h.Start(() => h.Session.PlanAsync(Goal));
            h.Enter(0).Answer();
            Finish(h.Owner.Next);
            string before = h.Session.CaptureState().Json;
            h.Owner.ThrowAfterClose = true;
            h.Owner.Close(cancel: true);
            Assert.Catch<OperationCanceledException>(() => Finish(task));
            Assert.AreEqual(before, h.Session.CaptureState().Json);
            Assert.IsFalse(h.Session.Busy);
            Assert.IsFalse(h.Events.Any(e => e.StartsWith("done:", StringComparison.Ordinal)));
            Assert.IsNull(h.Session.Traces.Single().Receipt);
        }

        static async Task<bool> AsResult(Task task)
        {
            await task.ConfigureAwait(false);
            return true;
        }
    }
}
