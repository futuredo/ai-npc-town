using Town.Core.Knowledge;
using Town.Core.Schedule;
using Town.Core.Social;
using Town.Core.World;
using P = Town.Core.Knowledge.Propositions;
using N = Town.Core.World.Ids.Npc;
using L = Town.Core.World.Ids.Loc;

namespace Town.Core.Content
{
    /// <summary>
    /// 《未竟之镇》第七章的初始状态：世界事实、初始认知、关系与日程。
    /// 类名沿用早期版本以免牵动调用方；以后改为从配置读取。
    /// </summary>
    public static class HammerEventSetup
    {
        public static readonly GameTime StartTime = GameTime.At(1, 9, 0);
        /// <summary>日落，墨钟敲响，灯节开始。</summary>
        public static readonly GameTime MarketTime = GameTime.At(1, 18, 0);
        /// <summary>昨晚那场雨。</summary>
        public static readonly GameTime BorrowTime = GameTime.At(0, 15, 0);
        public static readonly GameTime RainNight = GameTime.At(0, 20, 0);
        public const string ShelterEvidenceId = "C1_hammer_in_shelter";
        public const string WellEvidenceId = "E2_wisp_in_well";

        public sealed class Session
        {
            public WorldState World;
            public KnowledgeStore Knowledge;
            public RelationshipBook Relations;
            public ScheduleSystem Schedule;
            public RumorLedger Ledger;
        }

        public static Session Create(int sessionGen = 1)
        {
            var w = new WorldState(StartTime, sessionGen);

            w.Items[Ids.Item.Hammer] = new ItemState
            {
                Id = Ids.Item.Hammer,
                DisplayName = "符文锤",
                OwnerId = N.Amu,
                LocationId = L.Shelter,   // 真实位置：旧哨塔石亭的箱子
                Mark = "布伦的符文",
            };

            w.Npcs[N.Amu] = new NpcState { Id = N.Amu, DisplayName = Ids.Name(N.Amu), LocationId = L.Plaza, Activity = "对着熄灭的拱门发愁" };
            w.Npcs[N.Ahe] = new NpcState { Id = N.Ahe, DisplayName = Ids.Name(N.Ahe), LocationId = L.Store, Activity = "照看旅店" };
            w.Npcs[N.Xiaochuan] = new NpcState { Id = N.Xiaochuan, DisplayName = Ids.Name(N.Xiaochuan), LocationId = L.Homes, Activity = "准备出门送货" };
            w.PlayerLocationId = L.Plaza;
            w.Flags[Ids.Flag.BenchProgress] = 0;

            var k = new KnowledgeStore();
            var t = BorrowTime;

            // 布伦：亲手借出；推断「没还」（铺子里没有）；不信芬恩会偷；心里想收徒
            k.Learn(N.Amu, P.Took, true, 1f, SourceType.Experienced, t);
            k.Learn(N.Amu, P.Borrowed, true, 1f, SourceType.Experienced, t);
            k.Learn(N.Amu, P.Returned, false, 0.7f, SourceType.Inferred, StartTime);
            k.Learn(N.Amu, P.Stole, false, 0.8f, SourceType.Inferred, StartTime);
            k.Learn(N.Amu, P.WantsApprentice, true, 1f, SourceType.Experienced, StartTime);

            // 梅芙：看见芬恩拿走锤子，不知道是借的，怀疑是偷；昨晚在井边看见幽光，认定是恶灵
            k.Learn(N.Ahe, P.Took, true, 0.9f, SourceType.Witnessed, t);
            k.Learn(N.Ahe, P.Stole, true, 0.5f, SourceType.Inferred, StartTime);
            k.Learn(N.Ahe, P.WellGlow, true, 1f, SourceType.Witnessed, RainNight);
            k.Learn(N.Ahe, P.WellEvil, true, 0.6f, SourceType.Inferred, RainNight);

            // 芬恩：借、还、锤子位置都是亲历；灯灵跟着他飘到井边，他看清了；想学符文锻造
            k.Learn(N.Xiaochuan, P.Took, true, 1f, SourceType.Experienced, t);
            k.Learn(N.Xiaochuan, P.Borrowed, true, 1f, SourceType.Experienced, t);
            k.Learn(N.Xiaochuan, P.Returned, true, 1f, SourceType.Experienced, RainNight);
            k.Learn(N.Xiaochuan, P.Location, true, 1f, SourceType.Experienced, RainNight);
            k.Learn(N.Xiaochuan, P.WellGlow, true, 1f, SourceType.Witnessed, RainNight);
            k.Learn(N.Xiaochuan, P.WellWisp, true, 1f, SourceType.Witnessed, RainNight);
            k.Learn(N.Xiaochuan, P.WantsToLearn, true, 1f, SourceType.Experienced, StartTime);

            var r = new RelationshipBook();
            r.SetInitial(N.Amu, N.Xiaochuan, RelationLevel.Trust);
            r.SetInitial(N.Xiaochuan, N.Amu, RelationLevel.Trust);
            // 其余（包括所有人对玩家）都从「普通」开始

            var s = new ScheduleSystem();
            s.Add(new NpcSchedule(N.Amu,
                new ScheduleEntry(0, 0, L.Homes, "休息"),
                new ScheduleEntry(8, 0, L.Plaza, "对着熄灭的拱门发愁"),
                new ScheduleEntry(20, 0, L.Homes, "休息")));
            s.Add(new NpcSchedule(N.Ahe,
                new ScheduleEntry(0, 0, L.Homes, "休息"),
                new ScheduleEntry(8, 0, L.Store, "照看旅店"),
                new ScheduleEntry(14, 0, L.Plaza, "给广场送酒"),
                new ScheduleEntry(14, 40, L.Store, "照看旅店"),
                new ScheduleEntry(17, 50, L.Plaza, "等灯节开始"),
                new ScheduleEntry(20, 0, L.Homes, "休息")));
            s.Add(new NpcSchedule(N.Xiaochuan,
                new ScheduleEntry(0, 0, L.Homes, "准备出门送货"),
                new ScheduleEntry(9, 20, L.Store, "在旅店取货"),
                new ScheduleEntry(10, 0, L.Plaza, "送货"),
                new ScheduleEntry(10, 30, L.Homes, "送货"),
                new ScheduleEntry(11, 0, L.Store, "在旅店取货"),
                new ScheduleEntry(11, 40, L.Workshop, "在铁匠铺外张望"),
                new ScheduleEntry(12, 0, L.Homes, "午休", busy: true),
                new ScheduleEntry(13, 0, L.Store, "在旅店取货"),
                new ScheduleEntry(13, 40, L.Workshop, "在铁匠铺外张望"),
                new ScheduleEntry(14, 30, L.Homes, "送货"),
                new ScheduleEntry(15, 0, L.Store, "在旅店取货"),
                new ScheduleEntry(15, 40, L.Plaza, "送货"),
                new ScheduleEntry(16, 20, L.Homes, "送货"),
                new ScheduleEntry(17, 0, L.Store, "在旅店取货"),
                new ScheduleEntry(17, 50, L.Plaza, "等灯节开始"),
                new ScheduleEntry(20, 0, L.Homes, "休息")));

            return new Session { World = w, Knowledge = k, Relations = r, Schedule = s, Ledger = new RumorLedger() };
        }
    }
}
