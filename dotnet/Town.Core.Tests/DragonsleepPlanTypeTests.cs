using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepPlanTypeTests
    {
        [Test]
        public void 类型化规划器覆盖全部冻结起点_只在提供方边界转JSON()
        {
            foreach (var c in PlanCases.Frozen())
            {
                var session = new DragonsleepSession(new RuleProvider(), 7); c.Setup(session);
                var perception = ContextBuilder.PlanPerception(session.World, session.TimeLeftInPhase, null);
                DsPlan plan = BaselinePlanner.Plan(perception, c.Input);
                string raw = MiniJson.Write(plan.ToRecord());
                var received = (Dictionary<string, object>)MiniJson.Parse(raw);
                Assert.IsNull(DsPlan.TryParse(received, out var parsed), c.Id);
                Assert.AreEqual(raw, MiniJson.Write(parsed.ToRecord()), c.Id + " 往返字节及键序");
                Assert.AreEqual(plan.Steps.Count, parsed.Steps.Count);
                Assert.AreEqual(plan.Choices.Count, parsed.Choices.Count);
                CollectionAssert.AreEqual(Enumerable.Range(1, plan.Steps.Count).Select(i => "s" + i), plan.Steps.Select(s => s.Id));
            }
        }

        [Test]
        public void 计划记录固定各层键序_空字段数组与until原文不变()
        {
            var plan = new DsPlan { Goal = "g", Summary = "s" };
            var choice = new DsPlanChoice { Id = "helper", Question = "q" };
            choice.Options.Add(new DsPlanOption { Id = "none", Label = "n" });
            choice.Options.Add(new DsPlanOption { Id = "pip", Label = "p" });
            plan.Choices.Add(choice);
            plan.Steps.Add(new DsPlanStep { Id = "s1", Op = "wait", Until = "第3天·黄昏", When = "helper=none" });
            string expected = "{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[{\"id\":\"helper\",\"question\":\"q\",\"options\":[" +
                "{\"id\":\"none\",\"label\":\"n\"},{\"id\":\"pip\",\"label\":\"p\"}]}],\"steps\":[{\"id\":\"s1\",\"op\":\"wait\",\"verb\":\"\"," +
                "\"target\":\"\",\"item\":\"\",\"place\":\"\",\"until\":\"第3天·黄昏\",\"laws\":[],\"cites\":[],\"when\":\"helper=none\",\"why\":\"\"}]}";
            Assert.AreEqual(expected, MiniJson.Write(plan.ToRecord()));
            Assert.AreEqual(1, plan.ActiveSteps(plan.DefaultSelection()).Count);
        }

        [Test]
        public void 提供方边界仍拒绝多余字段_不能用类型化默认值掩盖坏输入()
        {
            var session = new DragonsleepSession(new RuleProvider());
            var plan = BaselinePlanner.Plan(ContextBuilder.PlanPerception(session.World, 4, null), "目标");
            var proposal = plan.ToRecord();
            ((Dictionary<string, object>)((List<object>)proposal["steps"])[0])["extra"] = "不接受";
            string before = session.CaptureState().Hash;
            var executor = new DsExecutor();
            var prepared = executor.Prepare(session.World, 0, 4, AiTaskKind.Plan, "player", "malformed", proposal);
            Assert.AreEqual("schema_invalid", prepared.FailureCode);
            Assert.AreEqual(before, session.CaptureState().Hash);
        }
    }
}
