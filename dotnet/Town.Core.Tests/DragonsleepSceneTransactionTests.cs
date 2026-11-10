using System;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepSceneTransactionTests
    {
        sealed class Transaction : IDsSceneRuleTransaction
        {
            public int Calls;
            public string Status = "committed";
            public DsSceneRuleWrite Write;
            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write)
            {
                Calls++; Write = write;
                Assert.AreEqual(before.Spent, after.Spent);
                Assert.AreEqual(before.PhaseBudget, after.PhaseBudget);
                if (Status == "committed") write.Apply();
                return Status;
            }
        }

        [Test]
        public void 场景移动能力只写一次_世界身份与旧回执完全一致()
        {
            var old = new DragonsleepSession(new RuleProvider());
            var current = new DragonsleepSession(new RuleProvider());
            var identity = current.World; var backend = new Transaction();
            var expected = old.EnterPlace("workshop");
            var actual = current.EnterPlace("workshop", backend);
            Assert.AreSame(identity, current.World);
            Assert.AreEqual(old.CaptureState().Json, current.CaptureState().Json);
            Assert.AreEqual(MiniJson.Write(expected.ToRecord()), MiniJson.Write(actual.ToRecord()));
            Assert.AreEqual(1, backend.Calls);
            Assert.Throws<InvalidOperationException>(() => backend.Write.Apply());
            Assert.IsNull(current.EnterPlace("workshop", backend));
            Assert.AreEqual(1, backend.Calls);
        }

        [Test]
        public void 场景写入前拒绝不回退旧提交_仍可重新尝试()
        {
            var session = new DragonsleepSession(new RuleProvider());
            var before = session.CaptureState().Json;
            var backend = new Transaction { Status = "stale_state" };
            var rejected = session.EnterPlace("workshop", backend);
            Assert.AreEqual("stale_state", rejected.Receipt.Status);
            Assert.AreEqual("stale_state", rejected.FailureCode);
            Assert.IsFalse(backend.Write.Applied);
            Assert.Throws<InvalidOperationException>(() => backend.Write.Apply(), "被拒后能力也必须立即失效");
            Assert.AreEqual(before, session.CaptureState().Json);
            backend.Status = "committed";
            Assert.IsTrue(session.EnterPlace("workshop", backend).Receipt.Applied);
            Assert.AreEqual(2, backend.Calls);
        }

        [Test]
        public void 无变化与重复请求不再次进入场景后端()
        {
            var world = DsContent.NewWorld(); var executor = new DsExecutor(); int spent = 0;
            var backend = new Transaction();
            var none = executor.PrepareRule(world, spent, 4, "none", "enter:none", (_, __) => { });
            Assert.AreEqual("no_change", executor.Commit(none, world, ref spent, 4, backend).Status);
            Assert.AreEqual(0, backend.Calls);
            var move = executor.PrepareRule(world, spent, 4, "move", "enter:workshop", (w, _) => w.PlayerPlace = "workshop");
            Assert.AreEqual("committed", executor.Commit(move, world, ref spent, 4, backend).Status);
            Assert.AreEqual("duplicate", executor.Commit(move, world, ref spent, 4, backend).Status);
            Assert.AreEqual(1, backend.Calls);
            var conflict = executor.PrepareRule(world, spent, 4, "move", "enter:docks", (w, _) => w.PlayerPlace = "docks");
            Assert.AreEqual("request_conflict", executor.Commit(conflict, world, ref spent, 4, backend).Status);
            Assert.AreEqual(1, backend.Calls);
        }

        [Test]
        public void 后端假报成功必须报契约错误_不能悄悄旧路径补写()
        {
            var session = new DragonsleepSession(new RuleProvider());
            var before = session.CaptureState().Json;
            Assert.Throws<InvalidOperationException>(() => session.EnterPlace("workshop", new InvalidTransaction()));
            Assert.AreEqual(before, session.CaptureState().Json);
            Assert.IsFalse(session.Busy);
        }

        sealed class InvalidTransaction : IDsSceneRuleTransaction
        {
            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write) => "committed";
        }

        [Test]
        public void 非移动规则不能借场景后端提交()
        {
            var world = DsContent.NewWorld(); var executor = new DsExecutor(); int spent = 0;
            var before = DsWorldSnapshot.Capture(world, spent, 4).Json; var backend = new Transaction();
            var plan = executor.PrepareRule(world, spent, 4, "phase", "phase_start", (w, _) => w.Advance());
            Assert.Throws<InvalidOperationException>(() => executor.Commit(plan, world, ref spent, 4, backend));
            Assert.AreEqual(before, DsWorldSnapshot.Capture(world, spent, 4).Json);
            Assert.AreEqual(0, backend.Calls);
        }
    }
}
