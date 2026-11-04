using Town.Core.World;

namespace Town.Core.Knowledge
{
    /// <summary>NPC 是怎么知道这件事的。</summary>
    public enum SourceType
    {
        Experienced,   // 亲历
        Witnessed,     // 目击
        ToldBy,        // 听某人说
        Inferred,      // 自己推断
        SawEvidence,   // 看到证据
    }

    /// <summary>某个 NPC 对某条命题的一条认知，带来源。只能由规则写入。</summary>
    public sealed class Belief
    {
        public string Id;
        public string NpcId;
        public string PropositionId;
        /// <summary>true = 相信命题为真；false = 相信命题为假。</summary>
        public bool Stance;
        /// <summary>0..1，由规则给定，不由模型给。</summary>
        public float Confidence;
        public SourceType Source;
        /// <summary>ToldBy 时为告知者 ID。</summary>
        public string SourceActorId;
        /// <summary>SawEvidence 时为证据 ID。</summary>
        public string EvidenceId;
        /// <summary>转述链追溯到的原始来源（用于「两人转述同一事实不算两份证据」）。</summary>
        public string OriginId;
        public GameTime LearnedAt;
        /// <summary>被新认知纠正时指向新 Belief 的 ID；null 表示仍有效。</summary>
        public string SupersededBy;

        public bool IsActive => SupersededBy == null;

        public override string ToString() =>
            $"[{Id}] {NpcId} {(Stance ? "信" : "不信")} {PropositionId} ({Source}{(SourceActorId != null ? ":" + SourceActorId : "")}, {Confidence:0.0}){(IsActive ? "" : " 已纠正")}";
    }
}
