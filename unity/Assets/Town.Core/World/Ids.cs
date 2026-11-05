using System.Collections.Generic;

namespace Town.Core.World
{
    /// <summary>
    /// 《回音镇》固定 ID。内部 ID 沿用早期版本（amu / ahe / xiaochuan 等），只改显示名，
    /// 避免牵动测试和日志格式；配置化时再统一改名。
    /// </summary>
    public static class Ids
    {
        public const string Player = "player";

        public static class Npc
        {
            public const string Amu = "amu";             // 布伦，矮人符文铁匠
            public const string Ahe = "ahe";             // 梅芙，「醉月」旅店老板娘
            public const string Xiaochuan = "xiaochuan"; // 芬恩，半精灵跑腿少年
            public static readonly string[] All = { Amu, Ahe, Xiaochuan };
        }

        public static class Loc
        {
            public const string Plaza = "plaza";         // 灯节广场（拱门在这里）
            public const string Workshop = "workshop";   // 符文铁匠铺
            public const string Store = "store";         // 「醉月」旅店
            public const string Shelter = "shelter";     // 旧哨塔石亭
            public const string Homes = "homes";         // 民居
            public const string Well = "well";           // 旧井
            public const string BellTower = "belltower"; // 钟楼（回音石板）
            public static readonly string[] All = { Plaza, Workshop, Store, Shelter, Homes, Well, BellTower };
        }

        public static class Item
        {
            public const string Hammer = "hammer";
        }

        public static class Flag
        {
            /// <summary>灯节拱门符文修理进度 0..100（沿用早期「长凳」的 ID）。</summary>
            public const string BenchProgress = "bench_progress";
            /// <summary>18:00 誓钟已敲响 / 灯节开始。</summary>
            public const string MarketStarted = "market_started";
            public const string ThiefMark = "thief_mark";         // 「芬恩偷了符文锤」显化
            public const string WispEvil = "wisp_evil";           // 「井里是恶灵」显化
            public const string WispGuardian = "wisp_guardian";   // 「井里是灯灵」显化
            public const string FinnBadge = "finn_badge";         // 「芬恩守信」显化
        }

        public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
        {
            [Player] = "你",
            [Npc.Amu] = "布伦",
            [Npc.Ahe] = "梅芙",
            [Npc.Xiaochuan] = "芬恩",
            [Loc.Plaza] = "灯节广场",
            [Loc.Workshop] = "符文铁匠铺",
            [Loc.Store] = "醉月旅店",
            [Loc.Shelter] = "旧哨塔石亭",
            [Loc.Homes] = "民居",
            [Loc.Well] = "旧井",
            [Loc.BellTower] = "钟楼",
        };

        public static string Name(string id) => id != null && Names.TryGetValue(id, out var n) ? n : (id ?? "路上");

        /// <summary>给模型的人物身份（姓名取自 <see cref="Names"/>，避免与显示名分叉）。只描述身份与口吻，不含认知或世界真值。</summary>
        public static readonly IReadOnlyDictionary<string, string> Personas = new Dictionary<string, string>
        {
            [Npc.Amu] = Names[Npc.Amu] + "，矮人符文铁匠，拱门符文的守护者；说话短、带反问，只信亲眼所见",
            [Npc.Ahe] = Names[Npc.Ahe] + "，醉月旅店老板娘，消息灵通、爱讲故事，说话热闹",
            [Npc.Xiaochuan] = Names[Npc.Xiaochuan] + "，半精灵跑腿少年，说话结巴、容易脸红，想学符文锻造",
        };

        public static string Persona(string npcId) =>
            npcId != null && Personas.TryGetValue(npcId, out var p) ? p : "小镇居民";
    }
}
