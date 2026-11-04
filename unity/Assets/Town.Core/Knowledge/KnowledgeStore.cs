using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.World;

namespace Town.Core.Knowledge
{
    /// <summary>
    /// 角色认知层。所有写入都必须带来源；每个 NPC 只能通过 <see cref="ViewFor"/> 读到自己的认知。
    /// </summary>
    public sealed class KnowledgeStore
    {
        private readonly List<Belief> _beliefs = new List<Belief>();
        private int _nextId = 1;

        public IReadOnlyList<Belief> AllForDebug => _beliefs;

        public Belief Learn(string npcId, string propositionId, bool stance, float confidence,
            SourceType source, GameTime at, string sourceActorId = null, string evidenceId = null, string originId = null)
        {
            if (string.IsNullOrEmpty(npcId)) throw new ArgumentException("npcId 不能为空");
            if (!Propositions.All.ContainsKey(propositionId)) throw new ArgumentException($"未知命题 {propositionId}");
            if (source == SourceType.ToldBy && string.IsNullOrEmpty(sourceActorId))
                throw new ArgumentException("听说来的认知必须记录告知者");
            if (source == SourceType.SawEvidence && string.IsNullOrEmpty(evidenceId))
                throw new ArgumentException("看到证据的认知必须记录证据 ID");

            var belief = new Belief
            {
                Id = $"B{_nextId++}",
                NpcId = npcId,
                PropositionId = propositionId,
                Stance = stance,
                Confidence = Math.Max(0f, Math.Min(1f, confidence)),
                Source = source,
                SourceActorId = sourceActorId,
                EvidenceId = evidenceId,
                OriginId = originId ?? sourceActorId ?? npcId,
                LearnedAt = at,
            };

            // 亲历/目击/证据 比 听说/推断 更强：强来源会纠正同一命题上的弱认知；冲突的弱来源不覆盖强来源。
            foreach (var old in ActiveFor(npcId, propositionId).ToList())
            {
                if (Strength(belief.Source) >= Strength(old.Source))
                    old.SupersededBy = belief.Id;
            }

            _beliefs.Add(belief);
            return belief;
        }

        public NpcPerceptionView ViewFor(string npcId) => new NpcPerceptionView(npcId, this);

        internal IEnumerable<Belief> ActiveFor(string npcId, string propositionId = null) =>
            _beliefs.Where(b => b.NpcId == npcId && b.IsActive && (propositionId == null || b.PropositionId == propositionId));

        internal IEnumerable<Belief> HistoryFor(string npcId) => _beliefs.Where(b => b.NpcId == npcId);

        public static int Strength(SourceType s)
        {
            switch (s)
            {
                case SourceType.Experienced: return 3;
                case SourceType.SawEvidence: return 3;
                case SourceType.Witnessed: return 2;
                case SourceType.ToldBy: return 1;
                case SourceType.Inferred: return 0;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// 某个 NPC 的只读视角。提示组装只能拿到这个，拿不到 WorldState 和其他人的认知。
    /// </summary>
    public sealed class NpcPerceptionView
    {
        private readonly KnowledgeStore _store;
        public string NpcId { get; }

        internal NpcPerceptionView(string npcId, KnowledgeStore store)
        {
            NpcId = npcId;
            _store = store;
        }

        public IReadOnlyList<Belief> ActiveBeliefs => _store.ActiveFor(NpcId).ToList();
        public IReadOnlyList<Belief> History => _store.HistoryFor(NpcId).ToList();

        /// <summary>当前对某命题的有效认知；不知道时返回 null。</summary>
        public Belief Current(string propositionId) =>
            _store.ActiveFor(NpcId, propositionId).OrderByDescending(b => KnowledgeStore.Strength(b.Source)).ThenByDescending(b => b.LearnedAt).FirstOrDefault();

        public bool Knows(string propositionId) => Current(propositionId) != null;

        /// <summary>模型引用的 Belief ID 是否属于本 NPC 且仍有效（越权引用检查用）。</summary>
        public bool OwnsActive(string beliefId) => _store.ActiveFor(NpcId).Any(b => b.Id == beliefId);
    }
}
