using System;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.Social;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;

namespace Town.Core.Rules
{
    public enum CarryState { None, ToPickup, ToDeliver, Delivered, Failed }

    /// <summary>
    /// 请人帮忙（芬恩亲自去还锤子）、承诺、道歉。
    /// 答应 ≠ 完成：只有居民按游戏时间真正走到、真正交到对方手里，才算完成。
    /// </summary>
    public sealed class HelpRules
    {
        public const string CarryTaskId = "carry_hammer";
        private readonly HammerEventSetup.Session _s;
        private readonly RumorRules _rumor;
        private readonly Action<string, string> _note;

        public CarryState Carry { get; private set; } = CarryState.None;
        public string CarryFailReason { get; private set; }
        /// <summary>符文锤是芬恩亲手交还给布伦的。</summary>
        public bool FinnReturnedHimself { get; private set; }
        public Commitment Promise { get; private set; }

        public HelpRules(HammerEventSetup.Session session, RumorRules rumor, Action<string, string> note)
        {
            _s = session;
            _rumor = rumor;
            _note = note ?? ((_, __) => { });
            _s.Schedule.Arrived += OnArrived;
        }

        private WorldState World => _s.World;
        private bool Near(string npcId) => World.Npcs[npcId].LocationId != null && World.Npcs[npcId].LocationId == World.PlayerLocationId;

        // ———————————— 请芬恩去还锤子 ————————————

        public CommandResult AskFinnToReturnHammer()
        {
            if (!Near(N.Xiaochuan)) return CommandResult.Reject("请先走到芬恩身边。");
            if (World.GetFlag(Ids.Flag.MarketStarted) != 0) return CommandResult.Reject("誓钟已经响过了。");
            if (Carry == CarryState.ToPickup || Carry == CarryState.ToDeliver) return CommandResult.Ok("芬恩：「我已经在办啦！」");
            if (Carry == CarryState.Delivered) return CommandResult.Ok("芬恩：「锤子已经交给布伦了。」");
            if (World.RequireItem(Ids.Item.Hammer).HolderId == N.Amu) return CommandResult.Reject("布伦已经拿回锤子了。");
            if (_s.Relations.Get(N.Xiaochuan, Ids.Player) == RelationLevel.Wary)
                return CommandResult.Reject("芬恩别过脸：「你不是到处说我偷东西吗？我不想帮你。」");
            var entry = _s.Schedule.Get(N.Xiaochuan)?.At(World.Now);
            if (entry != null && entry.Busy) return CommandResult.Reject("芬恩打着哈欠：「我在午休……一点以后再来找我吧。」");
            var where = _s.Knowledge.ViewFor(N.Xiaochuan).Current(Propositions.Location);
            if (where == null || !where.Stance) return CommandResult.Reject("芬恩：「我也不知道锤子在哪了……」");

            Carry = CarryState.ToPickup;
            _s.Schedule.TakeControl(N.Xiaochuan, CarryTaskId);
            World.Commit("help:accepted:xiaochuan", w => w.Npcs[N.Xiaochuan].CurrentTaskId = CarryTaskId);
            _s.Schedule.StartTravel(World, N.Xiaochuan, Ids.Loc.Shelter, "去石亭取符文锤");
            return CommandResult.Ok("芬恩一愣：「布伦没找到？我昨晚放在石亭了……好，我这就去拿给他！」（已接受，执行中）");
        }

        private void OnArrived(string npcId, string loc)
        {
            if (npcId != N.Xiaochuan) return;
            var hammer = World.RequireItem(Ids.Item.Hammer);
            if (Carry == CarryState.ToPickup && loc == Ids.Loc.Shelter)
            {
                if (hammer.LocationId != Ids.Loc.Shelter)
                {
                    // 锤子已经被别人拿走：芬恩亲眼看到箱子是空的
                    _s.Knowledge.Learn(N.Xiaochuan, Propositions.Location, false, 1f, SourceType.Experienced, World.Now);
                    Fail("箱子是空的，芬恩没找到锤子");
                    return;
                }
                World.GiveItem(Ids.Item.Hammer, N.Xiaochuan, "carry:picked");
                Carry = CarryState.ToDeliver;
                GoToBrunn();
            }
            else if (Carry == CarryState.ToDeliver)
            {
                var brunn = World.Npcs[N.Amu];
                if (brunn.LocationId != loc) { GoToBrunn(); return; }
                Deliver(loc);
            }
        }

        private void GoToBrunn()
        {
            var brunn = World.Npcs[N.Amu];
            string target = brunn.LocationId ?? brunn.TravelTo ?? Ids.Loc.Plaza;
            _s.Schedule.StartTravel(World, N.Xiaochuan, target, "把符文锤送去给布伦");
            if (World.Npcs[N.Xiaochuan].LocationId == target) Deliver(target);
        }

        private void Deliver(string loc)
        {
            World.GiveItem(Ids.Item.Hammer, N.Amu, "carry:delivered");
            var k = _s.Knowledge;
            var returned = k.Learn(N.Amu, Propositions.Returned, true, 1f, SourceType.Experienced, World.Now);
            var reliable = k.Learn(N.Amu, Propositions.Reliable, true, 1f, SourceType.Experienced, World.Now);
            _s.Ledger.Add(new Transmission { At = World.Now, Teller = N.Xiaochuan, Receiver = N.Amu, PropositionId = Propositions.Reliable,
                Stance = true, Mode = TellMode.Firsthand, OriginId = N.Amu, Adopted = true, Reaction = "布伦亲手接过了符文锤" });
            _rumor.ApplyRefutation(N.Amu, returned, N.Xiaochuan);
            _rumor.ApplyRefutation(N.Amu, reliable, N.Xiaochuan);
            FinnReturnedHimself = true;
            Carry = CarryState.Delivered;
            _s.Relations.ApplyOnce("finn_trusted_with_task", N.Xiaochuan, Ids.Player, +1, "你信任他，让他自己去把事情了结", World.Now);
            if (World.PlayerLocationId == loc)
            {
                _rumor.PlayerWitnessed(Propositions.Returned, true);
                _rumor.PlayerWitnessed(Propositions.Reliable, true);
            }
            Release();
            _note("help", "芬恩把符文锤亲手交给了布伦");
        }

        private void Fail(string reason)
        {
            Carry = CarryState.Failed;
            CarryFailReason = reason;
            Release();
            _note("help", reason);
        }

        private void Release()
        {
            _s.Schedule.ReleaseControl(N.Xiaochuan);
            World.Commit("help:released:xiaochuan", w => w.Npcs[N.Xiaochuan].CurrentTaskId = null);
        }

        // ———————————— 承诺 ————————————

        public CommandResult PromiseToBrunn()
        {
            if (!Near(N.Amu)) return CommandResult.Reject("请先走到布伦身边。");
            if (Promise != null) return CommandResult.Ok("布伦：「我记着呢，日落前。」");
            if (World.GetFlag(Ids.Flag.MarketStarted) != 0) return CommandResult.Reject("誓钟已经响过了。");
            if (World.RequireItem(Ids.Item.Hammer).HolderId == N.Amu) return CommandResult.Reject("锤子已经回到布伦手里了。");
            Promise = new Commitment("promise_hammer", Ids.Player, N.Amu, "日落前把符文锤找回来", World.Now, HammerEventSetup.MarketTime);
            _s.Knowledge.Learn(N.Amu, Propositions.Promise, true, 1f, SourceType.Experienced, World.Now);
            World.Commit("promise:made", _ => { });
            return CommandResult.Ok("布伦盯着你看了一会儿：「好，日落前。矮人记性很好。」");
        }

        // ———————————— 道歉 ————————————

        public CommandResult ApologizeToFinn()
        {
            if (!Near(N.Xiaochuan)) return CommandResult.Reject("请先走到芬恩身边。");
            if (_s.Relations.Get(N.Xiaochuan, Ids.Player) != RelationLevel.Wary) return CommandResult.Ok("芬恩：「啊？没什么好道歉的。」");
            var me = _s.Knowledge.ViewFor(Ids.Player);
            bool knowsTruth = (me.Current(Propositions.Borrowed)?.Stance ?? false) || (me.Current(Propositions.Returned)?.Stance ?? false);
            if (!knowsTruth) return CommandResult.Reject("芬恩：「你根本不知道发生了什么，道什么歉？」");
            _s.Relations.SetOnce("apology_finn", N.Xiaochuan, Ids.Player, RelationLevel.Neutral, "你道歉并说清了真相", World.Now);
            return CommandResult.Ok("芬恩犹豫了一下：「……好吧，我知道你明白了。」");
        }

        // ———————————— 每分钟检查 ————————————

        public void Tick()
        {
            if (World.RequireItem(Ids.Item.Hammer).HolderId == N.Amu)
            {
                _s.Relations.ApplyOnce("hammer_back", N.Amu, Ids.Player, +1, "符文锤回到了他手里", World.Now);
                if (Promise != null && Promise.State == CommitmentState.Pending)
                {
                    Promise.State = CommitmentState.Kept;
                    Promise.ResolvedAt = World.Now;
                    _note("promise", "约定兑现：日落前找回了符文锤");
                }
            }
            if (Promise != null && Promise.State == CommitmentState.Pending && World.Now >= Promise.Deadline)
            {
                Promise.State = CommitmentState.Broken;
                Promise.ResolvedAt = World.Now;
                _s.Relations.ApplyOnce("promise_broken", N.Amu, Ids.Player, -1, "你没有兑现日落前找回锤子的约定", World.Now);
                _note("promise", "约定落空");
            }
        }
    }
}
