using System;
using System.Collections;
using System.Linq;
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
    /// <summary>规则七：真实主场景、默认离线规划器与按钮；不读取密钥，不请求外网。</summary>
    public sealed class DragonsleepSocialRecoveryPresentationTests
    {
        DragonsleepGame _game; VisualElement _root;
        Func<IAiProvider> _factory; bool _reduced;
        DragonsleepSession S => _game.Session;
        DsWorld W => S.World;
        string Hash() => S.CaptureState().Hash;
        string Text(string name = "planPanel") => string.Join("\n", _root.Q(name).Query<Label>().ToList().Select(l => l.text));
        [UnitySetUp]
        public IEnumerator Load()
        {
            _factory = DragonsleepGame.ProviderFactory; _reduced = DsUiMotion.Reduced;
            DragonsleepGame.ProviderFactory = () => new RuleProvider(); DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null; yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>(); Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Click("beginJourney"); _game.Player.enabled = false; yield return null;
            Assert.IsInstanceOf<RuleProvider>(S.Provider); Assert.IsTrue(S.Provider.IsSimulated);
        }
        [UnityTearDown]
        public IEnumerator Restore()
        {
            try { if (_game != null) yield return Idle(); }
            finally
            {
                DragonsleepGame.ProviderFactory = _factory; DsUiMotion.Reduced = _reduced;
                if (_game != null) _game.Player.enabled = true;
            }
        }
        void Click(string name)
        {
            var b = _root.Q<Button>(name); Assert.IsNotNull(b, name); Assert.IsTrue(b.enabledInHierarchy, name);
            b.Focus(); using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }
        IEnumerator Idle()
        {
            float end = Time.realtimeSinceStartup + 12;
            while ((_game.Busy || S.Busy) && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsFalse(_game.Busy || S.Busy, "离线操作必须有界收尾"); yield return null;
        }
        void Fixture()
        {
            // 仅设置前置；custody、ally 与收回必须来自真实事务。覆盖重复出示、交付与取回的安全骰序。
            W.Seed = Enumerable.Range(1, 10000).First(seed => Enumerable.Range(1, 6).All(i => DsSeededRoll.Instance.Next(seed, i) >= 0.5));
            W.RollCounter = 0; W.PlayerPlace = W.Whelp.Place = W.Actors["maren"].Place = "cathedral";
            W.Actors["maren"].Trust = 0.05f; W.Items["pact_page"].Holder = "player";
            W.Whelp.Following = true; W.Whelp.Hidden = true; W.Whelp.TrustPlayer = 0.6f; W.Whelp.Fear = 0.4f;
            W.Flags["whelp_hidden_by_player"] = W.ClockText;
            W.PlayerKnows.UnionWith(new[] { "b_maren_pact", "b_maren_page", "law:law_guards_dusk" });
            var anchor = _game.PlacesRoot.Find("Place_cathedral"); Assert.IsNotNull(anchor);
            _game.Player.Teleport(anchor.position + new Vector3(-3, 0.2f, -3));
        }
        void Unchanged(string hash, int time, int results = 0)
        {
            Assert.AreEqual(hash, Hash()); Assert.AreEqual(time, S.TimeLeftInPhase);
            Assert.AreEqual("player", W.Items["pact_page"].Holder); Assert.IsFalse(W.Flags.ContainsKey("pact_custody:maren"));
            Assert.IsFalse(W.Flags.ContainsKey("ally:maren")); Assert.AreEqual(results, _game.ActivePlan.Results.Count);
        }
        IEnumerator Repair()
        {
            Click("openPlan"); yield return null;
            _root.Q<TextField>("planGoal").value = "请梅伦修女同行护送小烬";
            var field = _root.Q<DropdownField>("planDisclosure"); field.value = field.choices[2]; Assert.AreEqual(2, field.index);
            string before = Hash(); int time = S.TimeLeftInPhase; Click("proposePlan"); yield return Idle(); var run = _game.ActivePlan;
            CollectionAssert.AreEqual(new object[] { "maren" }, (System.Collections.IEnumerable)run.Constraints.ToRecord()["allowed_confidants"]);
            var old = run.LatestPlanTrace; var prefix = old.PlanReport.Plan.ActiveSteps(old.PlanReport.Plan.DefaultSelection());
            CollectionAssert.AreEqual(new[] { "show", "confide" }, prefix.Take(2).Select(s => s.Verb));
            Assert.IsTrue(old.PlanReport.Executable); Assert.AreEqual("awaiting_confirmation", run.Status);
            Unchanged(before, time); Assert.IsFalse(old.Receipt.Applied); Assert.IsFalse(W.Flags.ContainsKey("requested:pact_custody:maren"));
            Click("confirmPlan"); yield return null; Unchanged(before, time);
            Click("executePlanStep"); yield return Idle();
            Assert.AreEqual("success", run.Results.Single().Status);
            Assert.IsTrue(run.Results.Single().Receipts.Any(r => r.Applied));
            Assert.IsFalse(W.Flags.ContainsKey("requested:pact_custody:maren")); Assert.IsFalse(W.Flags.ContainsKey("ally:maren"));
            Click("executePlanStep"); yield return Idle(); Assert.AreEqual("needs_replan", run.Status);
            Assert.AreEqual("confide_custody_required", run.LastReasonCode.Code); Assert.IsTrue(run.Results[1].Receipts.Any(r => r.Applied));
            Assert.IsTrue(W.Flags.ContainsKey("requested:pact_custody:maren")); before = Hash(); time = S.TimeLeftInPhase; int history = run.Results.Count;
            Click("replanPlan"); yield return Idle(); Assert.AreSame(run, _game.ActivePlan);
            Assert.AreEqual("awaiting_confirmation", run.Status, Text()); Assert.AreNotEqual(old.RequestId, run.LatestPlanTrace.RequestId);
            var steps = run.Plan.ActiveSteps(run.Plan.DefaultSelection());
            CollectionAssert.AreEqual(new[] { "give", "confide" }, steps.Take(2).Select(s => s.Verb));
            Assert.IsTrue(steps.Take(2).All(s => s.Target == "maren")); Assert.AreEqual("pact_page", steps[0].Item);
            Assert.AreEqual("pending", run.Replans.Single().Decision); CollectionAssert.AreEqual(new[] { old.RequestId }, run.AcceptedRequestIds);
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf); Unchanged(before, time, history);
        }
        IEnumerator Act(string text, bool take = false)
        {
            // 离线关键词解析器看不到居民手中的原件；取回用同一真实执行器的结构化步骤入口。
            var task = take ? S.RunStepAsync(new DsPlanStep { Op = "act", Verb = "take", Target = "maren", Item = "pact_page" }.ToProposal(W.PlayerPlace), text)
                : S.ActAsync(text); float end = Time.realtimeSinceStartup + 8;
            while (!task.IsCompleted && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsTrue(task.IsCompleted); Assert.IsFalse(task.IsCanceled); Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            Assert.AreEqual(VerdictKind.Accepted, task.Result.Verdict.Kind, task.Result.Presented);
            Assert.IsTrue(task.Result.Receipt?.Applied == true, task.Result.Presented); yield return null;
        }
        IEnumerator Cooperate()
        {
            string before = Hash(); int time = S.TimeLeftInPhase; var run = _game.ActivePlan; int history = run.Results.Count;
            Click("confirmPlan"); yield return null; Assert.AreEqual("running", run.Status); Unchanged(before, time, history);
            Assert.AreEqual("accepted", run.Replans.Single().Decision); Assert.AreEqual(2, run.AcceptedRequestIds.Count);
            Click("executePlanStep"); yield return Idle(); Assert.AreEqual("maren", W.Items["pact_page"].Holder);
            Assert.IsTrue(W.Flags.ContainsKey("pact_custody:maren")); Assert.IsFalse(W.Flags.ContainsKey("ally:maren"));
            Assert.AreEqual(1, run.NextIndex); Assert.AreEqual(history + 1, run.Results.Count);
            Assert.IsTrue(run.Results.Last().Receipts.Any(r => r.Applied)); Assert.Less(W.Actors["maren"].Trust, 0.5f);
            Click("executePlanStep"); yield return Idle(); Assert.IsTrue(W.Flags.ContainsKey("ally:maren"));
            Assert.AreEqual(2, run.NextIndex); Assert.AreEqual(history + 2, run.Results.Count);
            Assert.IsTrue(run.Results.Last().Receipts.Any(r => r.Applied)); Assert.AreEqual("success", run.Results.Last().Status);
        }
        [UnityTest]
        public IEnumerator 原件补救经按钮重新确认_交付与托付各自提交且预告代价可读()
        {
            Fixture(); yield return Repair(); string preview = Text();
            string delivery = Text("planStep_" + _game.ActivePlan.Plan.ActiveSteps(_game.ActivePlan.Plan.DefaultSelection())[0].Id);
            yield return Cooperate(); StringAssert.Contains("交付", delivery); StringAssert.Contains("原件", delivery);
            Assert.IsTrue(preview.Contains("不再持有") || preview.Contains("失去原件") || preview.Contains("不能再向总管"),
                "确认前须中文说明交付原件的控制权代价，不能只写尝试行动或交付核验：\n" + preview);
        }
        [UnityTest]
        public IEnumerator 原件已离手的旧草稿拒绝确认_不执行不改世界()
        {
            Fixture(); yield return Repair(); W.Items["pact_page"].Holder = "place:cathedral";
            string before = Hash(); int time = S.TimeLeftInPhase; Click("confirmPlan"); yield return null;
            Assert.AreEqual(before, Hash()); Assert.AreEqual(time, S.TimeLeftInPhase);
            Assert.AreEqual("place:cathedral", W.Items["pact_page"].Holder); Assert.AreEqual("needs_replan", _game.ActivePlan.Status);
            Assert.Greater(_game.ActivePlan.Results.Count, _game.PlanResultOffset);
            Assert.IsTrue(_game.ActivePlan.Results.Skip(_game.PlanResultOffset)
                .All(r => r.Status == "void" && r.Receipts.Count == 0));
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf); Assert.IsFalse(W.Flags.ContainsKey("ally:maren"));
        }
        [UnityTest]
        public IEnumerator 重复出示不刷信任_原件合作成立后真实取回会撤销合作()
        {
            Fixture(); yield return Act("向梅伦修女出示契约页"); float trust = W.Actors["maren"].Trust;
            yield return Act("再次向梅伦修女出示契约页"); Assert.AreEqual(trust, W.Actors["maren"].Trust);
            Assert.Less(trust, 0.5f); Assert.AreEqual("player", W.Items["pact_page"].Holder);
            yield return Repair(); yield return Cooperate(); yield return Act("从梅伦修女手中取回契约页", take: true);
            Assert.AreEqual("player", W.Items["pact_page"].Holder);
            Assert.IsFalse(W.Flags.ContainsKey("pact_custody:maren")); Assert.IsFalse(W.Flags.ContainsKey("ally:maren"));
        }
    }
}
