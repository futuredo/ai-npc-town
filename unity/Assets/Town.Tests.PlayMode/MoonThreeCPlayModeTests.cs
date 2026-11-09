using System.Collections;
using System.Linq;
using NUnit.Framework;
using Town.Unity.Moonlight;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Town.Tests.PlayMode
{
    public sealed class MoonThreeCPlayModeTests
    {
        MoonPlayerController _player;
        MoonThirdPersonCamera _camera;
        MoonRescueHud _hud;
        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync("Moonlight_Village");
            yield return null;
            _player = Object.FindFirstObjectByType<MoonPlayerController>();
            _camera = Object.FindFirstObjectByType<MoonThirdPersonCamera>();
            _hud = Object.FindFirstObjectByType<MoonRescueHud>();
            Assert.IsNotNull(_player); Assert.IsNotNull(_camera);
            _player.readHardwareInput = false; _camera.readHardwareInput = false;
        }

        [UnityTest]
        public IEnumerator 第三人称全屏透视_相机相对移动_起停与动作一致()
        {
            Assert.IsFalse(_camera.GetComponent<Camera>().orthographic);
            Assert.AreEqual(new Rect(0, 0, 1, 1), _camera.GetComponent<Camera>().rect);
            Assert.IsFalse(_hud.IsModalOpen);
            Assert.AreEqual(DisplayStyle.None, _hud.GetComponent<UIDocument>().rootVisualElement.Q("rescue-panel").resolvedStyle.display);
            var start = _player.transform.position;
            for (int i = 0; i < 25; i++) { _player.SimulateMotion(Vector2.up, false, .02f); yield return null; }
            Assert.Greater(Vector3.Distance(start, _player.transform.position), .5f);
            Assert.Greater(_player.GetComponentInChildren<MoonActorAnimation>().WalkWeight, .5f);
            for (int i = 0; i < 16; i++) { _player.SimulateMotion(Vector2.zero, false, .02f); yield return null; }
            yield return new WaitForSeconds(.2f);
            Assert.Less(_player.PlanarVelocity.magnitude, .01f);
            Assert.Less(_player.GetComponentInChildren<MoonActorAnimation>().WalkWeight, .05f);
            _camera.Orbit(90, 0); _camera.UpdatePose(1);
            var before = _player.transform.position;
            for (int i = 0; i < 20; i++) { _player.SimulateMotion(Vector2.up, false, .02f); yield return null; }
            Assert.Greater(_player.transform.position.x - before.x, .4f, "镜头转向后，前进必须沿新的相机方向");
        }

        [UnityTest]
        public IEnumerator 环境实体不穿墙_相机遮挡立即收近且恢复_缩放俯仰有限()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.position = _player.transform.position + Vector3.forward * 1.2f + Vector3.up;
                wall.transform.localScale = new Vector3(4, 3, .2f); Physics.SyncTransforms();
                float start = _player.transform.position.z;
                for (int i = 0; i < 60; i++) { _player.SimulateMotion(Vector2.up, true, .02f); yield return null; }
                Assert.Less(_player.transform.position.z - start, 1.05f, "CharacterController 必须被实体墙挡住");
                _player.ResetPosition(); _camera.ResetView(); _camera.UpdatePose(1);
                var focus = _player.transform.position + Vector3.up * 1.25f;
                var direction = (_camera.transform.position - focus).normalized;
                wall.transform.position = focus + direction * 2;
                wall.transform.rotation = Quaternion.LookRotation(direction);
                Physics.SyncTransforms(); _camera.UpdatePose(.02f);
                Assert.Less(_camera.ActualDistance, 2.1f);
                Assert.Greater(_camera.ActualDistance, .1f);
                wall.SetActive(false); Physics.SyncTransforms();
                for (int i = 0; i < 60; i++) _camera.UpdatePose(.02f);
                Assert.Greater(_camera.ActualDistance, 5f, "环境遮挡：" + _camera.Obstruction + "; player=" + _player.transform.position);
                _camera.Zoom(-1000); Assert.AreEqual(2.4f, _camera.Distance);
                _camera.Zoom(1000); Assert.AreEqual(8, _camera.Distance);
                _camera.Orbit(0, 1000); Assert.AreEqual(65, _camera.Pitch);
                _camera.Orbit(0, -1000); Assert.AreEqual(-8, _camera.Pitch);
            }
            finally { Object.Destroy(wall); }
        }

        [UnityTest]
        public IEnumerator 走近芬恩才能互动_隔墙拒绝_面板阻止移动_关闭恢复原暂停状态()
        {
            var interaction = _player.GetComponent<MoonPlayerInteraction>();
            interaction.readHardwareInput = false;
            interaction.RefreshTarget(); Assert.IsNotNull(interaction.Current, "出生朝向应能读到附近人物，而不是先找面板");
            Assert.IsTrue(interaction.TryInteract()); Assert.IsTrue(_hud.IsModalOpen);
            Assert.IsTrue(_hud.game.execution.Paused);
            var start = _player.transform.position;
            for (int i = 0; i < 10; i++) { _player.SimulateMotion(Vector2.up, true, .02f); yield return null; }
            var delta = _player.transform.position - start; delta.y = 0;
            Assert.Less(delta.magnitude, .01f, "输入文本/查看条件期间不得偷走角色");
            _hud.SetModal(false); Assert.IsFalse(_hud.game.execution.Paused);
            var point = interaction.points.First(p => p.kind == MoonInteractionKind.Discuss);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.position = (point.Focus + _player.transform.position + Vector3.up) * .5f;
                wall.transform.localScale = new Vector3(3, 3, .2f);
                Physics.SyncTransforms();
                interaction.RefreshTarget(); Assert.IsNull(interaction.Current, "隔墙不能远程提出或确认约定");
            }
            finally { Object.Destroy(wall); }
            _hud.game.execution.Paused = true; _hud.SetModal(true); _hud.SetModal(false);
            Assert.IsTrue(_hud.game.execution.Paused, "查看面板不能解除玩家原本的暂停");
            _hud.game.execution.Paused = false;
            _hud.SetModal(true);
            _hud.game.execution.ResetExperiment(); yield return null;
            Assert.IsFalse(_hud.IsModalOpen);
            Assert.IsFalse(_hud.game.execution.Paused, "重开不能继承旧面板暂停");
            Assert.Less(Vector3.Distance(_player.transform.position, start), .12f, "重开应连同玩家位置一起复位");
        }
    }
}
