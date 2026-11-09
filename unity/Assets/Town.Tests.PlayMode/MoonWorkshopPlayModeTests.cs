using System.Collections;
using NUnit.Framework;
using Town.Core.Moonlight;
using Town.Unity.Moonlight;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Town.Tests.PlayMode
{
    public sealed class MoonWorkshopPlayModeTests
    {
        MoonWorkshopGame _game;
        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("Moonlight_Workshop");
            yield return null;
            _game = Object.FindFirstObjectByType<MoonWorkshopGame>();
            Assert.IsNotNull(_game);
            Assert.Less(Mathf.Abs(_game.finn.transform.position.y), 0.15f, "导航面必须贴合可见平台，不得用放宽到达半径掩盖碰撞错误");
        }

        [UnityTest]
        public IEnumerator 引路法实际经过下路并到达_候选同意不直接完成()
        {
            Assert.IsTrue(_game.SubmitFixture("follow", "让小月亮带路，我不替你承诺跳过去").Success);
            Assert.AreEqual(MoonTaskState.AwaitingConfirmation, _game.Workshop.State);
            Assert.AreEqual(0, _game.SuccessfulArrivalReceipts);
            _game.ConfirmAgreement();
            bool sawLower = false;
            float deadline = Time.realtimeSinceStartup + 20;
            while (_game.Workshop.State == MoonTaskState.Travelling && Time.realtimeSinceStartup < deadline)
            { sawLower |= _game.finn.transform.position.z < -2; yield return null; }
            Assert.IsTrue(sawLower, _game.Feedback);
            Assert.AreEqual(MoonTaskState.Completed, _game.Workshop.State, Diagnostics());
            Assert.AreEqual(1, _game.SuccessfulArrivalReceipts);
            Assert.Less(Vector3.Distance(_game.finn.transform.position, new Vector3(4, 0, 0)), 0.5f);
            Assert.AreEqual(2, _game.Workshop.FreeEnergy);
        }

        [UnityTest]
        public IEnumerator 分光法被真实碰撞体遮挡时停在起点_移开后稳定两秒走上路()
        {
            Assert.IsTrue(_game.SubmitFixture("split", "用两束光照住符文，保持亮着再走").Success);
            _game.ToggleOccluder();
            _game.ConfirmAgreement();
            Vector3 before = _game.finn.transform.position;
            yield return new WaitForSeconds(0.6f);
            Assert.IsFalse(_game.LightVerified);
            Assert.Less(Vector3.Distance(before, _game.finn.transform.position), 0.02f);
            Assert.IsFalse(_game.Workshop.CanMove);
            _game.ToggleOccluder();
            yield return new WaitForSeconds(1f);
            Assert.IsTrue(_game.LightVerified);
            Assert.Less(Vector3.Distance(before, _game.finn.transform.position), 0.02f, "未满两秒不得出发");
            bool sawUpper = false;
            float deadline = Time.realtimeSinceStartup + 20;
            while (_game.Workshop.State == MoonTaskState.Travelling && Time.realtimeSinceStartup < deadline)
            { sawUpper |= _game.finn.transform.position.z > 2; yield return null; }
            Assert.IsTrue(sawUpper, _game.Feedback);
            Assert.AreEqual(MoonTaskState.Completed, _game.Workshop.State, _game.Feedback);
        }

        [UnityTest]
        public IEnumerator 拒绝澄清不移动_切光中止后可重开且代际提升()
        {
            _game.SubmitFixture("decline", "你闭眼跳过去吧");
            Assert.AreEqual(MoonTaskState.None, _game.Workshop.State);
            _game.SubmitFixture("clarify", "帮我一下");
            Assert.IsNull(_game.Workshop.Agreement);
            _game.SubmitFixture("follow", "跟月光走"); _game.ConfirmAgreement();
            float deadline = Time.realtimeSinceStartup + 5;
            while (_game.finn.transform.position.x < -3.3f && Time.realtimeSinceStartup < deadline) yield return null;
            _game.ToggleLight();
            yield return new WaitForSeconds(0.2f);
            Vector3 before = _game.finn.transform.position;
            yield return new WaitForSeconds(0.4f);
            Assert.Less(Vector3.Distance(before, _game.finn.transform.position), 0.03f);
            Assert.AreEqual(MoonTaskState.Travelling, _game.Workshop.State, "中途切光是可补救等待，不是执行失败：" + Diagnostics());
            Assert.AreNotEqual(MoonTaskState.Completed, _game.Workshop.State);
            int generation = _game.Workshop.Generation;
            _game.ResetExperiment(); yield return null;
            Assert.AreEqual(generation + 1, _game.Workshop.Generation);
            Assert.AreEqual(MoonTaskState.None, _game.Workshop.State);
            Assert.AreEqual(2, _game.Workshop.FreeEnergy);
            Assert.Less(Vector3.Distance(_game.finn.transform.position, new Vector3(-4, 0, 0)), 0.05f, Diagnostics());
        }

        [UnityTest]
        public IEnumerator 途中书签恢复位置与约定_暂停且重新验光后才能继续()
        {
            _game.SubmitFixture("follow", "保持月光再带我过去"); _game.ConfirmAgreement();
            float deadline = Time.realtimeSinceStartup + 5;
            while (_game.finn.transform.position.x < -3.3f && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(_game.RecordBookmark());
            var saved = _game.finn.transform.position;
            yield return new WaitForSeconds(0.4f);
            int generation = _game.Workshop.Generation;
            _game.ToggleLight();
            Assert.IsTrue(_game.RestoreBookmark()); yield return null;
            Assert.AreEqual(generation + 1, _game.Workshop.Generation);
            Assert.IsTrue(_game.Paused);
            Assert.IsTrue(_game.Workshop.LightOn);
            Assert.AreEqual(MoonTaskState.Travelling, _game.Workshop.State);
            Assert.Less(Vector3.Distance(saved, _game.finn.transform.position), 0.05f);
            yield return new WaitForSeconds(0.3f);
            Assert.Less(Vector3.Distance(saved, _game.finn.transform.position), 0.05f);
            _game.Paused = false;
            deadline = Time.realtimeSinceStartup + 15;
            while (_game.Workshop.State == MoonTaskState.Travelling && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(MoonTaskState.Completed, _game.Workshop.State, Diagnostics());
            Assert.AreEqual(1, _game.SuccessfulArrivalReceipts);
        }

        string Diagnostics()
        {
            var agent = _game.finn;
            return $"{_game.Feedback}; position={agent.transform.position:F4}, next={agent.nextPosition:F4}, destination={agent.destination:F4}, remaining={agent.remainingDistance}, path={agent.pathStatus}/{agent.hasPath}/{agent.pathPending}, stopped={agent.isStopped}, waypoint={_game.Workshop.Waypoint}, light={_game.LightVerified}, canMove={_game.Workshop.CanMove}";
        }
    }
}
