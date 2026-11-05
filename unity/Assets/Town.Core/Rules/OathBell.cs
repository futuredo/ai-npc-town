using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;

namespace Town.Core.Rules
{
    /// <summary>回音石板上的一行：某条可显化说法现在有几人信、来自几个独立源头。</summary>
    public sealed class BellReading
    {
        public string PropositionId;
        public IReadOnlyList<string> Believers;
        public IReadOnlyList<string> Origins;
        public bool Ready => Believers.Count >= OathBell.MinBelievers && Origins.Count >= OathBell.MinOrigins;
    }

    /// <summary>
    /// 誓钟规则（玩法设计 v2 §3）：日落时，至少 2 位居民相信、且来自至少 2 个独立源头的可显化说法变成现实。
    /// 玩家（外乡人）的信念不计数。由规则统一判定，模型不能触发。
    /// </summary>
    public sealed class OathBell
    {
        public const int MinBelievers = 2;
        public const int MinOrigins = 2;

        private readonly HammerEventSetup.Session _s;
        private readonly Action<string, string> _note;
        private readonly List<BellReading> _manifested = new List<BellReading>();

        public bool Rung { get; private set; }
        public IReadOnlyList<BellReading> Manifested => _manifested;
        /// <summary>钟响时的完整读数（含未显化的），用于结算展示。</summary>
        public IReadOnlyList<BellReading> FinalReadings { get; private set; } = new BellReading[0];

        public OathBell(HammerEventSetup.Session session, Action<string, string> note)
        {
            _s = session;
            _note = note ?? ((_, __) => { });
        }

        /// <summary>回音石板：随时可读，只给数量，界面不显示具体是谁。</summary>
        public IReadOnlyList<BellReading> Read()
        {
            var list = new List<BellReading>();
            foreach (var p in Propositions.All.Values.Where(p => p.Manifestable).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var believers = new List<string>();
                var origins = new HashSet<string>();
                foreach (var npc in Ids.Npc.All)
                {
                    var b = _s.Knowledge.ViewFor(npc).Current(p.Id);
                    if (b == null || !b.Stance || b.Confidence < RumorRules.ShareThreshold) continue;
                    believers.Add(npc);
                    // 这个人的信念来自哪些源头：当前认知的源头 + 他听到过的所有同向说法的源头
                    origins.Add(RumorRules.OriginOf(b));
                    foreach (var o in _s.Ledger.OriginsHeard(npc, p.Id, true)) origins.Add(o);
                }
                list.Add(new BellReading { PropositionId = p.Id, Believers = believers, Origins = origins.OrderBy(o => o, StringComparer.Ordinal).ToList() });
            }
            return list;
        }

        public void Ring()
        {
            if (Rung) return;
            Rung = true;
            FinalReadings = Read();
            var ready = FinalReadings.Where(r => r.Ready).ToList();

            // 恶灵与灯灵互斥：都满足时信的人多者胜，一样多则灯灵困惑地离开。
            var evil = ready.FirstOrDefault(r => r.PropositionId == Propositions.WellEvil);
            var wisp = ready.FirstOrDefault(r => r.PropositionId == Propositions.WellWisp);
            if (evil != null && wisp != null)
            {
                if (evil.Believers.Count > wisp.Believers.Count) ready.Remove(wisp);
                else if (wisp.Believers.Count > evil.Believers.Count) ready.Remove(evil);
                else { ready.Remove(evil); ready.Remove(wisp); _note("bell", "镇上对井里的东西说法不一，灯灵困惑地飘走了"); }
            }

            _manifested.AddRange(ready);
            _s.World.Commit("bell:rung", w =>
            {
                foreach (var r in ready)
                {
                    if (r.PropositionId == Propositions.Stole) w.Flags[Ids.Flag.ThiefMark] = 1;
                    if (r.PropositionId == Propositions.WellEvil) w.Flags[Ids.Flag.WispEvil] = 1;
                    if (r.PropositionId == Propositions.WellWisp) w.Flags[Ids.Flag.WispGuardian] = 1;
                    if (r.PropositionId == Propositions.Reliable) w.Flags[Ids.Flag.FinnBadge] = 1;
                }
            });
            foreach (var r in ready) _note("bell", "显化：" + Propositions.All[r.PropositionId].ManifestEffect);
            if (ready.Count == 0) _note("bell", "誓钟响了三声，什么也没有改变");
        }
    }
}
