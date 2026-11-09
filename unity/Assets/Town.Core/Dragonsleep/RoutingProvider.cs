using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 按任务类型分派提供方（例如：玩家交互走真实模型，夜间居民/GM 走离线规则以节省额度）。
    /// 真实模型因<b>预算用尽</b>而拒绝时，可选地降级到离线规则并在 trace 中明确标注；
    /// 网络/服务失败绝不静默降级——失败要被看见、被计入研究数据。
    /// </summary>
    public sealed class RoutingProvider : IAiProvider, System.IDisposable
    {
        private readonly IAiProvider _primary;
        private readonly IAiProvider _offline;
        private readonly HashSet<AiTaskKind> _primaryKinds;
        public bool FallbackOnBudget { get; set; } = true;

        public RoutingProvider(IAiProvider primary, IAiProvider offline, IEnumerable<AiTaskKind> primaryKinds)
        {
            _primary = primary;
            _offline = offline;
            _primaryKinds = new HashSet<AiTaskKind>(primaryKinds);
        }

        public string Name => _primary.Name + "（" + string.Join("/", _primaryKinds) + "）+ 离线规则";
        public bool IsSimulated => false;
        public void Dispose()
        {
            (_primary as System.IDisposable)?.Dispose();
            if (!ReferenceEquals(_primary, _offline)) (_offline as System.IDisposable)?.Dispose();
        }

        public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
        {
            if (!_primaryKinds.Contains(packet.Kind))
            {
                var r = await _offline.CompleteAsync(packet, cancellationToken).ConfigureAwait(false);
                r.ProviderName = _offline.Name + "（按路由）";
                return r;
            }
            var result = await _primary.CompleteAsync(packet, cancellationToken).ConfigureAwait(false);
            if (FallbackOnBudget && result.Error == "budget_exhausted")
            {
                var r = await _offline.CompleteAsync(packet, cancellationToken).ConfigureAwait(false);
                r.ProviderName = _offline.Name + "（" + _primary.Name + " 预算用尽，已降级）";
                return r;
            }
            return result;
        }
    }
}
