using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public enum DayPhase { Dawn, Noon, Dusk, Night }

    /// <summary>一条认知：只属于一个持有者。模型只能引用自己持有的认知。</summary>
    public sealed class DsBelief
    {
        public string Id;
        public string Owner;
        public string Text;
        public string Topic;
        /// <summary>秘密只有在信任足够时才允许说出口（规则裁决，不靠模型自觉）。</summary>
        public float RevealTrust;
        /// <summary>该认知是否属实；不属实的认知是“错误相信”，可被传播。</summary>
        public bool True = true;
        public string Source = "seen";
    }

    /// <summary>世界规律：语义判定的依据（例：雏龙爱吃熏鱼、龙厌恶冷铁）。</summary>
    public sealed class DsLaw
    {
        public string Id;
        public string Text;
        public string[] SupportTags = Array.Empty<string>();
        public string[] HinderTags = Array.Empty<string>();
        /// <summary>公共常识无需发现即可引用；其他规律需玩家先从别人口中得知。</summary>
        public bool Common;
    }

    public sealed class DsItem
    {
        public string Id;
        public string Name;
        public string Holder;   // 角色 ID 或 "place:xxx"
        public HashSet<string> Tags = new HashSet<string>();
        public string Look;
        public bool Crafted;
    }

    public sealed class DsActor
    {
        public string Id;
        public string Name;
        public string Role;
        public string Persona;
        public string Goal;
        public string[] Values = Array.Empty<string>();
        public string Place;
        /// <summary>对玩家的信任，0..1。</summary>
        public float Trust;
        public float Fear;
        /// <summary>说服可接受的论点标签：命中才允许较大的信任变化。</summary>
        public string[] Persuadable = Array.Empty<string>();
    }

    public sealed class DsWhelp
    {
        public string Place = "docks";
        public bool Hidden = true;
        public float Hunger = 0.8f;
        public float Fear = 0.6f;
        public float TrustPlayer;
        public string CapturedBy;
        public bool WithMother;
        /// <summary>跟随玩家：玩家进入新地点时，雏龙一起移动（由规则写入，不由模型宣称）。</summary>
        public bool Following;
    }

    /// <summary>龙眠镇的世界真值。只有规则引擎能写；模型只看到分区后的 PerceptionView。</summary>
    public sealed class DsWorld
    {
        public int Day = 1;
        public DayPhase Phase = DayPhase.Dawn;
        public int Version;
        public int SessionGen = 1;
        public int Seed = 20261108;
        public int RollCounter;
        public string PlayerPlace = "market";
        public float Tension = 0.2f;
        public int Coins = 12;
        public int MajorEventsToday;

        public readonly Dictionary<string, DsActor> Actors = new Dictionary<string, DsActor>();
        public readonly List<DsBelief> Beliefs = new List<DsBelief>();
        public readonly HashSet<string> PlayerKnows = new HashSet<string>();
        public readonly Dictionary<string, DsLaw> Laws = new Dictionary<string, DsLaw>();
        public readonly Dictionary<string, DsItem> Items = new Dictionary<string, DsItem>();
        public readonly Dictionary<string, string> Places = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Flags = new Dictionary<string, string>();
        /// <summary>有向居民关系；只在相应居民的视角中暴露其自身的信任。</summary>
        public readonly Dictionary<string, float> Relations = new Dictionary<string, float>();
        public readonly List<string> Chronicle = new List<string>();
        public readonly List<string> PublicNews = new List<string>();
        public DsWhelp Whelp = new DsWhelp();

        /// <summary>
        /// 仅计划静态校验的试运行副本使用：固定掷骰（假设每次都成功、不被看见），且跳过依赖未知岗哨的前置条件。
        /// 不进入快照，正式世界与回放永远为空。
        /// </summary>
        internal double? ForcedRoll;
        internal bool DryRun;

        public IEnumerable<DsBelief> BeliefsOf(string owner) => Beliefs.Where(b => b.Owner == owner);
        public DsBelief Belief(string id) => Beliefs.FirstOrDefault(b => b.Id == id);
        public IEnumerable<DsItem> Inventory(string holder) => Items.Values.Where(i => i.Holder == holder);
        public bool KnowsLaw(string lawId) =>
            Laws.TryGetValue(lawId, out var law) && (law.Common || PlayerKnows.Contains("law:" + lawId));

        public int TimeIndex => (Day - 1) * 4 + (int)Phase;
        public bool FinalDawn => Day >= 4;

        /// <summary>时间推进一个时段。返回是否进入夜晚（夜晚由多智能体轮次处理）。</summary>
        public bool Advance()
        {
            if (Phase == DayPhase.Night) { Phase = DayPhase.Dawn; Day++; MajorEventsToday = 0; }
            else Phase++;
            Version++;
            return Phase == DayPhase.Night;
        }

        /// <summary>同一种子 + 同一计数 → 同一结果，用于重放与对比实验。</summary>
        public double NextRoll()
        {
            if (ForcedRoll.HasValue) { RollCounter++; return ForcedRoll.Value; }
            unchecked
            {
                uint x = (uint)(Seed * 2654435761u) ^ (uint)(++RollCounter * 40503u);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                return (x % 10000u) / 10000.0;
            }
        }

        public static string PhaseName(DayPhase p) =>
            p == DayPhase.Dawn ? "清晨" : p == DayPhase.Noon ? "正午" : p == DayPhase.Dusk ? "黄昏" : "深夜";

        public string ClockText => $"第{Day}天·{PhaseName(Phase)}";
    }
}
