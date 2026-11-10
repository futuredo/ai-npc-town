using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.OpenAI;

namespace Town.Core.Tests
{
    public class DragonsleepFrameworkTests
    {
        sealed class FirstPlan : IAiProvider
        {
            string _raw;
            public FirstPlan(DsPlan plan) { _raw = MiniJson.Write(plan.ToRecord()); }
            public string Name => "离线脚本";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                if (_raw == null) return new RuleProvider().CompleteAsync(packet, ct);
                string raw = _raw; _raw = null;
                return Task.FromResult(new ProviderResult { ProviderName = Name, RawText = raw, Simulated = true });
            }
        }

        [Test]
        public void 结构化时刻往返全部合法天数时段_保持原字节与序号()
        {
            for (int day = 1; day <= 4; day++)
                foreach (DayPhase phase in Enum.GetValues(typeof(DayPhase)))
                {
                    var moment = new DsMoment(day, phase);
                    Assert.IsTrue(DsMoment.TryParse(moment.ToString(), out var parsed));
                    Assert.AreEqual(day, parsed.Day);
                    Assert.AreEqual(phase, parsed.Phase);
                    Assert.AreEqual((day - 1) * 4 + (int)phase, parsed.Index);
                    var world = DsContent.NewWorld(); world.Day = day; world.Phase = phase;
                    Assert.AreEqual(world.ClockText, moment.ToString());
                    Assert.AreEqual(world.TimeIndex, moment.Index);
                }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("第0天·清晨")]
        [TestCase("第5天·清晨")]
        [TestCase("第01天·清晨")]
        [TestCase("第2天·早晨")]
        [TestCase(" 第1天·清晨")]
        public void 时刻边界保留原非法输入的拒绝范围(string text)
        {
            Assert.IsFalse(DsMoment.TryParse(text, out var moment));
            Assert.AreEqual(0, moment.Day);
            Assert.IsFalse(DsPlanValidator.TryClock(text, out int day, out var phase));
            Assert.AreEqual(0, day); Assert.AreEqual(DayPhase.Dawn, phase);
        }

        [TestCase("maren", "catacombs", "cathedral")]
        [TestCase("pip", "market", "docks")]
        public void 缺少当前观察时离线重规划按原因码换常去处_不读取中文显示(string actor, string alternate, string normal)
        {
            var s = new DragonsleepSession(new RuleProvider());
            s.World.Items["pact_page"].Holder = "player";
            var per = ContextBuilder.PlanPerception(s.World, 4, null);
            per.Remove("people_here"); // 本测试隔离旧失败码回退；当前观察优先由独立观察测试覆盖。
            var local = new DsPlanLocalContext(new DsReason("actor_absent", actor));
            var plan = BaselinePlanner.Plan(per, "请梅伦帮忙", local);
            Assert.IsTrue(plan.Steps.Where(x => x.Target == actor).All(x => x.Place == alternate));
            per["replan"] = new Dictionary<string, object> { ["reason"] = "梅伦修女不在这里；皮普不在这里" };
            var ordinary = BaselinePlanner.Plan(per, "请梅伦帮忙");
            Assert.IsTrue(ordinary.Steps.Where(x => x.Target == actor).All(x => x.Place == normal), "中文不再控制分支");
        }

        [Test]
        public void 裁决原因码不受角色显示名影响_且不进入旧轨迹()
        {
            var w = DsContent.NewWorld();
            w.Actors["maren"].Name = "任意显示名";
            var t = new AiTrace();
            DsRules.Adjudicate(w, new DsPlanStep { Verb = "confide", Target = "maren" }.ToProposal(w.PlayerPlace), t);
            Assert.AreEqual("actor_absent:maren", t.Verdict.ReasonCode.StableCode);
            Assert.AreEqual("任意显示名不在这里", t.Verdict.Reason);
            StringAssert.DoesNotContain("actor_absent", MiniJson.Write(t.ToRecord()));

            w.Items["travel_cloak"].Holder = "place:catacombs";
            DsRules.Adjudicate(w, new DsPlanStep { Verb = "hide", Target = "whelp", Item = "travel_cloak" }.ToProposal(w.PlayerPlace), t);
            Assert.AreEqual("item_missing:travel_cloak", t.Verdict.ReasonCode.StableCode);
        }

        [Test]
        public void 异地静态拒绝仍遮蔽位置_显示名不再影响隐私判断()
        {
            var plan = new DsPlan { Goal = "目标", Summary = "测试" };
            plan.Steps.Add(new DsPlanStep { Id = "remote", Op = "act", Verb = "confide", Target = "maren", Place = "docks" });
            var s = new DragonsleepSession(new FirstPlan(plan));
            var run = new DsPlanRun(s, "目标");
            run.ProposeAsync().Wait();
            var report = run.LatestPlanTrace.PlanReport;
            Assert.AreEqual("planning", run.Status);
            Assert.AreEqual("actor_absent:maren", report.Default.ReasonCode.StableCode);
            report.Default.Reason = "任意文案，不含旧关键词";
            var t = run.RepairProposalAsync().Result;
            StringAssert.Contains("该步骤不能保证目标在场", t.PlanContext);
            StringAssert.DoesNotContain("任意文案", t.PlanContext);
            var rebuilt = ContextBuilder.BuildTask(t.Prompt.TaskSnapshot, t.Prompt.OriginalPlayerInput);
            Assert.AreEqual(DsReplayFrame.Fingerprint(rebuilt), DsReplayFrame.Fingerprint(t.Prompt));
            Assert.AreEqual(OpenAiDragonsleepProvider.BuildPayload(rebuilt, OpenAiRunProfile.SixMechanismResearch).ToString(),
                OpenAiDragonsleepProvider.BuildPayload(t.Prompt, OpenAiRunProfile.SixMechanismResearch).ToString(), "仅构造请求，不联网");
        }
    }
}
