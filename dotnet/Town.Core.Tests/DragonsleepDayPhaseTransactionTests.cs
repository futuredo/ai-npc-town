using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>日间 SDK 事务边界：固定骰、离线提供方，不读取密钥或发起网络请求。</summary>
    public class DragonsleepDayPhaseTransactionTests
    {
        sealed class Transaction : IDsDayPhaseTransaction
        {
            public int Calls;
            public string RequestId;
            public DsWorldSnapshot Before;
            public DsWorldSnapshot After;
            public DsDayPhaseWrite Write;
            public Func<DsDayPhaseWrite, string> CommitAction;

            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsDayPhaseWrite write)
            {
                Calls++;
                RequestId = id; Before = before; After = after; Write = write;
                if (CommitAction != null) return CommitAction(write);
                write.Apply();
                return "committed";
            }
        }

        sealed class BlockingProvider : IAiProvider
        {
            public readonly TaskCompletionSource<ProviderResult> Reply =
                new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;
            public string Name => "day-phase-blocking-offline";
            public bool IsSimulated => true;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Calls++;
                return Reply.Task;
            }
        }

        static DragonsleepSession Session(DayPhase phase = DayPhase.Dawn, double roll = 0.9999)
        {
            var session = new DragonsleepSession(new RuleProvider(), 7, new DsFixedRoll(roll));
            var world = session.World;
            world.Phase = phase;
            world.Version = 9;
            world.RollCounter = 11;
            world.Whelp.Place = world.PlayerPlace;
            world.Whelp.Following = true;
            world.Whelp.Hidden = true;
            world.Whelp.Fear = 0.73123455f;
            world.Flags["whelp_hidden_by_player"] = "事务测试斗篷";
            return session;
        }

        static async Task SpendTwo(DragonsleepSession session)
        {
            var proposal = new DsPlanStep { Verb = "observe" }.ToProposal(session.World.PlayerPlace);
            var trace = await session.RunStepAsync(proposal, "观察以实际消耗两点时间");
            Assert.IsNull(trace.FailureCode, trace.Verdict.Reason);
            Assert.IsTrue(trace.Receipt.Applied);
            Assert.AreEqual(2, trace.Receipt.TimeCost);
            Assert.AreEqual(2, session.CaptureState().Spent);
        }

        [TestCase(DayPhase.Dawn, false, 0.9999)]
        [TestCase(DayPhase.Dawn, false, 0.0)]
        [TestCase(DayPhase.Dawn, true, 0.9999)]
        [TestCase(DayPhase.Dawn, true, 0.0)]
        [TestCase(DayPhase.Noon, false, 0.9999)]
        [TestCase(DayPhase.Noon, false, 0.0)]
        [TestCase(DayPhase.Noon, true, 0.9999)]
        [TestCase(DayPhase.Noon, true, 0.0)]
        public async Task 日间提交与真实旧入口完整快照一致_换岗只掷骰一次(DayPhase phase, bool spendTime, double roll)
        {
            var old = Session(phase, roll);
            var current = Session(phase, roll);
            if (spendTime) { await SpendTwo(old); await SpendTwo(current); }
            var identity = current.World;
            var before = current.CaptureState();
            Assert.AreEqual(old.CaptureState().Json, before.Json);
            var oldTraces = await old.EndPhaseAsync();
            Assert.IsEmpty(oldTraces, "日间旧入口不应执行居民或 GM 模型轮次");
            int traceCount = current.Traces.Count;
            int completed = 0;
            current.RuleEventCompleted += _ => completed++;
            var backend = new Transaction();
            backend.CommitAction = write =>
            {
                Assert.IsTrue(current.Busy);
                Assert.IsFalse(write.Applied);
                Assert.AreEqual(before.Json, current.CaptureState().Json, "准备候选不能先清零时间或推进世界");
                Assert.AreEqual(before.Json, backend.Before.Json);
                Assert.AreEqual(spendTime ? 2 : 0, backend.Before.Spent);
                Assert.AreEqual(0, backend.After.Spent);
                Assert.AreEqual(before.PhaseBudget, backend.After.PhaseBudget);
                write.Apply();
                Assert.IsTrue(write.Applied);
                Assert.AreSame(identity, current.World);
                Assert.AreEqual(backend.After.Json, current.CaptureState().Json, "Apply 返回前世界和 session_spent 必须同时到位");
                Assert.AreEqual(current.PhaseBudget, current.TimeLeftInPhase);
                return "committed";
            };

            var actual = current.EndDayPhase(backend);

            Assert.IsNotNull(actual);
            Assert.AreEqual(1, backend.Calls);
            Assert.AreEqual(backend.RequestId, actual.RequestId);
            Assert.AreEqual("phase_start", actual.Label);
            Assert.AreEqual("committed", actual.Receipt.Status);
            Assert.IsTrue(actual.Receipt.Applied);
            Assert.AreEqual(before.Hash, actual.Receipt.BeforeHash);
            Assert.AreEqual(backend.After.Hash, actual.Receipt.AfterHash);
            Assert.AreEqual(old.CaptureState().Json, current.CaptureState().Json, "包括完整世界与已用时间的逐字 JSON 对照");
            Assert.AreSame(identity, current.World);
            Assert.AreEqual(phase == DayPhase.Dawn ? DayPhase.Noon : DayPhase.Dusk, current.World.Phase);
            Assert.AreEqual(before.Restore().Day, current.World.Day);
            Assert.AreEqual(before.Restore().Whelp.Fear, current.World.Whelp.Fear, "换岗不能额外增加恐惧");
            Assert.AreEqual(before.Restore().RollCounter + (phase == DayPhase.Dawn ? 1 : 0), current.World.RollCounter);
            Assert.AreEqual(before.Restore().Version + (phase == DayPhase.Dawn ? 2 : 1), current.World.Version);
            bool captured = phase == DayPhase.Dawn && roll == 0.0;
            Assert.AreEqual(captured ? "osric" : null, current.World.Whelp.CapturedBy);
            Assert.AreEqual(captured, actual.WhelpCaptured);
            Assert.AreEqual(captured ? "castle" : "market", current.World.Whelp.Place);
            Assert.AreEqual(!captured, current.World.Whelp.Following);
            Assert.AreEqual(current.PhaseBudget, current.TimeLeftInPhase);
            Assert.AreEqual(traceCount, current.Traces.Count, "新入口不产生模型调用或 AI trace");
            Assert.AreEqual(1, current.RuleEvents.Count);
            Assert.AreSame(actual, current.RuleEvents[0]);
            Assert.AreEqual(1, completed);
            Assert.IsFalse(current.Busy);
        }

        [Test]
        public async Task 后端拒绝过期状态不回退_世界和剩余时间不变_写入能力关闭()
        {
            var session = Session(roll: 0.0);
            await SpendTwo(session);
            var identity = session.World;
            var before = session.CaptureState();
            int timeLeft = session.TimeLeftInPhase;
            int completed = 0;
            session.RuleEventCompleted += _ => completed++;
            var backend = new Transaction { CommitAction = _ => "stale_state" };

            var rejected = session.EndDayPhase(backend);

            Assert.AreEqual(1, backend.Calls);
            Assert.AreEqual(before.Json, backend.Before.Json);
            Assert.AreEqual(0, backend.After.Spent);
            Assert.AreEqual("stale_state", rejected.Receipt.Status);
            Assert.AreEqual("stale_state", rejected.FailureCode);
            Assert.IsFalse(rejected.Receipt.Applied);
            Assert.IsFalse(backend.Write.Applied);
            Assert.IsEmpty(rejected.Receipt.Effects);
            Assert.IsEmpty(rejected.Changes);
            Assert.AreEqual(before.Hash, rejected.Receipt.AfterHash);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.AreSame(identity, session.World);
            Assert.AreEqual(timeLeft, session.TimeLeftInPhase);
            Assert.IsEmpty(session.RuleEvents);
            Assert.AreEqual(0, completed);
            Assert.IsFalse(session.Busy);
            Assert.Throws<InvalidOperationException>(() => backend.Write.Apply(), "被保存的拒绝能力不能晚到补写");
            Assert.AreEqual(before.Json, session.CaptureState().Json);

            var closed = backend.Write;
            backend.CommitAction = null;
            Assert.IsTrue(session.EndDayPhase(backend).Receipt.Applied, "拒绝不锁死后续提交");
            Assert.AreEqual(2, backend.Calls);
            var committed = session.CaptureState().Json;
            Assert.Throws<InvalidOperationException>(() => closed.Apply());
            Assert.AreEqual(committed, session.CaptureState().Json);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task 后端假报成功或抛异常不补写_关闭能力并释放会话门闩(bool backendThrows)
        {
            var session = Session();
            await SpendTwo(session);
            var identity = session.World;
            var before = session.CaptureState();
            int timeLeft = session.TimeLeftInPhase;
            var failure = new InvalidOperationException("test_backend_failure");
            var backend = new Transaction
            {
                CommitAction = _ =>
                {
                    if (backendThrows) throw failure;
                    return "committed";
                }
            };

            var error = Assert.Throws<InvalidOperationException>(() => session.EndDayPhase(backend));

            if (backendThrows) Assert.AreSame(failure, error);
            else Assert.AreEqual("day_phase_write_contract", error.Message);
            Assert.AreEqual(1, backend.Calls);
            Assert.IsFalse(backend.Write.Applied);
            Assert.AreSame(identity, session.World);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.AreEqual(timeLeft, session.TimeLeftInPhase);
            Assert.IsEmpty(session.RuleEvents);
            Assert.IsFalse(session.Busy);
            Assert.Throws<InvalidOperationException>(() => backend.Write.Apply());
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.IsTrue(session.EndDayPhase(new Transaction()).Receipt.Applied, "异常后的门闩必须允许下一次正常提交");
        }

        [Test]
        public async Task 同一次后端能力只允许同步写一次_重复与保存后晚到写入均拒绝()
        {
            var session = Session();
            await SpendTwo(session);
            var identity = session.World;
            var backend = new Transaction();
            backend.CommitAction = write =>
            {
                write.Apply();
                var once = session.CaptureState().Json;
                Assert.IsTrue(write.Applied);
                Assert.AreEqual(backend.After.Json, once);
                Assert.AreEqual(0, session.CaptureState().Spent);
                Assert.Throws<InvalidOperationException>(() => write.Apply(), "Commit 尚未返回也不能重复 Apply");
                Assert.AreEqual(once, session.CaptureState().Json);
                return "committed";
            };

            Assert.IsTrue(session.EndDayPhase(backend).Receipt.Applied);
            var after = session.CaptureState().Json;
            Assert.AreSame(identity, session.World);
            Assert.AreEqual(1, backend.Calls);
            Assert.AreEqual(1, session.RuleEvents.Count);
            Assert.Throws<InvalidOperationException>(() => backend.Write.Apply(), "Commit 返回后保存的能力必须关闭");
            Assert.AreEqual(after, session.CaptureState().Json);
            Assert.IsFalse(session.Busy);
        }

        [TestCase(DayPhase.Dusk, 1, false)]
        [TestCase(DayPhase.Night, 1, false)]
        [TestCase(DayPhase.Dawn, 4, false)]
        [TestCase(DayPhase.Dawn, 1, true)]
        public async Task 非日间或终局或待确认造物在任何修改前拒绝且不进入后端(DayPhase phase, int day, bool pendingCraft)
        {
            var session = Session();
            await SpendTwo(session);
            if (pendingCraft)
            {
                session.World.PlayerPlace = "workshop";
                session.World.Whelp.Place = "workshop";
                var craft = await session.CraftAsync("用蓝晶石织一盏灯");
                Assert.AreEqual("awaiting_confirmation", craft.Receipt.Status);
                Assert.IsTrue(session.HasPendingCraft);
            }
            session.World.Phase = phase;
            session.World.Day = day;
            var identity = session.World;
            var before = session.CaptureState();
            int timeLeft = session.TimeLeftInPhase;
            int traceCount = session.Traces.Count;
            var backend = new Transaction();

            Assert.IsNull(session.EndDayPhase(backend));

            Assert.AreEqual(0, backend.Calls);
            Assert.IsNull(backend.Write);
            Assert.AreSame(identity, session.World);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.AreEqual(timeLeft, session.TimeLeftInPhase);
            Assert.AreEqual(pendingCraft, session.HasPendingCraft);
            Assert.AreEqual(traceCount, session.Traces.Count);
            Assert.IsEmpty(session.RuleEvents);
            Assert.IsFalse(session.Busy);
        }

        [Test]
        public async Task 会话等待离线提供方时立即拒绝日间推进_不排队不进入后端()
        {
            var session = Session();
            await SpendTwo(session);
            var provider = new BlockingProvider();
            session.Provider = provider;
            var identity = session.World;
            var before = session.CaptureState();
            int timeLeft = session.TimeLeftInPhase;
            var backend = new Transaction();
            var pending = session.SpeakAsync("marta", "你好");
            try
            {
                Assert.AreEqual(1, provider.Calls);
                Assert.IsTrue(session.Busy);
                Assert.IsNull(session.EndDayPhase(backend));
                Assert.AreEqual(0, backend.Calls);
                Assert.AreSame(identity, session.World);
                Assert.AreEqual(before.Json, session.CaptureState().Json);
                Assert.AreEqual(timeLeft, session.TimeLeftInPhase);
                Assert.IsEmpty(session.RuleEvents);
                Assert.IsFalse(pending.IsCompleted);
            }
            finally
            {
                provider.Reply.TrySetResult(new ProviderResult
                {
                    ProviderName = provider.Name, Simulated = true, EstimatedUsd = 0m, Error = "test_release"
                });
                await pending;
            }
            Assert.IsFalse(session.Busy);
            Assert.AreEqual(before.Json, session.CaptureState().Json);
            Assert.IsTrue(session.EndDayPhase(backend).Receipt.Applied);
            Assert.AreEqual(1, backend.Calls, "只有解除忙碌后的显式调用才能进入后端");
        }
    }
}
