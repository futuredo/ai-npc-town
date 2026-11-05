using System.Collections;
using NUnit.Framework;
using Town.Core.World;
using Town.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Town.Tests.PlayMode
{
    /// <summary>真正进入 Play：Core 日程驱动的居民能否靠 NavMesh 实际走到目的地。不调用任何模型。</summary>
    public sealed class TownPlayModeTests
    {
        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Town_Graybox");
            yield return null;
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
        public IEnumerator 誓钟敲响后结算可用()
        {
            var game = Object.FindFirstObjectByType<TownGame>();
            game.Runtime.Advance(9 * 60 + 1);
            yield return null;
            Assert.AreEqual(1, game.Session.World.GetFlag(Ids.Flag.MarketStarted));
            Assert.IsTrue(game.Runtime.Rules.Bell.Rung);
            Assert.IsNotEmpty(Town.Core.Outcome.FestivalOutcome.Endings(game.Runtime.Rules));
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
