using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>
    /// 生产异步边界的范围刻画：独立 owner-dispatch SDK 尚未接入 Session。
    /// 专用发起线程不是 Unity 主线程；本测试不代表生产线程归属缺口已修复。
    /// </summary>
    public class DragonsleepAsyncBoundaryTests
    {
        static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
        static TaskCompletionSource<T> Signal<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        static T Finish<T>(Task<T> task) => task.WaitAsync(Guard).GetAwaiter().GetResult();

        sealed class PendingProvider : IAiProvider
        {
            public readonly TaskCompletionSource<bool> Entered = Signal<bool>();
            public readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();
            public string Name => "async-boundary-offline";
            public bool IsSimulated => true;
            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                using (ct.Register(() => Reply.TrySetCanceled(ct)))
                {
                    Entered.TrySetResult(true);
                    return await Reply.Task.ConfigureAwait(false);
                }
            }
        }

        sealed class Caller
        {
            public readonly TaskCompletionSource<Task<AiTrace>> Issued = Signal<Task<AiTrace>>();
            readonly Thread _thread;
            public int ThreadId;
            public Caller(DragonsleepSession session, CancellationToken ct = default)
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        ThreadId = Thread.CurrentThread.ManagedThreadId;
                        var run = session.ActAsync("观察四周", ct);
                        Issued.TrySetResult(run);
                        run.GetAwaiter().GetResult(); // 保持发起线程存活，但不泵送 NUnit 的同步上下文。
                    }
                    catch (Exception error) { Issued.TrySetException(error); }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Drain(PendingProvider provider)
            {
                try { Finish(Task.Run(() => provider.Reply.TrySetCanceled())); Finish(Finish(Issued.Task)); }
                finally { Assert.IsTrue(_thread.Join(Guard), "专用发起线程必须退出；超时只用于防挂"); }
            }
        }

        sealed class RejectingScene : IDsSceneRuleTransaction
        {
            public int Calls;
            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write)
            { Calls++; return "stale_state"; }
        }

        [Test]
        public void 生产边界刻画_等待时移动被门闩拒绝_异步完成不回到发起线程()
        {
            var provider = new PendingProvider();
            var session = new DragonsleepSession(provider);
            var before = session.CaptureState();
            var proposal = new DsPlanStep { Verb = "observe" }.ToProposal(session.World.PlayerPlace);
            var expected = new DragonsleepSession(new RuleProvider());
            var expectedTrace = Finish(expected.RunStepAsync(proposal, "观察四周"));
            int completedThread = -1, callbacks = 0;
            string completedState = null;
            bool gateHeldAtCompletion = false;
            session.TraceCompleted += _ =>
            {
                completedThread = Thread.CurrentThread.ManagedThreadId;
                completedState = session.CaptureState().Json;
                gateHeldAtCompletion = session.Busy;
                callbacks++;
            };
            var caller = new Caller(session);
            try
            {
                var run = Finish(caller.Issued.Task);
                Finish(provider.Entered.Task);
                Assert.IsFalse(run.IsCompleted);
                Assert.IsTrue(session.Busy);
                Assert.IsNull(session.EnterPlace("workshop"));
                Assert.AreEqual(before.Json, session.CaptureState().Json);
                Assert.IsEmpty(session.RuleEvents);

                Finish(Task.Run(() => provider.Reply.TrySetResult(new ProviderResult
                { ProviderName = provider.Name, RawText = MiniJson.Write(proposal), Simulated = true, EstimatedUsd = 0m })));
                var trace = Finish(run);

                Assert.AreEqual(1, callbacks);
                Assert.AreNotEqual(caller.ThreadId, completedThread, "当前生产路径没有调度回专用发起线程");
                Assert.IsTrue(gateHeldAtCompletion);
                Assert.IsNull(trace.FailureCode);
                Assert.IsTrue(trace.Receipt.Applied);
                Assert.AreEqual(expectedTrace.Receipt.TimeCost, trace.Receipt.TimeCost);
                Assert.AreEqual(expected.CaptureState().Json, session.CaptureState().Json, "仅有正常观察的世界变化，失败移动无额外变化");
                Assert.AreEqual(completedState, session.CaptureState().Json);
                Assert.IsEmpty(session.RuleEvents);
                Assert.IsFalse(session.Busy);
            }
            finally { caller.Drain(provider); }
        }

        [Test]
        public void 提供方协作取消保留旧失败语义_释放门闩且世界不变()
        {
            var provider = new PendingProvider();
            var session = new DragonsleepSession(provider);
            var world = session.World;
            var before = session.CaptureState();
            using var cancellation = new CancellationTokenSource();
            var caller = new Caller(session, cancellation.Token);
            try
            {
                var run = Finish(caller.Issued.Task);
                Finish(provider.Entered.Task);
                Assert.IsFalse(run.IsCompleted);
                Assert.IsTrue(session.Busy);
                Finish(Task.Run(() => { cancellation.Cancel(); return true; }));
                var trace = Finish(run);

                Assert.AreEqual("cancelled", trace.FailureCode);
                Assert.AreEqual("cancelled", trace.Provider.Error);
                Assert.AreEqual(StageStatus.Fail, trace.Stage(AiStage.Provider).Status);
                Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Parse).Status);
                Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Commit).Status);
                Assert.IsNull(trace.Receipt);
                Assert.IsEmpty(trace.Changes);
                Assert.AreSame(world, session.World);
                Assert.AreEqual(before.Json, session.CaptureState().Json);
                Assert.IsFalse(session.Busy);
                var backend = new RejectingScene();
                Assert.AreEqual("stale_state", session.EnterPlace("workshop", backend)?.Receipt.Status);
                Assert.AreEqual(1, backend.Calls, "取消后仍能进入同步事务，探针拒绝写入以保持世界不变");
                Assert.AreEqual(before.Json, session.CaptureState().Json);
                Assert.IsFalse(session.Busy);
            }
            finally { caller.Drain(provider); }
        }
    }
}
