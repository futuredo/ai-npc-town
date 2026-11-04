using System;
using System.Collections.Generic;

namespace Town.Core.World
{
    /// <summary>物品的物理状态：在哪里、谁拿着、属于谁。</summary>
    public sealed class ItemState
    {
        public string Id;
        public string DisplayName;
        public string OwnerId;
        /// <summary>所在地点 ID；被人拿着时为 null。</summary>
        public string LocationId;
        /// <summary>持有者 ID（NPC 或玩家）；放在地点里时为 null。</summary>
        public string HolderId;
        /// <summary>物品上的可辨认标记，例如「作坊标记」。</summary>
        public string Mark;
    }

    public sealed class NpcState
    {
        public string Id;
        public string DisplayName;
        public string LocationId;
        public string CurrentTaskId;
    }

    /// <summary>
    /// 世界事实（真值层）。只有规则代码可以修改，每次修改必须走 <see cref="Commit"/> 使版本号 +1。
    /// NPC 大脑和提示组装不能直接拿到它。
    /// </summary>
    public sealed class WorldState
    {
        public int Version { get; private set; }
        public int SessionGen { get; private set; }
        public GameTime Now { get; private set; }

        public readonly Dictionary<string, ItemState> Items = new Dictionary<string, ItemState>();
        public readonly Dictionary<string, NpcState> Npcs = new Dictionary<string, NpcState>();
        /// <summary>事件标志，例如 bench_repaired、market_started。</summary>
        public readonly Dictionary<string, int> Flags = new Dictionary<string, int>();

        public string PlayerLocationId;

        public event Action<WorldState, string> Changed;

        public WorldState(GameTime start, int sessionGen = 1)
        {
            Now = start;
            SessionGen = sessionGen;
        }

        /// <summary>对世界做一次原子修改。reason 会进入日志。</summary>
        public void Commit(string reason, Action<WorldState> mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            mutation(this);
            Version++;
            Changed?.Invoke(this, reason);
        }

        public void AdvanceTime(int minutes)
        {
            if (minutes <= 0) return;
            Commit($"time+{minutes}", w => w.Now = w.Now.AddMinutes(minutes));
        }

        /// <summary>开始新局或读档：代际 +1，之前发出的模型请求全部作废。</summary>
        public void BumpSession() => SessionGen++;

        public int GetFlag(string key) => Flags.TryGetValue(key, out var v) ? v : 0;

        // —— 物品操作（规则层用） ——

        public void MoveItemToLocation(string itemId, string locationId, string reason)
        {
            var item = RequireItem(itemId);
            Commit(reason, w => { item.HolderId = null; item.LocationId = locationId; });
        }

        public void GiveItem(string itemId, string holderId, string reason)
        {
            var item = RequireItem(itemId);
            Commit(reason, w => { item.LocationId = null; item.HolderId = holderId; });
        }

        public ItemState RequireItem(string itemId)
        {
            if (!Items.TryGetValue(itemId, out var item))
                throw new KeyNotFoundException($"未知物品 {itemId}");
            return item;
        }
    }
}
