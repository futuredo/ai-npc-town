using System;
using System.Collections.Generic;
using Town.Core.World;

namespace Town.Core.Social
{
    /// <summary>关系只有三档，保证玩家读得懂（策划大纲 v0.2 §6）。</summary>
    public static class RelationLevel
    {
        public const int Wary = -1;
        public const int Neutral = 0;
        public const int Trust = 1;

        public static string Name(int level) => level <= Wary ? "戒备" : level >= Trust ? "信任" : "普通";
    }

    public sealed class RelationChange
    {
        public string From { get; }
        public string To { get; }
        public int Old { get; }
        public int New { get; }
        public string Reason { get; }
        public GameTime At { get; }

        internal RelationChange(string from, string to, int oldLevel, int newLevel, string reason, GameTime at)
        {
            From = from; To = to; Old = oldLevel; New = newLevel; Reason = reason; At = at;
        }

        public override string ToString() =>
            $"{Ids.Name(From)}对{Ids.Name(To)}：{RelationLevel.Name(Old)}→{RelationLevel.Name(New)}（{Reason}）";
    }

    /// <summary>
    /// 有向关系表（A 对 B 的态度）。只由规则修改，每次变化都记录原因；
    /// 同一触发键只生效一次，防止重复操作刷关系。
    /// </summary>
    public sealed class RelationshipBook
    {
        private readonly Dictionary<string, int> _levels = new Dictionary<string, int>();
        private readonly HashSet<string> _appliedTriggers = new HashSet<string>();
        private readonly List<RelationChange> _history = new List<RelationChange>();

        public IReadOnlyList<RelationChange> History => _history;
        public event Action<RelationChange> Changed;

        private static string Key(string from, string to) => from + "→" + to;

        public int Get(string from, string to) => _levels.TryGetValue(Key(from, to), out var v) ? v : RelationLevel.Neutral;

        /// <summary>初始化用，不记录历史。</summary>
        public void SetInitial(string from, string to, int level) => _levels[Key(from, to)] = Clamp(level);

        /// <summary>按触发键只应用一次。返回 true 表示等级确实发生了变化。</summary>
        public bool ApplyOnce(string triggerKey, string from, string to, int delta, string reason, GameTime at)
        {
            if (!_appliedTriggers.Add(triggerKey)) return false;
            return Shift(from, to, Clamp(Get(from, to) + delta), reason, at);
        }

        /// <summary>设到指定档位（例如道歉后从戒备回到普通），同样按触发键只生效一次。</summary>
        public bool SetOnce(string triggerKey, string from, string to, int level, string reason, GameTime at)
        {
            if (!_appliedTriggers.Add(triggerKey)) return false;
            return Shift(from, to, Clamp(level), reason, at);
        }

        public bool WasTriggered(string triggerKey) => _appliedTriggers.Contains(triggerKey);

        private bool Shift(string from, string to, int level, string reason, GameTime at)
        {
            int old = Get(from, to);
            if (old == level) return false;
            _levels[Key(from, to)] = level;
            var change = new RelationChange(from, to, old, level, reason, at);
            _history.Add(change);
            Changed?.Invoke(change);
            return true;
        }

        private static int Clamp(int v) => Math.Max(RelationLevel.Wary, Math.Min(RelationLevel.Trust, v));
    }
}
