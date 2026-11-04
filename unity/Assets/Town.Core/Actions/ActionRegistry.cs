using System.Collections.Generic;

namespace Town.Core.Actions
{
    /// <summary>合法动作的定义。模型只能从这里选，参数由校验器检查。</summary>
    public sealed class ActionDef
    {
        public string Id;
        public string Description;
        /// <summary>参数名列表，例如 target_location / item_id。</summary>
        public string[] Params;
        /// <summary>能执行此动作的 NPC；null 表示所有人都能。</summary>
        public string[] AllowedActors;
    }

    public static class ActionIds
    {
        public const string None = "none";
        public const string MoveTo = "move_to";
        public const string Wait = "wait";
        public const string GiveItem = "give_item";
        public const string FetchItem = "fetch_item";
        public const string StartRepair = "start_repair";
        public const string GuidePlayer = "guide_player";
    }

    public sealed class ActionRegistry
    {
        private readonly Dictionary<string, ActionDef> _defs = new Dictionary<string, ActionDef>();

        public IEnumerable<ActionDef> All => _defs.Values;

        public void Register(ActionDef def) => _defs[def.Id] = def;
        public bool TryGet(string id, out ActionDef def) => _defs.TryGetValue(id, out def);

        /// <summary>首个事件的 7 个动作（策划大纲 v0.2 / 实现大纲 §4）。</summary>
        public static ActionRegistry CreateDefault()
        {
            var r = new ActionRegistry();
            r.Register(new ActionDef { Id = ActionIds.None, Description = "不做动作，只回话", Params = new string[0] });
            r.Register(new ActionDef { Id = ActionIds.MoveTo, Description = "走到某个地点", Params = new[] { "target_location" } });
            r.Register(new ActionDef { Id = ActionIds.Wait, Description = "原地等待若干分钟", Params = new[] { "minutes" } });
            r.Register(new ActionDef { Id = ActionIds.GiveItem, Description = "把手上的物品交给某人", Params = new[] { "item_id", "to_actor" } });
            r.Register(new ActionDef { Id = ActionIds.FetchItem, Description = "去某处取回物品", Params = new[] { "item_id", "from_location" } });
            r.Register(new ActionDef { Id = ActionIds.StartRepair, Description = "用锤子修长凳", Params = new string[0], AllowedActors = new[] { World.Ids.Npc.Amu } });
            r.Register(new ActionDef { Id = ActionIds.GuidePlayer, Description = "带玩家去某个地点", Params = new[] { "target_location" } });
            return r;
        }
    }
}
