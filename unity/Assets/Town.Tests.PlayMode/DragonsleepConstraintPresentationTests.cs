using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.Unity.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Town.Tests.PlayMode
{
    /// <summary>M3 可靠性：真实主场景与按钮链路，仅离线提供方，不读取密钥或请求外网。</summary>
    public sealed class DragonsleepConstraintPresentationTests
    {
        DragonsleepGame _game;
        VisualElement _root;
        Func<IAiProvider> _factory;
        bool _reduced;
        Scripted _provider;

        sealed class Scripted : IAiProvider
        {
            public string Name => "offline-m3-constraints-fixture";
            public bool IsSimulated => true;
            public readonly Queue<string> Plans = new Queue<string>();
            public readonly List<PromptPacket> Packets = new List<PromptPacket>();
            public int ScriptedPlanCalls;
            public int RulePlanCalls;
            readonly RuleProvider _rules = new RuleProvider();

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
            {
                Packets.Add(packet);
                if (packet.Kind != AiTaskKind.Plan) return _rules.CompleteAsync(packet, cancellationToken);
                if (Plans.Count == 0)
                {
                    RulePlanCalls++;
                    return _rules.CompleteAsync(packet, cancellationToken);
                }
                ScriptedPlanCalls++;
                return Task.FromResult(new ProviderResult { ProviderName = Name, RawText = Plans.Dequeue(), Simulated = true });
            }
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            _factory = DragonsleepGame.ProviderFactory; _reduced = DsUiMotion.Reduced;
            _provider = new Scripted(); DragonsleepGame.ProviderFactory = () => _provider; DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null; yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>(); Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Click("beginJourney"); yield return null;
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            try { if (_game != null) yield return Idle(); }
            finally { DragonsleepGame.ProviderFactory = _factory; DsUiMotion.Reduced = _reduced; }
        }

        static void Activate(Button button)
        {
            Assert.IsNotNull(button); Assert.IsTrue(button.enabledInHierarchy, button.name + " 应可用");
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        void Click(string name) => Activate(_root.Q<Button>(name));
        IEnumerator Idle()
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (_game != null && _game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(_game == null || !_game.Busy, "操作必须收尾，不能悬挂 Busy"); yield return null;
        }
        IEnumerator Open()
        {
            Click("openPlan"); yield return null;
            Assert.IsNotNull(_root.Q("planPanel")); Assert.IsNotNull(_root.Q<DropdownField>("planDisclosure"));
        }
        void Disclosure(int index)
        {
            var field = _root.Q<DropdownField>("planDisclosure");
            Assert.IsNotNull(field); Assert.IsTrue(field.enabledInHierarchy);
            field.value = field.choices[index];
            Assert.AreEqual(index, field.index, "必须触发真实下拉框的值变更回调");
        }
        string State() => _game.Session.CaptureState().Hash;
        string Text() => string.Join("\n", _root.Q("planPanel").Query<Label>().ToList().Select(l => l.text));
        int PlanCalls() => _provider.Packets.Count(p => p.Kind == AiTaskKind.Plan);

        void Unchanged(string before, int time)
        {
            Assert.AreEqual(before, State(), "提案、改选、修正和确认均不得写入世界");
            Assert.AreEqual(time, _game.Session.TimeLeftInPhase, "执行下一步之前不得消耗时间");
            Assert.IsEmpty(_game.ActivePlan.Results, "不得自动执行任何步骤");
            Assert.IsFalse(_game.Session.World.Flags.Keys.Any(k => k.StartsWith("confided:", StringComparison.Ordinal)));
            Assert.IsFalse(_game.Session.World.Beliefs.Any(b => b.Topic == "plan"), "试运行不能把托付认知写入真实居民");
        }

        static DsPlanStep Step(string id, string op, string place = "", string until = "", string when = "",
            string verb = "", string target = "", string item = "") => new DsPlanStep
        {
            Id = id, Op = op, Place = place, Until = until, When = when, Verb = verb, Target = target, Item = item,
            Why = "离线明确条件呈现夹具",
        };

        static string Plan(params DsPlanStep[] prefix)
        {
            var plan = new DsPlan { Goal = "把小烬送回母亲身边", Summary = "完成前置安排后，在龙骨岗等到母亲降临。" };
            plan.Steps.AddRange(prefix);
            plan.Steps.Add(Step("home", "wait", "skull_hill", "第4天·清晨"));
            return MiniJson.Write(plan.ToRecord());
        }

        static string ChoicePlan()
        {
            var plan = new DsPlan { Goal = "归巢", Summary = "默认独行，也可请皮普望风，最后送小烬归巢。" };
            var choice = new DsPlanChoice { Id = "helper", Question = "是否托付护送计划？" };
            choice.Options.Add(new DsPlanOption { Id = "none", Label = "不向居民托付" });
            choice.Options.Add(new DsPlanOption { Id = "pip", Label = "托付皮普望风" }); plan.Choices.Add(choice);
            plan.Steps.Add(Step("first", "go", "workshop"));
            plan.Steps.Add(Step("tell_pip", "act", "docks", when: "helper=pip", verb: "confide", target: "pip"));
            plan.Steps.Add(Step("home", "wait", "skull_hill", "第4天·清晨"));
            return MiniJson.Write(plan.ToRecord());
        }

        void FollowingCloakedFixture()
        {
            // 显式规则夹具，不声称玩家已经通过 UI 完成喂食、遮盖和带上。
            var w = _game.Session.World;
            w.Whelp.Place = w.PlayerPlace; w.Whelp.Following = true; w.Whelp.Hidden = true;
            w.Whelp.TrustPlayer = 0.6f; w.Whelp.Fear = 0.4f;
            w.Flags["whelp_hidden_by_player"] = w.ClockText;
        }

        void MarenEvidenceFixture()
        {
            // 显式 Known / MarenTrust / 契约已出示夹具，隔离规则前置失败与玩家条件拒绝。
            var w = _game.Session.World;
            w.PlayerKnows.Add("b_maren_page"); w.PlayerKnows.Add("b_maren_pact");
            w.Items["pact_page"].Holder = "player"; w.Actors["maren"].Trust = 0.8f;
            w.Flags["shown:pact_page:maren"] = w.ClockText;
        }

        static void Allowed(DsPlanRun run, params string[] actorIds)
        {
            Assert.IsTrue(run.Constraints.HasRestrictions);
            CollectionAssert.AreEquivalent(actorIds, (List<object>)run.Constraints.ToRecord()["allowed_confidants"]);
        }

        IEnumerator PanelBounds()
        {
            // 只验运行时布局，不截图、不落盘，也不修改场景或 PanelSettings 资源。
            var panel = _game.GetComponent<UIDocument>().panelSettings;
            var previous = panel.targetTexture;
            foreach (var size in new[] { new Vector2Int(1600, 1000), new Vector2Int(1024, 768) })
            {
                var target = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGB32);
                target.Create(); panel.targetTexture = target;
                try
                {
                    for (int i = 0; i < 4; i++) yield return null;
                    var card = _root.Q("planPanel"); Assert.IsNotNull(card);
                    Inside(card, _root, "planPanel");
                    foreach (string name in new[] { "planGoal", "planDisclosure", "planContents", "planStatus" })
                    {
                        var control = card.Q(name); Assert.IsNotNull(control, name);
                        Inside(control, card, name);
                    }
                    var branch = card.Q<Label>("planBranch"); Assert.IsNotNull(branch);
                    if (!string.IsNullOrEmpty(branch.text)) Inside(branch, card, branch.name);
                    foreach (string name in new[] { "closePlan", "proposePlan", "cancelPlanRequest", "confirmPlan",
                        "executePlanStep", "replanPlan", "abandonPlan" })
                    {
                        var button = card.Q<Button>(name); Assert.IsNotNull(button, name);
                        if (button.resolvedStyle.display != DisplayStyle.None) Inside(button, card, name);
                    }
                    foreach (var button in card.Query<Button>().ToList().Where(b =>
                        b.name != null && b.name.StartsWith("planChoice_", StringComparison.Ordinal)))
                        Inside(button, card, button.name);
                }
                finally
                {
                    panel.targetTexture = previous; target.Release(); UnityEngine.Object.Destroy(target);
                }
            }
            yield return null;
        }

        static void Inside(VisualElement element, VisualElement outer, string name)
        {
            var rect = element.worldBound; var bounds = outer.worldBound;
            Assert.Greater(rect.width, 0, name); Assert.Greater(rect.height, 0, name);
            Assert.GreaterOrEqual(rect.xMin, bounds.xMin - 1, name); Assert.LessOrEqual(rect.xMax, bounds.xMax + 1, name);
            Assert.GreaterOrEqual(rect.yMin, bounds.yMin - 1, name); Assert.LessOrEqual(rect.yMax, bounds.yMax + 1, name);
        }

        [UnityTest]
        public IEnumerator 默认离线规划器遵守不托付_确认不写世界且收起后条件仍在()
        {
            yield return Open();
            CollectionAssert.AreEqual(new[] { "未限定", "不向居民托付（仅限制托付）", "只向梅伦托付（非必须同行）",
                "只向皮普托付（非必须帮忙）", "独行，不托付也不交原件", "必须请梅伦同行", "必须请皮普帮忙" },
                _root.Q<DropdownField>("planDisclosure").choices);
            Assert.AreEqual(0, _root.Q<DropdownField>("planDisclosure").index);
            _root.Q<TextField>("planGoal").value = "请皮普帮忙把小烬安全送回母亲身边";
            Disclosure(1); string before = State(); int time = _game.Session.TimeLeftInPhase;
            Click("proposePlan"); yield return Idle();
            var run = _game.ActivePlan;
            Assert.AreEqual(1, _provider.RulePlanCalls); Assert.AreEqual(0, _provider.ScriptedPlanCalls);
            Assert.AreEqual("awaiting_confirmation", run.Status, Text()); Allowed(run);
            Assert.IsTrue(run.Plan.Steps.All(s => s.Verb != "confide"), "明确条件应优先于目标文本里的皮普");
            StringAssert.Contains("player_constraints", run.LatestPlanTrace.PlanContext);
            StringAssert.Contains("本次已冻结的条件：不向居民托付", Text());
            StringAssert.Contains("不阻止另行对话或居民流言", Text());
            Unchanged(before, time); Assert.IsEmpty(run.AcceptedRequestIds);
            Click("confirmPlan"); yield return null;
            Assert.AreEqual("running", run.Status); Assert.AreEqual(0, run.NextIndex);
            Assert.AreEqual(1, run.AcceptedRequestIds.Count); Unchanged(before, time);
            Click("closePlan"); yield return null; yield return Open();
            Assert.AreSame(run, _game.ActivePlan); Assert.AreEqual(1, _root.Q<DropdownField>("planDisclosure").index);
            Allowed(run); StringAssert.Contains("本次已冻结的条件：不向居民托付", Text());
            yield return PanelBounds(); Unchanged(before, time); Assert.AreEqual(1, PlanCalls());
        }

        [UnityTest]
        public IEnumerator 默认独行可归巢但改选皮普违反条件_禁确认并可换回独行()
        {
            FollowingCloakedFixture(); _provider.Plans.Enqueue(ChoicePlan());
            yield return Open(); Disclosure(1); string before = State(); int time = _game.Session.TimeLeftInPhase;
            Click("proposePlan"); yield return Idle();
            var run = _game.ActivePlan; var report = run.LatestPlanTrace.PlanReport;
            Assert.AreEqual("awaiting_confirmation", run.Status, Text()); Assert.AreEqual(2, report.Branches.Count);
            Assert.IsTrue(report.Branches.All(b => b.Feasible && b.ReachesGoal), "包括皮普备选在内，规则试运行必须都能归巢");
            Assert.IsTrue(_root.Q<Button>("confirmPlan").enabledInHierarchy);
            Click("planChoice_helper_pip"); yield return null;
            Assert.AreEqual("awaiting_confirmation", run.Status); Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf);
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            StringAssert.Contains("托付对象不符合玩家明确选择", Text());
            Unchanged(before, time); Assert.IsEmpty(run.AcceptedRequestIds); yield return PanelBounds();
            Click("planChoice_helper_none"); yield return null;
            Assert.IsTrue(_root.Q<Button>("confirmPlan").enabledInHierarchy);
            Click("confirmPlan"); yield return null;
            Assert.AreEqual("running", run.Status); Assert.AreEqual("none", run.Selection["helper"]);
            Assert.IsTrue(run.ActiveSteps.All(s => s.Verb != "confide")); Assert.AreEqual(1, run.AcceptedRequestIds.Count);
            Allowed(run); Unchanged(before, time); Assert.AreEqual(1, PlanCalls());
        }

        [UnityTest]
        public IEnumerator 规则可执行的首案因托付条件拒绝_修正保留条件且出示不等于托付()
        {
            FollowingCloakedFixture(); MarenEvidenceFixture();
            _provider.Plans.Enqueue(Plan(Step("tell_maren", "act", "cathedral", verb: "confide", target: "maren")));
            _provider.Plans.Enqueue(Plan(Step("show_page", "act", "cathedral", verb: "show", target: "maren", item: "pact_page")));
            yield return Open(); Disclosure(1); string before = State(); int time = _game.Session.TimeLeftInPhase;
            Click("proposePlan"); yield return Idle();
            var run = _game.ActivePlan; var rejected = run.LatestPlanTrace;
            Assert.IsTrue(rejected.PlanReport.Executable, "夹具保证动作规则可执行，拒绝不能归因于缺凭据或低信任");
            Assert.IsTrue(rejected.PlanReport.Default.ReachesGoal); Assert.IsFalse(run.MeetsObjective(rejected));
            Assert.AreEqual("planning", run.Status); Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf);
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            StringAssert.Contains("托付对象不符合玩家明确选择", Text());
            Unchanged(before, time); Assert.IsEmpty(run.AcceptedRequestIds); yield return PanelBounds();
            Click("replanPlan"); yield return Idle();
            Assert.AreSame(run, _game.ActivePlan); Allowed(run);
            Assert.AreEqual("awaiting_confirmation", run.Status, Text());
            Assert.AreNotEqual(rejected.RequestId, run.LatestPlanTrace.RequestId);
            StringAssert.Contains("player_constraint", run.LatestPlanTrace.PlanContext);
            StringAssert.Contains("tell_maren", run.LatestPlanTrace.PlanContext);
            StringAssert.Contains("allowed_confidants", run.LatestPlanTrace.PlanContext);
            Assert.IsTrue(run.Plan.Steps.Any(s => s.Verb == "show" && s.Target == "maren"));
            Assert.IsTrue(run.Plan.Steps.All(run.Constraints.Allows), "条件只限制 confide，不应扩大为禁止 show");
            Assert.AreEqual("pending", run.Replans.Single().Decision); Assert.IsTrue(run.Replans.Single().NewPlanExecutable);
            Assert.IsTrue(_root.Q<Button>("confirmPlan").enabledInHierarchy);
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            StringAssert.Contains("本次已冻结的条件：不向居民托付", Text());
            Unchanged(before, time); Assert.IsEmpty(run.AcceptedRequestIds);
            Click("confirmPlan"); yield return null;
            Assert.AreEqual("running", run.Status); Assert.AreEqual("accepted", run.Replans.Single().Decision);
            Unchanged(before, time); Assert.AreEqual(2, PlanCalls());
        }

        [UnityTest]
        public IEnumerator 修改下拉草稿不会伪装成已生效_重新提出才冻结梅伦皮普或未限定()
        {
            FollowingCloakedFixture();
            for (int i = 0; i < 3; i++) _provider.Plans.Enqueue(ChoicePlan());
            yield return Open(); Disclosure(2); string before = State(); int time = _game.Session.TimeLeftInPhase;
            Click("proposePlan"); yield return Idle(); var marenRun = _game.ActivePlan;
            Assert.AreEqual("awaiting_confirmation", marenRun.Status, Text()); Allowed(marenRun, "maren");
            Disclosure(3); Click("closePlan"); yield return null; yield return Open();
            Assert.AreEqual(3, _root.Q<DropdownField>("planDisclosure").index);
            Assert.AreSame(marenRun, _game.ActivePlan); Allowed(marenRun, "maren");
            StringAssert.Contains("本次已冻结的条件：只向梅伦修女托付", Text());
            StringAssert.Contains("改条件需重新提出目标", Text());
            Click("planChoice_helper_pip"); yield return null;
            Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf);
            StringAssert.Contains("托付对象不符合玩家明确选择", Text());
            Assert.IsEmpty(marenRun.AcceptedRequestIds); Assert.AreEqual(1, PlanCalls()); Unchanged(before, time);
            Click("proposePlan"); yield return Idle(); var pipRun = _game.ActivePlan;
            Assert.AreNotSame(marenRun, pipRun); Assert.AreEqual("abandoned", marenRun.Status); Allowed(pipRun, "pip");
            Assert.AreEqual("awaiting_confirmation", pipRun.Status, Text());
            Click("planChoice_helper_pip"); yield return null;
            Assert.IsTrue(_root.Q<Button>("confirmPlan").enabledInHierarchy);
            StringAssert.Contains("本次已冻结的条件：只向皮普托付", Text());
            Disclosure(0); Click("closePlan"); yield return null; yield return Open();
            Assert.AreSame(pipRun, _game.ActivePlan); Allowed(pipRun, "pip");
            Assert.AreEqual(0, _root.Q<DropdownField>("planDisclosure").index);
            StringAssert.Contains("本次已冻结的条件：只向皮普托付", Text());
            Assert.AreEqual(2, PlanCalls()); Unchanged(before, time); Assert.IsEmpty(pipRun.AcceptedRequestIds);
            Click("proposePlan"); yield return Idle(); var unrestricted = _game.ActivePlan;
            Assert.AreNotSame(pipRun, unrestricted); Assert.AreEqual("abandoned", pipRun.Status);
            Assert.AreEqual("awaiting_confirmation", unrestricted.Status, Text()); Assert.IsFalse(unrestricted.Constraints.HasRestrictions);
            StringAssert.DoesNotContain("player_constraints", unrestricted.LatestPlanTrace.PlanContext);
            StringAssert.DoesNotContain("本次已冻结的条件：", Text());
            Click("planChoice_helper_pip"); yield return null;
            Assert.IsTrue(_root.Q<Button>("confirmPlan").enabledInHierarchy);
            Click("confirmPlan"); yield return null;
            Assert.AreEqual("running", unrestricted.Status); Assert.AreEqual("pip", unrestricted.Selection["helper"]);
            Assert.AreEqual(1, unrestricted.AcceptedRequestIds.Count); Unchanged(before, time); Assert.AreEqual(3, PlanCalls());
        }
    }
}
