using System;
using System.Collections;
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
    /// <summary>M4 真实主场景纯表现验收；提供方全部离线，不读取密钥，不发真实 API 请求。</summary>
    public sealed class DragonsleepStagePresentationTests
    {
        DragonsleepGame _game;
        Func<IAiProvider> _factory;
        bool _reduced;
        Gate _gate;

        DragonsleepSession S => _game.Session;
        DsWorld W => S.World;
        DragonsleepStageView V => _game.Stage;
        DragonCircle Circle => V.Mother.GetComponent<DragonCircle>();
        DsFollowCamera Follow => Camera.main.GetComponent<DsFollowCamera>();

        sealed class Gate : IAiProvider
        {
            public string Name => "offline-stage-gate";
            public bool IsSimulated => true;
            public volatile bool Entered;
            readonly TaskCompletionSource<ProviderResult> _pending = new TaskCompletionSource<ProviderResult>();

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Entered = true;
                return _pending.Task;
            }

            public void Release() => _pending.TrySetResult(new ProviderResult
            {
                ProviderName = Name,
                Simulated = true,
                Error = "fixture_done"
            });
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            _factory = DragonsleepGame.ProviderFactory;
            _reduced = DsUiMotion.Reduced;
            DragonsleepGame.ProviderFactory = () => new RuleProvider();
            DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null;
            yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            Assert.IsNotNull(_game);
            Assert.IsNotNull(V);
            Click("beginJourney");
            Assert.IsNotNull(Follow);
            Assert.IsTrue(Follow.enabled);
            _game.Player.enabled = false;
            Assert.IsTrue(S.Provider.IsSimulated);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            _gate?.Release();
            float deadline = Time.realtimeSinceStartup + 5;
            while (_game != null && (_game.Busy || S.Busy) && Time.realtimeSinceStartup < deadline)
                yield return null;
            DragonsleepGame.ProviderFactory = _factory;
            DsUiMotion.Reduced = _reduced;
            if (_game != null) _game.Player.enabled = true;
        }

        void Click(string name)
        {
            var b = _game.GetComponent<UIDocument>().rootVisualElement.Q<Button>(name);
            Assert.IsNotNull(b);
            Assert.IsTrue(b.enabledInHierarchy);
            b.Focus();
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        void Sync(string place)
        {
            string key = place == "castle" ? "castle_gate" : place == "skull_hill" ? "dragon_skull" : place;
            var anchor = _game.PlacesRoot.Find("Place_" + key);
            Assert.IsNotNull(anchor, place);
            _game.Player.Teleport(anchor.position + new Vector3(-3, 0.2f, -3));
        }

        void Ready(int seed = 20261108)
        {
            // 只构造现身、信任、地点与骰序前置；跟随/捕获/救援/团聚必须由下面的 Session 事务产生。
            W.Seed = seed;
            W.RollCounter = seed == 20261108 ? 3 : 0;
            W.PlayerPlace = W.Whelp.Place = "workshop";
            W.Whelp.Hidden = false;
            W.Whelp.TrustPlayer = 0.8f;
            W.Whelp.Fear = 0.2f;
            Sync(W.PlayerPlace);
        }

        IEnumerator Wait(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 8;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "离线事务不得悬挂");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            Assert.IsFalse(task.IsCanceled);
        }

        IEnumerator Quiet()
        {
            var before = S.CaptureState();
            int rolls = W.RollCounter, time = S.TimeLeftInPhase;
            string clock = W.ClockText;
            for (int i = 0; i < 4; i++) yield return null;
            Assert.AreEqual(before.Json, S.CaptureState().Json);
            Assert.AreEqual(before.Hash, S.CaptureState().Hash);
            Assert.AreEqual(rolls, W.RollCounter);
            Assert.AreEqual(time, S.TimeLeftInPhase);
            Assert.AreEqual(clock, W.ClockText);
        }

        IEnumerator Act(string text)
        {
            var task = S.ActAsync(text);
            yield return Wait(task);
            Assert.IsTrue(task.Result.Receipt?.Applied == true, task.Result.Presented);
            Assert.AreEqual(VerdictKind.Accepted, task.Result.Verdict.Kind);
            yield return Quiet();
        }

        IEnumerator ResetStage()
        {
            _game.Restart();
            Assert.AreEqual("market", W.PlayerPlace);
            yield return Quiet();
            Assert.Less(Vector3.Distance(_game.Player.transform.position,
                _game.PlacesRoot.Find("Place_market").position + new Vector3(-3, 0.2f, -3)), 0.1f);
            Assert.IsTrue(Follow.enabled);
            Assert.AreEqual(WhelpStageMode.Hidden, V.Frame.Mode);
            Assert.IsFalse(V.Cage.gameObject.activeSelf);
            Assert.IsFalse(V.Cloak.gameObject.activeSelf);
            Assert.IsFalse(V.Frame.MotherDescended);
            Assert.IsFalse(V.MotherSettled);
            Assert.IsFalse(V.IsFleeing);
            Assert.IsTrue(Circle.enabled);
            Assert.IsFalse(_game.Whelp.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator 真实带上和藏好事务_跟随玩家且斗篷替代实体_重启清斗篷()
        {
            Ready();
            yield return Act("带上小烬跟我走");
            Assert.AreEqual(WhelpStageMode.Following, V.Frame.Mode);
            var before = _game.Whelp.position;
            _game.Player.Teleport(_game.Player.transform.position + Vector3.right * 3);
            yield return Quiet();
            Assert.Less(Vector3.Distance(_game.Whelp.position - before, Vector3.right * 3), 0.05f);
            yield return Act("安静地用旅行斗篷把小烬藏好");
            Assert.AreEqual(WhelpStageMode.Concealed, V.Frame.Mode);
            Assert.IsTrue(V.Cloak.gameObject.activeSelf);
            Assert.IsFalse(_game.Whelp.gameObject.activeSelf);
            var cloak = V.Cloak.position;
            _game.Player.Teleport(_game.Player.transform.position + Vector3.forward * 2);
            yield return Quiet();
            Assert.Less(Vector3.Distance(V.Cloak.position - cloak, Vector3.forward * 2), 0.05f);
            yield return ResetStage();
        }

        [UnityTest]
        public IEnumerator 普通动效有界追随_不是只在规则提交时瞬移一次()
        {
            Ready();
            yield return Act("带上小烬跟我走");
            DsUiMotion.Reduced = false;
            var before = _game.Whelp.position;
            _game.Player.Teleport(_game.Player.transform.position + Vector3.right * 4);
            float deadline = Time.realtimeSinceStartup + 3;
            while (Vector3.Distance(before, _game.Whelp.position) < 0.1f && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.Greater(Vector3.Distance(before, _game.Whelp.position), 0.1f);
            yield return Quiet();
        }

        [UnityTest]
        public IEnumerator 岗位遵循清晨正午黄昏与泄露例外_多帧不推进世界()
        {
            Sync(W.PlayerPlace);
            W.Day = 2;
            Assert.GreaterOrEqual(V.GuardPosts.Count, 3);
            foreach (var phase in new[] { DayPhase.Dawn, DayPhase.Noon, DayPhase.Dusk })
            {
                W.Phase = phase;
                yield return Quiet();
                Assert.AreEqual(phase == DayPhase.Noon, V.Frame.Guards["market"]);
                foreach (var post in V.GuardPosts)
                {
                    bool watched = DsGuards.IsWatched(W, post.Key);
                    Assert.AreEqual(watched, V.Frame.Guards[post.Key]);
                    Assert.AreEqual(watched, post.Value.gameObject.activeSelf, post.Key + phase);
                }
            }
            W.Beliefs.Add(new DsBelief
            {
                Id = "stage_leak",
                Owner = "osric",
                Topic = "plan",
                Text = "夹具：总管已听说护送计划"
            });
            yield return Quiet();
            Assert.IsTrue(DsGuards.IsWatched(W, "skull_hill"));
            Assert.IsTrue(V.GuardPosts["skull_hill"].gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator 真实闯岗回执关笼_黄昏真实救援开笼并恢复跟随()
        {
            // 固定种子：带上 0.5141、巡逻 0.0313、救援 0.3294；不搜索种子。
            Ready(1);
            yield return Act("带上小烬跟我走");
            var captured = S.EnterPlace("castle");
            Sync("castle");
            Assert.IsTrue(captured.WhelpCaptured);
            Assert.IsTrue(captured.Receipt.Applied);
            yield return Quiet();
            Assert.AreEqual(WhelpStageMode.Captured, V.Frame.Mode);
            Assert.AreEqual("osric", V.Frame.Captor);
            Assert.IsTrue(V.Cage.gameObject.activeSelf);
            Assert.IsTrue(_game.Whelp.gameObject.activeSelf);
            var position = _game.Whelp.position;
            _game.Player.Teleport(_game.Player.transform.position + Vector3.right * 3);
            yield return Quiet();
            Assert.Less(Vector3.Distance(position, _game.Whelp.position), 0.05f);
            W.Phase = DayPhase.Dusk;
            W.PlayerKnows.Add("law:law_guards_dusk");
            yield return Act("趁黄昏换岗救出小烬");
            Assert.IsTrue(W.Flags.ContainsKey("whelp_rescued"));
            Assert.IsNull(W.Whelp.CapturedBy);
            Assert.IsFalse(V.Cage.gameObject.activeSelf);
            Assert.AreEqual(WhelpStageMode.Following, V.Frame.Mode);
        }

        [UnityTest]
        public IEnumerator 真实遇见总管受惊逃回码头_短暂逃跑后隐藏且演出不改世界()
        {
            Ready();
            yield return Act("带上小烬跟我走");
            // 夹具：黄昏无岗且恐惧接近阈值；逃离效果只能来自真实 EnterPlace 裁决。
            W.Phase = DayPhase.Dusk;
            W.Whelp.Fear = 0.75f;
            yield return Quiet();
            Assert.AreEqual(WhelpStageMode.Following, V.Frame.Mode);
            Assert.IsTrue(_game.Whelp.gameObject.activeSelf);
            Assert.AreEqual("market", W.Actors["osric"].Place);

            DsUiMotion.Reduced = false;
            var position = _game.Whelp.position;
            float started = Time.time;
            var fled = S.EnterPlace("market");
            Sync("market");
            Assert.IsNotNull(fled);
            Assert.AreEqual(VerdictKind.Partial, fled.Verdict.Kind);
            Assert.IsTrue(fled.Receipt.Applied);
            Assert.IsFalse(W.Whelp.Following);
            Assert.AreEqual("docks", W.Whelp.Place);
            Assert.IsTrue(W.Whelp.Hidden);
            Assert.IsTrue(W.Flags.ContainsKey("whelp_fled"));
            var beforeAnimation = S.CaptureState();

            float deadline = Time.realtimeSinceStartup + 4;
            while (!V.Frame.Fled && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(V.Frame.Fled);
            Assert.IsTrue(V.IsFleeing);
            Assert.IsTrue(_game.Whelp.gameObject.activeSelf);
            while (V.IsFleeing && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(V.IsFleeing, "逃跑演出必须在有界时间内结束");
            Assert.GreaterOrEqual(Time.time - started, 1.15f);
            Assert.IsFalse(_game.Whelp.gameObject.activeSelf);
            Assert.AreEqual(WhelpStageMode.Hidden, V.Frame.Mode);
            Assert.Greater(Vector3.Distance(position, _game.Whelp.position), 0.1f);
            Assert.AreEqual(beforeAnimation.Json, S.CaptureState().Json);
            Assert.AreEqual(beforeAnimation.Hash, S.CaptureState().Hash);
            yield return Quiet();
        }

        [UnityTest]
        public IEnumerator 计划提案不能触发降落_真实终局裁决才团聚_重启恢复盘旋()
        {
            Ready();
            yield return Act("带上小烬跟我走");
            var before = S.CaptureState().Hash;
            var plan = S.PlanAsync("把小烬送到母亲身边团聚");
            yield return Wait(plan);
            yield return Quiet();
            Assert.IsNotNull(plan.Result.PlanReport?.Plan);
            StringAssert.Contains("母亲", plan.Result.PlanReport.Plan.Summary);
            Assert.AreEqual(before, S.CaptureState().Hash);
            Assert.IsFalse(plan.Result.Receipt.Applied);
            Assert.IsFalse(V.Frame.MotherDescended);
            Assert.IsTrue(Circle.enabled);
            W.Day = 3;
            W.Phase = DayPhase.Dusk;
            Assert.IsTrue(S.EnterPlace("skull_hill").Receipt.Applied);
            Sync("skull_hill");
            W.Day = 4;
            W.Phase = DayPhase.Dawn;
            var direct = S.RunAsync(AiTaskKind.Direct, "gm", null);
            yield return Wait(direct);
            yield return Quiet();
            Assert.IsTrue(direct.Result.Receipt.Applied);
            Assert.IsTrue(W.Whelp.WithMother);
            Assert.AreEqual(WhelpStageMode.Reunited, V.Frame.Mode);
            Assert.IsTrue(V.Frame.MotherDescended);
            Assert.IsTrue(V.MotherSettled);
            Assert.IsFalse(Circle.enabled);
            Assert.Less(Vector3.Distance(_game.Whelp.position, V.Mother.position), 4);
            _game.EndPhase();
            float deadline = Time.realtimeSinceStartup + 8;
            while (_game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(_game.Busy);
            Assert.IsNotNull(_game.Ending);
            Assert.IsFalse(Follow.enabled);
            Assert.IsTrue(_game.Hud.ModalOpen);
            Click("viewEndingScene");
            yield return Quiet();
            Assert.IsFalse(_game.Hud.ModalOpen);
            Click("reopenEnding");
            yield return Quiet();
            Assert.IsTrue(_game.Hud.ModalOpen);
            Click("viewEndingScene");
            yield return ResetStage();
            Assert.Less(Vector3.Distance(Camera.main.transform.position, _game.Player.transform.position),
                Follow.Distance + Follow.Height + 3);
        }

        [UnityTest]
        public IEnumerator 其它真实终局裁决不会把未团聚雏龙演成团聚_重启清笼()
        {
            foreach (string ending in new[] { "iron_cage", "iron_cage_saved", "far_sea", "unresolved" })
            {
                Ready();
                W.Day = 4;
                // 夹具：已有扣押前置；终局效果仍来自下方真实 Direct 裁决。
                W.Whelp.CapturedBy = ending.StartsWith("iron") ? "osric" : ending == "far_sea" ? "vesna" : null;
                if (ending == "iron_cage_saved") W.Items["pact_page"].Holder = "player";
                var direct = S.RunAsync(AiTaskKind.Direct, "gm", null);
                yield return Wait(direct);
                Assert.IsTrue(direct.Result.Receipt?.Applied == true);
                yield return Quiet();
                Assert.IsTrue(V.Frame.MotherDescended);
                Assert.AreEqual(ending, DsEndings.Evaluate(W).Id);
                Assert.IsFalse(W.Whelp.WithMother);
                Assert.AreNotEqual(WhelpStageMode.Reunited, V.Frame.Mode);
                Assert.AreEqual(W.Whelp.CapturedBy, V.Frame.Captor);
                Assert.AreEqual(ending.StartsWith("iron"), V.Cage.gameObject.activeSelf);
                if (ending == "far_sea") Assert.IsFalse(_game.Whelp.gameObject.activeSelf);
                yield return ResetStage();
            }
        }

        [UnityTest]
        public IEnumerator 终局未团聚不能借地图或直接改Transform把雏龙带到母亲身边()
        {
            Ready();
            yield return Act("带上小烬跟我走");
            // 夹具只跳至期限；母龙降临仍由真实 Session 事务产生，小烬确实留在工坊。
            W.Day = 4;
            W.Phase = DayPhase.Dawn;
            var direct = S.RunAsync(AiTaskKind.Direct, "gm", null);
            yield return Wait(direct);
            Assert.IsTrue(direct.Result.Receipt?.Applied == true);
            yield return Quiet();
            Assert.IsTrue(V.Frame.FinalDawn);
            Assert.IsTrue(V.Frame.MotherDescended);
            Assert.AreEqual(WhelpStageMode.Following, V.Frame.Mode);
            Assert.AreEqual("workshop", W.Whelp.Place);
            Assert.IsFalse(W.Whelp.WithMother);
            Assert.AreEqual("unresolved", DsEndings.Evaluate(W).Id);

            var before = S.CaptureState();
            var playerPosition = _game.Player.transform.position;
            var whelpPosition = _game.Whelp.position;
            _game.TravelTo("dragon_skull");
            Assert.Less(Vector3.Distance(playerPosition, _game.Player.transform.position), 0.01f);
            yield return Quiet();
            Assert.Less(Vector3.Distance(playerPosition, _game.Player.transform.position), 0.01f);
            Assert.AreEqual(before.Hash, S.CaptureState().Hash);

            // 对抗夹具：仅移动 Unity Transform，不制造 Core 移动或团聚事实。
            Sync("skull_hill");
            Assert.Greater(Vector3.Distance(playerPosition, _game.Player.transform.position), 10);
            yield return Quiet();
            Assert.Less(Vector3.Distance(whelpPosition, _game.Whelp.position), 0.05f);
            Assert.Less(Vector3.Distance(_game.Whelp.position, _game.PlacesRoot.Find("Place_workshop").position), 5);
            Assert.Greater(Vector3.Distance(_game.Whelp.position, V.Mother.position), 10);
            Assert.AreEqual("workshop", W.PlayerPlace);
            Assert.AreEqual("workshop", W.Whelp.Place);
            Assert.IsFalse(W.Whelp.WithMother);
            Assert.AreNotEqual(WhelpStageMode.Reunited, V.Frame.Mode);
            Assert.AreEqual(before.Json, S.CaptureState().Json);
            Assert.AreEqual(before.Hash, S.CaptureState().Hash);
        }

        [UnityTest]
        public IEnumerator 等待提供方时冻结表现快照且不读取世界_释放后恢复同步()
        {
            Ready();
            yield return Quiet();
            var before = S.CaptureState().Hash;
            var frame = V.Frame;
            var whelp = W.Whelp;
            _gate = new Gate();
            S.Provider = _gate;
            _game.Submit(LoomMode.Act, "观察四周");
            Assert.IsTrue(_game.Busy);
            Assert.IsTrue(S.Busy);
            try
            {
                float enteredDeadline = Time.realtimeSinceStartup + 5;
                while (!_gate.Entered && Time.realtimeSinceStartup < enteredDeadline) yield return null;
                Assert.IsTrue(_gate.Entered, "冻结已完成且真实提供方挂起后，才能毒化表现读取哨兵");
                // 毒化只读哨兵：任何忙时 Capture 都会报错，释放提供方前恢复夹具。
                W.Whelp = null;
                for (int i = 0; i < 4; i++) yield return null;
                Assert.AreSame(frame, V.Frame);
            }
            finally
            {
                W.Whelp = whelp;
                _gate.Release();
            }
            float deadline = Time.realtimeSinceStartup + 5;
            while (_game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsFalse(_game.Busy);
            Assert.AreEqual(before, S.CaptureState().Hash);
            Assert.AreNotSame(frame, V.Frame);
            S.Provider = new RuleProvider();
            yield return Act("带上小烬跟我走");
            Assert.AreEqual(WhelpStageMode.Following, V.Frame.Mode);
        }
    }
}
