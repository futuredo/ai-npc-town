using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepPlannerObservationTests
    {
        const string Goal = "请梅伦和皮普帮忙护送小烬";

        static DragonsleepSession Ready(string here, IAiProvider provider = null)
        {
            var session = new DragonsleepSession(provider ?? new RuleProvider(), 7, new DsFixedRoll(.9999));
            var world = session.World;
            world.PlayerPlace = here;
            world.Whelp.Place = here;
            world.Whelp.Following = true;
            world.Whelp.Hidden = true;
            world.Whelp.TrustPlayer = .6f;
            world.Whelp.Fear = .3f;
            world.Flags["whelp_hidden_by_player"] = world.ClockText;
            world.Items["pact_page"].Holder = "player";
            world.Flags["requested:pact_custody:maren"] = world.ClockText;
            world.PlayerKnows.Add("b_maren_page");
            world.PlayerKnows.Add("b_maren_pact");
            return session;
        }

        static Dictionary<string, object> Perception(string here, params string[] people)
        {
            var session = Ready(here);
            var view = ContextBuilder.PlanPerception(session.World, session.TimeLeftInPhase, null);
            // 单独控制当前观测；规划器始终只接收裁剪后的字典。
            view["people_here"] = people.Select(id => (object)(id + "：当前可见居民")).ToList();
            return view;
        }

        static List<DsPlanStep> Helper(DsPlan plan, string id) => plan.Steps.Where(step => step.Target == id).ToList();
        static string Json(DsPlan plan) => MiniJson.Write(plan.ToRecord());

        static void AssertSeeking(List<DsPlanStep> steps, string candidate)
        {
            Assert.That(steps, Is.Not.Empty);
            Assert.That(steps.All(step => step.Place == candidate && step.Op == "act"), Is.True);
            foreach (var step in steps)
            {
                StringAssert.Contains("寻访", step.Why);
                StringAssert.Contains("不保证目标在场", step.Why);
                StringAssert.Contains("找到后再", step.Why);
            }
        }

        [Test]
        public void 十四冻结起点的计划JSON与既有录制逐字相同()
        {
            string file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "rules-7", "plans.replay.jsonl");
            var frames = File.ReadAllLines(file).Select(DsReplayFrame.Parse).ToList();
            var cases = PlanCases.Frozen();
            Assert.That(frames.Count, Is.EqualTo(14));
            Assert.That(cases.Count, Is.EqualTo(14));
            Assert.That(PlanCases.Fingerprint(cases),
                Is.EqualTo("bfac3c38bc8b20fd156211217eda55385a4f7875abe37f1075fcb8026ea66147"));
            for (int i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var session = new DragonsleepSession(new RuleProvider(), 7);
                c.Setup(session);
                var view = TaskPerceptionView.Capture(session.World, AiTaskKind.Plan, "player", session.TimeLeftInPhase).ToDictionary();
                Assert.That(frames[i].Input, Is.EqualTo(c.Input), c.Id + " 录制顺序");
                var expected = frames[i].Response.RawText;
                Assert.That(Json(BaselinePlanner.Plan(view, c.Input)), Is.EqualTo(expected), c.Id);
                view.Remove("people_here");
                Assert.That(Json(BaselinePlanner.Plan(view, c.Input)), Is.EqualTo(expected), c.Id + " 无当前居民字段兼容");
            }
        }

        [Test]
        public void 眼前梅伦优先于旧缺席码_交付与托付均使用当前地点()
        {
            foreach (string here in new[] { "catacombs", "cathedral", "workshop" })
            {
                foreach (var reason in new[] { default(DsReason), new DsReason("actor_absent", "maren"), new DsReason("world_changed") })
                {
                    var local = new DsPlanLocalContext(reason);
                    var steps = Helper(BaselinePlanner.Plan(Perception(here, "maren"), Goal, local), "maren");
                    CollectionAssert.AreEqual(new[] { "give", "confide" }, steps.Select(step => step.Verb));
                    Assert.That(steps.All(step => step.Place == here), Is.True, here);
                    CollectionAssert.AreEqual(new[] { "交付原件核验", "请梅伦同行" }, steps.Select(step => step.Why));
                }
            }
        }

        [Test]
        public void 当前梅伦缺席覆盖世界已变或旧缺席码_另一常去处只是寻访候选()
        {
            var scenarios = new[]
            {
                (here: "cathedral", reason: new DsReason("world_changed"), candidate: "catacombs"),
                (here: "cathedral", reason: new DsReason("actor_absent", "maren"), candidate: "catacombs"),
                (here: "catacombs", reason: new DsReason("actor_absent", "maren"), candidate: "cathedral"),
            };
            foreach (var scenario in scenarios)
            {
                var view = Perception(scenario.here);
                var local = new DsPlanLocalContext(scenario.reason);
                var steps = Helper(BaselinePlanner.Plan(view, Goal, local), "maren");
                CollectionAssert.AreEqual(new[] { "give", "confide" }, steps.Select(step => step.Verb));
                AssertSeeking(steps, scenario.candidate);
                Assert.That(steps.Any(step => step.Place == scenario.here), Is.False);
            }
        }

        [Test]
        public void 皮普同样优先眼前观测_码头缺席寻访集市而异地未知保持模板()
        {
            var absent = new DsPlanLocalContext(new DsReason("actor_absent", "pip"));
            foreach (string here in new[] { "market", "docks", "workshop" })
            {
                var step = Helper(BaselinePlanner.Plan(Perception(here, "pip"), Goal, absent), "pip").Single();
                Assert.That(step.Verb, Is.EqualTo("confide"));
                Assert.That(step.Place, Is.EqualTo(here));
                Assert.That(step.Why, Is.EqualTo("请皮普望风"));
            }
            var changed = new DsPlanLocalContext(new DsReason("world_changed"));
            AssertSeeking(Helper(BaselinePlanner.Plan(Perception("docks"), Goal, changed), "pip"), "market");
            AssertSeeking(Helper(BaselinePlanner.Plan(Perception("market"), Goal, absent), "pip"), "docks");
            var unknown = Helper(BaselinePlanner.Plan(Perception("market"), Goal), "pip").Single();
            Assert.That(unknown.Place, Is.EqualTo("docks"));
            Assert.That(unknown.Why, Is.EqualTo("请皮普望风"));
        }

        [Test]
        public void 两世界仅异地居民隐藏位置不同_裁剪感知与计划JSON均相同()
        {
            foreach (var actor in new[] { (id: "maren", a: "cathedral", b: "catacombs"), (id: "pip", a: "docks", b: "market") })
            {
                var first = Ready("workshop").World;
                var second = Ready("workshop").World;
                first.Actors[actor.id].Place = actor.a;
                second.Actors[actor.id].Place = actor.b;
                // 两份感知各自从完整世界裁剪；之后只把感知直接交给规划器，不再回读世界。
                var firstView = ContextBuilder.PlanPerception(first, 4, null);
                var secondView = ContextBuilder.PlanPerception(second, 4, null);
                Assert.That(MiniJson.Write(firstView), Is.EqualTo(MiniJson.Write(secondView)), actor.id);
                Assert.That(Json(BaselinePlanner.Plan(firstView, Goal)), Is.EqualTo(Json(BaselinePlanner.Plan(secondView, Goal))), actor.id);
            }
        }

        [Test]
        public void 缺少当前居民字段时保留常驻模板与原有缺席启发()
        {
            foreach (var actor in new[] { (id: "maren", usual: "cathedral", other: "catacombs"), (id: "pip", usual: "docks", other: "market") })
            {
                var view = Perception(actor.usual);
                view.Remove("people_here");
                var normal = Helper(BaselinePlanner.Plan(view, Goal), actor.id);
                Assert.That(normal.All(step => step.Place == actor.usual), Is.True);
                Assert.That(normal.Any(step => step.Why.Contains("寻访")), Is.False);
                view["where"] = actor.other;
                var local = new DsPlanLocalContext(new DsReason("actor_absent", actor.id));
                var fallback = Helper(BaselinePlanner.Plan(view, Goal, local), actor.id);
                Assert.That(fallback.All(step => step.Place == actor.other), Is.True);
                Assert.That(fallback.Any(step => step.Why.Contains("寻访")), Is.False);
                var remote = Helper(BaselinePlanner.Plan(Perception("workshop"), Goal, local), actor.id);
                Assert.That(remote.All(step => step.Place == actor.other), Is.True);
            }
        }

        [Test]
        public void 异地未知不从草稿预测反馈或失败文案推断位置与交付要求()
        {
            var view = Perception("workshop");
            view["public_cooperation_requests"] = new string[0];
            string original = Json(BaselinePlanner.Plan(view, Goal));
            view["replan"] = MiniJson.Parse("{\"reason\":\"梅伦去了墓穴，皮普去了集市\",\"rejected_proposal\":" +
                "{\"code\":\"actor_absent\",\"entity\":\"maren\",\"reason\":\"confide_custody_required\"}}");
            var plan = BaselinePlanner.Plan(view, Goal);
            Assert.That(Json(plan), Is.EqualTo(original));
            Assert.That(Helper(plan, "maren").First().Verb, Is.EqualTo("show"));
            Assert.That(Helper(plan, "maren").All(step => step.Place == "cathedral"), Is.True);
            Assert.That(Helper(plan, "pip").Single().Place, Is.EqualTo("docks"));
        }

        sealed class NightMigration : IAiProvider
        {
            readonly RuleProvider _rules = new RuleProvider();
            public string Name => "夜间迁移离线脚本";
            public bool IsSimulated => true;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                string raw;
                if (packet.Kind == AiTaskKind.AgentPlan)
                {
                    var view = (Dictionary<string, object>)MiniJson.Parse(packet.Sections.First(section => section.Name == "perception").Text);
                    bool maren = packet.ActorId == "maren";
                    raw = MiniJson.Write(new Dictionary<string, object>
                    {
                        ["verb"] = maren ? "move" : "wait", ["place"] = maren ? "catacombs" : view.Str("where"),
                        ["target"] = "", ["share"] = new List<object>(), ["reason"] = "夜里按脚本迁移，其余居民留守",
                    });
                }
                else if (packet.Kind == AiTaskKind.Direct) raw = "{\"event\":\"none\",\"intensity\":0,\"reason\":\"安静一夜\"}";
                else return _rules.CompleteAsync(packet, ct);
                return Task.FromResult(new ProviderResult { ProviderName = Name, RawText = raw, Simulated = true, EstimatedUsd = 0m });
            }
        }

        [Test]
        public async Task 夜间迁移使教堂交付作废_重规划经确认后寻访并实际交付托付()
        {
            var session = Ready("cathedral", new NightMigration());
            session.World.Phase = DayPhase.Night;
            var run = new DsPlanRun(session, "请梅伦帮忙护送小烬");
            Assert.That((await run.ProposeAsync()).PlanReport.Executable, Is.True);
            Assert.That(Helper(run.Plan, "maren").First().Place, Is.EqualTo("cathedral"));
            run.Accept();
            var stale = await run.StepAsync();
            Assert.That(stale.Failure, Is.EqualTo(DsPlanFailure.Stale));
            Assert.That(stale.ReasonCode.Code, Is.EqualTo("world_changed"));
            Assert.That(session.World.Actors["maren"].Place, Is.EqualTo("catacombs"));
            Assert.That(session.World.Items["pact_page"].Holder, Is.EqualTo("player"));
            Assert.That(run.Status, Is.EqualTo("needs_replan"));
            string before = session.CaptureState().Hash;
            var repaired = await run.ReplanAsync();
            Assert.That(repaired.PlanReport.Executable, Is.True);
            Assert.That(run.Status, Is.EqualTo("awaiting_confirmation"));
            AssertSeeking(Helper(run.Plan, "maren"), "catacombs");
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before), "重新规划只提案，不执行寻访或交付");
            run.Accept();
            Assert.That((await run.StepAsync()).Status, Is.EqualTo("success"));
            Assert.That(session.World.Items["pact_page"].Holder, Is.EqualTo("maren"));
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.False);
            Assert.That((await run.StepAsync()).Status, Is.EqualTo("success"));
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.True);
            Assert.That(session.Traces.All(trace => trace.Provider == null || trace.Provider.Simulated), Is.True);
        }
    }
}
