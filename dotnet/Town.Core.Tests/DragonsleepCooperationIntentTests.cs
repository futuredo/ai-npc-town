using System;
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
    /// <summary>合作意图 v2：全部离线，脚本只提供草稿，正式步骤仍经过真实执行器与回执。</summary>
    public sealed class DragonsleepCooperationIntentTests
    {
        sealed class Scripted : IAiProvider
        {
            public readonly Queue<string> Replies = new Queue<string>();
            public int Calls;
            public string Name => "cooperation-intent-offline";
            public bool IsSimulated => true;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                if (packet.Kind != AiTaskKind.Plan) throw new InvalidOperationException("此脚本只允许规划请求");
                Calls++;
                return Task.FromResult(new ProviderResult
                {
                    RawText = Replies.Dequeue(), ProviderName = Name, Simulated = true, EstimatedUsd = 0m
                });
            }
        }

        static DsPlan Draft(params DsPlanStep[] steps)
        {
            var plan = new DsPlan { Goal = "验证玩家选择的合作方式", Summary = "以真实回执判断合作" };
            plan.Steps.AddRange(steps);
            return plan;
        }

        static string Place(string helper) => helper == "maren" ? "cathedral" : "docks";
        static DsPlanStep Observe(string id) => new DsPlanStep { Id = id, Op = "act", Verb = "observe" };
        static DsPlanStep Confide(string id, string helper) => new DsPlanStep
        {
            Id = id, Op = "act", Verb = "confide", Target = helper, Place = Place(helper)
        };
        static DsPlanStep Give(string id, string target, string item = "pact_page") => new DsPlanStep
        {
            Id = id, Op = "act", Verb = "give", Target = target, Item = item, Place = Place(target)
        };
        static DsPlanConstraints Intent(string helper) => helper == "solo"
            ? DsPlanConstraints.Solo : DsPlanConstraints.WithHelper(helper);
        static void Reply(Scripted provider, DsPlan plan) => provider.Replies.Enqueue(MiniJson.Write(plan.ToRecord()));
        static string Payload(PromptPacket packet) => OpenAiDragonsleepProvider
            .BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch).ToString(Newtonsoft.Json.Formatting.None);

        static DragonsleepSession Session(Scripted provider)
        {
            var session = new DragonsleepSession(provider, 7, new DsFixedRoll(.9999));
            session.World.PlayerPlace = "cathedral";
            session.World.Items["pact_page"].Holder = "player";
            session.World.PlayerKnows.Add("b_maren_page");
            session.World.Actors["maren"].Trust = .8f;
            session.World.Flags["shown:pact_page:maren"] = session.World.ClockText;
            session.World.Whelp.TrustPlayer = .6f;
            return session;
        }

        [Test]
        public void v1保留仅限制托付语义_v2才声明合作意图与版本()
        {
            var v1 = new[] { DsPlanConstraints.KeepPrivate, DsPlanConstraints.Only("maren") };
            foreach (var constraints in v1)
            {
                Assert.IsFalse(constraints.HasCooperationIntent);
                Assert.IsNull(constraints.RequiredHelper);
                Assert.AreEqual(DsPlanConstraints.Version, constraints.ToRecord()["version"]);
                Assert.IsNull(constraints.Check(Draft(Observe("1")), new Dictionary<string, string>()));
                Assert.IsTrue(constraints.Allows(Give("2", "pip")), "v1 不追溯增加原件交付限制");
            }
            Assert.IsFalse(DsPlanConstraints.KeepPrivate.Allows(Confide("1", "maren")));
            Assert.IsTrue(DsPlanConstraints.Only("maren").Allows(Confide("1", "maren")));
            Assert.IsFalse(DsPlanConstraints.Only("maren").Allows(Confide("1", "pip")));

            foreach (string helper in new[] { "solo", "maren", "pip" })
            {
                var constraints = Intent(helper);
                Assert.IsTrue(constraints.HasCooperationIntent);
                Assert.AreEqual(helper == "solo" ? null : helper, constraints.RequiredHelper);
                Assert.AreEqual(DsPlanConstraints.CooperationVersion, constraints.ToRecord()["version"]);
            }
            Assert.Throws<ArgumentException>(() => DsPlanConstraints.WithHelper("osric"));
        }

        [Test]
        public void v2阻止全部非名单托付_只有梅伦合作可把契约原件交给梅伦()
        {
            foreach (string helper in new[] { "solo", "maren", "pip" })
            {
                var constraints = Intent(helper);
                foreach (string target in DsContent.NewWorld(7).Actors.Keys.Concat(new[] { "unknown" }))
                {
                    string context = helper + " -> " + target;
                    Assert.AreEqual(target == constraints.RequiredHelper, constraints.Allows(Confide("1", target)), context);
                    Assert.AreEqual(helper == "maren" && target == "maren", constraints.Allows(Give("2", target)), context);
                    Assert.IsTrue(constraints.Allows(Give("3", target, "iron_lock")), "不把普通赠物误当原件交付：" + context);
                }
                Assert.IsTrue(constraints.Allows(Observe("4")));
            }
        }

        [TestCase("maren", "pip")]
        [TestCase("pip", "maren")]
        public void 指定合作必须有激活的正向托付_只有指定居民公开盟友标记可免重复(string helper, string other)
        {
            var constraints = DsPlanConstraints.WithHelper(helper);
            var plan = Draft(Observe("1"), Confide("2", helper));
            plan.Steps[1].When = "route=help";
            var inactive = new Dictionary<string, string> { ["route"] = "solo" };
            var active = new Dictionary<string, string> { ["route"] = "help" };
            Assert.IsNotNull(constraints.Check(plan, inactive));
            Assert.IsNull(constraints.Check(plan, active));

            var world = Session(new Scripted()).World;
            world.Flags["confided:" + helper] = world.ClockText;
            world.Flags["pact_custody:maren"] = world.ClockText;
            world.Flags["ally:" + other] = world.ClockText;
            world.Actors[helper].Trust = 1f;
            world.Beliefs.Add(new DsBelief { Id = "private_agreement", Owner = helper, Topic = "plan", Text = "愿意帮忙" });
            Assert.IsFalse(constraints.CooperationAchieved(world), "私有认知、高信任、保管或另一居民的承诺均不能代替公开结盟");
            Assert.IsNotNull(constraints.Check(plan, inactive, constraints.CooperationAchieved(world)));
            world.Flags["ally:" + helper] = world.ClockText;
            Assert.IsTrue(constraints.CooperationAchieved(world));
            Assert.IsNull(constraints.Check(plan, inactive, constraints.CooperationAchieved(world)));
        }

        [TestCase("maren")]
        [TestCase("pip")]
        public async Task 只在摘要声称已合作不能通过目标_真实公开承诺才允许无托付草稿(string helper)
        {
            var provider = new Scripted();
            var session = Session(provider);
            var plan = Draft(Observe("1"));
            plan.Summary = "梅伦和皮普都已经同意同行，合作目标已完成";
            Reply(provider, plan);
            var run = new DsPlanRun(session, "请指定居民同行", constraints: DsPlanConstraints.WithHelper(helper));
            string before = session.CaptureState().Hash;
            var trace = await run.ProposeAsync();
            Assert.IsTrue(trace.PlanReport.Executable, "通用动作可执行不代表合作目标通过");
            Assert.IsFalse(run.MeetsObjective(trace));
            Assert.AreEqual("planning", run.Status);
            Assert.Throws<InvalidOperationException>(() => run.Accept());
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(before, session.CaptureState().Hash);

            // 另一次已发生的公开承诺作为夹具；不能由原草稿的 summary 生成。
            session.World.Flags["ally:" + helper] = session.World.ClockText;
            Reply(provider, Draft(Observe("2")));
            before = session.CaptureState().Hash;
            var revised = await run.RepairProposalAsync();
            Assert.IsTrue(run.MeetsObjective(revised));
            Assert.AreEqual("awaiting_confirmation", run.Status);
            run.Accept();
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual(before, session.CaptureState().Hash, "规划和确认本身都不执行世界动作");
        }

        [TestCase("maren", "pip")]
        [TestCase("pip", "maren")]
        public async Task 备选换人或独行不能绕过合作目标_确认入口重验被编辑的草稿(string helper, string other)
        {
            var provider = new Scripted();
            var session = Session(provider);
            var plan = Draft(Confide("chosen", helper), Confide("other", other), Observe("solo"));
            plan.Steps[0].When = "helper=" + helper;
            plan.Steps[1].When = "helper=" + other;
            plan.Steps[2].When = "helper=none";
            var choice = new DsPlanChoice { Id = "helper", Question = "请谁帮忙" };
            foreach (string option in new[] { helper, other, "none" })
                choice.Options.Add(new DsPlanOption { Id = option, Label = option });
            plan.Choices.Add(choice);
            Reply(provider, plan);
            var run = new DsPlanRun(session, "保持指定合作对象", constraints: DsPlanConstraints.WithHelper(helper));
            var trace = await run.ProposeAsync();
            Assert.IsTrue(trace.PlanReport.Branches.All(b => b.Feasible), "所有备选均应先通过普通动作校验");
            Assert.IsTrue(run.MeetsObjective(trace));
            string before = session.CaptureState().Hash;
            foreach (string unauthorized in new[] { other, "none" })
                Assert.Throws<InvalidOperationException>(() => run.Accept(new Dictionary<string, string> { ["helper"] = unauthorized }));

            var edited = run.Plan.Steps[0];
            edited.Verb = "observe";
            edited.Target = "";
            Assert.Throws<InvalidOperationException>(() => run.Accept(), "不能用过期报告掩盖正向托付已被删去");
            Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(before, session.CaptureState().Hash);
            edited.Verb = "confide";
            edited.Target = helper;
            run.Accept();
            CollectionAssert.AreEqual(new[] { "chosen" }, run.ActiveSteps.Select(s => s.Id));
        }

        [TestCase("solo", "confide", "pip")]
        [TestCase("solo", "give", "maren")]
        [TestCase("pip", "give", "pip")]
        [TestCase("pip", "give", "maren")]
        [TestCase("maren", "give", "pip")]
        [TestCase("maren", "confide", "pip")]
        public async Task 确认后步骤被篡改_必须在跨夜移动掷骰和耗时前拒绝(string helper, string verb, string target)
        {
            var provider = new Scripted();
            var session = Session(provider);
            var plan = Draft(Observe("1"), helper == "solo" ? Observe("2") : Confide("2", helper));
            Reply(provider, plan);
            var run = new DsPlanRun(session, "遵守合作方式", constraints: Intent(helper));
            await run.ProposeAsync();
            run.Accept();
            var step = run.ActiveSteps[0];
            step.Verb = verb;
            step.Target = target;
            step.Item = verb == "give" ? "pact_page" : "";
            step.Place = "docks";
            session.World.Phase = DayPhase.Night;
            string before = session.CaptureState().Hash;
            int traces = session.Traces.Count, events = session.RuleEvents.Count;
            var result = await run.StepAsync();
            Assert.AreEqual("player_constraint", result.ReasonCode.Code);
            Assert.AreEqual("needs_replan", run.Status);
            Assert.IsEmpty(result.RequestIds);
            Assert.IsEmpty(result.Receipts);
            Assert.AreEqual(before, session.CaptureState().Hash, "完整快照同时覆盖时间、位置、物品与掷骰计数");
            Assert.AreEqual(traces, session.Traces.Count);
            Assert.AreEqual(events, session.RuleEvents.Count);
            Assert.AreEqual(1, provider.Calls, "越权步骤不能先触发夜间居民与 GM");
            Assert.AreEqual("void", run.Results.Last().Status);
        }

        [TestCase("maren", "pip")]
        [TestCase("pip", "maren")]
        public async Task 移动回调篡改公开步骤和集合_不能替换已校验的冻结托付(string helper, string other)
        {
            var provider = new Scripted();
            var session = Session(provider);
            session.World.PlayerPlace = "workshop";
            Reply(provider, Draft(Confide("1", helper)));
            var run = new DsPlanRun(session, "按确认目标托付", constraints: DsPlanConstraints.WithHelper(helper));
            await run.ProposeAsync();
            run.Accept();
            bool changed = false;
            session.RuleEventCompleted += e =>
            {
                if (e.Label != "enter:" + Place(helper)) return;
                changed = true;
                var step = run.ActiveSteps[0];
                step.Verb = "give";
                step.Target = other;
                step.Item = "pact_page";
                step.Why = "回调注入文本";
                step.Laws.Add("unknown_law");
                step.Cites.Add("unknown_fact");
            };
            var result = await run.StepAsync();
            Assert.IsTrue(changed, "确实到达移动回调边界");
            Assert.AreEqual("give", run.ActiveSteps[0].Verb, "公开草稿确实被改写");
            Assert.AreEqual("success", result.Status, result.Reason);
            Assert.AreEqual("completed", run.Status);
            Assert.IsTrue(session.World.Flags.ContainsKey("ally:" + helper));
            Assert.IsFalse(session.World.Flags.ContainsKey("confided:" + other));
            Assert.AreEqual("player", session.World.Items["pact_page"].Holder);
            Assert.AreEqual("confide", session.Traces.Last().Proposal.Str("verb"));
            Assert.AreEqual(helper, session.Traces.Last().Proposal.Str("target"));
            Assert.IsEmpty(session.Traces.Last().Proposal.StrList("laws"));
            StringAssert.DoesNotContain("回调注入文本", session.Traces.Last().Proposal.Str("narration"));
            Assert.AreEqual(1, provider.Calls, "逐步执行不再次调用脚本规划提供方");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task 实际托付回执决定合作_低信任Partial不完成_授权交付后仍须托付(bool handOver)
        {
            var provider = new Scripted();
            var session = Session(provider);
            session.World.Actors["maren"].Trust = .1f;
            var plan = handOver ? Draft(Give("1", "maren"), Confide("2", "maren")) : Draft(Confide("1", "maren"));
            Reply(provider, plan);
            var constraints = DsPlanConstraints.WithHelper("maren");
            var run = new DsPlanRun(session, "请梅伦实际答应同行", constraints: constraints);
            string before = session.CaptureState().Hash;
            var trace = await run.ProposeAsync();
            Assert.IsTrue(trace.PlanReport.Executable, "试运行只能条件性预测合作，不读取私有信任");
            Assert.IsTrue(run.MeetsObjective(trace));
            run.Accept();
            Assert.AreEqual(before, session.CaptureState().Hash);
            Assert.IsFalse(constraints.CooperationAchieved(session.World));
            if (handOver)
            {
                var given = await run.StepAsync();
                Assert.AreEqual("success", given.Status, given.Reason);
                Assert.IsTrue(given.Receipts.Single().Applied);
                Assert.AreEqual("maren", session.World.Items["pact_page"].Holder);
                Assert.IsFalse(constraints.CooperationAchieved(session.World), "交付原件本身不冒充答应同行");
                Assert.AreEqual("running", run.Status);
            }
            var result = await run.StepAsync();
            Assert.IsTrue(result.Receipts.Single().Applied, "Partial 的公开条件与知情后果也会实际提交");
            Assert.IsTrue(session.World.Flags.ContainsKey("confided:maren"));
            Assert.AreEqual(handOver, constraints.CooperationAchieved(session.World));
            Assert.AreEqual(handOver ? VerdictKind.Accepted : VerdictKind.Partial, session.Traces.Last().Verdict.Kind);
            Assert.AreEqual(handOver ? "success" : "failed", result.Status);
            Assert.AreEqual(handOver ? "completed" : "needs_replan", run.Status);
            if (!handOver) Assert.AreEqual("confide_custody_required", result.ReasonCode.Code);
        }

        [TestCase("exhausted", "needs_replan")]
        [TestCase("homecoming", "failed")]
        [TestCase("deadline", "failed")]
        public async Task 草稿用尽或已经团聚仍无盟友_只能重规划或终局失败不能完成(string boundary, string expectedStatus)
        {
            var provider = new Scripted();
            var session = Session(provider);
            Reply(provider, Draft(Confide("1", "maren")));
            var run = new DsPlanRun(session, "必须取得梅伦合作", constraints: DsPlanConstraints.WithHelper("maren"));
            await run.ProposeAsync();
            run.Accept();
            // 确认后删除正向动作，或由外部规则先触达团聚/期限；均未产生居民承诺。
            if (boundary == "exhausted")
            {
                run.ActiveSteps[0].Verb = "observe";
                run.ActiveSteps[0].Target = "";
            }
            else if (boundary == "homecoming") session.World.Whelp.WithMother = true;
            else { session.World.Day = 4; session.World.Phase = DayPhase.Dawn; }
            string before = session.CaptureState().Hash;
            var result = await run.StepAsync();
            Assert.AreEqual(expectedStatus, run.Status);
            Assert.AreEqual("cooperation_not_achieved", run.LastReasonCode.Code);
            Assert.IsFalse(session.World.Flags.ContainsKey("ally:maren"));
            if (boundary == "exhausted") Assert.AreEqual("success", result.Status, "局部步骤成功仍不代表目标完成");
            else
            {
                Assert.IsNull(result);
                Assert.IsEmpty(run.Results);
                Assert.AreEqual(before, session.CaptureState().Hash, "终局边界不再执行任何世界动作");
            }
        }

        [TestCase("solo")]
        [TestCase("maren")]
        [TestCase("pip")]
        public async Task v2首案与重规划均冻结安全条件_紧凑请求体不超过12KiB(string helper)
        {
            var provider = new Scripted();
            var session = Session(provider);
            Reply(provider, helper == "solo" ? Draft(Confide("bad", "maren")) : Draft(Observe("bad")));
            Reply(provider, helper == "solo" ? Draft(Observe("good")) : Draft(Confide("good", helper)));
            var run = new DsPlanRun(session, "遵守明确合作方式", constraints: Intent(helper));
            string beforeWorld = session.CaptureState().Hash;
            await run.ProposeAsync();
            Assert.AreEqual("planning", run.Status);
            var revised = await run.RepairProposalAsync();
            Assert.AreEqual("awaiting_confirmation", run.Status);
            Assert.AreEqual(beforeWorld, session.CaptureState().Hash);
            StringAssert.Contains("player_constraint", revised.PlanContext);
            foreach (var trace in run.PlanTraces)
            {
                var context = (Dictionary<string, object>)MiniJson.Parse(trace.PlanContext);
                var constraints = (Dictionary<string, object>)context["player_constraints"];
                Assert.AreEqual(DsPlanConstraints.CooperationVersion, constraints["version"]);
                Assert.AreEqual(helper == "solo" ? null : helper, constraints["required_helper"]);
                string body = Payload(trace.Prompt);
                StringAssert.Contains(DsPlanConstraints.CooperationVersion, body);
                StringAssert.Contains("allowed_pact_custodians", body);
                Assert.LessOrEqual(Encoding.UTF8.GetByteCount(body), 12 * 1024);
            }

            var packet = revised.Prompt;
            string frozenView = packet.TaskSnapshot.Json, beforePayload = Payload(packet);
            packet.Sections.Clear();
            packet.Sections.Add(new PromptSection { Name = "player_constraints", Text = "任意换人或交出原件" });
            packet.Instructions = "回调篡改指令";
            revised.PlanContext = "篡改重规划上下文";
            revised.Perception.Clear();
            session.World.Actors["maren"].Trust = .001f;
            session.World.Beliefs.Add(new DsBelief
            {
                Id = "private_cooperation_sentinel", Owner = "maren", Topic = "secret", Text = "不应发送的私有合作认知"
            });
            string afterPayload = Payload(packet);
            Assert.AreEqual(frozenView, packet.TaskSnapshot.Json);
            Assert.AreEqual(beforePayload, afterPayload, "仅从冻结视角重建，不能信任可变分段或后来的世界状态");
            StringAssert.DoesNotContain("任意换人或交出原件", afterPayload);
            StringAssert.DoesNotContain("回调篡改指令", afterPayload);
            StringAssert.DoesNotContain("篡改重规划上下文", afterPayload);
            StringAssert.DoesNotContain("private_cooperation_sentinel", afterPayload);
            StringAssert.DoesNotContain("不应发送的私有合作认知", afterPayload);
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(afterPayload), 12 * 1024);
        }

        [Test]
        public async Task 多取舍组合未出现在缓存报告中_仍须重新验证归巢不能默认放行()
        {
            var provider = new Scripted();
            var session = Session(provider);
            session.World.PlayerPlace = "workshop";
            session.World.Whelp.Place = "workshop";
            session.World.Whelp.Following = true;
            session.World.Whelp.Hidden = true;
            session.World.Flags["whelp_hidden_by_player"] = session.World.ClockText;
            session.World.Flags["ally:pip"] = session.World.ClockText;
            var plan = Draft(Observe("always"), new DsPlanStep
            { Id = "home", Op = "wait", Place = "skull_hill", Until = "第4天·清晨", When = "route=home" });
            foreach (string id in new[] { "route", "detail" })
            {
                var choice = new DsPlanChoice { Id = id, Question = id };
                choice.Options.Add(new DsPlanOption { Id = id == "route" ? "home" : "a", Label = "第一项" });
                choice.Options.Add(new DsPlanOption { Id = id == "route" ? "stay" : "b", Label = "第二项" });
                plan.Choices.Add(choice);
            }
            Reply(provider, plan);
            var run = new DsPlanRun(session, "归巢并保持皮普合作", true, DsPlanConstraints.WithHelper("pip"));
            await run.ProposeAsync();
            Assert.AreEqual("awaiting_confirmation", run.Status);
            var chosen = new Dictionary<string, string> { ["route"] = "stay", ["detail"] = "b" };
            Assert.IsFalse(run.LatestPlanTrace.PlanReport.Branches.Any(b => b.Selection.All(x => chosen[x.Key] == x.Value)));
            string before = session.CaptureState().Hash;
            Assert.Throws<InvalidOperationException>(() => run.Accept(chosen));
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(before, session.CaptureState().Hash);
        }

        [Test]
        public async Task 保管后托付再拿回原件_原有盟友资格撤销不能被历史成功掩盖()
        {
            var provider = new Scripted();
            var session = Session(provider);
            session.World.Actors["maren"].Trust = .1f;
            Reply(provider, Draft(Give("1", "maren"), Confide("2", "maren"), new DsPlanStep
                { Id = "3", Op = "act", Verb = "take", Item = "pact_page", Place = "cathedral" }));
            var run = new DsPlanRun(session, "梅伦保持合作", constraints: DsPlanConstraints.WithHelper("maren"));
            await run.ProposeAsync();
            run.Accept();
            await run.StepAsync();
            await run.StepAsync();
            Assert.IsTrue(run.Constraints.CooperationAchieved(session.World));
            await run.StepAsync();
            Assert.IsFalse(run.Constraints.CooperationAchieved(session.World));
            Assert.AreEqual("needs_replan", run.Status);
            Assert.IsFalse((bool)run.ToRecord()["cooperation_achieved"]);
            Assert.IsFalse((bool)run.ToRecord()["objective_achieved"]);
        }
    }
}
