using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Content;
using Town.Core.Outcome;
using Town.Core.Rules;
using Town.Core.Runtime;
using Town.Core.Social;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;
using L = Town.Core.World.Ids.Loc;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Core.Tests
{
    /// <summary>《未竟之镇》v3 文字剧情与旧玩法隔离验证，不依赖素材、密钥或网络。</summary>
    public sealed class StoryRulesTests
    {
        private static void AdvanceTo(TownRuntime rt, int hour, int minute = 0) =>
            rt.Advance(GameTime.At(1, hour, minute).TotalMinutes - rt.Session.World.Now.TotalMinutes);

        [Test]
        public void 新局从空白页开始_守钟人不加入居民或墨钟计数()
        {
            using var rt = new TownRuntime();
            Assert.That(rt.Rules.Story.Chapter, Is.EqualTo(StoryChapter.BlankPage));
            Assert.That(rt.Rules.Story.History.Single().Id, Is.EqualTo("blank_page"));
            Assert.That(rt.Rules.Story.HasBookmark, Is.False);
            Assert.That(rt.Session.World.Npcs.Keys, Is.EquivalentTo(Ids.Npc.All));
            Assert.That(rt.Session.World.Npcs.Count, Is.EqualTo(3));
            Assert.That(rt.Session.World.Version, Is.Zero, "开场提示不伪造世界提交");
        }

        [TestCase("missing_hammer", 9, 10)]
        [TestCase("finn_morning", 11, 40)]
        [TestCase("fermentation", 12, 0)]
        [TestCase("finn_afternoon", 13, 40)]
        [TestCase("closing", 15, 0)]
        [TestCase("arch_deadline", 16, 50)]
        [TestCase("preparation_bell", 17, 0)]
        [TestCase("inkfall", 18, 0)]
        public void 剧情在指定分钟恰好触发一次(string id, int hour, int minute)
        {
            using var rt = new TownRuntime();
            int delta = GameTime.At(1, hour, minute).TotalMinutes - rt.Session.World.Now.TotalMinutes;
            rt.Advance(delta - 1);
            Assert.That(rt.Rules.Story.History.Any(b => b.Id == id), Is.False);
            rt.Advance(1);
            Assert.That(rt.Rules.Story.History.Single(b => b.Id == id).At, Is.EqualTo(GameTime.At(1, hour, minute)));
            rt.Rules.Tick();
            rt.Advance(1);
            Assert.That(rt.Rules.Story.History.Count(b => b.Id == id), Is.EqualTo(1));
            Assert.That(rt.Journal.Entries.Count(e => e.Kind == "story" && e.Detail.StartsWith(id + ":", StringComparison.Ordinal)), Is.EqualTo(1));
        }

        [Test]
        public void 一次快进整天与逐分钟推进的剧情和结局一致()
        {
            using var fast = new TownRuntime();
            using var minute = new TownRuntime();
            fast.Advance(9 * 60);
            for (int i = 0; i < 9 * 60; i++) minute.Advance(1);
            Assert.That(fast.Rules.Story.History.Select(b => (b.Id, b.At, b.Text)),
                Is.EqualTo(minute.Rules.Story.History.Select(b => (b.Id, b.At, b.Text))));
            Assert.That(fast.Rules.Story.History.Count, Is.EqualTo(9));
            Assert.That(fast.Rules.Story.Chapter, Is.EqualTo(StoryChapter.Inkfall));
            Assert.That(FestivalOutcome.Endings(fast.Rules).Select(e => e.Id),
                Is.EqualTo(FestivalOutcome.Endings(minute.Rules).Select(e => e.Id)));
        }

        [Test]
        public void 钟楼交谈和读石板有前置条件_书签只发一次且不改认知()
        {
            using var rt = new TownRuntime();
            Assert.That(rt.Execute(r => r.MeetKeeper()).Success, Is.False);
            Assert.That(rt.Rules.Story.ReadTablet(out var away).Success, Is.False);
            Assert.That(away, Is.Null);
            rt.SetPlayerLocation(L.BellTower);
            Assert.That(rt.Rules.Story.ReadTablet(out var locked).Success, Is.False);
            Assert.That(locked, Is.Null);
            int count = rt.Session.Knowledge.AllForDebug.Count;
            var met = rt.Execute(r => r.MeetKeeper());
            Assert.That(met.Success, Is.True);
            Assert.That(met.Message, Does.Contain("一人之信不落墨").And.Contain("一人之言不落墨").And.Contain("读者之信不落墨"));
            int version = rt.Session.World.Version;
            Assert.That(rt.Execute(r => r.MeetKeeper()).Success, Is.True);
            Assert.That(rt.Rules.Story.ReadTablet(out var readings).Success, Is.True);
            Assert.That(readings.Count, Is.EqualTo(rt.Rules.Bell.Read().Count));
            Assert.That(rt.Session.World.Version, Is.EqualTo(version));
            Assert.That(rt.Session.Knowledge.AllForDebug.Count, Is.EqualTo(count));
            Assert.That(rt.Session.Ledger.All, Is.Empty);
            Assert.That(rt.Rules.Story.History.Count(b => b.Id == "bookmark"), Is.EqualTo(1));
        }

        [TestCase(0, 0, "还没有人这么想。")]
        [TestCase(1, 1, "只有一个人这么想。")]
        [TestCase(2, 1, "两个人信了，可都是听同一个人说的。")]
        [TestCase(2, 2, "这件事已经够了，墨会落下来的。")]
        public void 石板点评只按人数与源头数量解释(int believers, int origins, string expected)
        {
            var reading = new BellReading
            {
                Believers = Enumerable.Range(0, believers).Select(i => "resident_" + i).ToArray(),
                Origins = Enumerable.Range(0, origins).Select(i => "origin_" + i).ToArray(),
            };
            Assert.That(reading.KeeperComment, Is.EqualTo(expected));
        }

        [Test]
        public void 剧情Tick本身不改世界认知关系或日程()
        {
            using var rt = new TownRuntime();
            rt.Session.World.AdvanceTime(8 * 60);
            int version = rt.Session.World.Version;
            int beliefs = rt.Session.Knowledge.AllForDebug.Count;
            var npcs = rt.Session.World.Npcs.Values.Select(n => (n.Id, n.LocationId, n.TravelTo, n.Activity, n.CurrentTaskId)).ToArray();
            rt.Rules.Story.Tick();
            Assert.That(rt.Session.World.Version, Is.EqualTo(version));
            Assert.That(rt.Session.Knowledge.AllForDebug.Count, Is.EqualTo(beliefs));
            Assert.That(rt.Session.Ledger.All, Is.Empty);
            Assert.That(rt.Session.World.Npcs.Values.Select(n => (n.Id, n.LocationId, n.TravelTo, n.Activity, n.CurrentTaskId)), Is.EqualTo(npcs));
            Assert.That(rt.Session.Relations.Get(N.Amu, Ids.Player), Is.EqualTo(RelationLevel.Neutral));
        }

        [Test]
        public void 锤子及时归还后不再发出缺锤截止警告()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.Shelter);
            rt.Execute(r => r.InspectShelter());
            rt.Execute(r => r.TakeHammer());
            rt.SetPlayerLocation(L.Plaza);
            rt.Execute(r => r.ReturnHammer());
            AdvanceTo(rt, 17);
            Assert.That(rt.Rules.Story.History.Single(b => b.Id == "missing_hammer").Text, Does.Contain("已经回到"));
            Assert.That(rt.Rules.Story.History.Any(b => b.Id == "arch_deadline"), Is.False);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.EqualTo(100));
        }

        [Test]
        public void 最后一页钟响前不可读_钟响后须在钟楼且只拆一次()
        {
            using var rt = new TownRuntime();
            Assert.Throws<InvalidOperationException>(() => FestivalOutcome.LastPage(rt.Rules));
            rt.SetPlayerLocation(L.BellTower);
            Assert.That(rt.Execute(r => r.ReadLastPage()).Success, Is.False);
            AdvanceTo(rt, 18);
            Assert.That(rt.Rules.Story.LastPageText, Is.Null);
            rt.SetPlayerLocation(L.Plaza);
            Assert.That(rt.Execute(r => r.ReadLastPage()).Success, Is.False);
            rt.SetPlayerLocation(L.BellTower);
            Assert.That(rt.Execute(r => r.ReadLastPage()).Message, Does.Contain("不需要作者"));
            int version = rt.Session.World.Version;
            rt.Execute(r => r.ReadLastPage());
            Assert.That(rt.Session.World.Version, Is.EqualTo(version));
            Assert.That(rt.Rules.Story.History.Count(b => b.Id == "last_page"), Is.EqualTo(1));
        }

        [Test]
        public async Task 善意谎言落墨与虚假说法落墨选择不同的作者判词()
        {
            using var good = new TownRuntime();
            AdvanceTo(good, 9, 30);
            good.SetPlayerLocation(L.Store);
            await good.TalkAsync(N.Xiaochuan, P.WellWisp);
            good.Execute(r => r.Tell(N.Ahe, P.WellWisp, TellMode.ClaimSeen));
            AdvanceTo(good, 18);
            Assert.That(FestivalOutcome.LastPage(good.Rules), Does.Contain("替他们写了一件好事"));

            using var bad = new TownRuntime();
            bad.SetPlayerLocation(L.Store);
            await bad.TalkAsync(N.Ahe, P.Stole);
            bad.SetPlayerLocation(L.Plaza);
            bad.Execute(r => r.Tell(N.Amu, P.Stole, TellMode.ClaimSeen));
            AdvanceTo(bad, 18);
            Assert.That(FestivalOutcome.LastPage(bad.Rules), Does.Contain("有些书，不该自己写完"));
        }

        [Test]
        public async Task 撒过谎但没写成好事_不误称纯真或善意谎言结尾()
        {
            using var rt = new TownRuntime();
            await rt.TalkAsync(N.Amu, P.Took);
            rt.Execute(r => r.Tell(N.Amu, P.Took, TellMode.ClaimSeen));
            AdvanceTo(rt, 18);
            Assert.That(FestivalOutcome.LastPage(rt.Rules), Does.Contain("没有替这一章写成好事"));
        }

        [Test]
        public async Task 钟响后新增谎言不改变已经封存的最后一页()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 18);
            string sealedPage = FestivalOutcome.LastPage(rt.Rules);
            await rt.TalkAsync(N.Amu, P.Took);
            rt.Execute(r => r.Tell(N.Amu, P.Took, TellMode.ClaimSeen));
            rt.SetPlayerLocation(L.BellTower);
            rt.Execute(r => r.ReadLastPage());
            Assert.That(rt.Rules.Story.LastPageText, Is.EqualTo(sealedPage));
        }

        [Test]
        public void 审计日志淘汰不丢剧情记录_重开清空书签和最后一页()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.BellTower);
            rt.Execute(r => r.MeetKeeper());
            AdvanceTo(rt, 18);
            rt.Execute(r => r.ReadLastPage());
            var ids = rt.Rules.Story.History.Select(b => b.Id).ToArray();
            for (int i = 0; i < 300; i++) rt.Journal.Append(rt.Session.World, "test", "bounded");
            Assert.That(rt.Journal.Entries.Count, Is.EqualTo(256));
            Assert.That(rt.Rules.Story.History.Select(b => b.Id), Is.EqualTo(ids));
            using var fresh = new TownRuntime(2);
            Assert.That(fresh.Rules.Story.History.Single().Id, Is.EqualTo("blank_page"));
            Assert.That(fresh.Rules.Story.HasBookmark, Is.False);
            Assert.That(fresh.Rules.Story.HasReadLastPage, Is.False);
            Assert.That(fresh.Rules.Story.LastPageText, Is.Null);
        }
    }
}
