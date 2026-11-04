using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Knowledge;

namespace Town.Core.Brain
{
    /// <summary>离线方案 A：PlayerText 为命题 ID；任意自由文本只给兜底，不假装理解。</summary>
    public sealed class RuleDialogueClient : ILLMClient
    {
        public Task<DialogueResult> ReplyAsync(NpcDialogueRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var belief = request.Beliefs.Where(b => b.PropositionId == request.PlayerText)
                .OrderByDescending(b => KnowledgeStore.Strength(
                    (SourceType)System.Enum.Parse(typeof(SourceType), b.Source)))
                .ThenByDescending(b => b.Confidence).FirstOrDefault();
            if (belief == null)
                return Task.FromResult(new DialogueResult { Success = true, Reply = "这件事我还不知道。" });
            string prefix = belief.Source == SourceType.ToldBy.ToString() ? "听" + belief.SourceActor + "说，" :
                belief.Source == SourceType.Inferred.ToString() ? "我猜测，" : "据我知道的情况，";
            return Task.FromResult(new DialogueResult
            {
                Success = true,
                Reply = prefix + (belief.Stance ? "我认为「" : "我不认为「") + belief.Text + "」。",
                CitedBeliefs = new[] { belief.Id }
            });
        }
    }
}
