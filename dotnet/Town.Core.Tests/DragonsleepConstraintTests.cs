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
    public class DragonsleepConstraintTests
    {
        sealed class Scripted : IAiProvider
        {
            public readonly Queue<string> Replies = new Queue<string>();
            public bool WaitAtNight;
            public string Name => "constraints-offline";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                string raw = WaitAtNight && packet.Kind == AiTaskKind.AgentPlan
                    ? "{\"verb\":\"wait\",\"place\":\"market\",\"target\":\"\",\"share\":[],\"reason\":\"等待\"}"
                    : WaitAtNight && packet.Kind == AiTaskKind.Direct ? "{\"event\":\"none\",\"intensity\":0,\"reason\":\"平静\"}"
                    : Replies.Dequeue();
                return Task.FromResult(new ProviderResult { RawText = raw, ProviderName = Name, Simulated = true, EstimatedUsd = 0m });
            }
        }

        static DsPlan Draft(params DsPlanStep[] steps)
        {
            var p = new DsPlan { Goal = "局部测试", Summary = "只验证执行条件" }; p.Steps.AddRange(steps); return p;
        }
        static DsPlanStep Observe(string id) => new DsPlanStep { Id = id, Op = "act", Verb = "observe" };
        static DsPlanStep Confide(string id, string target) => new DsPlanStep { Id = id, Op = "act", Verb = "confide",
            Target = target, Place = target == "maren" ? "cathedral" : "docks" };
        static DragonsleepSession Session(Scripted provider)
        {
            var s = new DragonsleepSession(provider, 7); s.World.PlayerPlace = "cathedral";
            s.World.Items["pact_page"].Holder = "player";
            s.World.PlayerKnows.Add("b_maren_page"); s.World.Actors["maren"].Trust = 0.8f;
            s.World.Flags["shown:pact_page:maren"] = s.World.ClockText;
            s.World.Whelp.TrustPlayer = 0.6f;
            return s;
        }
        static void Reply(Scripted p, DsPlan plan) => p.Replies.Enqueue(MiniJson.Write(plan.ToRecord()));

        [Test]
        public async Task 明确不托付_可执行的泄露草稿不能确认_世界和时间不变()
        {
            var p = new Scripted(); var s = Session(p); var before = s.CaptureState().Hash;
            Reply(p, Draft(Confide("1", "maren")));
            var run = new DsPlanRun(s, "请保密", constraints: DsPlanConstraints.KeepPrivate);
            var trace = await run.ProposeAsync();
            Assert.IsTrue(trace.PlanReport.Executable, "动作规则通过不等于玩家条件通过");
            Assert.IsFalse(run.MeetsObjective(trace)); Assert.AreEqual("planning", run.Status);
            Assert.Throws<InvalidOperationException>(() => run.Accept());
            Assert.AreEqual(before, s.CaptureState().Hash); Assert.IsEmpty(run.AcceptedRequestIds);
            StringAssert.Contains("player_constraints", trace.PlanContext);
            Assert.IsFalse(s.World.Flags.ContainsKey("confided:maren"));
        }

        [Test]
        public async Task 默认不加条件_保持既有行为且不偷偷分析中文目标()
        {
            var p = new Scripted(); var s = Session(p); Reply(p, Draft(Confide("1", "maren")));
            var run = new DsPlanRun(s, "这只是文字谁也别告诉"); var trace = await run.ProposeAsync();
            Assert.IsNull(trace.PlanContext); Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.IsFalse(run.Constraints.HasRestrictions);
        }

        [Test]
        public async Task 备选不能借已通过的默认分支绕过条件_拒绝不登记确认()
        {
            var p = new Scripted(); var s = Session(p); var draft = Draft(Observe("1"), Confide("2", "maren"));
            draft.Steps[1].When = "helper=maren";
            var choice = new DsPlanChoice { Id = "helper", Question = "是否托付" };
            choice.Options.Add(new DsPlanOption { Id = "none", Label = "独行" });
            choice.Options.Add(new DsPlanOption { Id = "maren", Label = "梅伦" }); draft.Choices.Add(choice);
            Reply(p, draft); var run = new DsPlanRun(s, "归巢", constraints: DsPlanConstraints.KeepPrivate); await run.ProposeAsync();
            Assert.IsTrue(run.LatestPlanTrace.PlanReport.Branches.All(b => b.Feasible));
            var before = s.CaptureState().Hash;
            Assert.Throws<InvalidOperationException>(() => run.Accept(new Dictionary<string, string> { ["helper"] = "maren" }));
            Assert.AreEqual("awaiting_confirmation", run.Status); Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(before, s.CaptureState().Hash); run.Accept(); Assert.AreEqual("running", run.Status);
        }

        [Test]
        public async Task 只向梅伦托付_不保证保密但绝不自动换皮普()
        {
            var p = new Scripted(); var s = Session(p); Reply(p, Draft(Confide("1", "maren")));
            var run = new DsPlanRun(s, "找帮手", constraints: DsPlanConstraints.Only("maren")); await run.ProposeAsync(); run.Accept();
            var result = await run.StepAsync(); Assert.AreEqual("success", result.Status);
            Assert.IsTrue(s.World.Flags.ContainsKey("ally:maren")); Assert.IsFalse(s.World.Flags.ContainsKey("confided:pip"));
            Assert.IsTrue(result.Receipts.Any(r => r.Applied));
        }

        [Test]
        public async Task 确认后可变步骤被换成越权托付_深夜和异地移动之前拦截()
        {
            var p = new Scripted(); var s = Session(p); Reply(p, Draft(Observe("1"), Observe("2")));
            var run = new DsPlanRun(s, "独行", constraints: DsPlanConstraints.KeepPrivate); await run.ProposeAsync(); run.Accept();
            var step = run.ActiveSteps[0]; step.Verb = "confide"; step.Target = "pip"; step.Place = "docks";
            s.World.Phase = DayPhase.Night;
            var before = s.CaptureState().Hash; var result = await run.StepAsync();
            Assert.AreEqual("player_constraint", result.ReasonCode.Code); Assert.AreEqual("needs_replan", run.Status);
            Assert.IsEmpty(result.RequestIds); Assert.IsEmpty(result.Receipts); Assert.AreEqual(before, s.CaptureState().Hash);
            Assert.AreEqual("void", run.Results.Last().Status); Assert.AreEqual(1, s.Traces.Count);
        }

        [Test]
        public async Task 首案修正与多轮重规划不能丢条件_最新违规反馈不混入隐藏状态()
        {
            var p = new Scripted(); var s = Session(p); var before = s.CaptureState().Hash;
            Reply(p, Draft(Confide("bad1", "maren"))); Reply(p, Draft(Confide("bad2", "pip"))); Reply(p, Draft(Observe("good")));
            var run = new DsPlanRun(s, "独行", constraints: DsPlanConstraints.KeepPrivate);
            await run.ProposeAsync(); var second = await run.RepairProposalAsync();
            StringAssert.Contains("player_constraint", second.PlanContext); StringAssert.Contains("bad1", second.PlanContext);
            var third = await run.ReplanAsync(); StringAssert.Contains("bad2", third.PlanContext);
            StringAssert.DoesNotContain("bad1", third.PlanContext); StringAssert.Contains("allowed_confidants", third.PlanContext);
            Assert.AreEqual("awaiting_confirmation", run.Status); Assert.AreEqual(before, s.CaptureState().Hash);
            Assert.IsFalse(run.Replans[0].NewPlanExecutable); Assert.IsTrue(run.Replans[1].NewPlanExecutable);
        }

        [Test]
        public async Task 确认后编辑提案在确认入口再校验_越权目标不登记()
        {
            var p = new Scripted(); var s = Session(p); Reply(p, Draft(Observe("1")));
            var run = new DsPlanRun(s, "独行", constraints: DsPlanConstraints.KeepPrivate); await run.ProposeAsync();
            run.Plan.Steps[0].Verb = "confide"; run.Plan.Steps[0].Target = "maren";
            Assert.Throws<InvalidOperationException>(() => run.Accept()); Assert.IsEmpty(run.AcceptedRequestIds);
        }

        [Test]
        public async Task 移动回调篡改公开草稿不能替换已经检查的实际步骤()
        {
            var p = new Scripted(); var s = Session(p); var step = Observe("1"); step.Place = "docks";
            Reply(p, Draft(step)); Reply(p, Draft(Observe("unused")));
            var run = new DsPlanRun(s, "独行", constraints: DsPlanConstraints.KeepPrivate); await run.ProposeAsync(); run.Accept();
            s.RuleEventCompleted += e =>
            {
                if (e.Label != "enter:docks") return;
                run.ActiveSteps[0].Verb = "confide"; run.ActiveSteps[0].Target = "pip";
                run.ActiveSteps[0].Laws.Add("unknown_law");
            };
            // RunStep 的固定提供方不消费本脚本队列；应执行冻结后的 observe，不能透露计划。
            var result = await run.StepAsync();
            Assert.AreEqual("success", result.Status); Assert.AreEqual("confide", run.ActiveSteps[0].Verb, "回调确实执行");
            Assert.IsFalse(s.World.Flags.ContainsKey("confided:pip"));
            Assert.IsFalse(s.World.Beliefs.Any(b => b.Owner == "pip" && b.Topic == "plan"));
            Assert.AreEqual("observe", s.Traces.Last().Proposal.Str("verb"));
        }

        [Test]
        public async Task 跨夜回调篡改原草稿不让冻结的合法步骤被误判失效()
        {
            var p = new Scripted { WaitAtNight = true }; var s = Session(p); s.World.Phase = DayPhase.Night;
            Reply(p, Draft(Observe("1")));
            var run = new DsPlanRun(s, "独行", constraints: DsPlanConstraints.KeepPrivate); await run.ProposeAsync(); run.Accept();
            s.TraceCompleted += t =>
            {
                if (t.Kind != AiTaskKind.AgentPlan) return;
                run.ActiveSteps[0].Verb = "lead"; run.ActiveSteps[0].Target = "whelp"; run.ActiveSteps[0].Place = "cathedral";
            };
            var result = await run.StepAsync();
            Assert.AreEqual("success", result.Status, result.Reason);
            Assert.AreEqual("lead", run.ActiveSteps[0].Verb, "夜间回调确实改写了公开草稿");
            Assert.AreEqual("observe", s.Traces.Last().Proposal.Str("verb"));
            Assert.AreEqual(2, s.World.Day); Assert.AreEqual("completed", run.Status);
        }

        [Test]
        public void 条件复制调用方数组且不导出可变集合()
        {
            var ids = new[] { "maren" }; var conditions = DsPlanConstraints.Only(ids); ids[0] = "pip";
            ((List<object>)conditions.ToRecord()["allowed_confidants"]).Clear();
            Assert.IsTrue(conditions.Allows(Confide("1", "maren"))); Assert.IsFalse(conditions.Allows(Confide("2", "pip")));
            Assert.Throws<ArgumentException>(() => DsPlanConstraints.Only(""));
            Assert.Throws<ArgumentNullException>(() => DsPlanConstraints.Only(null));
        }

        [TestCase("请皮普帮忙", true)]
        [TestCase("谁也别告诉", false)]
        public async Task 默认离线规划器也读取明确条件_不能被冲突目标文字覆盖(string goal, bool privateOnly)
        {
            var s = new DragonsleepSession(new RuleProvider(), 7);
            var run = new DsPlanRun(s, goal, true, privateOnly ? DsPlanConstraints.KeepPrivate : DsPlanConstraints.Only("maren"));
            string before = s.CaptureState().Hash; await run.ProposeAsync();
            Assert.AreEqual("awaiting_confirmation", run.Status, run.LatestPlanTrace.Presented);
            Assert.IsTrue(run.Plan.Steps.All(s => run.Constraints.Allows(s)));
            Assert.AreEqual(before, s.CaptureState().Hash);
            if (privateOnly) Assert.IsEmpty(run.Plan.Steps.Where(s => s.Verb == "confide"));
        }

        [Test]
        public async Task 受限请求冻结条件并从安全视角重建_不发送私有信任或可变段落()
        {
            var s = new DragonsleepSession(new RuleProvider(), 7);
            var run = new DsPlanRun(s, "归巢", true, DsPlanConstraints.KeepPrivate); await run.ProposeAsync();
            var packet = run.LatestPlanTrace.Prompt;
            string before = OpenAiDragonsleepProvider.BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch)
                .ToString(Newtonsoft.Json.Formatting.None);
            packet.Sections.Clear(); packet.Instructions = "不能信任的变更";
            s.World.Actors["maren"].Trust = 0.001f;
            s.World.Beliefs.Add(new DsBelief { Id = "private_sentinel", Owner = "maren", Topic = "secret", Text = "不应发送的秘密" });
            string after = OpenAiDragonsleepProvider.BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch)
                .ToString(Newtonsoft.Json.Formatting.None);
            Assert.AreEqual(before, after); StringAssert.Contains(DsPlanConstraints.Version, after);
            StringAssert.DoesNotContain("private_sentinel", after); StringAssert.DoesNotContain("不应发送的秘密", after);
            StringAssert.DoesNotContain("不能信任的变更", after);
            Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(after), 12 * 1024);
            var record = run.ToRecord(); Assert.IsTrue(record.ContainsKey("player_constraints"));
        }
    }
}
