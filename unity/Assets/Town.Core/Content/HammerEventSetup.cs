using Town.Core.Knowledge;
using Town.Core.World;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Core.Content
{
    /// <summary>
    /// 「集市前的工具失踪」初始状态：世界事实（策划 v0.2 §5.1）+ 初始认知（§5.3）。
    /// 以后会改成从配置读取；先写死，保证规格和代码一一对应、可测试。
    /// </summary>
    public static class HammerEventSetup
    {
        public static readonly GameTime StartTime = GameTime.At(1, 9, 0);
        public static readonly GameTime MarketTime = GameTime.At(1, 18, 0);
        /// <summary>昨天下午借锤子的时间。</summary>
        public static readonly GameTime BorrowTime = GameTime.At(0, 15, 0);
        public const string ShelterEvidenceId = "C1_hammer_in_shelter";

        public sealed class Session
        {
            public WorldState World;
            public KnowledgeStore Knowledge;
        }

        public static Session Create(int sessionGen = 1)
        {
            var w = new WorldState(StartTime, sessionGen);

            w.Items[Ids.Item.Hammer] = new ItemState
            {
                Id = Ids.Item.Hammer,
                DisplayName = "锤子",
                OwnerId = Ids.Npc.Amu,
                LocationId = Ids.Loc.Shelter,   // F4：真实位置在雨棚收纳箱
                Mark = "作坊标记",
            };

            w.Npcs[Ids.Npc.Amu] = new NpcState { Id = Ids.Npc.Amu, DisplayName = "阿木", LocationId = Ids.Loc.Plaza };
            w.Npcs[Ids.Npc.Ahe] = new NpcState { Id = Ids.Npc.Ahe, DisplayName = "阿禾", LocationId = Ids.Loc.Store };
            w.Npcs[Ids.Npc.Xiaochuan] = new NpcState { Id = Ids.Npc.Xiaochuan, DisplayName = "小川", LocationId = Ids.Loc.Homes };
            w.PlayerLocationId = Ids.Loc.Plaza;
            w.Flags[Ids.Flag.BenchProgress] = 0;

            var k = new KnowledgeStore();
            var t = BorrowTime;

            // 阿木：知道借出（亲历），推断「没还」（架子是空的），不知道位置，不信偷窃
            k.Learn(Ids.Npc.Amu, P.Took, true, 1f, SourceType.Experienced, t);
            k.Learn(Ids.Npc.Amu, P.Borrowed, true, 1f, SourceType.Experienced, t);
            k.Learn(Ids.Npc.Amu, P.Returned, false, 0.7f, SourceType.Inferred, StartTime);
            k.Learn(Ids.Npc.Amu, P.Stole, false, 0.8f, SourceType.Inferred, StartTime);

            // 阿禾：目击拿走，不知道借用约定，对偷窃有点怀疑（推断，低把握）
            k.Learn(Ids.Npc.Ahe, P.Took, true, 0.9f, SourceType.Witnessed, t);
            k.Learn(Ids.Npc.Ahe, P.Stole, true, 0.3f, SourceType.Inferred, StartTime);

            // 小川：借用、归还、位置都是亲历
            k.Learn(Ids.Npc.Xiaochuan, P.Took, true, 1f, SourceType.Experienced, t);
            k.Learn(Ids.Npc.Xiaochuan, P.Borrowed, true, 1f, SourceType.Experienced, t);
            k.Learn(Ids.Npc.Xiaochuan, P.Returned, true, 1f, SourceType.Experienced, t.AddMinutes(120));
            k.Learn(Ids.Npc.Xiaochuan, P.Location, true, 1f, SourceType.Experienced, t.AddMinutes(120));

            return new Session { World = w, Knowledge = k };
        }
    }
}
