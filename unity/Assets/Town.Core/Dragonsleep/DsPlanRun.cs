using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>计划失败的三类原因，对应不同的补救方式。</summary>
    public enum DsPlanFailure
    {
        None,
        /// <summary>前置条件不满足：规则在掷骰前拒绝，世界未改变、不耗时间。</summary>
        Precondition,
        /// <summary>执行失败：掷骰失败、被巡逻发现或受惊逃走，后果已经写入世界。</summary>
        Dice,
        /// <summary>世界已变：两步之间出现与计划相关的外部变化，剩余步骤作废。</summary>
        Stale,
    }

    public sealed class DsPlanStepResult
    {
        public string StepId, Op, Brief, Clock;
        /// <summary>success / failed / void（因世界已变或前一步失败而作废，未执行）。</summary>
        public string Status = "pending";
        public DsPlanFailure Failure;
        public string Reason = "";
        public DsReason ReasonCode;
        public string Presented = "";
        public readonly List<string> RequestIds = new List<string>();
        public readonly List<DsExecutionReceipt> Receipts = new List<DsExecutionReceipt>();
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["step"] = StepId, ["op"] = Op, ["brief"] = Brief, ["clock"] = Clock, ["status"] = Status,
            ["failure"] = Failure == DsPlanFailure.None ? null : Failure.ToString(), ["reason"] = Reason, ["presented"] = Presented,
            ["request_ids"] = RequestIds.Cast<object>().ToList(),
            ["receipts"] = Receipts.Where(r => r != null).Select(r => (object)new Dictionary<string, object>
            { ["request_id"] = r.RequestId, ["status"] = r.Status, ["applied"] = r.Applied, ["effects"] = r.Effects.Count }).ToList(),
        };
    }

    public sealed class DsReplanRecord
    {
        public DsPlanFailure Failure;
        public DsReason ReasonCode;
        public string Reason, FailedStep, OldPlan, NewPlan, NewRequestId, NewPlanVerdict;
        public bool NewPlanExecutable;
        /// <summary>pending / accepted / modified / abandoned / rejected（新计划本身不可行）。</summary>
        public string Decision = "pending";
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["failure"] = Failure.ToString(), ["reason"] = Reason, ["failed_step"] = FailedStep, ["old_plan"] = OldPlan,
            ["new_plan"] = NewPlan, ["new_request_id"] = NewRequestId, ["new_plan_verdict"] = NewPlanVerdict,
            ["new_plan_executable"] = NewPlanExecutable, ["decision"] = Decision,
        };
    }

    /// <summary>
    /// 一次“提出目标 → 计划 → 玩家确认取舍 → 逐步执行 → 失败重规划”的完整过程。
    /// 每一步都单独经过执行器提交并产生回执；计划永远不会一次提交多步。
    /// </summary>
    public sealed class DsPlanRun
    {
        public DragonsleepSession Session { get; }
        public string Goal { get; }
        /// <summary>由调用方明确指定，不能根据模型自己写的 goal 判断任务是否完成。</summary>
        public bool RequireHomecoming { get; }
        public DsPlanConstraints Constraints { get; }
        public DsPlan Plan { get; private set; }
        public AiTrace LatestPlanTrace { get; private set; }
        public Dictionary<string, string> Selection { get; private set; } = new Dictionary<string, string>();
        /// <summary>planning / awaiting_confirmation / running / needs_replan / completed / failed / abandoned。</summary>
        public string Status { get; private set; } = "planning";
        public DsPlanFailure LastFailure { get; private set; }
        public string LastReason { get; private set; } = "";
        public DsReason LastReasonCode { get; private set; }
        public readonly List<DsPlanStepResult> Results = new List<DsPlanStepResult>();
        public readonly List<DsReplanRecord> Replans = new List<DsReplanRecord>();
        public readonly List<AiTrace> PlanTraces = new List<AiTrace>();
        /// <summary>被玩家确认执行的计划请求；用来核对“含无效引用的计划从未被提交”。</summary>
        public readonly List<string> AcceptedRequestIds = new List<string>();
        public IReadOnlyList<DsPlanStep> ActiveSteps => _active;
        public int NextIndex => _next;
        public bool MeetsObjective(AiTrace t) => t?.FailureCode == null && t?.PlanReport?.Executable == true &&
            (!RequireHomecoming || t.PlanReport.Default.ReachesGoal) &&
            Constraints.Check(t.PlanReport.Plan, t.PlanReport.Plan.DefaultSelection(), Constraints.CooperationAchieved(Session.World)) == null;

        List<DsPlanStep> _active = new List<DsPlanStep>();
        int _next;
        DsWorldSnapshot _expected;
        DsPlanStep _failedStep;

        public DsPlanRun(DragonsleepSession session, string goal, bool requireHomecoming = false, DsPlanConstraints constraints = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Goal = ContextBuilder.Clip(goal);
            RequireHomecoming = requireHomecoming;
            Constraints = constraints ?? DsPlanConstraints.Unrestricted;
        }

        public async Task<AiTrace> ProposeAsync(CancellationToken ct = default)
        {
            var context = RequireHomecoming ? new Dictionary<string, object>
            { ["objective"] = "homecoming", ["completion"] = "小烬与母亲团聚；局部步骤结束仍需继续规划" } : null;
            if (Constraints.HasRestrictions)
            {
                context = context ?? new Dictionary<string, object>();
                context["player_constraints"] = Constraints.ToRecord();
            }
            AddDeadlineRecovery(context);
            var t = await Session.PlanAsync(Goal, context == null ? null : MiniJson.Write(context), ct).ConfigureAwait(false);
            Adopt(t);
            return t;
        }

        void Adopt(AiTrace t)
        {
            LatestPlanTrace = t;
            PlanTraces.Add(t);
            if (MeetsObjective(t))
            {
                Plan = t.PlanReport.Plan;
                Status = "awaiting_confirmation";
            }
            else if (Plan == null && Replans.Count == 0) Status = "planning";
            else Status = "needs_replan";
        }

        /// <summary>
        /// 玩家确认（可改选取舍）。若提出计划后世界已变，先复核剩余步骤，不可行则直接要求重规划。
        /// </summary>
        public void Accept(IDictionary<string, string> selection = null)
        {
            if (Status != "awaiting_confirmation") throw new InvalidOperationException("没有待确认的计划");
            var chosen = Plan.DefaultSelection();
            bool modified = false;
            if (selection != null)
                foreach (var kv in selection)
                {
                    var choice = Plan.Choices.FirstOrDefault(c => c.Id == kv.Key);
                    if (choice == null || choice.Options.All(o => o.Id != kv.Value)) throw new ArgumentException("未知取舍：" + kv.Key + "=" + kv.Value);
                    if (chosen[kv.Key] != kv.Value) modified = true;
                    chosen[kv.Key] = kv.Value;
                }
            // 玩家改选的备选在整体校验里不可行时拒绝确认，保持待确认，由玩家换选或重规划。
            var branch = LatestPlanTrace?.PlanReport?.Branches.FirstOrDefault(b => b.Selection.Count == chosen.Count
                && b.Selection.All(x => chosen.TryGetValue(x.Key, out var v) && v == x.Value));
            if (!Constraints.HasCooperationIntent)
            {
                if (branch != null && !branch.Feasible) throw new InvalidOperationException("该选项不可行：" + branch.FailedStep + " " + branch.Reason);
                if (RequireHomecoming && branch != null && !branch.ReachesGoal) throw new InvalidOperationException("该选项没有覆盖归巢目标");
            }
            string constraintIssue = Constraints.Check(Plan, chosen, Constraints.CooperationAchieved(Session.World));
            if (constraintIssue != null) throw new InvalidOperationException(constraintIssue);
            var current = Session.CaptureState();
            List<DsPlanStep> frozenSteps = null;
            if (Constraints.HasCooperationIntent)
            {
                // v2 确认实际选择的组合，不能借用仅覆盖部分分支的旧报告或可变草稿。
                var raw = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Write(Plan.ToRecord()));
                string shape = DsPlan.TryParse(raw, out var frozen);
                if (shape != null) throw new InvalidOperationException(shape);
                var report = DsPlanValidator.Validate(current, frozen);
                if (report.Code != null) throw new InvalidOperationException("当前草稿结构或引用无效：" + report.Code);
                string issue = Constraints.Check(frozen, chosen, Constraints.CooperationAchieved(Session.World));
                if (issue != null) throw new InvalidOperationException(issue);
                frozenSteps = frozen.ActiveSteps(chosen);
                var actualBranch = DsPlanValidator.Simulate(current, frozenSteps, chosen);
                if (!actualBranch.Feasible || (RequireHomecoming && !actualBranch.ReachesGoal))
                    throw new InvalidOperationException("当前选择未通过重新校验：" + actualBranch.Reason);
            }
            Selection = chosen;
            AcceptedRequestIds.Add(LatestPlanTrace?.RequestId);
            _active = frozenSteps ?? Plan.ActiveSteps(Selection);
            _next = 0;
            _expected = current;
            var pending = Replans.LastOrDefault(r => r.Decision == "pending");
            if (pending != null) pending.Decision = modified ? "modified" : "accepted";
            Status = "running";
            if (!Constraints.HasCooperationIntent && LatestPlanTrace?.Receipt != null && LatestPlanTrace.Receipt.BeforeHash != _expected.Hash)
            {
                var check = DsPlanValidator.Simulate(_expected, _active, Selection);
                if (!check.Feasible || (RequireHomecoming && !check.ReachesGoal))
                    FailBeforeStart("确认前世界已变，剩余计划无法保证完成目标，请根据当前可见状态重新规划");
            }
        }

        public void Abandon()
        {
            var pending = Replans.LastOrDefault(r => r.Decision == "pending");
            if (pending != null) pending.Decision = "abandoned";
            Status = "abandoned";
        }

        /// <summary>执行下一步。返回该步结果；计划已结束返回 null。</summary>
        public async Task<DsPlanStepResult> StepAsync(CancellationToken ct = default)
        {
            if (Status != "running") throw new InvalidOperationException("计划不在执行中：" + Status);
            var w = Session.World;
            if (w.FinalDawn || w.Whelp.WithMother || _next >= _active.Count) { FinishOrContinue(); return null; }
            // 移动事件/居民异步回调可以触达公开草稿。检查与实际执行必须消费同一份独立步骤，含集合。
            var step = FreezeStep(_active[_next]);
            var r = new DsPlanStepResult { StepId = step.Id, Op = step.Op, Brief = step.Brief(), Clock = w.ClockText };
            // 再核对实际下一步，不能依赖曾经检查过的可变提案；必须早于跨夜、移动、掷骰和耗时。
            if (!Constraints.Allows(step))
                return Fail(r, step, DsPlanFailure.Precondition, Constraints.HasCooperationIntent
                    ? "托付或原件交付违反玩家明确选择，步骤未执行" : "托付对象违反玩家明确选择，步骤未执行", executed: false,
                    reasonCode: new DsReason("player_constraint"));

            // 时间已到深夜：居民照常行动，之后再判断世界是否变了。
            if (Session.NightPending)
            {
                int events = Session.RuleEvents.Count;
                var night = await Session.EndPhaseAsync(ct).ConfigureAwait(false);
                r.RequestIds.AddRange(night.Select(x => x.RequestId));
                r.Receipts.AddRange(night.Select(x => x.Receipt));
                if (CapturedByPatrol(events)) return Fail(r, step, DsPlanFailure.Dice, "换岗巡逻发现了小烬");
                if (Session.World.FinalDawn) { FinishOrContinue(); return null; }
            }

            var now = Session.CaptureState();
            if (now.Hash != _expected.Hash)
            {
                var changes = RelevantChanges(_expected, now);
                var remaining = _active.Skip(_next + 1).Select(FreezeStep).ToList();
                remaining.Insert(0, step);
                var check = DsPlanValidator.Simulate(now, remaining, Selection);
                if (changes.Count > 0 || !check.Feasible)
                    return Fail(r, step, DsPlanFailure.Stale, changes.Count > 0 ? string.Join("；", changes) :
                        "剩余步骤在 " + check.FailedStep + " 无法保证前置条件，请根据当前可见状态重新规划", executed: false);
            }

            if (step.Place != "" && step.Place != Session.World.PlayerPlace)
            {
                var ev = Session.EnterPlace(step.Place);
                if (ev == null) return Fail(r, step, DsPlanFailure.Precondition, "会话正忙，未能移动", executed: false);
                r.RequestIds.Add(ev.RequestId); r.Receipts.Add(ev.Receipt); Append(r, ev.Presented);
                if (ev.FailureCode == "stale_state") return Fail(r, step, DsPlanFailure.Stale, "移动时世界已变", executed: false);
                if (ev.WhelpCaptured) return Fail(r, step, DsPlanFailure.Dice, "前往" + Session.World.Places[step.Place] + "时，卫兵发现了小烬");
                if (ev.FailureCode != null || ev.Verdict.Kind == VerdictKind.Rejected)
                    return Fail(r, step, DsPlanFailure.Precondition, ev.Verdict.Reason, executed: false, reasonCode: ev.Verdict.ReasonCode);
                if (ev.Verdict.Kind == VerdictKind.Partial)
                    return Fail(r, step, DsPlanFailure.Dice, ev.Verdict.Reason, reasonCode: ev.Verdict.ReasonCode);
            }

            switch (step.Op)
            {
                case "act":
                {
                    var t = await Session.RunStepAsync(step.ToProposal(Session.World.PlayerPlace), "计划步骤 " + step.Brief(), ct).ConfigureAwait(false);
                    r.RequestIds.Add(t.RequestId); r.Receipts.Add(t.Receipt); Append(r, t.Presented);
                    if (t.FailureCode == "stale_state" || t.FailureCode == "stale_context")
                        return Fail(r, step, DsPlanFailure.Stale, t.Presented, executed: false);
                    if (t.FailureCode != null)
                        return Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason, executed: false, reasonCode: t.Verdict.ReasonCode);
                    if (t.Changes.Any(c => c.Path == "whelp.captured_by" && c.After == "osric" && c.Before != "osric"))
                        return Fail(r, step, DsPlanFailure.Dice, "换岗巡逻发现了小烬");
                    if (t.Verdict.Kind == VerdictKind.Rejected)
                    {
                        bool rolled = t.Verdict.Roll.HasValue && t.Verdict.Difficulty.HasValue && t.Verdict.Roll.Value < t.Verdict.Difficulty.Value;
                        return rolled ? Fail(r, step, DsPlanFailure.Dice, t.Verdict.Reason, reasonCode: t.Verdict.ReasonCode)
                                      : Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason,
                                          executed: t.Receipt?.Applied == true, reasonCode: t.Verdict.ReasonCode);
                    }
                    if (t.Verdict.Kind == VerdictKind.Partial && step.Verb == "confide")
                        return Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason, reasonCode: t.Verdict.ReasonCode);
                    break;
                }
                case "wait":
                {
                    DsMoment.TryParse(step.Until, out var until);
                    int target = until.Index;
                    while (Session.World.TimeIndex < target && !Session.World.FinalDawn)
                    {
                        var before = Session.CaptureState();
                        int events = Session.RuleEvents.Count;
                        var traces = await Session.EndPhaseAsync(ct).ConfigureAwait(false);
                        r.RequestIds.AddRange(traces.Select(x => x.RequestId));
                        r.Receipts.AddRange(traces.Select(x => x.Receipt));
                        if (CapturedByPatrol(events)) return Fail(r, step, DsPlanFailure.Dice, "等待时换岗巡逻发现了小烬");
                        var changes = RelevantChanges(before, Session.CaptureState());
                        if (changes.Count > 0 && !Session.World.Whelp.WithMother)
                            return Fail(r, step, DsPlanFailure.Stale, string.Join("；", changes));
                    }
                    Append(r, "等到了" + Session.World.ClockText + "。");
                    break;
                }
            }

            r.Status = "success";
            Results.Add(r);
            _next++;
            _expected = Session.CaptureState();
            if (_next >= _active.Count || Session.World.FinalDawn || Session.World.Whelp.WithMother) FinishOrContinue();
            return r;
        }

        void FinishOrContinue()
        {
            if (!Constraints.CooperationAchieved(Session.World))
            {
                LastFailure = DsPlanFailure.None;
                LastReason = "指定居民尚未实际答应帮助；归巢或草稿结束不代表合作目标已完成";
                LastReasonCode = new DsReason("cooperation_not_achieved");
                _failedStep = null;
                Status = Session.World.FinalDawn || Session.World.Whelp.WithMother ? "failed" : "needs_replan";
                return;
            }
            if (!RequireHomecoming || Session.World.Whelp.WithMother) { Status = "completed"; return; }
            if (Session.World.FinalDawn)
            { Status = "failed"; LastReason = "归巢期限已过，未能团聚"; LastReasonCode = new DsReason("deadline_passed"); return; }
            LastFailure = DsPlanFailure.None;
            LastReason = "局部步骤已经完成，但归巢目标尚未达成，请从当前状态接续到团聚";
            LastReasonCode = new DsReason("goal_not_reached");
            _failedStep = null;
            Status = "needs_replan";
        }

        /// <summary>显式修正被拒的首个提案；不是 HTTP 重试，也不会自动确认或执行。</summary>
        public Task<AiTrace> RepairProposalAsync(CancellationToken ct = default)
        {
            if (Status != "planning" || LatestPlanTrace == null || LatestPlanTrace.Provider?.Error != null)
                throw new InvalidOperationException("当前没有可修正的语义提案");
            LastReason = "首个提案未通过校验，请根据拒绝反馈重新规划";
            LastReasonCode = new DsReason("proposal_rejected");
            Status = "needs_replan";
            return ReplanAsync(ct);
        }

        // 只送失败步骤与有限的校验反馈，不把试运行中探知的其他地点人物/雏龙状态泄露给模型。
        Dictionary<string, object> RejectedFeedback()
        {
            var t = LatestPlanTrace;
            if (t == null || MeetsObjective(t)) return null;
            string constraintIssue = Constraints.Check(t.PlanReport?.Plan, t.PlanReport?.Plan?.DefaultSelection(),
                Constraints.CooperationAchieved(Session.World));
            if (constraintIssue != null)
                return new Dictionary<string, object> { ["request_id"] = t.RequestId, ["scope"] = "player_constraint",
                    ["code"] = "player_constraint", ["reason"] = constraintIssue };
            if (t.FailureCode == null && t.PlanReport?.Executable == true)
                return new Dictionary<string, object> { ["request_id"] = t.RequestId, ["scope"] = "predicted_plan_outcome", ["code"] = "goal_not_reached",
                    ["proposed_steps"] = t.PlanReport.Plan.ActiveSteps(t.PlanReport.Plan.DefaultSelection()).Select(s => (object)s.Brief()).ToList(),
                    ["reason"] = "这些步骤没有覆盖归巢目标：必须先让小烬跟随（lead或rescue），再在期限前抵达龙骨岗并等到第4天清晨。仅玩家到达或只在summary写送回不算团聚。" };
            var b = t.PlanReport?.Default;
            var step = t.PlanReport?.Plan?.Steps.FirstOrDefault(s => s.Id == b?.FailedStep);
            string reason = b?.Reason ?? t.FailureCode ?? "结构未通过校验";
            if (step != null && step.Target != "" && step.Place != "" && step.Place != Session.World.PlayerPlace &&
                (Session.World.Actors.ContainsKey(step.Target) || step.Target == "whelp") &&
                (b?.ReasonCode.MasksRemoteTarget ?? false))
                reason = "该步骤不能保证目标在场；请依据已知线索或当前可见状态安排，不推断隐藏位置";
            if (reason.Length > 180) reason = reason.Substring(0, 180);
            return new Dictionary<string, object>
            {
                ["request_id"] = t.RequestId, ["scope"] = step == null ? "proposal_shape_or_references" : "predicted_after_prefix",
                ["prefix_steps"] = step == null ? new List<object>() : t.PlanReport.Plan.ActiveSteps(t.PlanReport.Plan.DefaultSelection())
                    .TakeWhile(s => s.Id != step.Id).Select(s => (object)s.Brief()).ToList(),
                ["step"] = step?.Brief(), ["code"] = t.FailureCode ??
                    (b?.ReasonCode.Code == "confide_custody_required" || b?.ReasonCode.Code == "confide_evidence_required"
                        ? b.ReasonCode.Code : "precondition"), ["reason"] = reason,
                ["state_warning"] = "这是上版草稿在前序步骤后的试运行判断，不是当前世界事实；这些草稿步骤均未执行。保留可行前序步骤，仅修正矛盾处。",
            };
        }

        /// <summary>从当前感知重新规划；历史步骤与旧计划仅保留在本地记录，防止过去的成功被误当成当前状态。</summary>
        public async Task<AiTrace> ReplanAsync(CancellationToken ct = default)
        {
            if (Status != "needs_replan") throw new InvalidOperationException("当前不需要重规划：" + Status);
            var context = new Dictionary<string, object>
            {
                ["failure"] = Label(LastFailure), ["reason"] = LastReason,
                ["failed_step"] = _failedStep?.Brief(),
                ["objective"] = RequireHomecoming ? "homecoming" : "player_goal",
                ["rejected_proposal"] = RejectedFeedback(),
            };
            if (Constraints.HasRestrictions) context["player_constraints"] = Constraints.ToRecord();
            AddDeadlineRecovery(context);
            var t = await Session.PlanWithLocalContextAsync(Goal, MiniJson.Write(context), new DsPlanLocalContext(LastReasonCode), ct)
                .ConfigureAwait(false);
            var record = new DsReplanRecord
            {
                Failure = LastFailure, Reason = LastReason, ReasonCode = LastReasonCode, FailedStep = _failedStep?.Id, OldPlan = Plan?.Brief(),
                NewPlan = t.PlanReport?.Plan?.Brief(), NewRequestId = t.RequestId, NewPlanVerdict = t.Presented,
                NewPlanExecutable = MeetsObjective(t),
            };
            if (!record.NewPlanExecutable) record.Decision = "rejected";
            Replans.Add(record);
            Adopt(t);
            return t;
        }

        // 只对显式 v2 目标补充眼前已知的末段约束；不读取远处岗哨、居民认知或预测位置。
        void AddDeadlineRecovery(Dictionary<string, object> context)
        {
            if (context == null || !Constraints.HasCooperationIntent || Session.World.Day != 3 ||
                Session.World.Phase != DayPhase.Dusk) return;
            DsPlayerKnowledge.WhelpStatus(Session.World, out string status);
            if (status != "caged_castle") return;
            context["deadline_recovery"] = new Dictionary<string, object>
            {
                ["scope"] = "current_player_observation",
                ["time_left_in_phase"] = Session.TimeLeftInPhase,
                ["action_cost"] = 2,
                ["movement_cost"] = 0,
                ["warning"] = "现在已是最后的黄昏，不能再等到明早救援；深夜不是合法等待终点。救援成功自动跟随。" +
                    "若救援后仍要遮盖，应先抵达龙骨岗再做最后行动，避免耗尽时段后还留在中转地点。" +
                    "已实际结盟不必重复托付；巡逻、救援与遮盖仍可能失败。时间不足时不能宣称可完成。",
            };
        }

        public static string Label(DsPlanFailure f)
        {
            switch (f)
            {
                case DsPlanFailure.Precondition: return "前置条件不满足";
                case DsPlanFailure.Dice: return "执行失败（掷骰/巡逻）";
                case DsPlanFailure.Stale: return "世界已变";
                default: return "无";
            }
        }

        DsPlanStepResult Fail(DsPlanStepResult r, DsPlanStep step, DsPlanFailure failure, string reason,
            bool executed = true, DsReason reasonCode = default)
        {
            // 世界已变而未执行的那一步记为作废；其余失败都记为失败（前置条件失败时世界未变）。
            r.Status = failure == DsPlanFailure.Stale && !executed ? "void" : "failed";
            r.Failure = failure; r.Reason = reason ?? "";
            r.ReasonCode = reasonCode.Code == null ? new DsReason(failure == DsPlanFailure.Stale ? "world_changed" :
                failure == DsPlanFailure.Dice ? "execution_failed" : "precondition") : reasonCode;
            Results.Add(r);
            foreach (var rest in _active.Skip(_next + 1))
                Results.Add(new DsPlanStepResult
                {
                    StepId = rest.Id, Op = rest.Op, Brief = rest.Brief(), Clock = Session.World.ClockText,
                    Status = "void", Reason = "前面的步骤失败，未执行"
                });
            LastFailure = failure; LastReason = r.Reason; _failedStep = step;
            LastReasonCode = r.ReasonCode;
            Status = "needs_replan";
            return r;
        }

        void FailBeforeStart(string reason)
        {
            var step = _active.FirstOrDefault();
            LastFailure = DsPlanFailure.Stale; LastReason = reason; _failedStep = step;
            LastReasonCode = new DsReason("world_changed");
            foreach (var s in _active)
                Results.Add(new DsPlanStepResult
                {
                    StepId = s.Id, Op = s.Op, Brief = s.Brief(), Clock = Session.World.ClockText, Status = "void",
                    Failure = s == step ? DsPlanFailure.Stale : DsPlanFailure.None, Reason = s == step ? reason : "计划作废"
                });
            Status = "needs_replan";
        }

        bool CapturedByPatrol(int fromEvent) =>
            Session.RuleEvents.Skip(fromEvent).Any(e => e.WhelpCaptured);

        static void Append(DsPlanStepResult r, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            r.Presented = r.Presented.Length == 0 ? text : r.Presented + " " + text;
        }

        static DsPlanStep FreezeStep(DsPlanStep source)
        {
            var step = new DsPlanStep { Id = source.Id, Op = source.Op, Verb = source.Verb, Target = source.Target,
                Item = source.Item, Place = source.Place, Until = source.Until, When = source.When, Why = source.Why };
            step.Laws.AddRange(source.Laws); step.Cites.AddRange(source.Cites);
            return step;
        }

        /// <summary>
        /// 两个状态之间、玩家能察觉且影响计划的变化：雏龙的可见状况、随身物件、与搜城相关的公开消息。
        /// 只用玩家可见的信息描述原因，原因会进入重规划上下文。
        /// </summary>
        public static List<string> RelevantChanges(DsWorldSnapshot before, DsWorldSnapshot after)
        {
            var a = before.Restore(); var b = after.Restore();
            var reasons = new List<string>();
            string sa = DsPlayerKnowledge.WhelpStatus(a, out string ca), sb = DsPlayerKnowledge.WhelpStatus(b, out string cb);
            if (ca != cb)
            {
                bool wasSeen = ca == "visible_here" || ca.StartsWith("following");
                bool nowSeen = cb == "visible_here" || cb.StartsWith("following");
                reasons.Add(wasSeen && !nowSeen && cb != "caged_castle" ? "小烬不见了（只知道：" + sb + "）" : "小烬的情况变了：" + sb);
            }
            var lost = a.Inventory("player").Select(i => i.Id).Except(b.Inventory("player").Select(i => i.Id)).ToList();
            foreach (var id in lost) reasons.Add("你身上的「" + a.Items[id].Name + "」不见了");
            foreach (var news in b.PublicNews.Skip(a.PublicNews.Count).Where(n => n.Contains("卫兵") || n.Contains("搜")))
                reasons.Add("镇上有新动静：" + news);
            return reasons;
        }

        public Dictionary<string, object> ToRecord()
        {
            var record = new Dictionary<string, object>
            {
                ["goal"] = Goal, ["status"] = Status, ["selection"] = Selection.ToDictionary(x => x.Key, x => (object)x.Value),
                ["require_homecoming"] = RequireHomecoming, ["homecoming_achieved"] = Session.World.Whelp.WithMother,
                ["plan"] = Plan?.ToRecord(), ["results"] = Results.Select(x => (object)x.ToRecord()).ToList(),
                ["replans"] = Replans.Select(x => (object)x.ToRecord()).ToList(),
                ["plan_requests"] = PlanTraces.Select(x => (object)x.RequestId).ToList(),
            };
            if (Constraints.HasRestrictions) record["player_constraints"] = Constraints.ToRecord();
            if (Constraints.HasCooperationIntent)
            {
                record["cooperation_achieved"] = Constraints.CooperationAchieved(Session.World);
                record["objective_achieved"] = Constraints.CooperationAchieved(Session.World) &&
                    (!RequireHomecoming || Session.World.Whelp.WithMother) && Status == "completed";
            }
            return record;
        }
    }
}
