using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Knowledge;

namespace Town.Core.Brain
{
    public interface ILLMClient
    {
        Task<DialogueResult> ReplyAsync(NpcDialogueRequest request, CancellationToken cancellationToken);
    }

    // 请求只接收该 NPC 的认知快照，不接收 WorldState，也不把全部命题或真值送给模型。
    public sealed class NpcDialogueRequest
    {
        public string NpcId { get; }
        public string PlayerText { get; }
        public IReadOnlyList<DialogueBelief> Beliefs { get; }

        private NpcDialogueRequest(string npcId, string playerText, DialogueBelief[] beliefs)
        {
            NpcId = npcId;
            PlayerText = playerText;
            Beliefs = Array.AsReadOnly(beliefs);
        }

        /// <param name="includeSecrets">默认不把该角色自己的秘密放进快照；只有角色信任玩家时才由规则层传 true。</param>
        public static NpcDialogueRequest From(NpcPerceptionView view, string playerText, bool includeSecrets = false)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (string.IsNullOrWhiteSpace(playerText) || playerText.Length > 500)
                throw new ArgumentException("玩家输入须为 1—500 个字符。", nameof(playerText));
            var beliefs = view.ActiveBeliefs
                .Where(b => includeSecrets || Propositions.All[b.PropositionId].SecretOwnerId != view.NpcId)
                .Select(b => new DialogueBelief(b)).ToArray();
            if (beliefs.Length > 24) throw new ArgumentException("首轮验证最多使用 24 条认知。");
            return new NpcDialogueRequest(view.NpcId, playerText, beliefs);
        }
    }

    public sealed class DialogueBelief
    {
        public string Id { get; }
        public string PropositionId { get; }
        public string Text { get; }
        public bool Stance { get; }
        public float Confidence { get; }
        public string Source { get; }
        public string SourceActor { get; }
        public string At { get; }

        internal DialogueBelief(Belief b)
        {
            Id = b.Id;
            PropositionId = b.PropositionId;
            Text = Propositions.All[b.PropositionId].Text;
            Stance = b.Stance;
            Confidence = b.Confidence;
            Source = b.Source.ToString();
            SourceActor = b.SourceActorId;
            At = b.LearnedAt.ToString();
        }
    }

    public sealed class TokenUsage
    {
        public int InputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    public sealed class DialogueResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string Reply { get; set; }
        public string[] CitedBeliefs { get; set; } = Array.Empty<string>();
        public TokenUsage Usage { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public const string Fallback = "我现在还不能确认，咱们先核实一下吧。";

        public static DialogueResult Failure(string code, TokenUsage usage = null) =>
            new DialogueResult { ErrorCode = code, Reply = Fallback, Usage = usage };
    }

    public static class DialogueValidator
    {
        // 这是有限事件的防漏词表，不声称能够判断所有自然语言事实或绕过方式。
        public static string Validate(NpcDialogueRequest request, string reply, string[] citations)
        {
            if (string.IsNullOrWhiteSpace(reply) || reply.Length > 500) return "invalid_reply";
            if (citations == null || citations.Distinct().Count() != citations.Length) return "invalid_citations";
            var ownIds = new HashSet<string>(request.Beliefs.Select(b => b.Id));
            if (citations.Any(id => !ownIds.Contains(id))) return "unknown_citation";
            var known = new HashSet<string>(request.Beliefs.Select(b => b.PropositionId));
            foreach (var p in Propositions.All.Values)
                if (!known.Contains(p.Id) && p.LeakKeywords.Any(word => reply.Contains(word)))
                    return "knowledge_leak";
            return null;
        }
    }
}
