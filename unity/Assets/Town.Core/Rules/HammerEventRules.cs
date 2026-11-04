using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.Tasks;
using Town.Core.World;

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

    /// <summary>只接受显式玩家交互。模型台词没有调用本规则的权限。</summary>
    public sealed class HammerEventRules
    {
        private readonly HammerEventSetup.Session _session;
        public RepairTask Repair { get; } = new RepairTask();
        public bool HasShelterEvidence { get; private set; }
        private WorldState World => _session.World;
        public HammerEventRules(HammerEventSetup.Session session) { _session = session; }

        public CommandResult InspectShelter()
        {
            if (World.PlayerLocationId != Ids.Loc.Shelter) return CommandResult.Reject("请先走到雨棚。 ");
            if (HasShelterEvidence) return CommandResult.Ok("已记录带作坊标记的锤子线索。");
            if (World.RequireItem(Ids.Item.Hammer).LocationId != Ids.Loc.Shelter)
                return CommandResult.Reject("箱子里已经没有锤子。");
            HasShelterEvidence = true;
            World.Commit("evidence:observed_shelter", _ => { });
            return CommandResult.Ok("发现带作坊标记的锤子，已记下发现位置。");
        }

        public CommandResult TakeHammer()
        {
            if (World.PlayerLocationId != Ids.Loc.Shelter) return CommandResult.Reject("请先走到雨棚。");
            if (!HasShelterEvidence) return CommandResult.Reject("请先查看收纳箱。");
            if (World.RequireItem(Ids.Item.Hammer).LocationId != Ids.Loc.Shelter)
                return CommandResult.Reject("锤子已被取走，不能重复领取。");
            World.GiveItem(Ids.Item.Hammer, Ids.Player, "hammer:taken");
            return CommandResult.Ok("已拿起锤子。");
        }

        public CommandResult ShowEvidence(string npcId)
        {
            if (!World.Npcs.TryGetValue(npcId, out var npc) || npc.LocationId != World.PlayerLocationId)
                return CommandResult.Reject("请先走到这位居民身边。");
            if (!HasShelterEvidence) return CommandResult.Reject("还没有可出示的线索。");
            if (_session.Knowledge.ViewFor(npcId).History.ExistsEvidence())
                return CommandResult.Ok("这位居民已经看过该线索。");
            // 只证明曾归还；取走之后不再把旧位置作为当前事实写入认知。
            _session.Knowledge.Learn(npcId, Propositions.Returned, true, 1f, SourceType.SawEvidence,
                World.Now, evidenceId: HammerEventSetup.ShelterEvidenceId);
            World.Commit("evidence:shown:" + npcId, _ => { });
            return CommandResult.Ok("已出示线索，并记录这位居民的认知变化。");
        }

        public CommandResult ReturnHammer()
        {
            if (World.PlayerLocationId != World.Npcs[Ids.Npc.Amu].LocationId)
                return CommandResult.Reject("请先走到阿木身边。");
            if (World.RequireItem(Ids.Item.Hammer).HolderId != Ids.Player)
                return CommandResult.Reject("你没有拿着锤子。");
            World.GiveItem(Ids.Item.Hammer, Ids.Npc.Amu, "hammer:returned");
            _session.Knowledge.Learn(Ids.Npc.Amu, Propositions.Returned, true, 1f,
                SourceType.Experienced, World.Now);
            World.Commit("knowledge:amu_received_hammer", _ => { });
            Tick();
            return CommandResult.Ok("锤子已归还阿木。");
        }

        public void Tick()
        {
            var hammer = World.RequireItem(Ids.Item.Hammer);
            if (Repair.State == RepairTaskState.WaitingForHammer && World.Now < HammerEventSetup.MarketTime &&
                hammer.HolderId == Ids.Npc.Amu && World.Npcs[Ids.Npc.Amu].LocationId == Ids.Loc.Plaza)
            {
                Repair.State = RepairTaskState.Running;
                Repair.StartedAt = World.Now;
                World.Commit("repair:started", w => w.Npcs[Ids.Npc.Amu].CurrentTaskId = "repair_bench");
            }
            if (Repair.State == RepairTaskState.Running)
            {
                int end = System.Math.Min(World.Now.TotalMinutes, HammerEventSetup.MarketTime.TotalMinutes);
                int elapsed = end - Repair.StartedAt.Value.TotalMinutes;
                int progress = System.Math.Min(100, elapsed * 100 / RepairTask.DurationMinutes);
                if (progress != World.GetFlag(Ids.Flag.BenchProgress))
                    World.Commit("repair:progress", w => w.Flags[Ids.Flag.BenchProgress] = progress);
                if (progress == 100)
                {
                    Repair.State = RepairTaskState.Completed;
                    World.Commit("repair:completed", w => w.Npcs[Ids.Npc.Amu].CurrentTaskId = null);
                }
            }
            if (World.Now >= HammerEventSetup.MarketTime && World.GetFlag(Ids.Flag.MarketStarted) == 0)
            {
                if (Repair.State != RepairTaskState.Completed) Repair.State = RepairTaskState.Expired;
                World.Commit("market:started", w =>
                {
                    w.Flags[Ids.Flag.MarketStarted] = 1;
                    w.Npcs[Ids.Npc.Amu].CurrentTaskId = null;
                });
            }
        }
    }

    internal static class EvidenceHistory
    {
        internal static bool ExistsEvidence(this System.Collections.Generic.IReadOnlyList<Belief> beliefs)
        {
            foreach (var belief in beliefs)
                if (belief.EvidenceId == HammerEventSetup.ShelterEvidenceId) return true;
            return false;
        }
    }
}
