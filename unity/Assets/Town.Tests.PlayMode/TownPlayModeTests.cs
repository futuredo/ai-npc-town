using System.Collections;
using NUnit.Framework;
using Town.Core.World;
using Town.Core.Runtime;
using Town.Core.Rules;
using Town.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Town.Tests.PlayMode
{
    /// <summary>真正进入 Play：Core 日程驱动的居民能否靠 NavMesh 实际走到目的地。不调用任何模型。</summary>
    public sealed class TownPlayModeTests
    {
        private string _saveDirectory;
        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Town_Graybox");
            yield return null;
            _saveDirectory = System.IO.Path.Combine(Application.temporaryCachePath, "town-tests-" + System.Guid.NewGuid().ToString("N"));
            Object.FindFirstObjectByType<TownGame>().SaveDirectoryOverride = _saveDirectory;
        }

        [TearDown]
        public void CleanTestSave()
        {
            if (_saveDirectory != null && System.IO.Directory.Exists(_saveDirectory)) System.IO.Directory.Delete(_saveDirectory, true);
        }

        [UnityTest]
        public IEnumerator URP实际运行且全部质量档已配置_灰盒材质可用()
        {
            yield return new WaitForEndOfFrame();
            Assert.IsNotNull(UnityEngine.Rendering.RenderPipelineManager.currentPipeline);
            Assert.AreEqual("UnityEngine.Rendering.Universal.UniversalRenderPipeline",
                UnityEngine.Rendering.RenderPipelineManager.currentPipeline.GetType().FullName);
            var pipeline = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            Assert.IsNotNull(pipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++)
                Assert.AreEqual(pipeline, QualitySettings.GetRenderPipelineAssetAt(i), QualitySettings.names[i]);
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            foreach (var material in renderer.sharedMaterials)
            {
                if (!material) continue;
                Assert.IsNotNull(material.shader, renderer.name);
                Assert.IsTrue(material.shader.isSupported, material.name);
                Assert.AreNotEqual("Hidden/InternalErrorShader", material.shader.name, material.name);
                Assert.AreNotEqual("Standard", material.shader.name, material.name);
            }
        }

        [UnityTest]
        public IEnumerator 芬恩按日程从民居走到旅店再到广场()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            Assert.IsNotNull(game);
            Assert.IsTrue(game.TryGetNpc(Ids.Npc.Xiaochuan, out var finn));
            Assert.IsTrue(game.TryGetLocation(Ids.Loc.Store, out var store));
            Assert.IsTrue(game.TryGetLocation(Ids.Loc.Plaza, out var plaza));
            game.realSecondsPerGameMinute = 0.25f;

            yield return WaitForGameTime(game, 9, 40, 60f);
            Assert.Less(Flat(finn.transform.position, store.StandPoint), 3.5f, "9:40 芬恩应在旅店");

            yield return WaitForGameTime(game, 10, 20, 60f);
            Assert.Less(Flat(finn.transform.position, plaza.StandPoint), 3.5f, "10:20 芬恩应在广场");
        }

        [UnityTest]
        public IEnumerator 墨钟敲响后结算可用()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            game.Runtime.Advance(9 * 60 + 1);
            yield return null;
            Assert.AreEqual(1, game.Session.World.GetFlag(Ids.Flag.MarketStarted));
            Assert.IsTrue(game.Runtime.Rules.Bell.Rung);
            Assert.IsNotEmpty(Town.Core.Outcome.FestivalOutcome.Endings(game.Runtime.Rules));
        }

        [UnityTest]
        public IEnumerator 玩家完整路径实际到达_拒绝地图外目标_暂停时不漂移()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            var player = game.Player;
            Assert.IsNotNull(player);
            Assert.IsTrue(game.TryGetLocation(Ids.Loc.BellTower, out var tower));
            var nav = player.Navigation;
            Assert.IsTrue(nav.TryNavigate(tower.StandPoint));
            Assert.AreNotEqual(Ids.Loc.BellTower, game.Session.World.PlayerLocationId, "点击目的地不等于到达");
            float start = Time.realtimeSinceStartup;
            while (nav.State == NavigationState.Moving && Time.realtimeSinceStartup - start < 15f) yield return null;
            yield return null;
            Assert.AreEqual(NavigationState.Arrived, nav.State);
            Assert.AreEqual(Ids.Loc.BellTower, game.Session.World.PlayerLocationId);
            Assert.IsTrue(game.Execute(new TownCommand(TownCommandKind.MeetKeeper)).Success);
            Assert.IsTrue(game.Execute(new TownCommand(TownCommandKind.ReadTablet)).Success);
            Assert.IsFalse(nav.TryNavigate(new Vector3(1000, 0, 1000)));
            Assert.AreEqual(NavigationState.Unreachable, nav.State);
            Assert.IsTrue(game.TryGetLocation(Ids.Loc.Plaza, out var plaza));
            Assert.IsTrue(nav.TryNavigate(plaza.StandPoint));
            game.Paused = true;
            yield return null;
            Vector3 before = player.transform.position;
            yield return new WaitForSeconds(0.3f);
            Assert.Less(Vector3.Distance(before, player.transform.position), 0.02f);
            Assert.IsFalse(game.CanInteract(Ids.Npc.Amu), "同一逻辑地点也不能隔空交谈");
        }

        [UnityTest]
        public IEnumerator 镜头跟随缩放有边界_重开清空移动和剧情()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            Assert.IsNotNull(game.CameraRig);
            Assert.AreEqual(game.Player.transform, game.CameraRig.Target);
            game.CameraRig.Zoom(-1000);
            Assert.AreEqual(8f, game.CameraRig.Distance);
            game.CameraRig.Zoom(1000);
            Assert.AreEqual(35f, game.CameraRig.Distance);
            game.CameraRig.Orbit(40, -1000);
            Assert.AreEqual(30f, game.CameraRig.Pitch);
            game.Runtime.Advance(540);
            int gen = game.Session.World.SessionGen;
            game.ResetSession();
            yield return null;
            Assert.AreEqual(gen + 1, game.Session.World.SessionGen);
            Assert.AreEqual(StoryChapter.BlankPage, game.Runtime.Rules.Story.Chapter);
            Assert.IsFalse(game.Runtime.Rules.Story.HasBookmark);
            Assert.AreEqual(NavigationState.Idle, game.Player.Navigation.State);
            Assert.AreEqual(23f, game.CameraRig.Distance);
        }

        [UnityTest]
        public IEnumerator Unity本地存读档恢复剧情坐标并提升代际()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            Assert.IsTrue(game.TryGetLocation(Ids.Loc.BellTower, out var tower));
            game.Player.ResetPosition(tower.StandPoint);
            game.ReportPlayerPosition(game.Player.transform.position);
            Assert.IsTrue(game.Execute(new TownCommand(TownCommandKind.MeetKeeper)).Success);
            game.Runtime.Advance(540);
            Assert.IsTrue(game.Execute(new TownCommand(TownCommandKind.ReadLastPage)).Success);
            var before = RuntimeEvidence.Hash(game.Runtime);
            var position = game.Player.transform.position;
            Assert.IsTrue(game.SaveSession(), game.SystemMessage);
            int gen = game.Session.World.SessionGen;
            game.ResetSession();
            Assert.IsTrue(game.LoadSession(), game.SystemMessage);
            yield return null;
            Assert.IsTrue(game.Paused);
            Assert.AreEqual(gen + 2, game.Session.World.SessionGen);
            Assert.AreEqual(before, RuntimeEvidence.Hash(game.Runtime));
            Assert.Less(Vector3.Distance(position, game.Player.transform.position), 0.05f);
            Assert.IsTrue(game.Runtime.Rules.Story.HasReadLastPage);
            // 仅删除测试使用的独立存档目录，不能污染用户自己的槽位。
        }

        private static IEnumerator WaitForGameTime(TownGame game, int hour, int minute, float timeoutSeconds)
        {
            var target = GameTime.At(1, hour, minute);
            float start = Time.realtimeSinceStartup;
            while (game.Session.World.Now < target && Time.realtimeSinceStartup - start < timeoutSeconds) yield return null;
            Assert.GreaterOrEqual(game.Session.World.Now.TotalMinutes, target.TotalMinutes, "等待游戏时间超时");
            // 给导航一点时间收尾（Core 已判定到达，表现层可能还差几步）
            float settle = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - settle < 2f) yield return null;
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }
    }
}
