using System.Collections.Generic;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepRollTests
    {
        [TestCase(7, new[] { 0.3548, 0.256, 0.6775, 0.6696, 0.0251 })]
        [TestCase(-1, new[] { 0.9435, 0.1111, 0.8544, 0.5631, 0.102 })]
        [TestCase(int.MaxValue, new[] { 0.3755, 0.9559, 0.2864, 0.4079, 0.534 })]
        public void 随机源保留重构前序列_快照恢复接续同一计数(int seed, double[] expected)
        {
            var world = DsContent.NewWorld(seed);
            for (int i = 0; i < expected.Length; i++)
            {
                var restored = DsWorldSnapshot.Capture(world, 0, 4).Restore();
                Assert.AreEqual(expected[i], world.NextRoll());
                Assert.AreEqual(expected[i], restored.NextRoll());
                Assert.AreEqual(i + 1, world.RollCounter);
                Assert.AreEqual(expected[i], DsSeededRoll.Instance.Next(seed, i + 1));
            }
        }

        [Test]
        public void 随机计数溢出仍按原无检查运算()
        {
            var world = DsContent.NewWorld(7); world.RollCounter = int.MaxValue;
            Assert.AreEqual(DsSeededRoll.Instance.Next(7, int.MinValue), world.NextRoll());
            Assert.AreEqual(int.MinValue, world.RollCounter);
        }

        [Test]
        public void 固定骰注入候选事务_提交前不改变原世界_且不污染默认执行器()
        {
            var world = DsContent.NewWorld(7); world.PlayerPlace = "docks";
            var proposal = new DsPlanStep { Verb = "lure", Target = "whelp" }.ToProposal("docks");
            var executor = new DsExecutor(new DsFixedRoll(0.9999));
            string before = DsWorldSnapshot.Capture(world, 0, 4).Hash;
            var plan = executor.Prepare(world, 0, 4, AiTaskKind.Adjudicate, "player", "fixed", proposal);
            Assert.AreEqual(before, DsWorldSnapshot.Capture(world, 0, 4).Hash);
            int spent = 0;
            Assert.IsTrue(executor.Commit(plan, world, ref spent, 4).Applied);
            Assert.AreEqual(1, world.RollCounter);
            Assert.AreEqual(0.256, world.NextRoll());
            Assert.AreSame(DsSeededRoll.Instance, new DsExecutor().RollSource);
            Assert.AreEqual(DsExecutionMode.Live, new DsExecutor().Mode);
        }

        [Test]
        public void 计划模式只跳过未知岗哨前置_正式执行仍拒绝且不掷骰()
        {
            var world = DsContent.NewWorld(7); world.PlayerPlace = "castle";
            world.Whelp.Place = "castle"; world.Whelp.CapturedBy = "osric";
            var proposal = new DsPlanStep { Verb = "rescue", Target = "whelp" }.ToProposal("castle");
            var live = new AiTrace();
            DsRules.Adjudicate(world, proposal, live, new DsFixedRoll(0.9999));
            Assert.AreEqual("place_guarded:castle", live.Verdict.ReasonCode.StableCode);
            Assert.AreEqual(0, world.RollCounter);
            var previewWorld = DsWorldSnapshot.Capture(world, 0, 4).Restore();
            var preview = new AiTrace();
            DsRules.Adjudicate(previewWorld, proposal, preview, new DsFixedRoll(0.9999), DsExecutionMode.PlanPreview);
            Assert.AreEqual(0.9999, preview.Verdict.Roll);
            Assert.AreEqual(1, previewWorld.RollCounter);
            Assert.IsNull(previewWorld.Whelp.CapturedBy);
            Assert.AreEqual("osric", world.Whelp.CapturedBy);
        }

        [Test]
        public void 固定源传到移动巡逻_换岗与居民搜寻_每次只增一个计数()
        {
            var session = new DragonsleepSession(new RuleProvider(), 7, new DsFixedRoll(0.9999));
            var world = session.World; world.PlayerPlace = "workshop";
            world.Whelp.Place = "workshop"; world.Whelp.Following = true; world.Whelp.Hidden = true;
            session.EnterPlace("castle");
            Assert.IsNull(world.Whelp.CapturedBy);
            Assert.AreEqual(1, world.RollCounter);
            DsRules.PhaseStart(world, new AiTrace(), new DsFixedRoll(0.9999));
            Assert.AreEqual(2, world.RollCounter);
            DsRules.AgentPlan(world, "osric", new Dictionary<string, object>
            {
                ["verb"] = "search", ["place"] = "castle", ["target"] = "", ["share"] = new List<object>(), ["reason"] = ""
            }, new AiTrace(), new DsFixedRoll(0.9999));
            Assert.IsNull(world.Whelp.CapturedBy);
            Assert.AreEqual(3, world.RollCounter);
        }
    }
}
