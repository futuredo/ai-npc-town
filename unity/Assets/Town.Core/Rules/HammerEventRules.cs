using System;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.Social;
using Town.Core.Tasks;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;

namespace Town.Core.Rules
{
    public sealed class CommandResult
    {
        public bool Success { get; }
        public string Message { get; }
        private CommandResult(bool success, string message) { Success = success; Message = message; }
        public static CommandResult Ok(string message) => new CommandResult(true, message);
        public static CommandResult Reject(string message) => new CommandResult(false, message);
    }

    /// <summary>
    /// 《回音镇》一局的全部规则入口。只接受显式玩家交互和按游戏时间推进的 Tick；模型台词没有调用本规则的权限。
    /// </summary>
    public sealed class HammerEventRules
    {
        private readonly HammerEventSetup.Session _session;

        public HammerEventSetup.Session Session => _session;
        public WorldState World => _session.World;
        public RepairTask Repair { get; } = new RepairTask();
        public bool HasShelterEvidence { get; private set; }
        public bool HasWellEvidence { get; private set; }
        public RumorRules Rumor { get; }
        public HelpRules Help { get; }
        public ApprenticeRules Apprentice { get; }
        public OathBell Bell { get; }

        /// <summary>规则层的说明性事件（kind, detail），由 Runtime 写入审计日志。</summary>
        public event Action<string, string> Noted;

        public HammerEventRules(HammerEventSetup.Session session)
        {
            _session = session;
            Action<string, string> note = (kind, detail) => Noted?.Invoke(kind, detail);
            Rumor = new RumorRules(session, note);
            Help = new HelpRules(session, Rumor, note);
            Apprentice = new ApprenticeRules(session, () => Help.FinnReturnedHimself, note);
            Bell = new OathBell(session, note);
            session.Relations.Changed += c => note("relation", c.ToString());
        }

        private bool Near(string npcId) =>
            World.Npcs.TryGetValue(npcId, out var npc) && npc.LocationId != null && npc.LocationId == World.PlayerLocationId;

        // ———————————— 调查 ————————————

        public CommandResult InspectShelter()
        {
            if (World.PlayerLocationId != Ids.Loc.Shelter) return CommandResult.Reject("请先走到旧哨塔石亭。");
            if (HasShelterEvidence) return CommandResult.Ok("你已经记下了箱子里的符文锤。");
            if (World.RequireItem(Ids.Item.Hammer).LocationId != Ids.Loc.Shelter)
                return CommandResult.Reject("箱子里空空的，只有一圈锤子压过的印子。");
            HasShelterEvidence = true;
            World.Commit("evidence:observed_shelter", _ => { });
            Rumor.PlayerSaw(Propositions.Returned, true, HammerEventSetup.ShelterEvidenceId);
            Rumor.PlayerSaw(Propositions.Location, true, HammerEventSetup.ShelterEvidenceId);
            return CommandResult.Ok("石亭的箱子里躺着一把刻着布伦符文的锤子——有人把它还回来了。");
        }

        public CommandResult InspectWell()
        {
            if (World.PlayerLocationId != Ids.Loc.Well) return CommandResult.Reject("请先走到旧井边。");
            if (HasWellEvidence) return CommandResult.Ok("井底那团小小的光还在发抖。");
            HasWellEvidence = true;
            World.Commit("evidence:observed_well", _ => { });
            Rumor.PlayerSaw(Propositions.WellGlow, true, HammerEventSetup.WellEvidenceId);
            Rumor.PlayerSaw(Propositions.WellWisp, true, HammerEventSetup.WellEvidenceId);
            return CommandResult.Ok("你探头往井里看：一团暖黄的小光缩在角落发抖。不是恶灵——是一只走失的灯灵。");
        }

        public CommandResult TakeHammer()
        {
            if (World.PlayerLocationId != Ids.Loc.Shelter) return CommandResult.Reject("请先走到旧哨塔石亭。");
            if (!HasShelterEvidence) return CommandResult.Reject("请先查看箱子。");
            if (World.RequireItem(Ids.Item.Hammer).LocationId != Ids.Loc.Shelter)
                return CommandResult.Reject("锤子已经不在这里了。");
            World.GiveItem(Ids.Item.Hammer, Ids.Player, "hammer:taken");
            return CommandResult.Ok("你拿起了符文锤，沉甸甸的。");
        }

        public CommandResult ShowEvidence(string npcId)
        {
            if (!Near(npcId)) return CommandResult.Reject("请先走到这位居民身边。");
            if (!HasShelterEvidence) return CommandResult.Reject("还没有可出示的线索。");
            if (!Rumor.ShowEvidence(npcId, Propositions.Returned, HammerEventSetup.ShelterEvidenceId))
                return CommandResult.Ok($"{Ids.Name(npcId)}已经看过这条线索了。");
            World.Commit("evidence:shown:" + npcId, _ => { });
            return CommandResult.Ok($"你描述了石亭箱子里的符文锤。{Ids.Name(npcId)}：「原来他早就还了……」");
        }

        public CommandResult ReturnHammer()
        {
            if (!Near(N.Amu)) return CommandResult.Reject("请先走到布伦身边。");
            if (World.RequireItem(Ids.Item.Hammer).HolderId != Ids.Player)
                return CommandResult.Reject("你没有拿着符文锤。");
            // 布伦拿回了锤子，但「芬恩已经还了」需要证据或芬恩本人来证明，不会自动知道。
            World.GiveItem(Ids.Item.Hammer, N.Amu, "hammer:returned");
            Help.Tick();
            TickRepair();
            return CommandResult.Ok("布伦接过符文锤：「好！得赶在日落前把拱门修好。」");
        }

        // ———————————— 传话与帮忙（转发给子规则） ————————————

        public CommandResult Tell(string npcId, string propositionId, TellMode mode)
        {
            var result = Rumor.Tell(npcId, propositionId, mode);
            if (result.Success && npcId == N.Amu && propositionId == Propositions.WantsToLearn &&
                (_session.Knowledge.ViewFor(N.Amu).Current(Propositions.WantsToLearn)?.Stance ?? false))
            {
                Apprentice.Evaluate(out var preview);
                return CommandResult.Ok(result.Message + " " + preview);
            }
            return result;
        }

        public CommandResult AskFinnToReturnHammer() => Help.AskFinnToReturnHammer();
        public CommandResult PromiseToBrunn() => Help.PromiseToBrunn();
        public CommandResult ApologizeToFinn() => Help.ApologizeToFinn();

        // ———————————— 每游戏分钟推进 ————————————

        public void Tick()
        {
            _session.Schedule.Tick(World);
            Rumor.TickGossip();
            Apprentice.Tick();
            Help.Tick();
            TickRepair();

            if (World.Now >= HammerEventSetup.MarketTime && World.GetFlag(Ids.Flag.MarketStarted) == 0)
            {
                if (Repair.State != RepairTaskState.Completed) Repair.State = RepairTaskState.Expired;
                Bell.Ring();
                World.Commit("market:started", w =>
                {
                    w.Flags[Ids.Flag.MarketStarted] = 1;
                    w.Npcs[N.Amu].CurrentTaskId = null;
                });
                Apprentice.Tick(); // 守信徽记显化后，布伦可能当场改口
            }
        }

        private void TickRepair()
        {
            var hammer = World.RequireItem(Ids.Item.Hammer);
            if (Repair.State == RepairTaskState.WaitingForHammer && World.Now < HammerEventSetup.MarketTime &&
                hammer.HolderId == N.Amu && World.Npcs[N.Amu].LocationId == Ids.Loc.Plaza)
            {
                Repair.State = RepairTaskState.Running;
                Repair.StartedAt = World.Now;
                World.Commit("repair:started", w =>
                {
                    w.Npcs[N.Amu].CurrentTaskId = "repair_arch";
                    w.Npcs[N.Amu].Activity = "修复拱门符文";
                });
            }
            if (Repair.State == RepairTaskState.Running)
            {
                int end = Math.Min(World.Now.TotalMinutes, HammerEventSetup.MarketTime.TotalMinutes);
                int elapsed = end - Repair.StartedAt.Value.TotalMinutes;
                int progress = Math.Min(100, elapsed * 100 / RepairTask.DurationMinutes);
                if (progress != World.GetFlag(Ids.Flag.BenchProgress))
                    World.Commit("repair:progress", w => w.Flags[Ids.Flag.BenchProgress] = progress);
                if (progress == 100)
                {
                    Repair.State = RepairTaskState.Completed;
                    World.Commit("repair:completed", w => w.Npcs[N.Amu].CurrentTaskId = null);
                }
            }
        }
    }
}
