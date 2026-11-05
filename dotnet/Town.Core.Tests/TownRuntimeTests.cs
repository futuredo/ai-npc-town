using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.Runtime;
using Town.Core.Tasks;
using Town.Core.World;

namespace Town.Core.Tests
{
    public sealed class TownRuntimeTests
    {
        private static void CollectAndReturn(TownRuntime runtime)
        {
            runtime.SetPlayerLocation(Ids.Loc.Shelter);
            Assert.That(runtime.Execute(r => r.InspectShelter()).Success, Is.True);
            Assert.That(runtime.Execute(r => r.TakeHammer()).Success, Is.True);
            runtime.SetPlayerLocation(Ids.Loc.Plaza);
            Assert.That(runtime.Execute(r => r.ReturnHammer()).Success, Is.True);
        }

        [Test]
        public void FullOfflineRouteRepairsBenchAndRecordsWorldChanges()
        {
            using var runtime = new TownRuntime();
            CollectAndReturn(runtime);
            runtime.Advance(59);
            Assert.That(runtime.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.LessThan(100));
            runtime.Advance(1);
            Assert.That(runtime.Rules.Repair.State, Is.EqualTo(RepairTaskState.Completed));
            Assert.That(runtime.Session.World.RequireItem(Ids.Item.Hammer).HolderId, Is.EqualTo(Ids.Npc.Amu));
            Assert.That(runtime.Journal.Entries.Any(e => e.Detail == "repair:completed"), Is.True);
        }

        [Test]
        public void LocationAndPossessionAreRequiredAndPickupCannotRepeat()
        {
            using var runtime = new TownRuntime();
            Assert.That(runtime.Execute(r => r.TakeHammer()).Success, Is.False);
            runtime.SetPlayerLocation(Ids.Loc.Shelter);
            Assert.That(runtime.Execute(r => r.TakeHammer()).Success, Is.False);
            runtime.Execute(r => r.InspectShelter());
            Assert.That(runtime.Execute(r => r.TakeHammer()).Success, Is.True);
            int version = runtime.Session.World.Version;
            Assert.That(runtime.Execute(r => r.TakeHammer()).Success, Is.False);
            Assert.That(runtime.Execute(r => r.ReturnHammer()).Success, Is.False);
            Assert.That(runtime.Session.World.Version, Is.EqualTo(version));
        }

        [Test]
        public void EvidenceCorrectsOnlyRecipientAndIsIdempotent()
        {
            using var runtime = new TownRuntime();
            runtime.SetPlayerLocation(Ids.Loc.Shelter);
            runtime.Execute(r => r.InspectShelter());
            runtime.SetPlayerLocation(Ids.Loc.Plaza);
            runtime.Execute(r => r.ShowEvidence(Ids.Npc.Amu));
            int count = runtime.Session.Knowledge.AllForDebug.Count;
            runtime.Execute(r => r.ShowEvidence(Ids.Npc.Amu));
            Assert.That(runtime.Session.Knowledge.AllForDebug.Count, Is.EqualTo(count));
            var amu = runtime.Session.Knowledge.ViewFor(Ids.Npc.Amu);
            Assert.That(amu.Current(Propositions.Returned).Stance, Is.True);
            Assert.That(amu.History.Any(b => b.PropositionId == Propositions.Returned && !b.IsActive), Is.True);
            Assert.That(runtime.Session.Knowledge.ViewFor(Ids.Npc.Ahe).Knows(Propositions.Returned), Is.False);
        }

        [Test]
        public void MarketDeadlineExpiresUnfinishedRepairEvenAfterLargeTimeJump()
        {
            using var runtime = new TownRuntime();
            runtime.Advance(8 * 60 + 30);
            CollectAndReturn(runtime);
            runtime.Advance(120);
            Assert.That(runtime.Rules.Repair.State, Is.EqualTo(RepairTaskState.Expired));
            Assert.That(runtime.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.EqualTo(50));
            Assert.That(runtime.Session.World.GetFlag(Ids.Flag.MarketStarted), Is.EqualTo(1));
            runtime.Advance(60);
            Assert.That(runtime.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.EqualTo(50));
        }

        [Test]
        public void TimelyCompletionSurvivesJumpPastMarket()
        {
            using var runtime = new TownRuntime();
            CollectAndReturn(runtime);
            runtime.Advance(10 * 60);
            Assert.That(runtime.Rules.Repair.State, Is.EqualTo(RepairTaskState.Completed));
        }

        [Test]
        public void HammerReturnedAfterDeadlineDoesNotStartRepair()
        {
            using var runtime = new TownRuntime();
            runtime.Advance(9 * 60);
            CollectAndReturn(runtime);
            Assert.That(runtime.Rules.Repair.State, Is.EqualTo(RepairTaskState.Expired));
            Assert.That(runtime.Session.World.GetFlag(Ids.Flag.BenchProgress), Is.Zero);
        }

        [Test]
        public async Task DefaultDialogueRespectsUnknownFactsAndKeepsMemoriesPrivate()
        {
            using var runtime = new TownRuntime();
            var result = await runtime.TalkAsync(Ids.Npc.Amu, Propositions.Location);
            Assert.That(result.Success, Is.True);
            Assert.That(result.Reply, Does.Not.Contain("石亭"));
            Assert.That(runtime.Memory.For(Ids.Npc.Amu).Count, Is.EqualTo(1));
            Assert.That(runtime.Memory.For(Ids.Npc.Ahe), Is.Empty);
            Assert.That(runtime.Session.World.Version, Is.Zero);
            Assert.That((await runtime.TalkAsync(Ids.Npc.Ahe, Propositions.Took)).ErrorCode, Is.EqualTo("npc_not_nearby"));
        }

        [Test]
        public async Task PendingDialoguePausesTimeAndPreventsSecondRequest()
        {
            var client = new DeferredClient();
            using var runtime = new TownRuntime(dialogue: client);
            var pending = runtime.TalkAsync(Ids.Npc.Amu, Propositions.Took);
            runtime.Advance(60);
            Assert.That(runtime.Session.World.Now, Is.EqualTo(HammerEventSetup.StartTime));
            Assert.That((await runtime.TalkAsync(Ids.Npc.Amu, Propositions.Took)).ErrorCode, Is.EqualTo("dialogue_busy"));
            client.Complete();
            Assert.That((await pending).Success, Is.True);
            Assert.That(runtime.IsDialoguePending, Is.False);
        }

        [Test]
        public async Task WorldChangeInvalidatesInFlightResponse()
        {
            var client = new DeferredClient();
            using var runtime = new TownRuntime(dialogue: client);
            var pending = runtime.TalkAsync(Ids.Npc.Amu, Propositions.Took);
            runtime.SetPlayerLocation(null);
            client.Complete();
            Assert.That((await pending).ErrorCode, Is.EqualTo("stale_response"));
            Assert.That(runtime.Memory.For(Ids.Npc.Amu), Is.Empty);
        }

        [Test]
        public async Task DisposedSessionCannotAcceptLateReplyOrPolluteNewSession()
        {
            var client = new DeferredClient();
            var old = new TownRuntime(dialogue: client);
            var pending = old.TalkAsync(Ids.Npc.Amu, Propositions.Took);
            old.Dispose();
            using var fresh = new TownRuntime(2);
            client.Complete();
            Assert.That((await pending).Success, Is.False);
            Assert.That(fresh.Memory.For(Ids.Npc.Amu), Is.Empty);
            Assert.That(fresh.Session.World.Version, Is.Zero);
        }

        [Test]
        public async Task ForeignCitationIsRejectedBeforeMemoryReceivesReply()
        {
            var client = new DeferredClient();
            using var runtime = new TownRuntime(dialogue: client);
            var pending = runtime.TalkAsync(Ids.Npc.Amu, Propositions.Took);
            client.Source.SetResult(new DialogueResult { Success = true, Reply = "测试", CitedBeliefs = new[] { "foreign" } });
            var result = await pending;
            Assert.That(result.ErrorCode, Is.EqualTo("unknown_citation"));
            Assert.That(runtime.Memory.For(Ids.Npc.Amu).Single().Reply, Is.EqualTo(DialogueResult.Fallback));
        }

        [Test]
        public void JournalIsBoundedAndNewSessionStartsClean()
        {
            using var runtime = new TownRuntime();
            for (int i = 0; i < 300; i++) runtime.Advance(1);
            Assert.That(runtime.Journal.Entries.Count, Is.EqualTo(256));
            using var fresh = new TownRuntime(2);
            Assert.That(fresh.Journal.Entries.Count, Is.EqualTo(1));
            Assert.That(fresh.Rules.HasShelterEvidence, Is.False);
        }

        private sealed class DeferredClient : ILLMClient
        {
            public readonly TaskCompletionSource<DialogueResult> Source = new TaskCompletionSource<DialogueResult>();
            public Task<DialogueResult> ReplyAsync(NpcDialogueRequest request, CancellationToken cancellationToken) => Source.Task;
            public void Complete() => Source.SetResult(new DialogueResult { Success = true, Reply = "我不确定。" });
        }
    }
}
