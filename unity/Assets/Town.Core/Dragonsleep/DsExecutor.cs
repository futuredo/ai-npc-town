using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>规则只能修改私有候选世界；本类是 Session 内唯一的提案提交入口。</summary>
    public sealed class DsExecutor
    {
        public const string RulesVersion = "dragonsleep-rules-6";
        readonly Dictionary<string, DsExecutionReceipt> _committed = new Dictionary<string, DsExecutionReceipt>();
        public IDsRoll RollSource { get; }
        public DsExecutionMode Mode { get; }

        public DsExecutor(IDsRoll rolls = null, DsExecutionMode mode = DsExecutionMode.Live)
        { RollSource = rolls ?? DsSeededRoll.Instance; Mode = mode; }

        internal void EnterPlace(DsWorld world, string place, AiTrace trace) => DsRules.EnterPlace(world, place, trace, RollSource);
        internal void PhaseStart(DsWorld world, AiTrace trace) => DsRules.PhaseStart(world, trace, RollSource);

        public void Reset() => _committed.Clear();

        public DsExecutionPlan Prepare(DsWorld world, int spent, int phaseBudget, AiTaskKind kind,
            string actorId, string requestId, Dictionary<string, object> proposal)
        {
            if (string.IsNullOrEmpty(requestId)) throw new ArgumentException("requestId");
            var before = DsWorldSnapshot.Capture(world, spent, phaseBudget);
            var trial = new AiTrace { Kind = kind, ActorId = actorId, RequestId = requestId };
            var candidate = before.Restore();
            // 独立副本：规则中剔除非法引用不改写原始模型提案。
            var copy = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Write(proposal));
            string identity = DsWorldSnapshot.Digest(kind + "|" + actorId + "|" + MiniJson.Write(proposal));
            string error = DsProposalValidator.Check(kind, copy);
            int timeCost = 0, nextSpent = spent;
            if (error != null) Fail(trial, "schema_invalid", error);
            else
            {
                try
                {
                    switch (kind)
                    {
                        case AiTaskKind.Dialogue: DsRules.Dialogue(candidate, actorId, copy, trial); break;
                        case AiTaskKind.Adjudicate: DsRules.Adjudicate(candidate, copy, trial, RollSource, Mode); break;
                        case AiTaskKind.AgentPlan: DsRules.AgentPlan(candidate, actorId, copy, trial, RollSource); break;
                        case AiTaskKind.Direct: DsRules.Direct(candidate, copy, trial); break;
                        case AiTaskKind.Craft: DsRules.Craft(candidate, copy, trial); break;
                        case AiTaskKind.Plan: EvaluatePlan(before, copy, trial); break;
                        default: Fail(trial, "task_not_supported", "未知任务类型"); break;
                    }
                    if (trial.FailureCode == null)
                    {
                        timeCost = TimeCost(kind, copy.Str("verb"), candidate, trial);
                        ApplyTime(candidate, ref nextSpent, phaseBudget, timeCost, trial, RollSource);
                        // 一次事务只增长一个版本；包括掷骰、时间、日志、旗标等隐式变化。
                        candidate.Version = world.Version;
                        if (DsWorldSnapshot.Capture(candidate, nextSpent, phaseBudget).Hash != before.Hash) candidate.Version++;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is KeyNotFoundException
                    || e is FormatException || e is OverflowException)
                { Fail(trial, "execution_error", "候选执行失败，未提交任何世界变化（" + e.GetType().Name + "）"); }
            }
            if (trial.FailureCode != null)
            {
                candidate = before.Restore(); nextSpent = spent; timeCost = 0; trial.Changes.Clear();
            }
            var after = DsWorldSnapshot.Capture(candidate, nextSpent, phaseBudget);
            return new DsExecutionPlan(before, after, trial, identity, timeCost);
        }

        /// <summary>
        /// 无模型参与的世界规则事务（玩家移动、换岗巡逻）。同样在候选世界里执行，
        /// 经过完整状态核对与去重后一次提交，并产生实际差异回执。
        /// </summary>
        public DsExecutionPlan PrepareRule(DsWorld world, int spent, int phaseBudget, string requestId, string label, Action<DsWorld, AiTrace> rule)
        {
            if (string.IsNullOrEmpty(requestId)) throw new ArgumentException("requestId");
            var before = DsWorldSnapshot.Capture(world, spent, phaseBudget);
            var trial = new AiTrace { Kind = AiTaskKind.Adjudicate, ActorId = "world", RequestId = requestId };
            var candidate = before.Restore();
            try
            {
                rule(candidate, trial);
                candidate.Version = world.Version;
                if (trial.FailureCode == null && DsWorldSnapshot.Capture(candidate, spent, phaseBudget).Hash != before.Hash) candidate.Version++;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is KeyNotFoundException)
            { Fail(trial, "execution_error", "世界规则执行失败，未提交任何变化（" + e.GetType().Name + "）"); }
            if (trial.FailureCode != null) { candidate = before.Restore(); trial.Changes.Clear(); }
            var after = DsWorldSnapshot.Capture(candidate, spent, phaseBudget);
            return new DsExecutionPlan(before, after, trial, DsWorldSnapshot.Digest("rule|" + label + "|" + before.Hash), 0);
        }

        /// <summary>行动的时间花费：对话 1、行动/造物 2、求助 1；深夜或未掷骰的拒绝不耗时。计划试运行共用。</summary>
        internal static int TimeCost(AiTaskKind kind, string verb, DsWorld candidate, AiTrace trial)
        {
            int cost = kind == AiTaskKind.Dialogue ? 1 : kind == AiTaskKind.Adjudicate || kind == AiTaskKind.Craft ? 2 : 0;
            if (kind == AiTaskKind.Adjudicate && verb == "confide") cost = 1;
            if (candidate.Phase == DayPhase.Night || (trial.Verdict.Kind == VerdictKind.Rejected && !trial.Verdict.Roll.HasValue)) cost = 0;
            return cost;
        }

        internal static void ApplyTime(DsWorld candidate, ref int spent, int phaseBudget, int cost, AiTrace trial, IDsRoll rolls = null)
        {
            if (cost <= 0) return;
            string clockBefore = candidate.ClockText + " / 剩余 " + (phaseBudget - spent);
            spent += cost;
            while (spent >= phaseBudget) { spent -= phaseBudget; candidate.Advance(); AppendPhaseStart(candidate, trial, rolls); }
            trial.Changes.Add(new WorldChange { Path = "world.time", Before = clockBefore,
                After = candidate.ClockText + " / 剩余 " + (phaseBudget - spent), Note = "消耗 " + cost + " 点时间" });
        }

        /// <summary>计划只做整体校验，不改候选世界：结构/引用问题直接拦截，不可行给出原因。</summary>
        static void EvaluatePlan(DsWorldSnapshot before, Dictionary<string, object> proposal, AiTrace trial)
        {
            DsPlan.TryParse(proposal, out var plan);
            var report = DsPlanValidator.Validate(before, plan);
            trial.PlanReport = report;
            if (report.Code != null) { Fail(trial, report.Code, string.Join("；", report.Issues.Take(4))); return; }
            var def = report.Default;
            if (!def.Feasible)
            {
                trial.Verdict.Kind = VerdictKind.Rejected;
                trial.Verdict.Reason = "计划在步骤 " + def.FailedStep + " 不可行：" + def.Reason;
                trial.Verdict.ReasonCode = def.ReasonCode;
                trial.Presented = "这份计划走不通——" + def.FailedStep + "：" + def.Reason;
                return;
            }
            var bad = report.Branches.Skip(1).Where(b => !b.Feasible).ToList();
            trial.Verdict.Kind = bad.Count == 0 ? VerdictKind.Accepted : VerdictKind.Partial;
            trial.Verdict.Reason = def.Reason + (bad.Count == 0 ? "" : "；" + bad.Count + " 个备选不可行");
            foreach (var b in bad)
                trial.Verdict.Notes.Add(string.Join(",", b.Selection.Select(x => x.Key + "=" + x.Value))
                    + " 在 " + b.FailedStep + " 不可行：" + b.Reason);
            trial.Presented = "计划：" + plan.Summary;
        }

        /// <summary>行动消耗时间跨入新时段时，同一事务内执行换岗巡逻。</summary>
        static void AppendPhaseStart(DsWorld candidate, AiTrace trial, IDsRoll rolls)
        {
            var patrol = new AiTrace();
            DsRules.PhaseStart(candidate, patrol, rolls);
            trial.Changes.AddRange(patrol.Changes);
            trial.Verdict.Notes.AddRange(patrol.Verdict.Notes);
            if (!string.IsNullOrEmpty(patrol.Presented)) trial.Verdict.Notes.Add("换岗：" + patrol.Presented);
        }

        public DsExecutionReceipt Commit(DsExecutionPlan plan, DsWorld world, ref int spent, int phaseBudget)
        {
            string key = world.SessionGen + ":" + plan.RequestId;
            if (_committed.TryGetValue(key, out var prior))
                return new DsExecutionReceipt(plan, prior.Identity == plan.Identity ? "duplicate" : "request_conflict", false,
                    DsWorldSnapshot.Capture(world, spent, phaseBudget));
            var current = DsWorldSnapshot.Capture(world, spent, phaseBudget);
            if (current.Hash != plan.Before.Hash)
                return new DsExecutionReceipt(plan, "stale_state", false, current);
            if (plan.FailureCode != null)
                return new DsExecutionReceipt(plan, plan.FailureCode, false, current);
            var next = plan.After.Restore();
            bool changed = plan.Before.Hash != plan.After.Hash;
            if (changed) { DsWorldSnapshot.CopyInto(next, world); spent = plan.After.Spent; }
            var receipt = new DsExecutionReceipt(plan, changed ? "committed" : "no_change", changed, plan.After);
            _committed.Add(key, receipt);
            return receipt;
        }

        static void Fail(AiTrace t, string code, string message)
        {
            t.FailureCode = code; t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = message;
            t.Verdict.ReasonCode = new DsReason(code); t.ValidationIssues.Add(message); t.Presented = message;
        }
    }

    /// <summary>候选状态不暴露可变对象；调用者只能查看评估结果，不能修改待提交世界。</summary>
    public sealed class DsExecutionPlan
    {
        internal DsWorldSnapshot Before { get; }
        internal DsWorldSnapshot After { get; }
        internal AiTrace Evaluation { get; }
        internal string Identity { get; }
        public string RequestId => Evaluation.RequestId;
        public string FailureCode => Evaluation.FailureCode;
        public string BeforeHash => Before.Hash;
        public string AfterHash => After.Hash;
        public int TimeCost { get; }
        internal DsExecutionPlan(DsWorldSnapshot before, DsWorldSnapshot after, AiTrace evaluation, string identity, int timeCost)
        { Before = before; After = after; Evaluation = evaluation; Identity = identity; TimeCost = timeCost; }
    }

    public sealed class DsExecutionReceipt
    {
        public string RequestId { get; }
        public string Status { get; }
        public bool Applied { get; }
        public string BeforeHash { get; }
        public string AfterHash { get; }
        public int VersionBefore { get; }
        public int VersionAfter { get; }
        public int TimeCost { get; }
        public IReadOnlyList<WorldChange> Effects { get; }
        internal string Identity { get; }
        internal DsExecutionReceipt(DsExecutionPlan plan, string status, bool applied, DsWorldSnapshot actual)
        {
            RequestId = plan.RequestId; Status = status; Applied = applied; Identity = plan.Identity;
            BeforeHash = plan.Before.Hash; AfterHash = actual.Hash;
            VersionBefore = plan.Before.Restore().Version; VersionAfter = actual.Restore().Version;
            TimeCost = applied ? plan.TimeCost : 0;
            Effects = applied ? DsWorldSnapshot.Diff(plan.Before, actual) : Array.Empty<WorldChange>();
        }
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["rules_version"] = DsExecutor.RulesVersion, ["request_id"] = RequestId,
            ["status"] = Status, ["applied"] = Applied, ["before_hash"] = BeforeHash, ["after_hash"] = AfterHash,
            ["version_before"] = VersionBefore, ["version_after"] = VersionAfter, ["time_cost"] = TimeCost,
            ["effects"] = Effects.Select(c => (object)new Dictionary<string, object> { ["path"] = c.Path, ["before"] = c.Before, ["after"] = c.After }).ToList()
        };
    }

    public static class DsProposalValidator
    {
        public static string Check(AiTaskKind kind, Dictionary<string, object> p)
        {
            string[] strings, arrays, numbers;
            switch (kind)
            {
                case AiTaskKind.Dialogue:
                    strings = new[] { "say", "action" }; arrays = new[] { "cites", "reveal", "persuaded_by" }; numbers = new[] { "trust_delta" }; break;
                case AiTaskKind.Adjudicate:
                    strings = new[] { "verb", "target", "item", "place", "narration" };
                    arrays = new[] { "laws" }; numbers = new[] { "difficulty" }; break;
                case AiTaskKind.AgentPlan:
                    strings = new[] { "verb", "place", "target", "reason" }; arrays = new[] { "share" }; numbers = Array.Empty<string>(); break;
                case AiTaskKind.Direct: strings = new[] { "event", "reason" }; arrays = Array.Empty<string>(); numbers = new[] { "intensity" }; break;
                case AiTaskKind.Craft: strings = new[] { "name", "look" }; arrays = new[] { "consume", "tags" }; numbers = new[] { "potency" }; break;
                case AiTaskKind.Plan: return DsPlan.TryParse(p, out _);
                default: return "未知任务";
            }
            foreach (string key in strings)
                if (!(p.TryGetValue(key, out var v) && v is string s && s.Length <= 1000)) return "字符串缺失、类型错误或过长：" + key;
            foreach (string key in arrays)
                if (!(p.TryGetValue(key, out var v) && v is List<object> list && list.Count <= 24
                    && list.All(x => x is string s && s.Length <= 128))) return "数组缺失、类型错误或过长：" + key;
            foreach (string key in numbers)
                if (!(p.TryGetValue(key, out var v) && v is double n && !double.IsNaN(n) && !double.IsInfinity(n))) return "数值缺失或非法：" + key;
            var keys = new HashSet<string>(strings.Concat(arrays).Concat(numbers));
            if (p.Keys.Any(x => !keys.Contains(x))) return "包含未声明的字段";
            return null;
        }
    }
}
