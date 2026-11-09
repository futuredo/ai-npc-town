using System.Collections;
using System.Linq;
using NUnit.Framework;
using Town.Core.Moonlight;
using Town.Unity.Moonlight;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Town.Tests.PlayMode
{
    public sealed class MoonVillagePlayModeTests
    {
        MoonRescueGame _game;
        VisualElement _ui;
        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("Moonlight_Village");
            yield return null;
            _game = Object.FindFirstObjectByType<MoonRescueGame>();
            _ui = Object.FindFirstObjectByType<MoonRescueHud>().GetComponent<UIDocument>().rootVisualElement;
            Assert.Greater(_game.execution.worldOrigin.magnitude, 100, "验证在现成场景世界位置执行，不是又回到零点技术台");
            Assert.IsTrue(_game.execution.finn.isOnNavMesh);
            Assert.IsNotNull(Object.FindFirstObjectByType<Terrain>());
        }

        [UnityTest]
        public IEnumerator 现成场景下路_界面确认_熄灯补救后完整结局() => CompleteThroughUi("route-follow", -1, true);

        [UnityTest]
        public IEnumerator 现成场景上路_界面确认_实际经过上路并完整结局() => CompleteThroughUi("route-split", 1, false);

        IEnumerator CompleteThroughUi(string route, int side, bool interrupt)
        {
            Submit(route); Submit("propose"); yield return null;
            Assert.AreEqual(MoonTaskState.AwaitingConfirmation, _game.execution.Workshop.State);
            Assert.AreEqual(0, _game.Chapter.SavedLights);
            Submit("depart"); yield return null;
            Assert.AreEqual(RescuePhase.Escort, _game.Chapter.Phase);
            if (interrupt)
            {
                float limit = Time.realtimeSinceStartup + 5;
                while (_game.execution.finn.transform.position.x < _game.execution.worldOrigin.x - 3.3f && Time.realtimeSinceStartup < limit) yield return null;
                Submit("light"); yield return new WaitForSeconds(.2f);
                var positions = _game.lights.Select(a => a.transform.position).ToArray();
                yield return new WaitForSeconds(.35f);
                for (int i = 0; i < positions.Length; i++) Assert.Less(Vector3.Distance(positions[i], _game.lights[i].transform.position), .03f);
                Assert.AreEqual(RescuePhase.Escort, _game.Chapter.Phase, "失光应能补救，不能提前宣告失败或获救");
                Submit("light"); yield return null;
            }
            var visited = new bool[3]; float deadline = Time.realtimeSinceStartup + 25;
            while (_game.Chapter.Phase == RescuePhase.Escort && Time.realtimeSinceStartup < deadline)
            {
                for (int i = 0; i < visited.Length; i++) visited[i] |= (_game.lights[i].transform.position.z - _game.execution.worldOrigin.z) * side > 2;
                yield return null;
            }
            Assert.IsTrue(visited.All(v => v), Diagnostics());
            Assert.AreEqual(RescuePhase.Settled, _game.Chapter.Phase, Diagnostics());
            Assert.IsTrue(_game.Chapter.FinnSafe); Assert.AreEqual(3, _game.Chapter.SavedLights);
            Assert.AreEqual(1, _game.execution.SuccessfulArrivalReceipts);
            StringAssert.Contains("没有人被写成灯芯", _game.Chapter.Ending);
            Submit("restart"); yield return null;
            Assert.AreEqual(RescuePhase.Preparation, _game.Chapter.Phase);
            Assert.IsNull(_game.Chapter.Ending); Assert.AreEqual(0, _game.Chapter.SavedLights);
        }

        [UnityTest]
        public IEnumerator 途中收束与重开需要二次确认_不能单击抹掉进度()
        {
            Submit("propose"); yield return null; Submit("depart"); yield return null;
            int generation = _game.execution.Workshop.Generation;
            Submit("restart"); yield return null;
            Assert.AreEqual(generation, _game.execution.Workshop.Generation);
            Assert.AreEqual(RescuePhase.Escort, _game.Chapter.Phase);
            Submit("finish"); yield return null;
            Assert.AreEqual(RescuePhase.Escort, _game.Chapter.Phase);
            Submit("finish"); yield return null;
            Assert.AreEqual(RescuePhase.Settled, _game.Chapter.Phase);
            Assert.AreEqual(0, _game.Chapter.SavedLights);
            Submit("restart"); yield return null;
            Assert.AreEqual(generation + 1, _game.execution.Workshop.Generation);
            Assert.AreEqual(RescuePhase.Preparation, _game.Chapter.Phase);
        }

        void Submit(string name)
        {
            var hud = Object.FindFirstObjectByType<MoonRescueHud>();
            hud.SetModal(true);
            var button = _ui.Q<Button>(name);
            Assert.IsNotNull(button, name); Assert.IsTrue(button.enabledInHierarchy, name);
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
            hud.SetModal(false);
        }
        string Diagnostics() => $"state={_game.execution.Workshop.State}, phase={_game.Chapter.Phase}, light={_game.execution.LightVerified}, waypoint={_game.execution.Workshop.Waypoint}, feedback={_game.execution.Feedback}; " +
            string.Join("; ", _game.lights.Select(a => $"{a.name}: {a.transform.position}, stopped={a.isStopped}, path={a.pathStatus}"));
    }
}
