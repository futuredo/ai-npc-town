using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepExecutorTests
    {
        static Dictionary<string, object> Buy() => (Dictionary<string, object>)MiniJson.Parse("{\"verb\":\"buy\",\"target\":\"marta\",\"item\":\"smoked_fish\",\"place\":\"market\",\"laws\":[],\"difficulty\":0.1,\"narration\":\"递出银币换鱼\"}");

        [Test]
        public void 准备不写世界_提交包含实际物品金钱时间_重复不再扣费()
        {
            var w = DsContent.NewWorld(); var executor = new DsExecutor(); int spent = 0;
            var before = DsWorldSnapshot.Capture(w, spent, 4);
            var plan = executor.Prepare(w, spent, 4, AiTaskKind.Adjudicate, "player", "buy-1", Buy());
            Assert.AreEqual(before.Hash, DsWorldSnapshot.Capture(w, spent, 4).Hash);
            var receipt = executor.Commit(plan, w, ref spent, 4);
            Assert.AreEqual("committed", receipt.Status);
            Assert.AreEqual(9, w.Coins); Assert.AreEqual(2, spent); Assert.AreEqual(1, w.Version);
            Assert.That(receipt.Effects.Select(x => x.Path), Does.Contain("items.smoked_fish.holder"));
            var after = DsWorldSnapshot.Capture(w, spent, 4).Hash;
            Assert.AreEqual("duplicate", executor.Commit(plan, w, ref spent, 4).Status);
            Assert.AreEqual(after, DsWorldSnapshot.Capture(w, spent, 4).Hash);
        }

        [Test]
        public void 外部改状态但未增版本也会拒绝旧计划()
        {
            var w = DsContent.NewWorld(); var executor = new DsExecutor(); int spent = 0;
            var plan = executor.Prepare(w, spent, 4, AiTaskKind.Adjudicate, "player", "buy-1", Buy());
            w.Coins = 1;
            var receipt = executor.Commit(plan, w, ref spent, 4);
            Assert.AreEqual("stale_state", receipt.Status);
            Assert.AreEqual(1, w.Coins); Assert.AreEqual("marta", w.Items["smoked_fish"].Holder); Assert.AreEqual(0, spent);
        }

        [Test]
        public void 规则内部异常不会留下半笔交易或时间消耗()
        {
            var w = DsContent.NewWorld(); var executor = new DsExecutor(); int spent = 0;
            w.Items["smoked_fish"].Tags.Remove("price:3"); w.Items["smoked_fish"].Tags.Add("price:broken");
            string hash = DsWorldSnapshot.Capture(w, spent, 4).Hash;
            var plan = executor.Prepare(w, spent, 4, AiTaskKind.Adjudicate, "player", "bad-price", Buy());
            var receipt = executor.Commit(plan, w, ref spent, 4);
            Assert.AreEqual("execution_error", receipt.Status); Assert.IsFalse(receipt.Applied);
            Assert.AreEqual(hash, DsWorldSnapshot.Capture(w, spent, 4).Hash);
        }

        [Test]
        public void 快照完整往返且不丢浮点精度_所有六机制均可离线重放()
        {
            var s = new DragonsleepSession(new RuleProvider());
            s.World.Actors["pip"].Trust = 0.12345679f;
            var snap = s.CaptureState();
            Assert.AreEqual(snap.Hash, DsWorldSnapshot.Parse(snap.Json).Hash);
            Assert.AreEqual(s.World.Actors["pip"].Trust, snap.Restore().Actors["pip"].Trust);
            void Check(AiTaskKind kind, string actor, string input)
            {
                var before = s.CaptureState();
                var t = s.RunAsync(kind, actor, input).Result;
                if(s.HasPendingCraft) t=s.ConfirmCraft(true);
                var frame = DsReplayFrame.Capture(before, s.CaptureState(), t);
                Assert.IsTrue(DsReplayFrame.Parse(MiniJson.Write(frame.ToRecord())).VerifyAsync().Result, kind + ": " + t.FailureCode);
            }
            Check(AiTaskKind.Dialogue, "marta", "最近有没有看到冒烟的东西？");
            Check(AiTaskKind.Dialogue, "marta", "谢谢你照顾孩子");
            Check(AiTaskKind.Adjudicate, "player", "向玛尔妲买一串熏鱼");
            s.World.PlayerPlace = "workshop";
            Check(AiTaskKind.Craft, "player", "用余烬灰和熏鱼织一个余烬哨");
            Check(AiTaskKind.AgentPlan, "pip", null);
            Check(AiTaskKind.Direct, "gm", null);
        }

        [Test]
        public void 重放拒绝不同规则版本与篡改提示摘要()
        {
            var s = new DragonsleepSession(new RuleProvider()); var before = s.CaptureState();
            var t = s.SpeakAsync("marta", "你好").Result;
            var frame = DsReplayFrame.Capture(before, s.CaptureState(), t);
            frame.PromptHash = "tampered";
            Assert.IsFalse(frame.VerifyAsync().Result);
            Assert.IsTrue(frame.VerifyExecutionAsync().Result,"执行回放不把提示迭代当作世界执行错误");
            frame.RulesVersion = "future-rules";
            Assert.IsFalse(frame.VerifyAsync().Result);
            Assert.IsFalse(frame.VerifyExecutionAsync().Result,"执行回放也不能绕过规则版本");
        }

        [Test]
        public void 造物必须确认_取消不消耗_确认不再调用模型_过期不能提交()
        {
            var s=new DragonsleepSession(new RuleProvider());s.World.PlayerPlace="workshop";
            var before=s.CaptureState();
            var t=s.CraftAsync("用蓝晶石织一盏灯").Result;
            Assert.IsTrue(s.HasPendingCraft);Assert.AreEqual("awaiting_confirmation",t.Receipt.Status);
            Assert.AreEqual(before.Hash,s.CaptureState().Hash);
            Assert.IsTrue(DsReplayFrame.Capture(before,s.CaptureState(),t).VerifyAsync().Result);
            t=s.ConfirmCraft(false);Assert.AreEqual(before.Hash,s.CaptureState().Hash);
            Assert.IsTrue(DsReplayFrame.Capture(before,s.CaptureState(),t).VerifyAsync().Result);
            s.CraftAsync("用蓝晶石织一盏灯").Wait();t=s.ConfirmCraft(true);
            Assert.AreEqual(2,s.Traces.Count,"确认不增加模型调用或重复 trace");
            Assert.AreEqual("consumed",s.World.Items["crystal_blue"].Holder);
            Assert.IsNull(s.ConfirmCraft(true));
            var stale=new DragonsleepSession(new RuleProvider());stale.World.PlayerPlace="workshop";
            stale.CraftAsync("用蓝晶石织一盏灯").Wait();stale.World.Items["crystal_blue"].Holder="consumed";
            var afterChange=stale.CaptureState().Hash;
            Assert.AreEqual("stale_state",stale.ConfirmCraft(true).FailureCode);
            Assert.AreEqual(afterChange,stale.CaptureState().Hash);
        }

        [Test]
        public void 缺失字段和非有限数值不能绕过结构验证()
        {
            var p = Buy(); p.Remove("laws");
            Assert.IsNotNull(DsProposalValidator.Check(AiTaskKind.Adjudicate, p));
            Assert.IsFalse(MiniJson.TryParseObject("{\"n\":1e999}", out _, out _));
            Assert.IsFalse(MiniJson.TryParseObject("{\"verb\":\"wait\",\"verb\":\"take\"}", out _, out _));
        }

        sealed class Fixture : IAiProvider
        {
            public string Json; public string Name => "fixture"; public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket p, CancellationToken ct) => Task.FromResult(new ProviderResult { ProviderName = Name, RawText = Json, Simulated = true, EstimatedUsd = 0 });
        }

        [Test]
        public void 失败的递物不会转移物品_不可食用的物品不能喂龙()
        {
            var p = new Fixture { Json = "{\"verb\":\"give\",\"target\":\"marta\",\"item\":\"iron_lock\",\"place\":\"market\",\"laws\":[],\"difficulty\":1,\"narration\":\"递给她\"}" };
            bool failed = false;
            for (int seed = 1; seed < 20 && !failed; seed++)
            {
                var s = new DragonsleepSession(p, seed); s.World.Items["iron_lock"].Holder = "player";
                var t = s.ActAsync("递出铁锁").Result;
                if (t.Verdict.Kind != VerdictKind.Rejected) continue;
                failed = true; Assert.AreEqual("player", s.World.Items["iron_lock"].Holder);
            }
            Assert.IsTrue(failed);
            var feed = new DragonsleepSession(new RuleProvider()); feed.World.PlayerPlace = "docks"; feed.World.Items["iron_lock"].Holder = "player";
            var trace = feed.ActAsync("把铁锁喂给雏龙").Result;
            Assert.AreEqual(VerdictKind.Rejected, trace.Verdict.Kind); Assert.AreEqual(0, trace.Receipt.TimeCost);
            Assert.AreEqual("player", feed.World.Items["iron_lock"].Holder);
        }
    }
}
