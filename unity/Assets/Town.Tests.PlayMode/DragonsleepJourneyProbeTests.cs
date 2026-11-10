using System.Collections;
using System.Linq;
using NUnit.Framework;
using Town.Unity.Dragonsleep;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Town.Tests.PlayMode
{
    public sealed class DragonsleepJourneyProbeTests
    {
        [UnityTest]
        public IEnumerator 默认离线场景完整短局到团聚_现场重开并生成第二份草稿()
        {
            var factory = DragonsleepGame.ProviderFactory;
            DragonsleepJourneyProbe probe = null;
            try
            {
                DragonsleepGame.ProviderFactory = () => new Town.Core.Dragonsleep.RuleProvider();
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                probe = new GameObject("JourneyProbeTest").AddComponent<DragonsleepJourneyProbe>(); probe.Begin();
                float deadline = Time.realtimeSinceStartup + 185;
                while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(probe.Finished, "有界验证器必须完成");
                Assert.IsTrue(probe.Result.passed, probe.Result.failure);
                Assert.IsTrue(probe.Result.withMother && probe.Result.finalDawn && probe.Result.stageReunited);
                Assert.IsTrue(probe.Result.restarted && probe.Result.secondProposal);
                Assert.Greater(probe.Result.appliedReceipts, 0); Assert.Greater(probe.Result.successfulSteps, 0);
                Assert.Greater(probe.Result.replans, 0, "固定种子应覆盖真实失败补救");
                Assert.IsTrue(probe.Result.realExecutionFailure); Assert.Greater(probe.Result.choiceButtons, 0);
                Assert.Greater(probe.Result.voidSteps, 0); Assert.AreEqual(0, probe.Result.runtimeErrors);
                Assert.Greater(probe.Result.sessionOwnerCallbacks, 0);
                Assert.AreEqual(0, probe.Result.sessionWrongThreadCallbacks);
            }
            finally
            {
                if (probe != null) Object.Destroy(probe.gameObject);
                DragonsleepGame.ProviderFactory = factory;
            }
        }

        [UnityTest]
        public IEnumerator 显式低信任夹具_默认离线按钮补救_交付和托付分别提交()
        {
            var factory = DragonsleepGame.ProviderFactory; DragonsleepJourneyProbe probe = null;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider();
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                probe = new GameObject("SocialProbeTest").AddComponent<DragonsleepJourneyProbe>();
                probe.Begin(seed: 109, socialFixture: true);
                float deadline = Time.realtimeSinceStartup + 185;
                while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(probe.Finished); Assert.IsTrue(probe.Result.passed, probe.Result.failure);
                Assert.IsTrue(probe.Result.socialFixture && probe.Result.actualCooperationRejection);
                Assert.IsTrue(probe.Result.deliveryCostVisible && probe.Result.custodyDelivered && probe.Result.allyMaren);
                Assert.AreEqual(1, probe.Result.replans); Assert.AreEqual(2, probe.Result.planCalls);
                Assert.IsFalse(probe.Result.finalDawn, "社会前缀夹具不冒充完整归巢局。");
                Assert.AreEqual(0, probe.Result.runtimeErrors);
            }
            finally
            {
                if (probe != null) Object.Destroy(probe.gameObject);
                DragonsleepGame.ProviderFactory = factory;
            }
        }

        [UnityTest]
        public IEnumerator 实际地图按钮的移动由SDK提交_零耗时且不调用模型()
        {
            var factory = DragonsleepGame.ProviderFactory; DragonsleepJourneyProbe probe = null;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider();
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                probe = new GameObject("SceneProbeTest").AddComponent<DragonsleepJourneyProbe>();
                probe.Begin(sceneOnly: true);
                float deadline = Time.realtimeSinceStartup + 15;
                while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(probe.Finished); Assert.IsTrue(probe.Result.passed, probe.Result.failure);
                Assert.AreEqual(2, probe.Result.sceneCommits); Assert.AreEqual(0, probe.Result.runtimeErrors);
                Assert.IsFalse(probe.Result.finalDawn, "只验证地图入口，不冒充完整计划迁移。");
            }
            finally
            {
                if (probe != null) Object.Destroy(probe.gameObject);
                DragonsleepGame.ProviderFactory = factory;
            }
        }

        [UnityTest]
        public IEnumerator 三种显式合作目标_真实按钮完整局仅按实际盟友和团聚完成()
        {
            var factory = DragonsleepGame.ProviderFactory;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider();
                foreach (string mode in new[] { "solo", "maren", "pip" })
                {
#if UNITY_EDITOR
                    yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                        new LoadSceneParameters(LoadSceneMode.Single));
#else
                    yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                    var probe = new GameObject("CooperationProbeTest").AddComponent<DragonsleepJourneyProbe>();
                    try
                    {
                        probe.Begin(seed: 253421, cooperationMode: mode);
                        float deadline = Time.realtimeSinceStartup + 185;
                        while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                        Assert.IsTrue(probe.Finished); Assert.IsTrue(probe.Result.passed, mode + ": " + probe.Result.failure);
                        Assert.IsTrue(probe.Result.objectiveAchieved && probe.Result.withMother && probe.Result.finalDawn);
                        Assert.AreEqual(0, probe.Result.runtimeErrors);
                        if (mode == "solo") Assert.AreEqual(0, probe.Result.confideCalls);
                        else Assert.Greater(probe.Result.confideCalls, 0);
                        if (mode != "maren") Assert.AreEqual(0, probe.Result.pactGiveCalls);
                    }
                    finally { if (probe != null) Object.Destroy(probe.gameObject); }
                }
            }
            finally { DragonsleepGame.ProviderFactory = factory; }
        }

        [UnityTest]
        public IEnumerator 实际观察与地图日间SDK推进_耗时和世界一并提交()
        {
            var factory = DragonsleepGame.ProviderFactory; DragonsleepJourneyProbe probe = null;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider();
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                probe = new GameObject("DayProbeTest").AddComponent<DragonsleepJourneyProbe>(); probe.Begin(dayOnly: true);
                float deadline = Time.realtimeSinceStartup + 20;
                while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(probe.Finished); Assert.IsTrue(probe.Result.passed, probe.Result.failure);
                Assert.AreEqual(2, probe.Result.dayCommits); Assert.AreEqual(2, probe.Result.sceneCommits);
                Assert.AreEqual(0, probe.Result.runtimeErrors);
            }
            finally
            {
                if (probe != null) Object.Destroy(probe.gameObject);
                DragonsleepGame.ProviderFactory = factory;
            }
        }

        [UnityTest]
        public IEnumerator 最后黄昏被捕夹具_真实按钮立即救援并在期限前归巢()
        {
            var factory = DragonsleepGame.ProviderFactory; DragonsleepJourneyProbe probe = null;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider();
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                probe = new GameObject("LateRescueProbeTest").AddComponent<DragonsleepJourneyProbe>();
                probe.Begin(seed: 109, lateRescue: true);
                float deadline = Time.realtimeSinceStartup + 30;
                while (!probe.Finished && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(probe.Finished); Assert.IsTrue(probe.Result.passed, probe.Result.failure);
                Assert.IsTrue(probe.Result.lateRescue && probe.Result.objectiveAchieved && probe.Result.withMother && probe.Result.finalDawn);
                Assert.AreEqual(0, probe.Result.confideCalls, "既有盟友无需临近期限再托付");
                Assert.AreEqual(0, probe.Result.pactGiveCalls); Assert.AreEqual(0, probe.Result.runtimeErrors);
            }
            finally
            {
                if (probe != null) Object.Destroy(probe.gameObject);
                DragonsleepGame.ProviderFactory = factory;
            }
        }

        [UnityTest]
        public IEnumerator 普通最终跨夜后立即重开_旧揭示协程不能填回新局详情()
        {
            var factory = DragonsleepGame.ProviderFactory; bool reduced = DsUiMotion.Reduced;
            try
            {
                DragonsleepGame.ProviderFactory = () => new RuleProvider(); DsUiMotion.Reduced = false;
#if UNITY_EDITOR
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#else
                yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
                yield return null; yield return null;
                var game = Object.FindFirstObjectByType<DragonsleepGame>();
                var root = game.GetComponent<UIDocument>().rootVisualElement;
                void Click(string name)
                {
                    var button = root.Q<Button>(name); Assert.IsNotNull(button); Assert.IsTrue(button.enabledInHierarchy);
                    button.Focus(); using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
                }
                Click("beginJourney"); yield return null;
                // 专门复现协程窗口的晚局夹具；完整新局另由上面的默认提供方验证器覆盖。
                game.Session.World.Day = 3; game.Session.World.Phase = DayPhase.Dusk;
                game.Hud.StageRevealSeconds = 0.05f;
                Click("openMap"); yield return null; Click("endPhase");
                float deadline = Time.realtimeSinceStartup + 12;
                while (game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsNotNull(game.Ending); int generation = game.Session.World.SessionGen;
                Click("restartJourney"); yield return null;
                Assert.AreEqual(generation + 1, game.Session.World.SessionGen);
                Assert.IsEmpty(game.Session.Traces); Assert.IsEmpty(game.Session.RuleEvents); Assert.IsNull(game.ActivePlan);
                Assert.IsTrue(root.Query<Label>().ToList().Any(l => l.text == "新的一局，尚无调用。"));
                yield return new WaitForSecondsRealtime(8 * game.Hud.StageRevealSeconds + 0.1f);
                Assert.AreEqual(0, root.Q("lensDetail").childCount); Assert.AreEqual(0, root.Q("lensStages").childCount);
                Assert.IsTrue(root.Query<Label>().ToList().Any(l => l.text == "新的一局，尚无调用。"));
                Assert.IsNull(game.Ending); Assert.AreEqual("market", game.Session.World.PlayerPlace);
                Click("openPlan"); yield return null;
                Assert.IsFalse(root.Q("planPanel").Query<Label>().ToList().Any(l => (l.text ?? "").Contains("历史步骤")));
                Assert.IsFalse(root.Q<Button>("confirmPlan").enabledInHierarchy);
                Assert.IsFalse(root.Q<Button>("executePlanStep").enabledInHierarchy);
            }
            finally { DragonsleepGame.ProviderFactory = factory; DsUiMotion.Reduced = reduced; }
        }
    }
}
