using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Knowledge;
using Town.Core.Outcome;
using Town.Core.Rules;
using Town.Core.Runtime;
using Town.Core.Social;
using Town.Core.Tasks;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;
using L = Town.Core.World.Ids.Loc;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Core.Tests
{
    /// <summary>《回音镇》v2 玩法规则：日程、传话、同源不叠加、誓钟显化、帮忙、拜师与结局。全部离线。</summary>
    public sealed class EchofordGameplayTests
    {
        private static void AdvanceTo(TownRuntime rt, int hour, int minute = 0)
        {
            int target = GameTime.At(1, hour, minute).TotalMinutes;
            rt.Advance(target - rt.Session.World.Now.TotalMinutes);
        }

        private static Belief Current(TownRuntime rt, string who, string prop) => rt.Session.Knowledge.ViewFor(who).Current(prop);
        private static bool Believes(TownRuntime rt, string who, string prop) => Current(rt, who, prop)?.Stance ?? false;
        private static int Relation(TownRuntime rt, string from, string to = Ids.Player) => rt.Session.Relations.Get(from, to);

        // ———————————— 日程 ————————————

        [Test]
        public void 居民按日程走动_在路上时不能交谈()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 10, 1);
            Assert.That(rt.Session.World.Npcs[N.Xiaochuan].IsTravelling, Is.True, "10:00 芬恩从旅店出发去广场");
            AdvanceTo(rt, 10, 5);
            Assert.That(rt.Session.World.Npcs[N.Xiaochuan].LocationId, Is.EqualTo(L.Plaza));
            AdvanceTo(rt, 12, 30);
            Assert.That(rt.Session.World.Npcs[N.Xiaochuan].LocationId, Is.EqualTo(L.Homes));
            Assert.That(rt.Session.Schedule.IsBusy(N.Xiaochuan, rt.Session.World.Now), Is.True, "午休");
            AdvanceTo(rt, 14, 5);
            Assert.That(rt.Session.World.Npcs[N.Ahe].LocationId, Is.EqualTo(L.Plaza), "梅芙下午去广场送酒");
        }

        // ———————————— 不插手的一天 ————————————

        [Test]
        public void 什么都不做_谣言停在一个源头_钟响后什么也没发生()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 18, 1);
            var w = rt.Session.World;
            Assert.That(w.GetFlag(Ids.Flag.MarketStarted), Is.EqualTo(1));
            Assert.That(rt.Rules.Bell.Manifested, Is.Empty);
            Assert.That(Believes(rt, N.Amu, P.Stole), Is.False, "布伦对单一传言要证据");
            Assert.That(Believes(rt, N.Ahe, P.Borrowed), Is.True, "布伦反驳时告诉梅芙是借的");
            Assert.That(Believes(rt, N.Amu, P.WellEvil), Is.True, "布伦听信了井里有恶灵");
            var evil = rt.Rules.Bell.FinalReadings.Single(r => r.PropositionId == P.WellEvil);
            Assert.That(evil.Believers.Count, Is.EqualTo(2));
            Assert.That(evil.Origins, Is.EqualTo(new[] { N.Ahe }), "两个人信，但都来自梅芙一个源头");
            Assert.That(FestivalOutcome.Endings(rt.Rules).Select(e => e.Id), Does.Contain("quiet_night"));
        }

        [Test]
        public void 在广场能偷听到梅芙的闲聊()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 14, 10);
            Assert.That(Believes(rt, Ids.Player, P.WellEvil), Is.True);
            Assert.That(rt.Session.Ledger.All.Any(t => t.Receiver == Ids.Player && t.Mode == TellMode.Overheard), Is.True);
            Assert.That(rt.Session.Ledger.All.Any(t => t.Teller == N.Ahe && t.Receiver == N.Amu && t.Mode == TellMode.GossipExaggerated), Is.True,
                "梅芙不知道是借的，把「拿走」说成了「偷」");
        }

        // ———————————— 同源不叠加与谎言 ————————————

        [Test]
        public async Task 如实转述同一源头的话没用_说成亲眼所见就成了第二个源头_钟响烙下窃贼印记()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.Store);
            Assert.That((await rt.TalkAsync(N.Ahe, P.Stole)).Success, Is.True);
            Assert.That(Current(rt, Ids.Player, P.Stole).OriginId, Is.EqualTo(N.Ahe));

            rt.SetPlayerLocation(L.Plaza);
            rt.Execute(r => r.Tell(N.Amu, P.Stole, TellMode.Faithful));
            Assert.That(Believes(rt, N.Amu, P.Stole), Is.False, "转述梅芙的话：仍是梅芙一个源头");

            rt.Execute(r => r.Tell(N.Amu, P.Stole, TellMode.ClaimSeen));
            Assert.That(Believes(rt, N.Amu, P.Stole), Is.True);
            Assert.That(rt.Session.Ledger.PlayerLies.Count(), Is.EqualTo(1), "你并没有亲眼看到");

            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.ThiefMark), Is.EqualTo(1));
            Assert.That(FestivalOutcome.Endings(rt.Rules).Select(e => e.Id), Does.Contain("thief_mark"));
        }

        [Test]
        public async Task 添油加醋单独不够_梅芙也说过之后布伦信了_誓钟数到两个源头()
        {
            using var rt = new TownRuntime();
            await rt.TalkAsync(N.Amu, P.Took); // 从布伦那里得知芬恩拿走了锤子
            Assert.That(rt.Execute(r => r.Tell(N.Amu, P.Took, TellMode.Exaggerate)).Success, Is.True);
            Assert.That(Believes(rt, N.Amu, P.Stole), Is.False, "只有你一个源头");

            AdvanceTo(rt, 14, 10); // 梅芙来广场又说了一遍：第二个源头
            Assert.That(Believes(rt, N.Amu, P.Stole), Is.True);
            AdvanceTo(rt, 18, 1);
            var stole = rt.Rules.Bell.FinalReadings.Single(r => r.PropositionId == P.Stole);
            Assert.That(stole.Origins, Is.EquivalentTo(new[] { Ids.Player, N.Ahe }));
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.ThiefMark), Is.EqualTo(1));
        }

        [Test]
        public async Task 当面指控芬恩_他会戒备你并说出真相_道歉后才肯帮忙()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.Store);
            await rt.TalkAsync(N.Ahe, P.Stole);
            AdvanceTo(rt, 9, 30); // 芬恩到旅店取货
            rt.Execute(r => r.Tell(N.Xiaochuan, P.Stole, TellMode.Faithful));
            Assert.That(Relation(rt, N.Xiaochuan), Is.EqualTo(RelationLevel.Wary));
            Assert.That(Believes(rt, Ids.Player, P.Borrowed), Is.True, "芬恩辩解：是借的");
            Assert.That(Believes(rt, Ids.Player, P.Returned), Is.True, "芬恩辩解：已经还了");

            Assert.That(rt.Execute(r => r.AskFinnToReturnHammer()).Success, Is.False);
            Assert.That(rt.Execute(r => r.ApologizeToFinn()).Success, Is.True);
            Assert.That(rt.Execute(r => r.AskFinnToReturnHammer()).Success, Is.True);
        }

        // ———————————— 芬恩亲手还锤与拜师 ————————————

        private static void FinnReturnsHammer(TownRuntime rt)
        {
            AdvanceTo(rt, 10, 5); // 芬恩在广场送货，玩家也在广场
            Assert.That(rt.Execute(r => r.AskFinnToReturnHammer()).Success, Is.True);
            AdvanceTo(rt, 10, 15);
        }

        [Test]
        public void 芬恩亲手还锤_布伦开始修拱门_芬恩信任你()
        {
            using var rt = new TownRuntime();
            FinnReturnsHammer(rt);
            Assert.That(rt.Rules.Help.Carry, Is.EqualTo(CarryState.Delivered));
            Assert.That(rt.Session.World.RequireItem(Ids.Item.Hammer).HolderId, Is.EqualTo(N.Amu));
            Assert.That(rt.Rules.Repair.State, Is.EqualTo(RepairTaskState.Running));
            Assert.That(Believes(rt, N.Amu, P.Reliable), Is.True);
            Assert.That(Relation(rt, N.Xiaochuan), Is.EqualTo(RelationLevel.Trust));
            Assert.That(Relation(rt, N.Amu), Is.EqualTo(RelationLevel.Trust));
            Assert.That(Current(rt, Ids.Player, P.Reliable).Source, Is.EqualTo(SourceType.Witnessed), "你在场亲眼看见");
            Assert.That(rt.Session.Schedule.IsTaskControlled(N.Xiaochuan), Is.False, "任务结束后回到日程");
        }

        [Test]
        public void 锤子被你先拿走了_芬恩白跑一趟_任务失败而不是瞬移完成()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 10, 5);
            rt.Execute(r => r.AskFinnToReturnHammer());
            rt.SetPlayerLocation(L.Shelter);
            rt.Execute(r => r.InspectShelter());
            rt.Execute(r => r.TakeHammer());
            AdvanceTo(rt, 10, 15);
            Assert.That(rt.Rules.Help.Carry, Is.EqualTo(CarryState.Failed));
            Assert.That(rt.Session.World.RequireItem(Ids.Item.Hammer).HolderId, Is.EqualTo(Ids.Player));
            Assert.That(Believes(rt, N.Xiaochuan, P.Location), Is.False);
        }

        [Test]
        public async Task 秘密只对信任的人说()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 10, 5);
            var shy = await rt.TalkAsync(N.Xiaochuan, P.WantsToLearn);
            Assert.That(shy.CitedBeliefs, Is.Empty);
            Assert.That(rt.Session.Knowledge.ViewFor(Ids.Player).Knows(P.WantsToLearn), Is.False);

            rt.Execute(r => r.AskFinnToReturnHammer());
            AdvanceTo(rt, 10, 15);
            await rt.TalkAsync(N.Xiaochuan, P.WantsToLearn);
            Assert.That(Believes(rt, Ids.Player, P.WantsToLearn), Is.True);
            await rt.TalkAsync(N.Amu, P.WantsApprentice);
            Assert.That(Believes(rt, Ids.Player, P.WantsApprentice), Is.True, "锤子回来后布伦也信任你了");
        }

        [Test]
        public async Task 牵线拜师_芬恩亲手还锤后布伦当场收徒_守信的学徒()
        {
            using var rt = new TownRuntime();
            FinnReturnsHammer(rt);
            await rt.TalkAsync(N.Xiaochuan, P.WantsToLearn);
            var told = rt.Execute(r => r.Tell(N.Amu, P.WantsToLearn, TellMode.Faithful));
            Assert.That(told.Message, Does.Contain("靠得住"));
            rt.Advance(1);
            Assert.That(rt.Rules.Apprentice.State, Is.EqualTo(ApprenticeState.Formal));

            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.EqualTo(100));
            Assert.That(FestivalOutcome.Endings(rt.Rules).Select(e => e.Id), Does.Contain("loyal_apprentice"));
        }

        [Test]
        public async Task 自己拿锤子还回去_布伦只答应试用()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.Shelter);
            rt.Execute(r => r.InspectShelter());
            rt.Execute(r => r.TakeHammer());
            rt.SetPlayerLocation(L.Plaza);
            rt.Execute(r => r.ShowEvidence(N.Amu));
            rt.Execute(r => r.ReturnHammer());
            Assert.That(Relation(rt, N.Amu), Is.EqualTo(RelationLevel.Trust));

            AdvanceTo(rt, 10, 5);
            await rt.TalkAsync(N.Amu, P.WantsApprentice);
            rt.Execute(r => r.Tell(N.Xiaochuan, P.WantsApprentice, TellMode.Faithful));
            rt.Advance(1);
            Assert.That(rt.Rules.Apprentice.State, Is.EqualTo(ApprenticeState.Trial));
        }

        // ———————————— 井中灯灵 ————————————

        [Test]
        public void 亲眼看过灯灵再告诉梅芙_两个独立源头_灯灵成为守护灵()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(L.Well);
            Assert.That(rt.Execute(r => r.InspectWell()).Success, Is.True);
            rt.SetPlayerLocation(L.Store);
            rt.Execute(r => r.Tell(N.Ahe, P.WellWisp, TellMode.ClaimSeen));
            Assert.That(Believes(rt, N.Ahe, P.WellWisp), Is.True);
            Assert.That(Believes(rt, N.Ahe, P.WellEvil), Is.False, "灯灵的说法纠正了恶灵的猜测");
            Assert.That(rt.Session.Ledger.PlayerLies, Is.Empty, "你真的看到了，不是谎言");

            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.WispGuardian), Is.EqualTo(1));
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.WispEvil), Is.Zero);
        }

        [Test]
        public async Task 没看过却说成亲眼所见_好事成真_一个善意的谎言()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 9, 30); // 芬恩在旅店
            rt.SetPlayerLocation(L.Store);
            await rt.TalkAsync(N.Xiaochuan, P.WellWisp);
            rt.Execute(r => r.Tell(N.Ahe, P.WellWisp, TellMode.ClaimSeen));
            Assert.That(rt.Session.Ledger.PlayerLies.Count(), Is.EqualTo(1));

            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.WispGuardian), Is.EqualTo(1));
            Assert.That(FestivalOutcome.Endings(rt.Rules).Select(e => e.Id), Does.Contain("kind_lie"));
        }

        [Test]
        public async Task 如实转述芬恩的话_只有芬恩一个源头_灯灵不会显化()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 9, 30);
            rt.SetPlayerLocation(L.Store);
            await rt.TalkAsync(N.Xiaochuan, P.WellWisp);
            rt.Execute(r => r.Tell(N.Ahe, P.WellWisp, TellMode.Faithful));
            Assert.That(Believes(rt, N.Ahe, P.WellWisp), Is.True);

            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Session.World.GetFlag(Ids.Flag.WispGuardian), Is.Zero);
            var wisp = rt.Rules.Bell.FinalReadings.Single(r => r.PropositionId == P.WellWisp);
            Assert.That(wisp.Believers.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(wisp.Origins, Is.EqualTo(new[] { N.Xiaochuan }));
        }

        // ———————————— 约定、手账边界、结算 ————————————

        [Test]
        public void 承诺落空_布伦对你戒备()
        {
            using var rt = new TownRuntime();
            Assert.That(rt.Execute(r => r.PromiseToBrunn()).Success, Is.True);
            AdvanceTo(rt, 18, 1);
            Assert.That(rt.Rules.Help.Promise.State, Is.EqualTo(CommitmentState.Broken));
            Assert.That(Relation(rt, N.Amu), Is.EqualTo(RelationLevel.Wary));
        }

        [Test]
        public void 手账里没有的事不能传()
        {
            using var rt = new TownRuntime();
            var result = rt.Execute(r => r.Tell(N.Amu, P.Location, TellMode.ClaimSeen));
            Assert.That(result.Success, Is.False);
            Assert.That(rt.Session.Ledger.All, Is.Empty);
        }

        [Test]
        public void 回音石板初始读数()
        {
            using var rt = new TownRuntime();
            var board = rt.Rules.Bell.Read().ToDictionary(r => r.PropositionId);
            Assert.That(board[P.Stole].Believers, Is.EqualTo(new[] { N.Ahe }));
            Assert.That(board[P.WellEvil].Believers, Is.EqualTo(new[] { N.Ahe }));
            Assert.That(board[P.WellWisp].Believers, Is.EqualTo(new[] { N.Xiaochuan }));
            Assert.That(board[P.Reliable].Believers, Is.Empty);
            Assert.That(board.Values.Any(r => r.Ready), Is.False);
        }

        [Test]
        public void 结算溯源与头条都能生成()
        {
            using var rt = new TownRuntime();
            AdvanceTo(rt, 18, 1);
            Assert.That(FestivalOutcome.Trace(rt.Rules), Is.Not.Empty);
            Assert.That(FestivalOutcome.Headlines(rt.Rules).Count, Is.EqualTo(3));
        }
    }
}
