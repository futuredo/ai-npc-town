using System;
using System.Threading;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;

namespace Town.PieGame
{
    /// <summary>无 live 引用的规则 worker；每次从 before 恢复独立世界并创建独立执行器。</summary>
    public sealed class ActionTaskHandler : ITaskHandler
    {
        // 同一 handler 可并行服务多个准备；评估只属于产生它的异步调用，不使用“最后一次结果”共享槽。
        readonly AsyncLocal<EvaluationCapture> _capture = new AsyncLocal<EvaluationCapture>();
        internal sealed class EvaluationCapture { internal DsActionEvaluation Evaluation; }

        internal EvaluationCapture StartCapture()
        {
            var capture = new EvaluationCapture();
            _capture.Value = capture;
            return capture;
        }

        internal void EndCapture() => _capture.Value = null;

        /// <summary>纯测试/诊断入口；相同 before/raw 在任意线程产生相同候选字节。</summary>
        public DsExecutionPlan Evaluate(string before, string raw)
        {
            if (!MiniJson.TryParseObject(raw, out var proposal, out _)) throw new FormatException("action_parse_failed");
            var snapshot = DsWorldSnapshot.Parse(before);
            var executor = new DsExecutor(DsSeededRoll.Instance, DsExecutionMode.Live);
            return executor.Prepare(snapshot.Restore(), snapshot.Spent, snapshot.PhaseBudget,
                AiTaskKind.Adjudicate, "player", "kernel-action-evaluation", proposal);
        }

        public Candidate Prepare(string before, string raw)
        {
            var plan = Evaluate(before, raw);
            var capture = _capture.Value;
            if (capture != null) capture.Evaluation = plan.EvaluationSnapshot;
            // 规则拒绝且已掷骰仍可能耗时，不能按 VerdictKind.Rejected 丢掉候选。
            if (plan.FailureCode != null) throw new HostRejection();
            return new Candidate(Protocol.Hash(raw), plan.AfterJson);
        }
    }
}
