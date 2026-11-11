using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;

namespace Town.PieGame
{
    /// <summary>行动专用 SDK 链路：冻结一次提示 → 真实 Kernel 准备 → Kernel 唯一权威提交。</summary>
    public sealed class KernelActionTransaction : IDsActionTransaction
    {
        public const string TaskKind = "dragonsleep.action";
        readonly IPieGameActionKernel _kernel;
        readonly ActionTaskHandler _handler = new ActionTaskHandler();
        readonly object _recordGate = new object();
        readonly List<ReplayFrame> _privateRecords = new List<ReplayFrame>();
        readonly ConditionalWeakTable<DsActionPreparation, Preparation> _preparations =
            new ConditionalWeakTable<DsActionPreparation, Preparation>();

        /// <summary>仅本地私有诊断/回放；含原始模型文本，禁止加入公开 trace、receipt 或模型提示。</summary>
        public IReadOnlyList<ReplayFrame> PrivateRecordList
        {
            get { lock (_recordGate) return Array.AsReadOnly(_privateRecords.ToArray()); }
        }

        public KernelActionTransaction(IPieGameActionKernel kernel)
        {
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _kernel.Register(TaskKind, _handler);
        }

        public IDsActionRequest Begin(string requestId, PromptPacket packet)
        {
            var frozenPacket = packet.FreezeCopy();
            var payload = ActionPromptCodec.Freeze(frozenPacket);
            var request = _kernel.Begin(requestId, TaskKind, payload);
            return new FrozenAction(this, request, frozenPacket);
        }

        public async Task<IDsActionPreparation> PrepareAsync(IDsActionRequest request, IAiProvider provider,
            CancellationToken ct = default)
        {
            if (!(request is FrozenAction frozen) || !ReferenceEquals(frozen.Owner, this))
                throw new ArgumentException("action_request_owner", nameof(request));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            var adapted = new FrozenActionProvider(frozen, provider);
            var capture = _handler.StartCapture();
            try
            {
                var prepared = await _kernel.PrepareAsync(frozen.Request, adapted, ct).ConfigureAwait(false);
                if (prepared.Receipt.Code == "ready")
                {
                    var record = ReplayFrame.Capture(prepared);
                    lock (_recordGate) _privateRecords.Add(record);
                }
                var result = new DsActionPreparation(frozen, prepared.Receipt.Code,
                    MapFailure(prepared.Receipt.Code, capture.Evaluation, adapted.Result), capture.Evaluation, adapted.Result);
                _preparations.Add(result, prepared);
                return result;
            }
            finally { _handler.EndCapture(); }
        }

        public DsActionCommit Commit(IDsActionPreparation prepared, DsActionWrite write, CancellationToken ct = default)
        {
            if (!(prepared is DsActionPreparation preparation) || !_preparations.TryGetValue(preparation, out var kernelPreparation))
                throw new ArgumentException("action_preparation_owner", nameof(prepared));
            var receipt = _kernel.Commit(kernelPreparation, write, ct);
            return new DsActionCommit(receipt.Code, MapFailure(receipt.Code, preparation.Evaluation, preparation.Provider),
                receipt.Code == "committed", receipt.CancellationAfterCommit);
        }

        /// <summary>不把 stale_state 伪装为 Session 的 stale_context；只适配已有解析/提供方/规则失败名称。</summary>
        public static string MapFailure(string kernelCode, DsActionEvaluation evaluation = null, ProviderResult provider = null)
        {
            switch (kernelCode)
            {
                case "ready": case "committed": case "duplicate": return null;
                case "parse_failed": return "parse_error";
                case "host_rejected": return evaluation?.FailureCode ?? "host_rejected";
                case "provider_error":
                    return provider?.Error ?? (provider != null && string.IsNullOrEmpty(provider.RawText) ? "empty_response" : "provider_error");
                default: return kernelCode;
            }
        }

        sealed class FrozenAction : IDsActionRequest
        {
            internal readonly KernelActionTransaction Owner;
            internal readonly Request Request;
            internal readonly PromptPacket Packet;
            public string RequestId => Request.Id;
            public string Fingerprint => Request.Payload.Fingerprint;
            internal FrozenAction(KernelActionTransaction owner, Request request, PromptPacket packet)
            { Owner = owner; Request = request; Packet = packet; }
        }

        sealed class FrozenActionProvider : IProvider
        {
            readonly FrozenAction _request;
            readonly IAiProvider _provider;
            internal ProviderResult Result;
            internal FrozenActionProvider(FrozenAction request, IAiProvider provider) { _request = request; _provider = provider; }

            public async Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
            {
                bool matches;
                try
                {
                    var current = ActionPromptCodec.Freeze(_request.Packet);
                    matches = payload != null && payload.Binding == ActionPromptCodec.Binding &&
                        payload.Fingerprint == _request.Fingerprint && current.Fingerprint == payload.Fingerprint;
                }
                catch (Exception error) when (error is ArgumentException || error is FormatException || error is InvalidOperationException)
                { matches = false; }
                if (!matches)
                {
                    Result = new ProviderResult
                    { ProviderName = _provider.Name, Simulated = _provider.IsSimulated, Error = "frozen_payload_mismatch" };
                    return null;
                }
                // 必须是 Begin 时的同一 packet 对象；此处不调用 ContextBuilder，也不读任何世界快照。
                Result = CopyProviderResult(await _provider.CompleteAsync(_request.Packet, cancellationToken).ConfigureAwait(false));
                if (Result == null || Result.Error != null || string.IsNullOrEmpty(Result.RawText)) return null;
                var usage = Result.InputTokens.HasValue && Result.OutputTokens.HasValue
                    ? new Usage(Result.InputTokens.Value, Result.OutputTokens.Value, Result.EstimatedUsd) : null;
                return new ProviderReply(Result.RawText, usage);
            }

            static ProviderResult CopyProviderResult(ProviderResult value) => value == null ? null : new ProviderResult
            {
                ProviderName = value.ProviderName, RawText = value.RawText, Error = value.Error,
                ResponseStatus = value.ResponseStatus, IncompleteReason = value.IncompleteReason,
                InputTokens = value.InputTokens, OutputTokens = value.OutputTokens, Simulated = value.Simulated,
                // 仅 Kernel 路径的副本归一化；缺少完整 usage 时不把提供方离线默认 0 当作已知费用。
                EstimatedUsd = value.InputTokens.HasValue && value.OutputTokens.HasValue ? value.EstimatedUsd : null
            };
        }
    }
}
