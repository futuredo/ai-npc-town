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
    /// <summary>v2 合作条件：真实主场景与 HUD 按钮，仅离线提供方；前置夹具不代表完整新局玩法验收。</summary>
    public sealed class DragonsleepCooperationPresentationTests
    {
        DragonsleepGame _game;
        VisualElement _root;
        Func<IAiProvider> _factory;
        bool _reduced, _playerEnabled;

        DragonsleepSession S => _game.Session;
        DsWorld W => S.World;

        sealed class Scripted : IAiProvider
        {
            public string Name => "offline-v2-cooperation-fixture";
            public bool IsSimulated => true;
            public readonly Queue<string> Plans = new Queue<string>();
            public readonly List<PromptPacket> Packets = new List<PromptPacket>();
            readonly RuleProvider _rules = new RuleProvider();

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
            {
                Packets.Add(packet);
                if (packet.Kind != AiTaskKind.Plan) return _rules.CompleteAsync(packet, cancellationToken);
                Assert.IsNotEmpty(Plans, "脚本规划用尽时不得静默兜底或自动重试");
                return Task.FromResult(new ProviderResult { ProviderName = Name, RawText = Plans.Dequeue(), Simulated = true });
            }
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            _factory = DragonsleepGame.ProviderFactory;
            _reduced = DsUiMotion.Reduced;
            DragonsleepGame.ProviderFactory = () => new RuleProvider();
            DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return Layout();
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Click("beginJourney");
            _playerEnabled = _game.Player.enabled;
            _game.Player.enabled = false;
            yield return Layout();
            Assert.IsInstanceOf<RuleProvider>(S.Provider);
            Assert.IsTrue(S.Provider.IsSimulated);
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            try { if (_game != null) yield return Idle(); }
            finally
            {
                DragonsleepGame.ProviderFactory = _factory;
                DsUiMotion.Reduced = _reduced;
                if (_game != null) _game.Player.enabled = _playerEnabled;
            }
        }

        static IEnumerator Layout()
        {
            // 面板重建后先跨帧布局，再重新查询按钮；不保留已从视觉树移除的控件引用。
            yield return null;
            yield return null;
        }

        void Click(string name)
        {
            var button = _root.Q<Button>(name);
            Assert.IsNotNull(button, name);
            Assert.IsTrue(button.enabledInHierarchy, name + " 应可用");
            Assert.Greater(button.worldBound.width, 0, name + " 应已布局");
            Assert.Greater(button.worldBound.height, 0, name + " 应已布局");
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }

        void ButtonEnabled(string name, bool expected)
        {
            var button = _root.Q<Button>(name);
            Assert.IsNotNull(button, name);
            Assert.AreEqual(expected, button.enabledSelf, name);
            if (expected) Assert.IsTrue(button.enabledInHierarchy, name);
        }

        IEnumerator Idle()
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while ((_game.Busy || S.Busy) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(_game.Busy || S.Busy, "离线操作必须有界收尾");
            yield return Layout();
        }

        IEnumerator Open()
        {
            Click("openPlan");
            yield return Layout();
            Assert.IsNotNull(_root.Q("planPanel"));
            Assert.IsNotNull(_root.Q<DropdownField>("planDisclosure"));
        }

        void Disclosure(int index)
        {
            var field = _root.Q<DropdownField>("planDisclosure");
            Assert.IsNotNull(field);
            Assert.IsTrue(field.enabledInHierarchy);
            field.value = field.choices[index];
            Assert.AreEqual(index, field.index, "必须经过真实下拉框值变更回调");
        }

        string Text() => string.Join("\n", _root.Q("planPanel").Query<Label>().ToList().Select(l => l.text));

        void FollowingWithPactFixture()
        {
            // 显式设置跟随、遮盖、凭据和居民地点前置；不是玩家从新局完成这些行为的证明。
            // 不设置 confided、custody 或 ally；合作必须由测试中的真实执行回执产生。
            W.PlayerPlace = W.Whelp.Place = W.Actors["pip"].Place = "docks";
            W.Actors["maren"].Place = "cathedral";
            W.Actors["maren"].Trust = 0.8f;
            W.Whelp.Following = true;
            W.Whelp.Hidden = true;
            W.Whelp.TrustPlayer = 0.6f;
            W.Whelp.Fear = 0.4f;
            W.Flags["whelp_hidden_by_player"] = W.ClockText;
            W.Items["pact_page"].Holder = "player";
            W.PlayerKnows.UnionWith(new[] { "b_maren_page", "b_maren_pact", "law:law_guards_dusk" });
            var anchor = _game.PlacesRoot.Find("Place_docks");
            Assert.IsNotNull(anchor);
            _game.Player.Teleport(anchor.position + new Vector3(-3, 0.2f, -3));
        }

        void Unchanged(DsWorldSnapshot before, int time)
        {
            var after = S.CaptureState();
            Assert.AreEqual(before.Json, after.Json, "提案、改条件草稿、修正和确认均不得写入世界");
            Assert.AreEqual(before.Hash, after.Hash);
            Assert.AreEqual(time, S.TimeLeftInPhase, "执行下一步之前不得耗时");
            Assert.IsEmpty(_game.ActivePlan.Results, "不得自动执行计划步骤");
            Assert.AreEqual("player", W.Items["pact_page"].Holder);
            Assert.IsFalse(W.Flags.Keys.Any(k => k.StartsWith("confided:", StringComparison.Ordinal)));
            Assert.IsFalse(W.Flags.Keys.Any(k => k.StartsWith("ally:", StringComparison.Ordinal)));
            Assert.IsFalse(W.Flags.ContainsKey("pact_custody:maren"));
            Assert.IsFalse(W.Beliefs.Any(b => b.Topic == "plan"), "试运行不能把计划认知写进真实居民");
        }

        static void Cooperation(DsPlanRun run, string helper)
        {
            var expected = helper == null ? DsPlanConstraints.Solo : DsPlanConstraints.WithHelper(helper);
            Assert.IsTrue(run.Constraints.HasCooperationIntent);
            Assert.AreEqual(helper, run.Constraints.RequiredHelper);
            string expectedJson = MiniJson.Write(expected.ToRecord());
            Assert.AreEqual(expectedJson, MiniJson.Write(run.Constraints.ToRecord()));
            var context = (Dictionary<string, object>)MiniJson.Parse(run.LatestPlanTrace.PlanContext);
            var frozen = context.Obj("player_constraints");
            Assert.IsNotNull(frozen, "首次与修正请求都必须携带已冻结的玩家条件");
            Assert.AreEqual(DsPlanConstraints.CooperationVersion, frozen.Str("version"));
            Assert.AreEqual(expectedJson, MiniJson.Write(frozen));
        }

        static bool GivesPact(DsPlanStep step) => step.Op == "act" && step.Verb == "give" && step.Item == "pact_page";

        static DsPlanStep ConfidePip() => new DsPlanStep
        {
            Id = "tell_pip", Op = "act", Verb = "confide", Target = "pip", Place = "docks", Why = "请皮普实际答应望风",
        };

        static string ScriptedPlan(bool extraDelivery)
        {
            var plan = new DsPlan { Goal = "请皮普帮忙送小烬归巢", Summary = "先请皮普望风，再带小烬等母亲降临。" };
            plan.Steps.Add(ConfidePip());
            if (extraDelivery) plan.Steps.Add(new DsPlanStep
            {
                Id = "give_maren", Op = "act", Verb = "give", Target = "maren", Item = "pact_page", Place = "cathedral",
                Why = "越权夹具：皮普合作条件没有授权向梅伦交付原件",
            });
            plan.Steps.Add(new DsPlanStep { Id = "home", Op = "wait", Place = "skull_hill", Until = "第4天·清晨" });
            return MiniJson.Write(plan.ToRecord());
        }

        [UnityTest]
        public IEnumerator 下拉独行由默认离线规划器生成无托付无交原件草稿_确认不写世界()
        {
            FollowingWithPactFixture();
            yield return Open();
            var field = _root.Q<DropdownField>("planDisclosure");
            Assert.AreEqual(7, field.choices.Count, "v2 追加选项，不能挤占旧 0 至 3 索引");
            CollectionAssert.AreEqual(new[] { "未限定", "不向居民托付", "只向梅伦托付", "只向皮普托付" },
                field.choices.Take(4).Select(choice => choice.Split('（')[0]));
            Assert.AreEqual(0, field.index);
            StringAssert.Contains("独行", field.choices[4]);
            StringAssert.Contains("必须请梅伦", field.choices[5]);
            StringAssert.Contains("必须请皮普", field.choices[6]);
            _root.Q<TextField>("planGoal").value = "请皮普或梅伦帮忙，把小烬送回母亲身边";
            Disclosure(4);
            yield return Layout();
            var before = S.CaptureState();
            int time = S.TimeLeftInPhase;
            Click("proposePlan");
            yield return Idle();
            var run = _game.ActivePlan;
            Assert.IsInstanceOf<RuleProvider>(S.Provider);
            Assert.AreEqual("awaiting_confirmation", run.Status, Text());
            Cooperation(run, null);
            Assert.IsTrue(run.Plan.Steps.All(s => s.Verb != "confide" && !GivesPact(s)), "独行不能偷偷保留托付或原件交付备选");
            Assert.IsTrue(run.LatestPlanTrace.PlanReport.Default.ReachesGoal);
            Assert.IsFalse(run.LatestPlanTrace.Receipt.Applied);
            StringAssert.Contains("本次已冻结的条件：独行", Text());
            StringAssert.Contains("不交出原件", Text());
            Unchanged(before, time);
            Assert.IsEmpty(run.AcceptedRequestIds);
            ButtonEnabled("confirmPlan", true);
            ButtonEnabled("executePlanStep", false);
            Click("confirmPlan");
            yield return Layout();
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual(0, run.NextIndex);
            Assert.AreEqual(1, run.AcceptedRequestIds.Count);
            Assert.IsTrue(run.ActiveSteps.All(run.Constraints.Allows));
            Unchanged(before, time);
            Assert.AreEqual(1, S.Traces.Count(t => t.Kind == AiTaskKind.Plan));
        }

        [UnityTest]
        public IEnumerator 必须皮普优先于目标里的梅伦_改选梅伦草稿不改冻结且重新提出才生效()
        {
            FollowingWithPactFixture();
            yield return Open();
            _root.Q<TextField>("planGoal").value = "请梅伦修女同行，帮我把小烬送回母亲身边";
            Disclosure(6);
            yield return Layout();
            var before = S.CaptureState();
            int time = S.TimeLeftInPhase;
            Click("proposePlan");
            yield return Idle();
            var pipRun = _game.ActivePlan;
            Assert.AreEqual("awaiting_confirmation", pipRun.Status, Text());
            Cooperation(pipRun, "pip");
            Assert.IsTrue(pipRun.Plan.Steps.Any(s => s.Verb == "confide" && s.Target == "pip"));
            Assert.IsTrue(pipRun.Plan.Steps.All(s => s.Verb != "confide" || s.Target == "pip"));
            Assert.IsFalse(pipRun.Plan.Steps.Any(GivesPact), "必须皮普不授权把原件额外交给梅伦");
            Assert.IsFalse(pipRun.Constraints.CooperationAchieved(W), "草稿中的承诺不等于实际 ally");
            StringAssert.Contains("必须求得皮普实际帮助", Text());
            Unchanged(before, time);
            Disclosure(5);
            yield return Layout();
            Click("closePlan");
            yield return Layout();
            yield return Open();
            Assert.AreEqual(5, _root.Q<DropdownField>("planDisclosure").index);
            Assert.AreSame(pipRun, _game.ActivePlan);
            Cooperation(pipRun, "pip");
            StringAssert.Contains("必须求得皮普实际帮助", Text());
            StringAssert.Contains("草稿里的条件不会改写此选择", Text());
            ButtonEnabled("confirmPlan", true);
            Unchanged(before, time);
            Assert.IsEmpty(pipRun.AcceptedRequestIds);
            Assert.AreEqual(1, S.Traces.Count(t => t.Kind == AiTaskKind.Plan));
            Click("proposePlan");
            yield return Idle();
            var marenRun = _game.ActivePlan;
            Assert.AreNotSame(pipRun, marenRun);
            Assert.AreEqual("abandoned", pipRun.Status);
            Assert.AreEqual("awaiting_confirmation", marenRun.Status, Text());
            Cooperation(marenRun, "maren");
            Assert.IsTrue(marenRun.Plan.Steps.Any(s => s.Verb == "confide" && s.Target == "maren"));
            Assert.IsTrue(marenRun.Plan.Steps.All(marenRun.Constraints.Allows));
            StringAssert.Contains("必须求得梅伦修女实际帮助", Text());
            Unchanged(before, time);
            Assert.IsEmpty(marenRun.AcceptedRequestIds);
            Assert.AreEqual(2, S.Traces.Count(t => t.Kind == AiTaskKind.Plan));
        }

        [UnityTest]
        public IEnumerator 皮普草稿额外交梅伦原件被拒_修正保留v2条件且确认执行后才实际结盟()
        {
            FollowingWithPactFixture();
            var provider = new Scripted();
            provider.Plans.Enqueue(ScriptedPlan(true));
            provider.Plans.Enqueue(ScriptedPlan(false));
            S.Provider = provider;
            yield return Open();
            Disclosure(6);
            yield return Layout();
            var before = S.CaptureState();
            int time = S.TimeLeftInPhase;
            Click("proposePlan");
            yield return Idle();
            var run = _game.ActivePlan;
            var rejected = run.LatestPlanTrace;
            Cooperation(run, "pip");
            Assert.IsTrue(rejected.PlanReport.Executable, "动作规则允许交付，拒绝必须来自玩家的 v2 合作条件");
            Assert.IsTrue(rejected.PlanReport.Default.ReachesGoal);
            Assert.IsFalse(run.MeetsObjective(rejected));
            Assert.AreEqual("planning", run.Status, Text());
            ButtonEnabled("confirmPlan", false);
            ButtonEnabled("executePlanStep", false);
            ButtonEnabled("replanPlan", true);
            StringAssert.Contains("原件交付不符合合作条件", Text());
            Unchanged(before, time);
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.AreEqual(1, provider.Packets.Count(p => p.Kind == AiTaskKind.Plan));
            Click("replanPlan");
            yield return Idle();
            Assert.AreSame(run, _game.ActivePlan);
            Assert.AreEqual("awaiting_confirmation", run.Status, Text());
            Assert.AreNotEqual(rejected.RequestId, run.LatestPlanTrace.RequestId);
            Cooperation(run, "pip");
            var context = (Dictionary<string, object>)MiniJson.Parse(run.LatestPlanTrace.PlanContext);
            Assert.AreEqual("player_constraint", context.Obj("rejected_proposal").Str("code"));
            Assert.AreEqual(rejected.RequestId, context.Obj("rejected_proposal").Str("request_id"));
            StringAssert.Contains("give_maren", run.LatestPlanTrace.PlanContext);
            Assert.IsTrue(run.Plan.Steps.Any(s => s.Verb == "confide" && s.Target == "pip"));
            Assert.IsTrue(run.Plan.Steps.All(run.Constraints.Allows));
            Assert.IsFalse(run.Plan.Steps.Any(GivesPact));
            Assert.AreEqual("pending", run.Replans.Single().Decision);
            Assert.IsTrue(run.Replans.Single().NewPlanExecutable);
            ButtonEnabled("confirmPlan", true);
            ButtonEnabled("executePlanStep", false);
            Unchanged(before, time);
            Assert.IsEmpty(run.AcceptedRequestIds);
            Assert.IsFalse(run.Constraints.CooperationAchieved(W));
            Click("confirmPlan");
            yield return Layout();
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual("accepted", run.Replans.Single().Decision);
            CollectionAssert.AreEqual(new[] { run.LatestPlanTrace.RequestId }, run.AcceptedRequestIds);
            Assert.AreEqual(0, run.NextIndex);
            Unchanged(before, time);
            Click("executePlanStep");
            yield return Idle();
            Assert.AreEqual("running", run.Status, "合作成立仍不等于归巢完成，后续步骤不可自动执行");
            Assert.AreEqual(1, run.NextIndex);
            Assert.AreEqual("tell_pip", run.Results.Single().StepId);
            Assert.AreEqual("success", run.Results.Single().Status);
            Assert.IsTrue(run.Results.Single().Receipts.Any(r => r.Applied));
            Assert.IsTrue(W.Flags.ContainsKey("confided:pip"));
            Assert.IsTrue(W.Flags.ContainsKey("ally:pip"), "必须由真实托付事务产生实际帮助承诺");
            Assert.IsTrue(run.Constraints.CooperationAchieved(W));
            Assert.IsFalse(W.Flags.ContainsKey("confided:maren"));
            Assert.IsFalse(W.Flags.ContainsKey("ally:maren"));
            Assert.IsFalse(W.Flags.ContainsKey("pact_custody:maren"));
            Assert.AreEqual("player", W.Items["pact_page"].Holder);
            Assert.AreEqual(time - 1, S.TimeLeftInPhase, "只执行一次托付，只消耗该步骤的一点时间");
            Assert.AreNotEqual(before.Hash, S.CaptureState().Hash);
            Assert.AreEqual(2, provider.Packets.Count(p => p.Kind == AiTaskKind.Plan));
            Assert.IsEmpty(provider.Plans);
        }
    }
}
