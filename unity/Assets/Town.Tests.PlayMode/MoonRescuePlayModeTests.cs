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
    public sealed class MoonRescuePlayModeTests
    {
        MoonRescueGame _game;
        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("Moonlight_Rescue");
            yield return null;
            _game = Object.FindFirstObjectByType<MoonRescueGame>();
            Assert.IsNotNull(_game);
            Assert.AreEqual(3, _game.lights.Length);
        }

        [UnityTest]
        public IEnumerator 实际下路护送三灯与芬恩_动画随导航切换() => Escort("follow", -1);

        [UnityTest]
        public IEnumerator 实际上路护送三灯与芬恩_不以角色台词当获救() => Escort("split", 1);

        IEnumerator Escort(string fixture, int side)
        {
            Assert.IsTrue(_game.execution.SubmitFixture(fixture, "保持试过的月光，带上漂流灯").Success);
            Assert.AreEqual(RescuePhase.Preparation, _game.Chapter.Phase);
            Assert.AreEqual(0, _game.Chapter.SavedLights);
            Assert.IsTrue(_game.ConfirmDeparture());
            var animation = _game.execution.finn.GetComponentInChildren<MoonActorAnimation>();
            Assert.IsNotNull(animation);
            var visited = new bool[3]; bool animated = false;
            float deadline = Time.realtimeSinceStartup + 25;
            while (_game.Chapter.Phase == RescuePhase.Escort && Time.realtimeSinceStartup < deadline)
            {
                for (int i = 0; i < 3; i++) visited[i] |= _game.lights[i].transform.position.z * side > 2;
                animated |= animation.WalkWeight > 0.5f;
                yield return null;
            }
            Assert.IsTrue(visited.All(v => v), Diagnostics());
            Assert.IsTrue(animated, "实际位移应驱动步行动画，而非 T Pose 滑行");
            Assert.AreEqual(RescuePhase.Settled, _game.Chapter.Phase, Diagnostics());
            Assert.IsTrue(_game.Chapter.FinnSafe);
            Assert.AreEqual(3, _game.Chapter.SavedLights);
            StringAssert.Contains("没有人被写成灯芯", _game.Chapter.Ending);
            Assert.AreEqual(1, _game.execution.SuccessfulArrivalReceipts);
            Assert.AreEqual(3, _game.Chapter.Events.Count(e => e.StartsWith("漂流灯")));
            yield return new WaitForSeconds(0.3f);
            Assert.Less(animation.WalkWeight, 0.05f);
        }

        [UnityTest]
        public IEnumerator 局内UI使用已导入图片_切光暂停全部灯_重开不串局()
        {
            var hud = Object.FindFirstObjectByType<MoonRescueHud>();
            Assert.IsNotNull(hud.paperTexture); Assert.IsNotNull(hud.buttonTexture); Assert.IsNotNull(hud.titleTexture);
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            Assert.IsNotNull(root.Q<ScrollView>("rescue-panel"));
            Assert.IsNotNull(root.Q<TextField>("method"));
            Assert.IsFalse(_game.execution.showDeveloperPanel);
            _game.execution.SubmitFixture("follow", "灯灭就停，不要把没走完的灯算作获救");
            _game.ConfirmDeparture();
            float deadline = Time.realtimeSinceStartup + 5;
            while (_game.lights[0].transform.position.x < -3.3f && Time.realtimeSinceStartup < deadline) yield return null;
            _game.execution.ToggleLight(); yield return new WaitForSeconds(0.2f);
            var positions = _game.lights.Select(a => a.transform.position).ToArray();
            yield return new WaitForSeconds(0.4f);
            for (int i = 0; i < 3; i++) Assert.Less(Vector3.Distance(positions[i], _game.lights[i].transform.position), 0.03f);
            Assert.AreEqual(0, _game.Chapter.SavedLights);
            _game.FinishNow();
            Assert.AreEqual(RescuePhase.Settled, _game.Chapter.Phase);
            Assert.IsFalse(_game.Chapter.FinnSafe);
            StringAssert.Contains("没有有效到达回执", _game.Chapter.Ending);
            _game.execution.ResetExperiment(); yield return null;
            Assert.AreEqual(RescuePhase.Preparation, _game.Chapter.Phase);
            Assert.IsNull(_game.Chapter.Ending);
            Assert.AreEqual(0, _game.Chapter.Events.Count);
            Assert.AreEqual(0, _game.Chapter.SavedLights);
            for (int i = 0; i < 3; i++) Assert.Less(Vector3.Distance(_game.lights[i].transform.position, new Vector3(-4.5f, 0, (i - 1) * 0.3f)), 0.06f);
        }

        string Diagnostics() => $"Finn={_game.execution.Workshop.State}/{_game.execution.finn.transform.position:F3}, phase={_game.Chapter.Phase}, light={_game.execution.LightVerified}, saved={_game.Chapter.SavedLights}, feedback={_game.execution.Feedback}; " +
            string.Join("; ", _game.lights.Select(a => $"{a.name}: {a.transform.position:F3}, dest={a.destination:F3}, stopped={a.isStopped}, path={a.pathStatus}")) + "; " +
            string.Join("; ", _game.execution.Workshop.History.TakeLast(4).Select(r => r.Code + ":" + r.Text));
    }
}
