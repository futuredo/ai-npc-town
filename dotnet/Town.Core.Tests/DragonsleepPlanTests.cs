using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>M2 多步计划、整体校验、逐步执行、三类失败重规划与求助取舍。全部离线；不读取密钥、不发网络请求。</summary>
    public class DragonsleepPlanTests
    {
        /// <summary>队列里有脚本就用脚本，否则交给离线规则（夜里居民与 GM 照常行动）。</summary>
        sealed class Scripted : IAiProvider
        {
            public readonly Queue<string> Replies = new Queue<string>();
            readonly RuleProvider _rules = new RuleProvider();
            public string Name => "scripted";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
                Replies.Count > 0 ? Task.FromResult(new ProviderResult { ProviderName = Name, RawText = Replies.Dequeue(), Simulated = true })
                                  : _rules.CompleteAsync(packet, ct);
        }

        static string Step(string id, string op, string verb = "", string target = "", string item = "", string place = "",
            string until = "", string laws = "", string cites = "", string when = "") =>
            "{\"id\":\"" + id + "\",\"op\":\"" + op + "\",\"verb\":\"" + verb + "\",\"target\":\"" + target + "\",\"item\":\"" + item +
            "\",\"place\":\"" + place + "\",\"until\":\"" + until + "\",\"laws\":[" + laws + "],\"cites\":[" + cites + "],\"when\":\"" + when + "\",\"why\":\"\"}";

        static string Plan(string steps, string choices = "") =>
            "{\"goal\":\"把小烬送回母亲身边\",\"summary\":\"测试计划\",\"choices\":[" + choices + "],\"steps\":[" + steps + "]}";

        static string Choice(string id, params string[] options) =>
            "{\"id\":\"" + id + "\",\"question\":\"选哪个？\",\"options\":[" + string.Join(",", options.Select(o => "{\"id\":\"" + o + "\",\"label\":\"" + o + "\"}")) + "]}";

        static string Fixed(string ev) => "{\"event\":\"" + ev + "\",\"intensity\":0.8,\"reason\":\"测试\"}";

        static void Informed(DsWorld w)
        { w.PlayerKnows.Add("b_pip_whelp_place"); w.PlayerKnows.Add("b_pip_fish"); w.PlayerKnows.Add("law:law_whelp_fish"); }

        static void Follow(DsWorld w, bool cloaked)
        {
            w.Whelp.Place = w.PlayerPlace; w.Whelp.Following = true; w.Whelp.TrustPlayer = 0.6f; w.Whelp.Fear = 0.4f; w.Whelp.Hidden = cloaked;
            if (cloaked) w.Flags["whelp_hidden_by_player"] = "第1天·清晨";
        }

        static void Caged(DsWorld w)
        {
            Informed(w);
            w.Day = 2; w.Phase = DayPhase.Noon; w.PlayerPlace = "market"; w.PlayerKnows.Add("law:law_guards_dusk");
            w.Whelp.CapturedBy = "osric"; w.Whelp.Place = "castle"; w.Whelp.Hidden = false; w.Whelp.TrustPlayer = 0.6f;
            w.Flags["seen_by_guards"] = "第1天·黄昏";
        }

        static void PactEscort(DsWorld w)
        {
            Informed(w);
            w.Phase = DayPhase.Noon; w.PlayerPlace = "docks"; Follow(w, true);
            w.Items["pact_page"].Holder = "player"; w.PlayerKnows.Add("b_maren_page"); w.PlayerKnows.Add("b_maren_pact");
        }

        // ---------------- 计划提案与整体校验 ----------------

        [Test]
        public void 基线首个计划通过整体校验_只提案不改变世界()
        {
            var s = new DragonsleepSession(new RuleProvider(), 11);
            Informed(s.World);
            var before = s.CaptureState().Hash;
            int time = s.TimeLeftInPhase;
            var t = s.PlanAsync("把小烬安全送回母亲身边，避开卫兵。").Result;
            Assert.IsNull(t.FailureCode, t.Presented);
            Assert.IsTrue(t.PlanReport.Executable, t.Presented);
            Assert.IsTrue(t.PlanReport.Default.ReachesGoal, "试运行（假设掷骰都成功）应能走到团聚");
            Assert.AreEqual(0, t.PlanReport.InvalidReferences.Count);
            Assert.AreEqual(before, s.CaptureState().Hash, "计划不改变世界");
            Assert.AreEqual(time, s.TimeLeftInPhase, "规划不耗时间");
            Assert.AreEqual("plan_only", t.Receipt.Status);
            Assert.AreEqual(StageStatus.Skipped, t.Stage(AiStage.Commit).Status);
            Assert.IsTrue(t.PlanReport.Plan.Steps.All(x => DsPlan.Ops.Contains(x.Op)));
            Assert.IsTrue(t.PlanReport.Plan.Choices.Any(c => c.Id == "helper"), "求助对象作为关键取舍交给玩家");
        }

        [Test]
        public void 引用玩家不知道的事实_物件或规律会被拦截_不能确认执行()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p, 3);
            var before = s.CaptureState().Hash;
            p.Replies.Enqueue(Plan(
                Step("s1", "act", "search", place: "catacombs", cites: "\"b_maren_page\"") + "," +
                Step("s2", "act", "show", "maren", "pact_page", "cathedral", laws: "\"law_guards_dusk\"") + "," +
                Step("s3", "go", place: "skull_hill", cites: "\"guard:skull_hill\"")));
            var run = new DsPlanRun(s, "送小烬回家");
            var t = run.ProposeAsync().Result;
            Assert.AreEqual("invalid_reference", t.FailureCode);
            Assert.GreaterOrEqual(t.PlanReport.InvalidReferences.Count, 4, string.Join("；", t.PlanReport.InvalidReferences));
            Assert.That(string.Join("；", t.PlanReport.InvalidReferences), Does.Contain("guard:skull_hill"));
            Assert.AreEqual(before, s.CaptureState().Hash);
            Assert.AreEqual("planning", run.Status);
            Assert.Throws<InvalidOperationException>(() => run.Accept());
            Assert.AreEqual(0, run.AcceptedRequestIds.Count, "含无效引用的计划从未被确认");
        }

        [TestCase("{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[],\"steps\":[{\"id\":\"s1\",\"op\":\"teleport\",\"verb\":\"\",\"target\":\"\",\"item\":\"\",\"place\":\"skull_hill\",\"until\":\"\",\"laws\":[],\"cites\":[],\"when\":\"\",\"why\":\"\"}]}")]
        [TestCase("{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[],\"steps\":[{\"id\":\"s1\",\"op\":\"act\",\"verb\":\"observe\",\"target\":\"\",\"item\":\"\",\"place\":\"\",\"until\":\"\",\"laws\":[],\"cites\":[],\"when\":\"helper=pip\",\"why\":\"\"}]}")]
        [TestCase("{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[],\"steps\":[{\"id\":\"s1\",\"op\":\"wait\",\"verb\":\"\",\"target\":\"\",\"item\":\"\",\"place\":\"\",\"until\":\"第2天·深夜\",\"laws\":[],\"cites\":[],\"when\":\"\",\"why\":\"\"}]}")]
        [TestCase("{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[],\"steps\":[],\"extra\":1}")]
        [TestCase("{\"goal\":\"g\",\"summary\":\"s\",\"choices\":[],\"steps\":[]}")]
        public void 结构错误的计划被拒绝_世界不变(string raw)
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            var before = s.CaptureState().Hash;
            p.Replies.Enqueue(raw);
            var t = s.PlanAsync("x").Result;
            Assert.AreEqual("schema_invalid", t.FailureCode, t.Presented);
            Assert.AreEqual(before, s.CaptureState().Hash);
        }

        [Test]
        public void 步骤过多被拒绝()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(string.Join(",", Enumerable.Range(1, DsPlan.MaxSteps + 1).Select(i => Step("s" + i, "act", "observe")))));
            Assert.AreEqual("schema_invalid", s.PlanAsync("x").Result.FailureCode);
        }

        [Test]
        public void 不可行的计划指出步骤与原因_不执行任何一步()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            var before = s.CaptureState().Hash;
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe") + "," + Step("s2", "act", "lead", "whelp", place: "market")));
            var t = s.PlanAsync("x").Result;
            Assert.IsNull(t.FailureCode);
            Assert.AreEqual(VerdictKind.Rejected, t.Verdict.Kind);
            Assert.IsFalse(t.PlanReport.Executable);
            Assert.AreEqual("s2", t.PlanReport.Default.FailedStep);
            Assert.That(t.Presented, Does.Contain("s2"));
            Assert.AreEqual(before, s.CaptureState().Hash);
        }

        [Test]
        public void 等待终点已过的计划不可行()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            s.World.Day = 2;
            p.Replies.Enqueue(Plan(Step("s1", "wait", place: "workshop", until: "第1天·黄昏")));
            var t = s.PlanAsync("x").Result;
            Assert.IsFalse(t.PlanReport.Executable);
            Assert.That(t.PlanReport.Default.Reason, Does.Contain("已经过去"));
        }

        [Test]
        public void 计划感知只含玩家已知_不含其他地点的岗哨与他人秘密()
        {
            var w = DsContent.NewWorld();
            w.Day = 3; w.Phase = DayPhase.Noon; w.PlayerPlace = "market";
            var json = MiniJson.Write(ContextBuilder.PlanPerception(w, 4, null));
            StringAssert.Contains("正午有卫兵在集市巡逻", json);
            StringAssert.DoesNotContain("龙骨岗下设了岗哨", json);
            StringAssert.DoesNotContain("b_vesna_poachers", json);
            StringAssert.DoesNotContain("布好了网", json);
            StringAssert.DoesNotContain("pact_page", json);
            w.PlayerKnows.Add("b_maren_page");
            json = MiniJson.Write(ContextBuilder.PlanPerception(w, 4, null));
            StringAssert.Contains("pact_page", json);
            StringAssert.Contains("据说藏在墓穴", json);
        }

        [Test]
        public void 改选整体校验中不可行的备选会被拒绝_仍可确认可行选项()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe", when: "route=a") + "," + Step("s2", "act", "lead", "whelp", place: "market", when: "route=b"),
                Choice("route", "a", "b")));
            var run = new DsPlanRun(s, "x");
            var t = run.ProposeAsync().Result;
            Assert.AreEqual(VerdictKind.Partial, t.Verdict.Kind);
            Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.Throws<InvalidOperationException>(() => run.Accept(new Dictionary<string, string> { ["route"] = "b" }));
            Assert.AreEqual("awaiting_confirmation", run.Status);
            run.Accept(new Dictionary<string, string> { ["route"] = "a" });
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual(new[] { "s1" }, run.ActiveSteps.Select(x => x.Id).ToArray());
        }

        // ---------------- 逐步执行 ----------------

        [TestCase(false, "awaiting_confirmation")]
        [TestCase(true, "planning")]
        public void 局部步骤可行不冒充归巢目标已覆盖(bool homecoming, string expected)
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe")));
            var run = new DsPlanRun(s, "目标由调用者指定", homecoming);
            var t = run.ProposeAsync().Result;
            Assert.IsTrue(t.PlanReport.Executable, "通用可执行指标不随任务目标改写");
            Assert.AreEqual(expected, run.Status);
            if (homecoming) Assert.Throws<InvalidOperationException>(() => run.Accept());
            else { run.Accept(); run.StepAsync().Wait(); Assert.AreEqual("completed", run.Status); }
            Assert.IsFalse(s.World.Whelp.WithMother);
        }

        [Test]
        public void 未覆盖团聚的可执行提案修正时明确反馈目标缺口()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(Step("s1", "wait", place: "workshop", until: "第4天·清晨")));
            var run = new DsPlanRun(s, "归巢", true);
            run.ProposeAsync().Wait();
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe")));
            var revised = run.RepairProposalAsync().Result;
            StringAssert.Contains("goal_not_reached", revised.PlanContext);
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(1, s.World.Day, "不能执行到期限后才发现目标未覆盖");
        }

        [Test]
        public void 改选可执行但不归巢的分支不能绕过目标校验()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p); Follow(s.World, true);
            p.Replies.Enqueue(Plan(Step("1", "wait", place: "skull_hill", until: "第4天·清晨", when: "route=home") + "," +
                Step("2", "wait", place: "workshop", until: "第4天·清晨", when: "route=stay"), Choice("route", "home", "stay")));
            var run = new DsPlanRun(s, "归巢", true); run.ProposeAsync().Wait();
            Assert.Throws<InvalidOperationException>(() => run.Accept(new Dictionary<string, string> { ["route"] = "stay" }));
            Assert.IsEmpty(run.AcceptedRequestIds);
            run.Accept(); Assert.AreEqual("running", run.Status);
        }

        [Test]
        public void 两步之间异地居民变化不会通过静态失败原因泄露()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            s.World.PlayerPlace = "market";
            p.Replies.Enqueue(Plan(Step("1", "act", "observe") + "," + Step("2", "act", "confide", "pip", place: "docks")));
            s.World.Whelp.TrustPlayer = 0.6f;
            var run = new DsPlanRun(s, "请皮普帮忙"); run.ProposeAsync().Wait(); run.Accept(); run.StepAsync().Wait();
            s.World.Actors["pip"].Place = "cathedral";
            var result = run.StepAsync().Result;
            Assert.AreEqual(DsPlanFailure.Stale, result.Failure);
            p.Replies.Enqueue(Plan(Step("1", "act", "observe")));
            var t = run.ReplanAsync().Result;
            StringAssert.DoesNotContain("皮普不在", t.PlanContext);
            StringAssert.DoesNotContain("cathedral", t.PlanContext);
            StringAssert.Contains("无法保证前置条件", t.PlanContext);
        }

        [Test]
        public void 当前动作限制以重新关押为准_不把历史成功当当前自由()
        {
            var s = new DragonsleepSession(new RuleProvider()); Caged(s.World);
            var text = MiniJson.Write(ContextBuilder.PlanPerception(s.World, 4, null)["current_requirements"]);
            StringAssert.Contains("必须重新解除关押", text);
            StringAssert.Contains("现在不可feed/lure/hide/lead", text);
            StringAssert.DoesNotContain("至少还需成功", text, "笼中不建议先亲近");
        }

        [Test]
        public void 当前限制只由玩家视角推导_隐藏状态不会影响提示()
        {
            var s = new DragonsleepSession(new RuleProvider());
            var first = MiniJson.Write(ContextBuilder.PlanPerception(s.World, 4, null));
            s.World.Whelp.CapturedBy = "vesna"; s.World.Whelp.Fear = 1;
            s.World.Actors["osric"].Trust = 0.99f;
            var second = MiniJson.Write(ContextBuilder.PlanPerception(s.World, 4, null));
            Assert.AreEqual(first, second);
            StringAssert.Contains("至少还需成功2次", second);
        }

        [Test]
        public void 已同行居民作为当前公开状态提供_无需依赖旧计划叙述()
        {
            var s = new DragonsleepSession(new RuleProvider());
            s.World.Flags["ally:maren"] = s.World.ClockText;
            s.World.Flags["shown:pact_page:maren"] = s.World.ClockText;
            var view = ContextBuilder.PlanPerception(s.World, 4, null);
            CollectionAssert.Contains((List<object>)view["companions"], "maren");
            CollectionAssert.Contains((List<object>)view["shown_by_player"], "pact_page:maren");
        }

        [Test]
        public void 预测的重复跟随错误必须带前序草稿_不冒充当前已跟随()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(Step("1", "act", "lure", "whelp", place: "docks") + "," +
                Step("2", "act", "lure", "whelp", place: "docks") + "," + Step("3", "act", "hide", "whelp", "travel_cloak", "docks") + "," +
                Step("4", "act", "lead", "whelp", place: "docks") + "," + Step("5", "act", "lead", "whelp", place: "workshop")));
            var run = new DsPlanRun(s, "带上小烬"); var first = run.ProposeAsync().Result;
            Assert.AreEqual("5", first.PlanReport.Default.FailedStep);
            p.Replies.Enqueue(Plan(Step("1", "act", "observe")));
            var next = run.RepairProposalAsync().Result;
            var context = (Dictionary<string, object>)MiniJson.Parse(next.PlanContext);
            var feedback = (Dictionary<string, object>)context["rejected_proposal"];
            Assert.AreEqual("predicted_after_prefix", feedback["scope"]);
            Assert.AreEqual(4, ((List<object>)feedback["prefix_steps"]).Count);
            Assert.IsFalse(s.World.Whelp.Following);
            Assert.IsFalse(context.ContainsKey("done_steps"));
            StringAssert.Contains("均未执行", next.PlanContext);
        }

        [Test]
        public void 每次修正携带最新拒绝_不执行提案且不暴露异地居民位置()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            p.Replies.Enqueue(Plan(Step("s1", "act", "rescue", "whelp", place: "market")));
            var run = new DsPlanRun(s, "检查语义反馈");
            run.ProposeAsync().Wait();
            string before = s.CaptureState().Hash;
            p.Replies.Enqueue(Plan(Step("s2", "act", "confide", "maren", place: "docks")));
            var second = run.RepairProposalAsync().Result;
            StringAssert.Contains("用不着救", second.PlanContext);
            p.Replies.Enqueue(Plan(Step("s3", "act", "observe")));
            var third = run.ReplanAsync().Result;
            StringAssert.Contains("s2", third.PlanContext);
            StringAssert.Contains("不能保证目标在场", third.PlanContext);
            StringAssert.DoesNotContain("用不着救", third.PlanContext);
            StringAssert.DoesNotContain("cathedral", third.PlanContext);
            Assert.AreEqual(before, s.CaptureState().Hash);
            Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.IsEmpty(run.AcceptedRequestIds);
        }

        [Test]
        public void 托付未获同行同意时试运行与实际判定一致()
        {
            var p = new Scripted(); var s = new DragonsleepSession(p);
            s.World.PlayerPlace = "cathedral";
            p.Replies.Enqueue(Plan(Step("s1", "act", "confide", "maren", place: "cathedral")));
            var trace = s.PlanAsync("请梅伦同行").Result;
            Assert.IsFalse(trace.PlanReport.Executable);
            StringAssert.Contains("凭据", trace.PlanReport.Default.Reason);
        }

        [Test]
        public void 信任不足遮盖在掷骰前拒绝并给出真实原因()
        {
            var s = new DragonsleepSession(new RuleProvider());
            s.World.PlayerPlace = "docks"; s.World.Whelp.TrustPlayer = 0.1f;
            var before = s.CaptureState().Hash;
            var t = s.RunStepAsync(new DsPlanStep { Verb = "hide", Target = "whelp", Item = "travel_cloak" }.ToProposal("docks"), "遮盖").Result;
            Assert.AreEqual(VerdictKind.Rejected, t.Verdict.Kind);
            Assert.IsNull(t.Verdict.Roll);
            StringAssert.Contains("至少0.3", t.Verdict.Reason);
            Assert.AreEqual(before, s.CaptureState().Hash);
        }

        [Test]
        public void 逐步执行_每步单独提交并带回执_从不一次提交多步()
        {
            bool found = false;
            for (int seed = 1; seed <= 60 && !found; seed++)
            {
                var p = new Scripted();
                var s = new DragonsleepSession(p, seed);
                var w = s.World;
                Informed(w); w.Phase = DayPhase.Noon; w.PlayerPlace = "docks"; w.Whelp.Hidden = false; w.Whelp.TrustPlayer = 0.9f; w.Whelp.Fear = 0.3f;
                p.Replies.Enqueue(Plan(Step("s1", "act", "hide", "whelp", "travel_cloak", "docks") + "," + Step("s2", "act", "lead", "whelp", place: "docks") + "," +
                                       Step("s3", "go", place: "workshop")));
                var run = new DsPlanRun(s, "带上小烬去工坊");
                run.ProposeAsync().Wait();
                run.Accept();
                var results = new List<DsPlanStepResult>();
                while (run.Status == "running") { var r = run.StepAsync().Result; if (r != null) results.Add(r); }
                if (run.Status != "completed") continue;
                found = true;
                Assert.AreEqual(3, results.Count);
                Assert.IsTrue(results.All(r => r.Status == "success"));
                var ids = results.SelectMany(r => r.RequestIds).ToList();
                Assert.AreEqual(ids.Count, ids.Distinct().Count(), "每步各自的请求");
                foreach (var r in results)
                {
                    Assert.AreEqual(1, r.Receipts.Count, r.StepId + " 只有一份回执");
                    Assert.AreEqual("committed", r.Receipts[0].Status);
                    Assert.AreEqual(r.Receipts[0].VersionBefore + 1, r.Receipts[0].VersionAfter, "一步一个版本");
                }
                Assert.AreEqual("workshop", w.Whelp.Place);
                Assert.IsTrue(w.Whelp.Following);
                var stepTrace = s.Traces.First(x => x.PlayerInput.StartsWith("计划步骤 s1"));
                Assert.AreEqual(StageStatus.Ok, stepTrace.Stage(AiStage.Provider).Status);
                Assert.That(stepTrace.Provider.ProviderName, Does.Contain("计划步骤"));
                Assert.AreEqual(0m, stepTrace.Provider.EstimatedUsd);
            }
            Assert.IsTrue(found, "60 个种子内至少有一局三步都成功");
        }

        [Test]
        public void 前置条件失败_试运行不读未来岗哨_执行时被岗哨拒绝且不耗时()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p, 5);
            Caged(s.World);
            p.Replies.Enqueue(Plan(Step("s1", "act", "rescue", "whelp", place: "castle") + "," + Step("s2", "wait", place: "skull_hill", until: "第4天·清晨")));
            var run = new DsPlanRun(s, "救出小烬");
            var t = run.ProposeAsync().Result;
            Assert.IsTrue(t.PlanReport.Executable, "玩家不知道王城白天的岗哨，计划校验也不读取它");
            run.Accept();
            var r = run.StepAsync().Result;
            Assert.AreEqual(DsPlanFailure.Precondition, r.Failure);
            Assert.That(r.Reason, Does.Contain("卫兵"));
            Assert.AreEqual("failed", r.Status);
            Assert.AreEqual(4, s.TimeLeftInPhase, "掷骰前被拒绝，不耗时间");
            Assert.AreEqual("osric", s.World.Whelp.CapturedBy);
            Assert.AreEqual("needs_replan", run.Status);
            Assert.AreEqual("void", run.Results.Last().Status, "后续步骤作废");

            var replan = run.ReplanAsync().Result;
            Assert.That(replan.PlanContext, Does.Contain("前置条件不满足"));
            Assert.IsTrue(replan.PlanReport.Executable, replan.Presented);
            Assert.IsTrue(replan.PlanReport.Plan.Steps.Any(x => x.Op == "wait" && x.Until.EndsWith("黄昏")), "基线改为等黄昏换岗");
            Assert.AreEqual("awaiting_confirmation", run.Status);
            run.Accept();
            Assert.AreEqual("accepted", run.Replans.Single().Decision);
        }

        [Test]
        public void 掷骰失败_后果已写入_从新状态重新规划()
        {
            bool found = false;
            for (int seed = 1; seed <= 80 && !found; seed++)
            {
                var s = new DragonsleepSession(new RuleProvider(), seed);
                var w = s.World; Informed(w);
                w.Items["smoked_fish"].Holder = "player"; w.Items["smoked_fish"].Tags.Remove("price:3");
                var run = new DsPlanRun(s, "把小烬安全送回母亲身边");
                run.ProposeAsync().Wait();
                run.Accept();
                DsPlanStepResult fail = null;
                for (int i = 0; i < 4 && run.Status == "running"; i++)
                {
                    var r = run.StepAsync().Result;
                    if (r?.Failure == DsPlanFailure.Dice && r.Op == "act") fail = r;
                }
                if (fail == null) continue;
                found = true;
                Assert.AreEqual("failed", fail.Status);
                Assert.IsTrue(fail.Receipts.Any(x => x.Applied), "失败的后果（恐惧、时间）已经提交");
                Assert.That(fail.Reason, Does.Contain("掷骰"));
                Assert.IsTrue(run.Results.SkipWhile(x => x != fail).Skip(1).All(x => x.Status == "void"));
                float fear = w.Whelp.Fear;
                var t = run.ReplanAsync().Result;
                Assert.That(t.PlanContext, Does.Contain("执行失败"));
                Assert.That(t.Prompt.Sections.First(x => x.Name == "perception").Text, Does.Contain("replan"), "重规划上下文冻结在感知里");
                Assert.IsTrue(t.PlanReport.Executable, t.Presented);
                Assert.AreEqual(fear, w.Whelp.Fear, "重规划本身不改世界");
                Assert.AreEqual(1, run.Replans.Count);
                Assert.AreEqual(DsPlanFailure.Dice, run.Replans[0].Failure);
            }
            Assert.IsTrue(found, "80 个种子内至少出现一次掷骰失败");
        }

        [Test]
        public void 世界已变_两步之间搜城_剩余步骤作废并重规划()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p, 9);
            var w = s.World; Informed(w);
            w.Day = 2; w.PlayerPlace = "workshop"; Follow(w, true);
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe") + "," + Step("s2", "act", "observe") + "," + Step("s3", "wait", place: "skull_hill", until: "第4天·清晨")));
            var run = new DsPlanRun(s, "送小烬回家");
            run.ProposeAsync().Wait();
            run.Accept();
            Assert.AreEqual("success", run.StepAsync().Result.Status);
            p.Replies.Enqueue(Fixed("guard_sweep"));
            var gm = s.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, p).Result;
            Assert.IsTrue(gm.Receipt.Applied);
            var r = run.StepAsync().Result;
            Assert.AreEqual(DsPlanFailure.Stale, r.Failure);
            Assert.AreEqual("void", r.Status, "世界已变时这一步没有执行");
            Assert.AreEqual(0, r.Receipts.Count);
            Assert.That(r.Reason, Does.Contain("卫兵"));
            Assert.AreEqual("void", run.Results.Last().Status);
            var t = run.ReplanAsync().Result;
            Assert.That(t.PlanContext, Does.Contain("世界已变"));
            Assert.AreEqual(DsPlanFailure.Stale, run.Replans.Single().Failure);
        }

        [Test]
        public void 无关的世界变化不作废计划()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p, 9);
            var w = s.World;
            w.PlayerPlace = "workshop";
            p.Replies.Enqueue(Plan(Step("s1", "act", "observe") + "," + Step("s2", "act", "observe")));
            var run = new DsPlanRun(s, "x");
            run.ProposeAsync().Wait();
            run.Accept();
            run.StepAsync().Wait();
            p.Replies.Enqueue(Fixed("snowfall"));
            s.RunWithProviderAsync(AiTaskKind.Direct, "gm", null, p).Wait();
            Assert.AreEqual("success", run.StepAsync().Result.Status, "落雪与计划无关，复核通过后继续");
            Assert.AreEqual("completed", run.Status);
        }

        // ---------------- 求助取舍 ----------------

        static string Confide(string who) =>
            "{\"verb\":\"confide\",\"target\":\"" + who + "\",\"item\":\"\",\"place\":\"\",\"laws\":[],\"difficulty\":0.1,\"narration\":\"\"}";

        [Test]
        public void 托付梅伦要契约凭据_托付皮普要小烬信任_其他人只会记下()
        {
            var p = new Scripted();
            var s = new DragonsleepSession(p);
            var w = s.World;
            w.PlayerPlace = "cathedral";
            p.Replies.Enqueue(Confide("maren"));
            var t = s.ActAsync("托付梅伦").Result;
            Assert.AreEqual(VerdictKind.Partial, t.Verdict.Kind);
            Assert.IsNotNull(w.Belief("b_plan_maren"), "她知道了计划");
            Assert.IsFalse(w.Flags.ContainsKey("ally:maren"));
            Assert.AreEqual(3, s.TimeLeftInPhase, "托付耗 1 点时间");

            w.Flags["shown:pact_page:maren"] = "第1天·清晨"; w.Actors["maren"].Trust = 0.6f;
            p.Replies.Enqueue(Confide("maren"));
            Assert.AreEqual(VerdictKind.Accepted, s.ActAsync("托付梅伦").Result.Verdict.Kind);
            Assert.IsTrue(w.Flags.ContainsKey("ally:maren"));

            w.PlayerPlace = "docks"; w.Whelp.TrustPlayer = 0.2f;
            p.Replies.Enqueue(Confide("pip"));
            Assert.AreEqual(VerdictKind.Partial, s.ActAsync("托付皮普").Result.Verdict.Kind);
            Assert.IsFalse(w.Flags.ContainsKey("ally:pip"));
            w.Whelp.TrustPlayer = 0.5f;
            p.Replies.Enqueue(Confide("pip"));
            Assert.AreEqual(VerdictKind.Accepted, s.ActAsync("托付皮普").Result.Verdict.Kind);
            Assert.IsTrue(w.Flags.ContainsKey("ally:pip"));

            w.Whelp.Following = true; w.Whelp.Place = "docks"; w.Whelp.Hidden = true; w.Flags["whelp_hidden_by_player"] = "x";
            DsGuards.SightChance(w, out var notes);
            Assert.IsTrue(notes.Any(n => n.Contains("梅伦")) && notes.Any(n => n.Contains("皮普")));
            Assert.AreEqual(0.05, DsGuards.SightChance(w, out _), 1e-6, "斗篷 0.25 − 梅伦 0.20 − 皮普 0.10，下限 0.05");

            w.PlayerPlace = "market";
            p.Replies.Enqueue(Confide("whelp"));
            Assert.AreEqual(VerdictKind.Rejected, s.ActAsync("托付小烬").Result.Verdict.Kind);
        }

        [Test]
        public void 总管得知计划后_龙骨岗黄昏也有岗()
        {
            var w = DsContent.NewWorld();
            w.Day = 3; w.Phase = DayPhase.Dusk;
            Assert.IsFalse(DsGuards.IsWatched(w, "skull_hill"));
            w.Beliefs.Add(new DsBelief { Id = "b_plan_pip@osric", Owner = "osric", Topic = "plan", Text = "听皮普说：言契师打算把小烬送上龙骨岗" });
            Assert.IsTrue(DsGuards.IsWatched(w, "skull_hill", out var reason));
            Assert.That(reason, Does.Contain("得知了你的打算"));
            w.Day = 4; w.Phase = DayPhase.Dawn;
            Assert.IsFalse(DsGuards.IsWatched(w, "skull_hill"), "终局撤岗");
        }

        [Test]
        public void 皮普口风不紧_离线居民策略在第二夜把计划传给总管()
        {
            var s = new DragonsleepSession(new RuleProvider(), 4);
            var w = s.World;
            w.PlayerPlace = "docks"; w.Whelp.Hidden = false; w.Whelp.TrustPlayer = 0.5f;
            var t = s.ActAsync("把护送计划托付给皮普，请他望风").Result;
            Assert.AreEqual("confide", t.Proposal.Str("verb"));
            Assert.IsTrue(w.Flags.ContainsKey("ally:pip"));
            s.World.PlayerPlace = "workshop";
            int day = w.Day;
            while (w.Day == day) s.EndPhaseAsync().Wait();
            Assert.AreEqual("market", w.Actors["pip"].Place, "第一夜：皮普去集市弄吃的");
            Assert.IsFalse(DsGuards.OsricKnowsPlan(w));
            day = w.Day;
            while (w.Day == day) s.EndPhaseAsync().Wait();
            Assert.IsTrue(DsGuards.OsricKnowsPlan(w), "第二夜：在集市被总管撞见，说漏了嘴");
            Assert.IsNotNull(w.Belief("b_plan_pip@osric"));
            Assert.IsTrue(s.Traces.Any(x => x.Kind == AiTaskKind.AgentPlan && x.ActorId == "pip" && x.Proposal?.Str("verb") == "tell"));
        }

        [Test]
        public void 同一起点三种求助选择产生不同后续()
        {
            var outcome = new Dictionary<string, DsWorld>();
            foreach (var option in new[] { "maren", "pip", "none" })
            {
                var s = new DragonsleepSession(new RuleProvider(), 21);
                PactEscort(s.World);
                var run = new DsPlanRun(s, "把小烬安全送回母亲身边。");
                run.ProposeAsync().Wait();
                for (int guard = 0; guard < 40 && run.PlanTraces.Count <= 8; guard++)
                {
                    if (run.Status == "awaiting_confirmation") run.Accept(new Dictionary<string, string> { ["helper"] = option });
                    else if (run.Status == "running") run.StepAsync().Wait();
                    else if (run.Status == "needs_replan") run.ReplanAsync().Wait();
                    else break;
                }
                outcome[option] = s.World;
            }
            Assert.IsTrue(outcome["maren"].Flags.ContainsKey("ally:maren"));
            Assert.IsFalse(DsGuards.OsricKnowsPlan(outcome["maren"]));
            Assert.IsNotNull(outcome["pip"].Belief("b_plan_pip"));
            Assert.IsTrue(DsGuards.OsricKnowsPlan(outcome["pip"]), "皮普的口风让总管得知计划");
            Assert.IsFalse(outcome["none"].Flags.Keys.Any(k => k.StartsWith("confided:")));
            Assert.IsFalse(DsGuards.OsricKnowsPlan(outcome["none"]));
        }

        // ---------------- 回放 ----------------

        [Test]
        public void 计划与结构化步骤都可离线严格回放()
        {
            var s = new DragonsleepSession(new RuleProvider(), 13);
            Informed(s.World);
            var before = s.CaptureState();
            string context = "{\"failure\":\"执行失败（掷骰/巡逻）\",\"reason\":\"测试\"}";
            var plan = s.PlanAsync("把小烬送回家", context).Result;
            var frame = DsReplayFrame.Parse(MiniJson.Write(DsReplayFrame.Capture(before, s.CaptureState(), plan).ToRecord()));
            Assert.AreEqual(context, frame.PlanContext);
            Assert.IsTrue(frame.VerifyAsync().Result, "计划帧严格回放");

            before = s.CaptureState();
            var step = s.RunStepAsync(new Dictionary<string, object>
            {
                ["verb"] = "observe", ["target"] = "", ["item"] = "", ["place"] = "", ["laws"] = new List<object>(), ["difficulty"] = 0.5, ["narration"] = ""
            }, "计划步骤 s1 observe").Result;
            var stepFrame = DsReplayFrame.Capture(before, s.CaptureState(), step);
            Assert.IsTrue(stepFrame.VerifyAsync().Result, "结构化步骤帧严格回放");
        }
    }
}
