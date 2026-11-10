using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.OpenAI;

namespace Town.Core.Tests
{
    /// <summary>第3天黄昏的晚救援回归；全部离线，本地夹具不是从全新开局游玩的验收。</summary>
    public sealed class DragonsleepLateRescueTests
    {
        const string Goal = "请皮普继续帮忙，把小烬安全送回母亲身边";

        static DragonsleepSession Caged(int left = 4, bool cloak = true, double roll = .9999,
            int day = 3, DayPhase phase = DayPhase.Dusk)
        {
            var session = new DragonsleepSession(new RuleProvider(), 7, new DsFixedRoll(roll));
            var w = session.World;
            // 明确设置本地中途状态：玩家已目睹关押、学过换岗规律，皮普此前已公开答应帮助。
            w.Day = day; w.Phase = phase; w.PlayerPlace = "skull_hill";
            w.Whelp.Place = "castle"; w.Whelp.CapturedBy = "osric"; w.Whelp.Following = false;
            w.Whelp.TrustPlayer = .6f; w.Whelp.Fear = .2f; w.Whelp.Hidden = true;
            w.Flags["seen_by_guards"] = w.ClockText;
            w.Flags["ally:pip"] = w.ClockText;
            w.PlayerKnows.Add("law:law_guards_dusk");
            w.Items["travel_cloak"].Holder = cloak ? "player" : "place:workshop";
            // 随身原件仅供保留性断言；不因此开放其他居民的秘密或未知规律。
            w.Items["pact_page"].Holder = "player";
            w.PlayerKnows.Add("b_maren_page");
            session.RestoreState(DsWorldSnapshot.Capture(w, 4 - left, 4));
            return session;
        }

        static DsPlanRun Run(DragonsleepSession session) =>
            new DsPlanRun(session, Goal, true, DsPlanConstraints.WithHelper("pip"));

        static Dictionary<string, object> View(DragonsleepSession session)
        {
            string context = MiniJson.Write(new Dictionary<string, object>
            {
                ["objective"] = "homecoming", ["player_constraints"] = DsPlanConstraints.WithHelper("pip").ToRecord(),
            });
            return TaskPerceptionView.Capture(session.World, AiTaskKind.Plan, "player", session.TimeLeftInPhase, context).ToDictionary();
        }

        static string Json(DsPlan plan) => MiniJson.Write(plan.ToRecord());

        static void AssertLateRoute(DsPlan plan, bool cloak = true)
        {
            string[] expected = cloak ? new[] { "rescue@castle", "hide@skull_hill", "wait@skull_hill" }
                : new[] { "rescue@castle", "wait@skull_hill" };
            CollectionAssert.AreEqual(expected, plan.Steps.Select(s => (s.Op == "act" ? s.Verb : s.Op) + "@" + s.Place));
            Assert.IsEmpty(plan.Choices);
            Assert.IsTrue(plan.Steps.All(s => s.When == "" && s.Place != "workshop" && s.Verb != "confide"));
            Assert.AreEqual("whelp", plan.Steps[0].Target);
            Assert.AreEqual("第4天·清晨", plan.Steps.Single(s => s.Op == "wait").Until, "不以深夜作为等待终点");
            if (cloak)
            {
                var hide = plan.Steps.Single(s => s.Verb == "hide");
                Assert.AreEqual("whelp", hide.Target); Assert.AreEqual("travel_cloak", hide.Item);
            }
        }

        static async Task FinishConfirmed(DsPlanRun run)
        {
            for (int i = 0; i < 4 && run.Status == "running"; i++) await run.StepAsync();
            Assert.AreEqual("completed", run.Status, run.LastReason);
            Assert.IsTrue(run.Session.World.Whelp.WithMother);
            Assert.IsTrue(run.Session.World.FinalDawn);
            Assert.AreEqual(4, run.Session.World.Day); Assert.AreEqual(DayPhase.Dawn, run.Session.World.Phase);
            Assert.AreEqual("skull_hill", run.Session.World.Whelp.Place);
            Assert.IsTrue(run.Session.World.Flags.ContainsKey("ally:pip"));
            Assert.AreEqual("player", run.Session.World.Items["pact_page"].Holder);
            Assert.IsTrue(run.Results.All(r => r.Status == "success"));
            Assert.AreEqual(true, run.ToRecord()["objective_achieved"]);
            Assert.IsTrue(run.Session.Traces.All(t => t.Provider == null || t.Provider.Simulated));
        }

        [Test]
        public async Task 第三天黄昏四点_安全感知生成直达路线_预演与确认均不写世界()
        {
            var session = Caged(); var before = session.CaptureState().Hash;
            var view = View(session);
            Assert.AreEqual("caged_castle", view.Str("whelp_status"));
            Assert.AreEqual(4, view.Num("time_left_in_phase"));
            var safePlan = BaselinePlanner.Plan(view, Goal);
            AssertLateRoute(safePlan);
            var run = Run(session); var trace = await run.ProposeAsync();
            Assert.AreEqual(Json(safePlan), Json(trace.PlanReport.Plan));
            Assert.IsNull(trace.FailureCode); Assert.IsEmpty(trace.PlanReport.InvalidReferences);
            Assert.IsTrue(trace.PlanReport.Executable); Assert.IsTrue(trace.PlanReport.Default.ReachesGoal);
            Assert.IsTrue(run.MeetsObjective(trace)); Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.AreEqual("plan_only", trace.Receipt.Status);
            Assert.AreEqual(StageStatus.Skipped, trace.Stage(AiStage.Commit).Status);
            Assert.AreEqual(before, session.CaptureState().Hash);
            run.Accept();
            Assert.AreEqual("running", run.Status); Assert.AreEqual(1, run.AcceptedRequestIds.Count);
            Assert.AreEqual(before, session.CaptureState().Hash, "确认不等于执行救援或移动");
            Assert.IsEmpty(run.Results); Assert.IsEmpty(session.RuleEvents);
            Assert.AreEqual(1, session.Traces.Count);
        }

        [Test]
        public async Task 成功固定骰逐步提交_先救出再在龙骨岗遮盖_终局与母亲团聚且皮普仍为盟友()
        {
            var session = Caged(); var run = Run(session);
            await run.ProposeAsync(); run.Accept(); AssertLateRoute(run.Plan);
            var rescue = await run.StepAsync();
            Assert.AreEqual("success", rescue.Status, rescue.Reason);
            Assert.IsTrue(rescue.Receipts.Any(r => r.Applied && r.TimeCost == 2));
            Assert.IsNull(session.World.Whelp.CapturedBy); Assert.IsTrue(session.World.Whelp.Following);
            Assert.IsFalse(session.World.Whelp.Hidden); Assert.AreEqual("castle", session.World.PlayerPlace);
            Assert.AreEqual(2, session.TimeLeftInPhase);
            var hide = await run.StepAsync();
            Assert.AreEqual("success", hide.Status, hide.Reason);
            Assert.AreEqual("skull_hill", session.World.PlayerPlace); Assert.IsTrue(session.World.Whelp.Hidden);
            Assert.AreEqual(DayPhase.Night, session.World.Phase);
            await FinishConfirmed(run);
            Assert.IsFalse(session.RuleEvents.Any(e => e.Label == "enter:workshop"));
            Assert.IsFalse(session.Traces.Any(t => t.Proposal?.Str("verb") == "confide"));
            Assert.IsTrue(session.Traces.Any(t => t.Kind == AiTaskKind.Direct && t.Proposal?.Str("event") == "mother_descends"));
        }

        [Test]
        public async Task 仅剩两点仍提同一路线但预演不可达_拒绝确认且不自动推进()
        {
            var session = Caged(left: 2); var before = session.CaptureState().Hash;
            var run = Run(session); var trace = await run.ProposeAsync();
            AssertLateRoute(trace.PlanReport.Plan);
            Assert.AreEqual(Json(BaselinePlanner.Plan(View(Caged()), Goal)), Json(trace.PlanReport.Plan));
            Assert.IsNull(trace.FailureCode); Assert.IsFalse(trace.PlanReport.Executable);
            Assert.IsFalse(trace.PlanReport.Default.ReachesGoal); Assert.IsFalse(run.MeetsObjective(trace));
            Assert.AreEqual("planning", run.Status);
            Assert.Throws<InvalidOperationException>(() => run.Accept());
            Assert.ThrowsAsync<InvalidOperationException>(async () => { await run.StepAsync(); });
            Assert.IsEmpty(run.AcceptedRequestIds); Assert.IsEmpty(run.Results); Assert.IsEmpty(session.RuleEvents);
            Assert.AreEqual(before, session.CaptureState().Hash);
            Assert.AreEqual(2, session.TimeLeftInPhase); Assert.AreEqual(1, session.Traces.Count);
            Assert.AreEqual("osric", session.World.Whelp.CapturedBy);
        }

        [Test]
        public async Task 无随身披风_仅救援再上龙骨岗等待_成功固定骰可实际团聚()
        {
            var session = Caged(cloak: false); var run = Run(session);
            var trace = await run.ProposeAsync();
            AssertLateRoute(trace.PlanReport.Plan, false);
            Assert.IsTrue(trace.PlanReport.Executable); Assert.IsTrue(trace.PlanReport.Default.ReachesGoal);
            Assert.IsFalse(trace.PlanReport.Plan.Steps.Any(s => s.Item == "travel_cloak"));
            run.Accept(); await FinishConfirmed(run);
            Assert.IsFalse(session.World.Whelp.Hidden, "无披风不凭空补做遮盖");
            Assert.AreEqual("place:workshop", session.World.Items["travel_cloak"].Holder);
        }

        [TestCase(DayPhase.Noon)]
        [TestCase(DayPhase.Dusk)]
        public void 第二天笼中仍使用旧工坊模板_不提前套用末日黄昏路线(DayPhase phase)
        {
            var session = Caged(day: 2, phase: phase);
            var plan = BaselinePlanner.Plan(View(session), Goal);
            Assert.AreEqual("workshop", plan.Steps.Single(s => s.Verb == "hide").Place);
            Assert.IsTrue(plan.Steps.Any(s => s.Op == "wait" && s.Place == "workshop" && s.Until == "第3天·黄昏"));
            Assert.AreEqual("skull_hill", plan.Steps.Last().Place);
            Assert.AreEqual("第4天·清晨", plan.Steps.Last().Until);
            if (phase == DayPhase.Noon)
            {
                Assert.AreEqual("wait", plan.Steps[0].Op); Assert.AreEqual("第2天·黄昏", plan.Steps[0].Until);
            }
            else Assert.AreEqual("rescue", plan.Steps[0].Verb);
        }

        [Test]
        public void 仅异地居民隐藏位置与私有信任恐惧不同_安全感知与计划均相同()
        {
            var first = Caged(); var second = Caged();
            foreach (string id in first.World.Actors.Keys)
            {
                first.World.Actors[id].Place = "market"; second.World.Actors[id].Place = "catacombs";
                first.World.Actors[id].Trust = .01f; second.World.Actors[id].Trust = .99f;
                first.World.Actors[id].Fear = .98f; second.World.Actors[id].Fear = .02f;
            }
            Assert.AreNotEqual(first.CaptureState().Hash, second.CaptureState().Hash);
            var a = View(first); var b = View(second);
            Assert.AreEqual(MiniJson.Write(a), MiniJson.Write(b));
            Assert.AreEqual(Json(BaselinePlanner.Plan(a, Goal)), Json(BaselinePlanner.Plan(b, Goal)));
            AssertLateRoute(BaselinePlanner.Plan(a, Goal));
        }

        [Test]
        public async Task 末段上下文只扩显式合作协议_发送体限长且重规划保留剩余时间()
        {
            var session = Caged(roll: .0001); var run = Run(session);
            var first = await run.ProposeAsync();
            var context = MiniJson.Parse(first.PlanContext) as Dictionary<string, object>;
            var deadline = context["deadline_recovery"] as Dictionary<string, object>;
            Assert.AreEqual("current_player_observation", deadline.Str("scope"));
            Assert.AreEqual(4, deadline.Num("time_left_in_phase"));
            Assert.IsFalse(context.ContainsKey("actors"));
            string payload = OpenAiDragonsleepProvider.BuildPayload(first.Prompt, OpenAiRunProfile.SixMechanismResearch)
                .ToString(Newtonsoft.Json.Formatting.None);
            Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(payload), 12 * 1024);
            run.Accept(); await run.StepAsync();
            var repaired = await run.ReplanAsync();
            context = MiniJson.Parse(repaired.PlanContext) as Dictionary<string, object>;
            deadline = context["deadline_recovery"] as Dictionary<string, object>;
            Assert.AreEqual(2, deadline.Num("time_left_in_phase"));
            var old = new DsPlanRun(Caged(), Goal, true, DsPlanConstraints.Only("pip"));
            Assert.IsFalse((await old.ProposeAsync()).PlanContext.Contains("deadline_recovery"), "v1 不暗加新请求字段");
            var earlier = Run(Caged(day: 2));
            Assert.IsFalse((await earlier.ProposeAsync()).PlanContext.Contains("deadline_recovery"));
        }

        [Test]
        public async Task 低骰首次救援失败仍耗时且保留原件_后续作废_第二草稿不可谎报完成或自动重试()
        {
            var session = Caged(roll: .0001); var run = Run(session);
            Assert.IsTrue((await run.ProposeAsync()).PlanReport.Default.ReachesGoal, "预演的成功假设不替代真实掷骰");
            run.Accept(); var failed = await run.StepAsync();
            Assert.AreEqual("failed", failed.Status); Assert.AreEqual(DsPlanFailure.Dice, failed.Failure);
            Assert.IsTrue(failed.Receipts.Any(r => r.Applied && r.TimeCost == 2));
            Assert.AreEqual("needs_replan", run.Status); Assert.AreEqual(2, session.TimeLeftInPhase);
            Assert.AreEqual(3, session.World.Day); Assert.AreEqual(DayPhase.Dusk, session.World.Phase);
            Assert.AreEqual("osric", session.World.Whelp.CapturedBy);
            Assert.AreEqual(.3f, session.World.Whelp.Fear, .0001f);
            Assert.IsTrue(session.World.Flags.ContainsKey("rescue_failed"));
            Assert.AreEqual("player", session.World.Items["pact_page"].Holder);
            Assert.AreEqual(2, run.Results.Skip(1).Count()); Assert.IsTrue(run.Results.Skip(1).All(r => r.Status == "void"));
            Assert.AreEqual(1, session.Traces.Count(t => t.Kind == AiTaskKind.Adjudicate));
            string before = session.CaptureState().Hash; int results = run.Results.Count, events = session.RuleEvents.Count;
            var second = await run.ReplanAsync();
            AssertLateRoute(second.PlanReport.Plan);
            Assert.IsFalse(second.PlanReport.Default.ReachesGoal); Assert.IsFalse(run.MeetsObjective(second));
            Assert.IsFalse(run.Replans.Single().NewPlanExecutable); Assert.AreEqual("rejected", run.Replans.Single().Decision);
            Assert.AreEqual("needs_replan", run.Status); Assert.IsFalse(session.World.Whelp.WithMother);
            Assert.AreEqual(false, run.ToRecord()["objective_achieved"]);
            Assert.Throws<InvalidOperationException>(() => run.Accept());
            Assert.ThrowsAsync<InvalidOperationException>(async () => { await run.StepAsync(); });
            Assert.AreEqual(before, session.CaptureState().Hash, "失败后的规划只提案，不能消费剩余时间自动重救");
            Assert.AreEqual(results, run.Results.Count); Assert.AreEqual(events, session.RuleEvents.Count);
            Assert.AreEqual(1, run.AcceptedRequestIds.Count); Assert.AreEqual(2, run.PlanTraces.Count);
            Assert.AreEqual(1, session.Traces.Count(t => t.Kind == AiTaskKind.Adjudicate));
        }
    }
}
