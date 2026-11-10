using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepOwnerDispatchTests
    {
        static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        static TaskCompletionSource<T> Signal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        static T Finish<T>(Task<T> task) => task.WaitAsync(Guard).GetAwaiter().GetResult();

        static DragonsleepSession Session(IAiProvider p, IDsOwnerThreadDispatcher owner = null) =>
            new DragonsleepSession(p, 7, new DsFixedRoll(0.9999), ownerDispatcher: owner);

        static string Receipt(AiTrace t) => MiniJson.Write(t.Receipt?.ToRecord());

        static List<string> Watch(DragonsleepSession s, Action check)
        {
            var events = new List<string>();
            s.StageChanged += (t, stage) =>
            {
                check();
                events.Add(t.RequestId + ":" + stage + ":" + t.Stage(stage).Status);
            };
            s.TraceCompleted += t =>
            {
                check();
                events.Add("done:" + t.RequestId);
            };
            s.RuleEventCompleted += e =>
            {
                check();
                events.Add("rule:" + e.RequestId);
            };
            return events;
        }

        static Task<T> Start<T>(Func<Task<T>> call)
        {
            var issued = Signal<Task<T>>();
            int owner = Thread.CurrentThread.ManagedThreadId;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    Assert.AreNotEqual(owner, Thread.CurrentThread.ManagedThreadId);
                    issued.SetResult(call());
                }
                catch (Exception e)
                {
                    issued.SetException(e);
                }
            }) { IsBackground = true };
            thread.Start();
            Assert.IsTrue(thread.Join(Guard), "发起线程只发起调用，不等待或泵送 NUnit 上下文");
            return Finish(issued.Task);
        }

        sealed class OwnerQueue : IDsOwnerThreadDispatcher
        {
            readonly object sync = new object();
            readonly Queue<(Action Run, Action Cancel)> jobs = new Queue<(Action, Action)>();
            readonly int owner = Thread.CurrentThread.ManagedThreadId;
            TaskCompletionSource<bool> next = Signal<bool>();
            bool closed;

            public Task<bool> Next
            {
                get
                {
                    lock (sync)
                        return jobs.Count > 0 ? Task.FromResult(true) : next.Task;
                }
            }

            public Task<T> InvokeAsync<T>(Func<T> syncBlock)
            {
                var result = Signal<T>();
                Action cancel = () => result.TrySetCanceled();
                lock (sync)
                {
                    if (closed)
                        cancel();
                    else
                    {
                        jobs.Enqueue((() =>
                        {
                            try
                            {
                                result.TrySetResult(syncBlock());
                            }
                            catch (Exception e)
                            {
                                result.TrySetException(e);
                            }
                        }, cancel));
                        next.TrySetResult(true);
                    }
                }
                return result.Task;
            }

            public void Drain()
            {
                Assert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId);
                Finish(Next);
                (Action Run, Action Cancel) job;
                lock (sync)
                {
                    job = jobs.Dequeue();
                    if (jobs.Count == 0)
                        next = Signal<bool>();
                }
                job.Run();
            }

            public T Pump<T>(Task<T> task)
            {
                while (!task.IsCompleted)
                {
                    var queued = Next;
                    if (Finish(Task.WhenAny(task, queued)) == queued)
                        Drain();
                }
                return Finish(task);
            }

            public void Close()
            {
                lock (sync)
                {
                    closed = true;
                    foreach (var job in jobs)
                        job.Cancel();
                    jobs.Clear();
                }
            }
        }

        sealed class Call
        {
            public PromptPacket Packet;
            public readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();

            public void Answer() => Reply.SetResult(Finish(new RuleProvider().CompleteAsync(Packet, CancellationToken.None)));
        }

        sealed class ScriptProvider : IAiProvider
        {
            public readonly TaskCompletionSource<Call>[] Entered = Enumerable.Range(0, 8).Select(_ => Signal<Call>()).ToArray();
            public int Calls;
            int stopped;

            public string Name => "owner-dispatch-offline";
            public bool IsSimulated => true;

            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                var call = new Call { Packet = packet };
                int index = Interlocked.Increment(ref Calls) - 1;
                using (ct.Register(() => call.Reply.TrySetCanceled(ct)))
                {
                    Entered[index].SetResult(call);
                    if (Volatile.Read(ref stopped) != 0)
                        call.Reply.TrySetCanceled();
                    return await call.Reply.Task.ConfigureAwait(false);
                }
            }

            public void Cancel()
            {
                Interlocked.Exchange(ref stopped, 1);
                foreach (var slot in Entered.Where(s => s.Task.IsCompletedSuccessfully))
                    slot.Task.Result.Reply.TrySetCanceled();
            }
        }

        sealed class Run : IDisposable
        {
            public readonly OwnerQueue Owner = new OwnerQueue();
            public readonly ScriptProvider Provider = new ScriptProvider();
            public readonly DragonsleepSession S;
            public readonly List<string> Events;
            readonly List<Task<AiTrace>> runs = new List<Task<AiTrace>>();

            public Run()
            {
                S = Session(Provider, Owner);
                int owner = Thread.CurrentThread.ManagedThreadId;
                Events = Watch(S, () =>
                {
                    Assert.AreEqual(owner, Thread.CurrentThread.ManagedThreadId);
                    Assert.IsTrue(S.Busy);
                });
            }

            public Task<AiTrace> Act(CancellationToken ct = default)
            {
                var task = Start(() => S.ActAsync("观察四周", ct));
                runs.Add(task);
                return task;
            }

            public Call Freeze()
            {
                Owner.Drain();
                return Finish(Provider.Entered[0].Task);
            }

            public void Dispose()
            {
                Provider.Cancel();
                Owner.Close();
                foreach (var task in runs)
                {
                    try
                    {
                        Finish(task);
                    }
                    catch (Exception)
                    {
                        /* 测试主体断言错误；这里只防止悬挂。 */
                    }
                }
            }
        }

        [Test]
        public void 首次泵送前无痕迹_等待与回包均不偷写_事件归属及回执等同旧入口()
        {
            using var h = new Run();
            var before = h.S.CaptureState().Json;
            var task = h.Act();
            Finish(h.Owner.Next);
            Assert.IsEmpty(h.S.Traces);
            Assert.IsEmpty(h.Events);
            Assert.AreEqual(before, h.S.CaptureState().Json);

            var call = h.Freeze();
            int count = h.Events.Count;
            Assert.IsTrue(h.S.Busy);
            Assert.IsNull(h.S.EnterPlace("workshop"));
            Assert.IsEmpty(h.S.RuleEvents);
            Assert.AreEqual(before, h.S.CaptureState().Json);

            call.Answer();
            Finish(h.Owner.Next);
            Assert.IsFalse(task.IsCompleted);
            Assert.IsTrue(h.S.Busy);
            Assert.AreEqual(count, h.Events.Count);
            Assert.AreEqual(before, h.S.CaptureState().Json);
            Assert.IsNull(h.S.Traces.Single().Provider);
            Assert.IsNull(h.S.Traces.Single().Proposal);

            var actual = h.Owner.Pump(task);
            var old = Session(new RuleProvider());
            var events = Watch(old, () => { });
            var expected = Finish(old.ActAsync("观察四周"));
            Assert.IsTrue(actual.Receipt.Applied);
            Assert.AreEqual(Receipt(expected), Receipt(actual));
            Assert.AreEqual(old.CaptureState().Json, h.S.CaptureState().Json);
            CollectionAssert.AreEqual(events, h.Events);
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 协作取消仍是取消失败而非回执_所有完成通知在所有者且释放门闩()
        {
            using var h = new Run();
            using var ct = new CancellationTokenSource();
            string before = h.S.CaptureState().Json;
            var task = h.Act(ct.Token);
            h.Freeze();

            int count = h.Events.Count;
            ct.Cancel();
            Finish(h.Owner.Next);
            Assert.AreEqual(count, h.Events.Count);
            Assert.IsTrue(h.S.Busy);

            var trace = h.Owner.Pump(task);
            Assert.AreEqual("cancelled", trace.FailureCode);
            Assert.AreEqual("cancelled", trace.Provider.Error);
            Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Provider).Status);
            Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Parse).Status);
            Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Commit).Status);
            Assert.IsNull(trace.Receipt);
            Assert.IsEmpty(trace.Changes);
            Assert.AreEqual(before, h.S.CaptureState().Json);
            Assert.AreEqual(1, h.Events.Count(e => e.StartsWith("done:")));
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 提供方异常不被包装_所有者恰好通知完成一次且世界不变()
        {
            using var h = new Run();
            string before = h.S.CaptureState().Json;
            var task = h.Act();
            var call = h.Freeze();
            var failure = new InvalidOperationException("provider_fault");

            int count = h.Events.Count;
            call.Reply.SetException(failure);
            Finish(h.Owner.Next);
            Assert.AreEqual(count, h.Events.Count);
            Assert.IsTrue(h.S.Busy);

            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => h.Owner.Pump(task)));
            Assert.AreEqual(1, h.Events.Count(e => e.StartsWith("done:")));
            Assert.IsNull(h.S.Traces.Single().Receipt);
            Assert.AreEqual(before, h.S.CaptureState().Json);
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 第二请求取得门闩后才冻结_感知包含首请求已提交状态()
        {
            using var h = new Run();
            var first = h.Act();
            var call = h.Freeze();
            var second = h.Act();
            Assert.AreEqual(1, h.S.Traces.Count);
            Assert.AreEqual(1, h.Provider.Calls);
            Assert.IsFalse(h.Owner.Next.IsCompleted);

            call.Answer();
            h.Owner.Drain();
            var one = Finish(first);
            string afterFirst = h.S.CaptureState().Json;
            h.Owner.Drain();
            var next = Finish(h.Provider.Entered[1].Task);
            Assert.AreEqual(one.WorldVersionAfter, h.S.Traces[1].WorldVersionBefore);
            Assert.AreEqual(afterFirst, h.S.CaptureState().Json);
            Assert.IsTrue(h.S.Busy);

            next.Answer();
            var two = h.Owner.Pump(second);
            var old = Session(new RuleProvider());
            var expectedOne = Finish(old.ActAsync("观察四周"));
            var expectedTwo = Finish(old.ActAsync("观察四周"));
            CollectionAssert.AreEqual(
                expectedTwo.Prompt.Sections.Select(s => s.Name + s.Text),
                two.Prompt.Sections.Select(s => s.Name + s.Text));
            Assert.AreEqual(Receipt(expectedOne), Receipt(one));
            Assert.AreEqual(Receipt(expectedTwo), Receipt(two));
            Assert.AreEqual(old.CaptureState().Json, h.S.CaptureState().Json);
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 关闭队列取消尚未冻结请求_不回退执行且后续请求不锁死()
        {
            using var h = new Run();
            string before = h.S.CaptureState().Json;
            var task = h.Act();
            Finish(h.Owner.Next);
            h.Owner.Close();

            Assert.Catch<OperationCanceledException>(() => Finish(task));
            Assert.IsEmpty(h.S.Traces);
            Assert.IsEmpty(h.Events);
            Assert.AreEqual(0, h.Provider.Calls);
            Assert.IsFalse(h.S.Busy);

            Assert.Catch<OperationCanceledException>(() => Finish(h.Act()));
            Assert.AreEqual(before, h.S.CaptureState().Json);
            Assert.AreEqual(0, h.Provider.Calls);
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 关闭队列取消已回包但未提交的后半段_不补写或异线程通知()
        {
            using var h = new Run();
            string before = h.S.CaptureState().Json;
            var task = h.Act();
            var call = h.Freeze();
            int count = h.Events.Count;
            call.Answer();
            Finish(h.Owner.Next);
            h.Owner.Close();

            Assert.Catch<OperationCanceledException>(() => Finish(task));
            Assert.AreEqual(before, h.S.CaptureState().Json);
            Assert.AreEqual(count, h.Events.Count);
            Assert.IsNull(h.S.Traces.Single().Receipt);
            Assert.IsFalse(h.S.Busy);
        }

        [Test]
        public void 真实造物提案只调用一次_准备及待确认不写_确认放弃过期回执与两次通知归属一致()
        {
            for (int mode = 0; mode < 3; mode++)
            {
                using var h = new Run();
                var old = Session(new RuleProvider());
                h.S.World.PlayerPlace = old.World.PlayerPlace = "workshop";
                string before = h.S.CaptureState().Json;
                var notices = new List<string>();
                h.S.TraceCompleted += t =>
                {
                    notices.Add(t.Receipt.Status);
                    if (t.CraftDecision == "pending")
                        Assert.IsTrue(h.S.HasPendingCraft);
                };
                h.S.StageChanged += (_, __) => Assert.AreEqual(before, h.S.CaptureState().Json);

                var task = Start(() => h.S.CraftAsync("用蓝晶石织一盏灯"));
                var call = h.Freeze();
                Assert.AreEqual(before, h.S.CaptureState().Json);
                call.Answer();
                Finish(h.Owner.Next);
                Assert.IsFalse(h.S.HasPendingCraft);

                var pending = h.Owner.Pump(task);
                var expected = Finish(old.CraftAsync("用蓝晶石织一盏灯"));
                Assert.IsTrue(h.S.HasPendingCraft);
                Assert.AreEqual("pending", pending.CraftDecision);
                Assert.AreEqual(before, h.S.CaptureState().Json);
                Assert.AreEqual(Receipt(expected), Receipt(pending));

                if (mode == 2)
                {
                    h.S.World.Coins--;
                    old.World.Coins--;
                }
                string decisionBefore = h.S.CaptureState().Json;
                var actual = h.S.ConfirmCraft(mode != 1);
                expected = old.ConfirmCraft(mode != 1);
                string status = mode == 0 ? "committed" : mode == 1 ? "declined" : "stale_state";
                Assert.AreSame(pending, actual);
                Assert.AreEqual(status, actual.Receipt.Status);
                Assert.AreEqual(mode == 0, actual.Receipt.Applied);
                Assert.AreEqual(Receipt(expected), Receipt(actual));
                Assert.AreEqual(old.CaptureState().Json, h.S.CaptureState().Json);
                if (mode != 0)
                    Assert.AreEqual(decisionBefore, h.S.CaptureState().Json);

                CollectionAssert.AreEqual(new[] { "awaiting_confirmation", status }, notices);
                Assert.AreEqual(2, h.Events.Count(e => e.StartsWith("done:")));
                Assert.AreEqual(1, h.Provider.Calls);
                Assert.IsFalse(h.S.HasPendingCraft);
                Assert.IsNull(h.S.ConfirmCraft(true));
                Assert.IsFalse(h.S.Busy);
            }
        }

        [Test]
        public void 日间及跨夜两侧推进均归所有者_逐居民回包顺序和完整规则回执等同旧入口()
        {
            foreach (var phase in new[] { DayPhase.Dawn, DayPhase.Dusk })
            {
                using var h = new Run();
                var old = Session(new RuleProvider());
                h.S.World.Phase = old.World.Phase = phase;
                var expectedEvents = Watch(old, () => { });
                string before = h.S.CaptureState().Json;
                var task = Start(() => h.S.EndPhaseAsync());
                Finish(h.Owner.Next);
                Assert.IsEmpty(h.Events);
                Assert.IsEmpty(h.S.Traces);
                Assert.AreEqual(before, h.S.CaptureState().Json);

                var actors = h.S.World.Actors.Keys.OrderBy(x => x, StringComparer.Ordinal).Concat(new[] { "gm" }).ToArray();
                for (int i = 0; phase == DayPhase.Dusk && i < actors.Length; i++)
                {
                    var call = h.Owner.Pump(h.Provider.Entered[i].Task);
                    Assert.AreEqual(actors[i], call.Packet.ActorId);
                    Assert.AreEqual(i + 1, h.Provider.Calls);
                    Assert.IsTrue(h.S.Busy);
                    string frozen = h.S.CaptureState().Json;
                    int count = h.Events.Count;

                    call.Answer();
                    Finish(h.Owner.Next);
                    Assert.AreEqual(frozen, h.S.CaptureState().Json);
                    Assert.AreEqual(count, h.Events.Count);
                    Assert.IsFalse(task.IsCompleted);
                }

                var actual = h.Owner.Pump(task);
                var expected = Finish(old.EndPhaseAsync());
                CollectionAssert.AreEqual(expected.Select(Receipt), actual.Select(Receipt));
                CollectionAssert.AreEqual(expectedEvents, h.Events);
                CollectionAssert.AreEqual(
                    old.RuleEvents.Select(e => MiniJson.Write(e.ToRecord())),
                    h.S.RuleEvents.Select(e => MiniJson.Write(e.ToRecord())));
                Assert.AreEqual(old.CaptureState().Json, h.S.CaptureState().Json);
                Assert.AreEqual(phase == DayPhase.Dusk ? 6 : 0, h.Provider.Calls);
                Assert.IsFalse(h.S.Busy);
            }
        }
    }
}
