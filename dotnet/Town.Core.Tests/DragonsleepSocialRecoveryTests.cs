using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.OpenAI;

namespace Town.Core.Tests
{
    public sealed class DragonsleepSocialRecoveryTests
    {
        static DragonsleepSession Session(string place = "cathedral")
        {
            var session = new DragonsleepSession(new RuleProvider(), 7, new DsFixedRoll(.9999));
            session.World.PlayerPlace = place;
            return session;
        }

        static AiTrace Act(DragonsleepSession session, string verb, string target = "", string item = "") =>
            session.RunStepAsync(Step("action", verb, target, item).ToProposal(session.World.PlayerPlace), "社会补救").Result;

        static DsPlanStep Step(string id, string verb, string target = "maren", string item = "") =>
            new DsPlanStep { Id = id, Op = "act", Verb = verb, Target = target, Item = item, Place = "cathedral" };

        static DsPlan Plan(params DsPlanStep[] steps)
        {
            var plan = new DsPlan { Goal = "请梅伦同行", Summary = "交付原件后托付" };
            plan.Steps.AddRange(steps);
            return plan;
        }

        sealed class Plans : IAiProvider
        {
            readonly Queue<string> _replies;
            public Plans(params DsPlan[] plans) => _replies = new Queue<string>(plans.Select(p => MiniJson.Write(p.ToRecord())));
            public string Name => "社会补救离线脚本";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
                Task.FromResult(new ProviderResult
                {
                    ProviderName = Name, RawText = _replies.Dequeue(), Simulated = true, EstimatedUsd = 0m
                });
        }

        [TestCase(false, "confide_evidence_required")]
        [TestCase(true, "confide_custody_required")]
        public void 低信任缺口实际Partial只公开要求不泄露信任(bool shown, string code)
        {
            var session = Session();
            session.World.Actors["maren"].Trust = .2f;
            if (shown) session.World.Flags["shown:pact_page:maren"] = session.World.ClockText;
            var trace = Act(session, "confide", "maren");
            var perception = ContextBuilder.PlanPerception(session.World, session.TimeLeftInPhase, null);
            Assert.That(trace.Verdict.Kind, Is.EqualTo(VerdictKind.Partial));
            Assert.That(trace.Verdict.ReasonCode.Code, Is.EqualTo(code));
            Assert.That(trace.Receipt.Applied, Is.True);
            Assert.That(session.World.Flags.ContainsKey("requested:pact_custody:maren"), Is.True);
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.False);
            StringAssert.DoesNotContain("trust_in_player", MiniJson.Write(perception));
        }

        [Test]
        public void 低信任交付转移原件但不加礼物信任_再次托付才结盟()
        {
            var session = Session();
            var world = session.World;
            world.Items["pact_page"].Holder = "player";
            world.Actors["maren"].Trust = .2f;
            var give = Act(session, "give", "maren", "pact_page");
            Assert.That(give.Receipt.Applied, Is.True);
            Assert.That(give.Receipt.TimeCost, Is.EqualTo(2));
            Assert.That(world.Items["pact_page"].Holder, Is.EqualTo("maren"));
            Assert.That(world.Flags.ContainsKey("pact_custody:maren"), Is.True);
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.False);
            Assert.That(world.Actors["maren"].Trust, Is.EqualTo(.2f).Within(.00001f));
            Assert.That(Act(session, "confide", "maren").Receipt.Applied, Is.True);
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.True);
            var denied = Act(session, "show", "osric", "pact_page");
            Assert.That(denied.Receipt.Applied, Is.False, "交出后不能拿同一原件向总管出示");
        }

        [Test]
        public void 未持有原件的交付拒绝不转移不耗时()
        {
            var session = Session();
            session.World.Items["pact_page"].Holder = "maren";
            string before = session.CaptureState().Hash;
            var trace = Act(session, "give", "maren", "pact_page");
            Assert.That(trace.Receipt.Applied, Is.False);
            Assert.That(trace.Verdict.ReasonCode.StableCode, Is.EqualTo("item_not_held:pact_page"));
            Assert.That(session.World.Items["pact_page"].Holder, Is.EqualTo("maren"));
            Assert.That(session.TimeLeftInPhase, Is.EqualTo(4));
            Assert.That(session.World.Flags.ContainsKey("pact_custody:maren"), Is.False);
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
        }

        [Test]
        public void 伪契约标签出示无特殊信任收益或资格()
        {
            var session = Session();
            var world = session.World;
            world.Items["iron_lock"].Holder = "player";
            world.Items["iron_lock"].Tags.Add("pact");
            float trust = world.Actors["maren"].Trust;
            Act(session, "show", "maren", "iron_lock");
            var trace = Act(session, "confide", "maren");
            Assert.That(world.Actors["maren"].Trust, Is.EqualTo(trust));
            Assert.That(trace.Verdict.Kind, Is.EqualTo(VerdictKind.Partial));
            Assert.That(trace.Verdict.ReasonCode.Code, Is.EqualTo("confide_evidence_required"));
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.False);
        }

        [Test]
        public void 成功取回原件清除保管和盟友()
        {
            var session = Session();
            var world = session.World;
            world.Items["pact_page"].Holder = "player";
            Act(session, "give", "maren", "pact_page");
            Act(session, "confide", "maren");
            Assert.That(Act(session, "take", "", "pact_page").Receipt.Applied, Is.True);
            Assert.That(world.Items["pact_page"].Holder, Is.EqualTo("player"));
            Assert.That(world.Flags.ContainsKey("pact_custody:maren"), Is.False);
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 仅实际公开要求触发交付_预测拒绝码不能冒充公开条件(bool actualRequest)
        {
            var session = Session();
            var world = session.World;
            world.Items["pact_page"].Holder = "player";
            if (actualRequest) world.Flags["requested:pact_custody:maren"] = world.ClockText;
            const string predicted = "{\"rejected_proposal\":{\"code\":\"confide_custody_required\"}}";
            var perception = ContextBuilder.PlanPerception(world, session.TimeLeftInPhase, predicted);
            var plan = BaselinePlanner.Plan(perception, "请梅伦帮忙");
            var branch = plan.Steps.Where(s => s.When == "helper=maren").ToList();
            Assert.That(branch.Any(s => s.Verb == "give"), Is.EqualTo(actualRequest));
            Assert.That(branch.Any(s => s.Verb == "show"), Is.EqualTo(!actualRequest));
            Assert.That(branch.Any(s => s.Verb == "confide"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 私有信任不改变感知与条件性试运行(bool shown)
        {
            var views = new List<string>();
            var reports = new List<string>();
            foreach (float trust in new[] { .1f, .9f })
            {
                var session = Session("workshop");
                session.World.Actors["maren"].Trust = trust;
                if (shown) session.World.Flags["shown:pact_page:maren"] = "第1天·清晨";
                views.Add(MiniJson.Write(ContextBuilder.PlanPerception(session.World, session.TimeLeftInPhase, null)));
                var report = DsPlanValidator.Validate(session.CaptureState(), Plan(Step("1", "confide")));
                reports.Add(MiniJson.Write(report.ToRecord()));
                Assert.That(report.Executable, Is.EqualTo(shown));
                Assert.That(session.World.Flags.ContainsKey("requested:pact_custody:maren"), Is.False);
            }
            Assert.That(views[1], Is.EqualTo(views[0]));
            Assert.That(reports[1], Is.EqualTo(reports[0]));
        }

        [Test]
        public void 远处保管原件持有人改变不改变玩家视角与试运行()
        {
            var views = new List<string>();
            var reports = new List<string>();
            foreach (string holder in new[] { "maren", "place:catacombs" })
            {
                var session = Session("workshop");
                session.World.Flags["pact_custody:maren"] = "第1天·清晨";
                session.World.Items["pact_page"].Holder = holder;
                views.Add(MiniJson.Write(ContextBuilder.PlanPerception(session.World, session.TimeLeftInPhase, null)));
                var report = DsPlanValidator.Validate(session.CaptureState(), Plan(Step("1", "confide")));
                reports.Add(MiniJson.Write(report.ToRecord()));
                Assert.That(report.Executable, Is.True);
            }
            Assert.That(views[1], Is.EqualTo(views[0]));
            Assert.That(reports[1], Is.EqualTo(reports[0]));
        }

        [Test]
        public async Task 低信任首案预测合作_真实拒绝后重规划并重新确认交付()
        {
            var provider = new Plans(Plan(Step("1", "show", item: "pact_page"), Step("2", "confide")),
                Plan(Step("1", "give", item: "pact_page"), Step("2", "confide")));
            var session = new DragonsleepSession(provider, 7, new DsFixedRoll(.9999));
            session.World.PlayerPlace = "cathedral";
            session.World.Items["pact_page"].Holder = "player";
            session.World.Actors["maren"].Trust = .15f;
            var run = new DsPlanRun(session, "请梅伦同行");
            string before = session.CaptureState().Hash;
            Assert.That((await run.ProposeAsync()).PlanReport.Executable, Is.True);
            Assert.That(run.Status, Is.EqualTo("awaiting_confirmation"));
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
            run.Accept();
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
            Assert.That((await run.StepAsync()).Status, Is.EqualTo("success"));
            var denied = await run.StepAsync();
            Assert.That(denied.Receipts.Single().Applied, Is.True);
            Assert.That(denied.ReasonCode.Code, Is.EqualTo("confide_custody_required"));
            Assert.That(run.Status, Is.EqualTo("needs_replan"));
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.False);
            before = session.CaptureState().Hash;
            var repaired = await run.ReplanAsync();
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
            Assert.That(run.Status, Is.EqualTo("awaiting_confirmation"));
            Assert.That(run.Replans.Single().ReasonCode.Code, Is.EqualTo("confide_custody_required"));
            StringAssert.Contains("rejected_proposal", repaired.PlanContext);
            run.Accept();
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
            Assert.That((await run.StepAsync()).Receipts.Single().Applied, Is.True);
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.False);
            Assert.That((await run.StepAsync()).Receipts.Single().Applied, Is.True);
            Assert.That(session.World.Flags.ContainsKey("ally:maren"), Is.True);
        }

        [Test]
        public void 请求体从冻结安全视角重建且紧凑JSON不超12KiB()
        {
            var session = Session();
            const string context = "{\"player_constraints\":{\"version\":\"ds-plan-constraints-v1\",\"allowed_confidants\":[\"maren\"]}}";
            string Payload(TaskPerceptionView view) => OpenAiDragonsleepProvider.BuildPayload(
                ContextBuilder.BuildTask(view, "请规划归巢"), OpenAiRunProfile.SixMechanismResearch)
                .ToString(Newtonsoft.Json.Formatting.None);
            var first = TaskPerceptionView.Capture(session.World, AiTaskKind.Plan, "player", session.TimeLeftInPhase, context);
            string payload = Payload(first);
            session.World.Actors["maren"].Trust = .99f;
            session.World.Beliefs.Add(new DsBelief
            {
                Id = "private_sentinel", Owner = "maren", Topic = "secret", Text = "私有认知", RevealTrust = .1f
            });
            var second = TaskPerceptionView.Capture(session.World, AiTaskKind.Plan, "player", session.TimeLeftInPhase, context);
            Assert.That(second.Json, Is.EqualTo(first.Json));
            Assert.That(Payload(second), Is.EqualTo(payload));
            StringAssert.DoesNotContain("private_sentinel", payload);
            StringAssert.Contains(DsPlanConstraints.Version, payload);
            Assert.That(Encoding.UTF8.GetByteCount(payload), Is.LessThanOrEqualTo(12 * 1024));
        }
    }
}
