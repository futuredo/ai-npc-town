namespace Town.Core.World
{
    /// <summary>首个事件用到的固定 ID。配置化之前先集中在这里，避免字符串散落各处。</summary>
    public static class Ids
    {
        public const string Player = "player";

        public static class Npc
        {
            public const string Amu = "amu";             // 阿木，木匠
            public const string Ahe = "ahe";             // 阿禾，杂货店主
            public const string Xiaochuan = "xiaochuan"; // 小川，跑腿青年
            public static readonly string[] All = { Amu, Ahe, Xiaochuan };
        }

        public static class Loc
        {
            public const string Plaza = "plaza";         // 中央广场
            public const string Workshop = "workshop";   // 木工作坊
            public const string Store = "store";         // 杂货铺
            public const string Shelter = "shelter";     // 公共雨棚
            public const string Homes = "homes";         // 住宅区门口
            public static readonly string[] All = { Plaza, Workshop, Store, Shelter, Homes };
        }

        public static class Item
        {
            public const string Hammer = "hammer";
        }

        public static class Flag
        {
            public const string BenchProgress = "bench_progress"; // 0..100
            public const string MarketStarted = "market_started";
        }
    }
}
