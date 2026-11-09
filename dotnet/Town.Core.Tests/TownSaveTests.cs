using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Brain;
using Town.Core.Knowledge;
using Town.Core.Runtime;
using Town.Core.Social;
using Town.Core.World;

namespace Town.Core.Tests
{
    public sealed class TownSaveTests
    {
        private static void Do(TownRuntime rt, TownCommandKind kind, string npc = null, string prop = null, TellMode mode = TellMode.Faithful) =>
            Assert.That(rt.Execute(new TownCommand(kind, npc, prop, mode)).Success, Is.True);

        private static TownRuntime RoundTrip(TownRuntime rt) => TownSaveCodec.Restore(TownSaveCodec.Decode(TownSaveCodec.Encode(rt.CreateSave())), 7);

        [Test]
        public void 新局与调查搬运维修中的存档可继续推进且不会重复剧情()
        {
            using var rt = new TownRuntime();
            using (var fresh = RoundTrip(rt)) Assert.That(RuntimeEvidence.Hash(fresh), Is.EqualTo(RuntimeEvidence.Hash(rt)));
            rt.SetPlayerLocation(Ids.Loc.BellTower);
            Do(rt, TownCommandKind.MeetKeeper);
            rt.SetPlayerLocation(Ids.Loc.Shelter);
            Do(rt, TownCommandKind.InspectShelter);
            Do(rt, TownCommandKind.TakeHammer);
            rt.SetPlayerLocation(Ids.Loc.Plaza);
            Do(rt, TownCommandKind.Promise);
            Do(rt, TownCommandKind.ReturnHammer);
            rt.Advance(17);
            using var loaded = RoundTrip(rt);
            Assert.That(loaded.Session.World.SessionGen, Is.EqualTo(7));
            Assert.That(RuntimeEvidence.Hash(loaded), Is.EqualTo(RuntimeEvidence.Hash(rt)));
            Assert.That(loaded.Rules.Story.HasBookmark, Is.True);
            rt.Advance(9 * 60 - 17);
            loaded.Advance(9 * 60 - 17);
            Assert.That(RuntimeEvidence.Hash(loaded), Is.EqualTo(RuntimeEvidence.Hash(rt)), "恢复后未来演化也必须一致");
            Assert.That(loaded.Rules.Story.History.Count(b => b.Id == "inkfall"), Is.EqualTo(1));
        }

        [Test]
        public async Task 芬恩搬运途中和正式拜师后读档保持任务认知溯源和秘密门槛()
        {
            using var rt = new TownRuntime();
            rt.Advance(30);
            rt.SetPlayerLocation(Ids.Loc.Store);
            Do(rt, TownCommandKind.AskFinnToReturn);
            rt.Advance(2);
            using var carrying = RoundTrip(rt);
            Assert.That(carrying.Session.Schedule.IsTaskControlled(Ids.Npc.Xiaochuan), Is.True);
            rt.Advance(20); carrying.Advance(20);
            Assert.That(RuntimeEvidence.Hash(carrying), Is.EqualTo(RuntimeEvidence.Hash(rt)));
            rt.Advance(13); // 10:05，芬恩回到广场
            rt.SetPlayerLocation(Ids.Loc.Plaza);
            Assert.That((await rt.TalkAsync(Ids.Npc.Xiaochuan, Propositions.WantsToLearn)).Success, Is.True);
            Do(rt, TownCommandKind.Tell, Ids.Npc.Amu, Propositions.WantsToLearn);
            rt.Advance(1);
            using var apprentice = RoundTrip(rt);
            Assert.That(RuntimeEvidence.Hash(apprentice), Is.EqualTo(RuntimeEvidence.Hash(rt)));
            Assert.That(apprentice.Rules.Apprentice.State, Is.EqualTo(Town.Core.Rules.ApprenticeState.Formal));
        }

        [Test]
        public async Task 谎言落墨与拆页后读档保留封存判词_之后可正常离线对话()
        {
            using var rt = new TownRuntime();
            rt.SetPlayerLocation(Ids.Loc.Store);
            await rt.TalkAsync(Ids.Npc.Ahe, Propositions.Stole);
            rt.SetPlayerLocation(Ids.Loc.Plaza);
            Do(rt, TownCommandKind.Tell, Ids.Npc.Amu, Propositions.Stole, TellMode.ClaimSeen);
            rt.Advance(540);
            rt.SetPlayerLocation(Ids.Loc.BellTower);
            Do(rt, TownCommandKind.ReadLastPage);
            using var loaded = RoundTrip(rt);
            Assert.That(loaded.Rules.Story.LastPageText, Is.EqualTo(rt.Rules.Story.LastPageText));
            Assert.That(loaded.Session.Ledger.PlayerLies.Count(), Is.EqualTo(1));
            loaded.SetPlayerLocation(Ids.Loc.Plaza);
            Assert.That((await loaded.TalkAsync(Ids.Npc.Amu, Propositions.Borrowed)).Success, Is.True);
            using var again = RoundTrip(loaded);
            Assert.That(RuntimeEvidence.Hash(again), Is.EqualTo(RuntimeEvidence.Hash(loaded)));
        }

        [Test]
        public async Task 回放使用已验证的录音_不调用原模型客户端_也不漏掉信任前含糊回答()
        {
            var client = new CountingClient();
            using var rt = new TownRuntime(dialogue: client);
            await rt.TalkAsync(Ids.Npc.Amu, Propositions.WantsApprentice);
            await rt.TalkAsync(Ids.Npc.Amu, "你好");
            Assert.That(client.Calls, Is.EqualTo(1));
            using var loaded = RoundTrip(rt);
            Assert.That(client.Calls, Is.EqualTo(1));
            Assert.That(loaded.Memory.For(Ids.Npc.Amu).Select(m => m.Reply), Is.EqualTo(rt.Memory.For(Ids.Npc.Amu).Select(m => m.Reply)));
        }

        [Test]
        public void 不接受未知版本未知命令过量回放损坏文件和非数坐标()
        {
            using var rt = new TownRuntime();
            var save = rt.CreateSave();
            save.Schema++;
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Encode(save));
            save.Schema = TownSave.CurrentSchema;
            save.Steps = new[] { new ReplayStep { Kind = "command", Command = 999 } };
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Restore(save, 2));
            save.Steps = new[] { new ReplayStep { Kind = "advance", Minutes = int.MaxValue } };
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Restore(save, 2));
            Assert.Throws<InvalidDataException>(() => rt.CreateSave(new SpatialBookmark { X = float.NaN }));
        }

        [Test]
        public void 文件边界和校验码拒绝坏档_外部开发改动不能伪造可恢复存档()
        {
            using var rt = new TownRuntime();
            var bytes = TownSaveCodec.Encode(rt.CreateSave());
            var text = Encoding.UTF8.GetString(bytes).Replace("Sha256\":\"", "Sha256\":\"0");
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Decode(Encoding.UTF8.GetBytes(text)));
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Decode(new byte[TownSaveCodec.MaxBytes + 1]));
            Assert.Throws<InvalidDataException>(() => TownSaveCodec.Decode(Encoding.UTF8.GetBytes("not json")));
            Assert.Throws<InvalidDataException>(() => rt.CreateSave(new SpatialBookmark { X = float.NaN }));
            rt.Session.World.Commit("developer:cheat", w => w.Flags[Ids.Flag.BenchProgress] = 100);
            Assert.Throws<InvalidDataException>(() => rt.CreateSave());
        }

        [Test]
        public void 原子写盘保留备份_主档损坏可恢复_双坏档不动当前局()
        {
            string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "save-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "chapter7.json");
            try
            {
                var store = new TownSaveStore(path);
                using var rt = new TownRuntime();
                string firstHash = RuntimeEvidence.Hash(rt);
                store.Write(rt.CreateSave());
                rt.Advance(30);
                store.Write(rt.CreateSave());
                using (var latest = store.Read(2, out _, out bool backup))
                { Assert.That(backup, Is.False); Assert.That(RuntimeEvidence.Hash(latest), Is.EqualTo(RuntimeEvidence.Hash(rt))); }
                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
                File.WriteAllText(path, "corrupted");
                using (var previous = store.Read(3, out _, out bool backup))
                { Assert.That(backup, Is.True); Assert.That(RuntimeEvidence.Hash(previous), Is.EqualTo(firstHash)); }
                File.WriteAllText(path + ".bak", "corrupted");
                string current = RuntimeEvidence.Hash(rt);
                Assert.Throws<InvalidDataException>(() => store.Read(4, out _, out _));
                Assert.That(RuntimeEvidence.Hash(rt), Is.EqualTo(current));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        public async Task 对话进行中不能存档_任意委托也不能伪装成可回放命令()
        {
            var client = new DeferredClient();
            using var rt = new TownRuntime(dialogue: client);
            var pending = rt.TalkAsync(Ids.Npc.Amu, "你好");
            Assert.Throws<InvalidOperationException>(() => rt.CreateSave());
            client.Source.SetResult(new DialogueResult { Success = true, Reply = "你好。" });
            await pending;
            rt.Execute(r => r.MeetKeeper());
            Assert.Throws<InvalidOperationException>(() => rt.CreateSave());
        }

        private sealed class CountingClient : ILLMClient
        {
            public int Calls;
            public Task<DialogueResult> ReplyAsync(NpcDialogueRequest r, CancellationToken t)
            { Calls++; return Task.FromResult(new DialogueResult { Success = true, Reply = "页外的朋友，你好。" }); }
        }
        private sealed class DeferredClient : ILLMClient
        {
            public readonly TaskCompletionSource<DialogueResult> Source = new TaskCompletionSource<DialogueResult>();
            public Task<DialogueResult> ReplyAsync(NpcDialogueRequest r, CancellationToken t) => Source.Task;
        }
    }
}
