using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>行动的宿主边界；Begin/Commit 在所有者线程，Prepare 只能使用冻结请求与私有候选。</summary>
    public interface IDsActionTransaction
    {
        IDsActionRequest Begin(string requestId, PromptPacket packet);
        Task<IDsActionPreparation> PrepareAsync(IDsActionRequest request, IAiProvider provider, CancellationToken ct = default);
        DsActionCommit Commit(IDsActionPreparation prepared, DsActionWrite write, CancellationToken ct = default);
    }

    /// <summary>不向 Core 暴露 Kernel、权威世界或可变提示字节的请求句柄。</summary>
    public interface IDsActionRequest
    {
        string RequestId { get; }
        string Fingerprint { get; }
    }

    public interface IDsActionPreparation
    {
        ProviderResult Provider { get; }
        DsExecutionPlan Plan { get; }
        /// <summary>ready 或适配后的失败码；失败时 Plan 可以为空，提供方旁路独立保留。</summary>
        string Code { get; }
    }

    /// <summary>不可变规则评估；内部 Plan 仅供 Session 生成旧版回执，绝不持有 live world。</summary>
    public sealed class DsActionEvaluation
    {
        internal DsExecutionPlan Plan { get; }
        public string FailureCode { get; }
        public VerdictKind Kind { get; }
        public string Reason { get; }
        public DsReason ReasonCode { get; }
        public double? Roll { get; }
        public double? Difficulty { get; }
        public string Presented { get; }
        public int TimeCost { get; }
        public IReadOnlyList<string> Notes { get; }
        public IReadOnlyList<string> ValidationIssues { get; }
        readonly WorldChange[] _changes;
        // WorldChange 是旧版可变类型；每次取出都深复制，不能经此修改评估。
        public IReadOnlyList<WorldChange> Changes => Array.AsReadOnly(_changes.Select(CopyChange).ToArray());

        internal DsActionEvaluation(DsExecutionPlan plan)
        {
            Plan = plan;
            var trace = plan.Evaluation;
            FailureCode = trace.FailureCode;
            Kind = trace.Verdict.Kind;
            Reason = trace.Verdict.Reason;
            ReasonCode = trace.Verdict.ReasonCode;
            Roll = trace.Verdict.Roll;
            Difficulty = trace.Verdict.Difficulty;
            Presented = trace.Presented;
            TimeCost = plan.TimeCost;
            Notes = Array.AsReadOnly(trace.Verdict.Notes.ToArray());
            ValidationIssues = Array.AsReadOnly(trace.ValidationIssues.ToArray());
            _changes = trace.Changes.Select(CopyChange).ToArray();
        }

        static WorldChange CopyChange(WorldChange change) => new WorldChange
        { Path = change.Path, Before = change.Before, After = change.After, Note = change.Note };

        // Worker 使用固定占位 requestId；结果构造时只复制本地评估并绑定真实编号，不触及权威世界。
        internal DsExecutionPlan BindPlan(string requestId)
        {
            var trace = new AiTrace
            {
                Kind = AiTaskKind.Adjudicate, ActorId = "player", RequestId = requestId,
                FailureCode = FailureCode, Presented = Presented,
                Verdict = new Verdict { Kind = Kind, Reason = Reason, ReasonCode = ReasonCode, Roll = Roll, Difficulty = Difficulty }
            };
            trace.Verdict.Notes.AddRange(Notes);
            trace.ValidationIssues.AddRange(ValidationIssues);
            trace.Changes.AddRange(_changes.Select(CopyChange));
            return new DsExecutionPlan(Plan.Before, Plan.After, trace, Plan.Identity, TimeCost);
        }
    }

    /// <summary>失败的 Kernel Preparation 不携带 Reply，故提供方原结果必须通过独立旁路保留。</summary>
    public sealed class DsActionPreparation : IDsActionPreparation
    {
        readonly ProviderResult _provider;
        public IDsActionRequest Request { get; }
        public string KernelCode { get; }
        public string FailureCode { get; }
        public DsActionEvaluation Evaluation { get; }
        public DsExecutionPlan Plan => Evaluation?.Plan;
        public string Code => FailureCode ?? KernelCode;
        public ProviderResult Provider => CopyProvider(_provider);

        public DsActionPreparation(IDsActionRequest request, string kernelCode, string failureCode,
            DsActionEvaluation evaluation, ProviderResult provider)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            KernelCode = kernelCode;
            FailureCode = failureCode;
            Evaluation = evaluation == null ? null : new DsActionEvaluation(evaluation.BindPlan(request.RequestId));
            _provider = CopyProvider(provider);
        }

        static ProviderResult CopyProvider(ProviderResult value) => value == null ? null : new ProviderResult
        {
            ProviderName = value.ProviderName, RawText = value.RawText, Error = value.Error,
            ResponseStatus = value.ResponseStatus, IncompleteReason = value.IncompleteReason,
            InputTokens = value.InputTokens, OutputTokens = value.OutputTokens,
            EstimatedUsd = value.EstimatedUsd, Simulated = value.Simulated
        };
    }

    public sealed class DsActionCommit
    {
        public string KernelCode { get; }
        public string FailureCode { get; }
        public string Code => FailureCode ?? KernelCode;
        public bool Applied { get; }
        public bool CancellationAfterCommit { get; }
        public DsActionCommit(string kernelCode, string failureCode = null, bool applied = false, bool cancellationAfterCommit = false)
        {
            KernelCode = kernelCode;
            FailureCode = failureCode;
            Applied = applied;
            CancellationAfterCommit = cancellationAfterCommit;
        }
    }

    /// <summary>Session 签发的单次写能力，只能在 Kernel 的 port.Write 临界区使用；失败和 duplicate 不可调用。</summary>
    public sealed class DsActionWrite
    {
        readonly DsWorld _candidate;
        readonly int _spent;
        readonly Action<DsWorld, int> _apply;
        readonly string _afterJson;
        bool _active = true;
        public bool Applied { get; private set; }
        internal DsActionWrite(DsExecutionPlan plan, Action<DsWorld, int> apply)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            _candidate = plan.After.Restore();
            _spent = plan.After.Spent;
            _afterJson = plan.After.Json;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        public void Apply(string state)
        {
            if (!_active || Applied) throw new InvalidOperationException("action_write_not_active");
            if (!string.Equals(state, _afterJson, StringComparison.Ordinal))
                throw new InvalidOperationException("action_candidate_mismatch");
            // 即使内部写入失败，也不能重入或重用这张能力；宿主写入必须原子替换或无副作用地抛出。
            _active = false;
            _apply(_candidate, _spent);
            Applied = true;
        }

        internal void Close() => _active = false;
    }
}
