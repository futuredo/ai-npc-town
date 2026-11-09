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
        public string Reason, FailedStep, OldPlan, NewPlan, NewRequestId, NewPlanVerdict;
        public bool NewPlanExecutable;
        /// <summary>pending / accepted / modified / abandoned / rejected（新计划本身不可行）。</summary>
        public string Decision = "pending";
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["failure"] = Failure.ToString(), ["reason"] = Reason, ["failed_step"] = FailedStep, ["old_plan"] = OldPlan,
            ["new_plan"] = NewPlan, ["new_request_id"] = NewRequestId, ["new_plan_verdict"] = NewPlanVerdict, ["new_plan_executable"] = NewPlanExecutable, ["decision"] = Decision,
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
        public DsPlan Plan { get; private set; }
        public AiTrace LatestPlanTrace { get; private set; }
        public Dictionary<string, string> Selection { get; private set; } = new Dictionary<string, string>();
        /// <summary>planning / awaiting_confirmation / running / needs_replan / completed / abandoned。</summary>
        public string Status { get; private set; } = "planning";
        public DsPlanFailure LastFailure { get; private set; }
        public string LastReason { get; private set; } = "";
        public readonly List<DsPlanStepResult> Results = new List<DsPlanStepResult>();
        public readonly List<DsReplanRecord> Replans = new List<DsReplanRecord>();
        public readonly List<AiTrace> PlanTraces = new List<AiTrace>();
        /// <summary>被玩家确认执行的计划请求；用来核对“含无效引用的计划从未被提交”。</summary>
        public readonly List<string> AcceptedRequestIds = new List<string>();
        public IReadOnlyList<DsPlanStep> ActiveSteps => _active;
        public int NextIndex => _next;

        List<DsPlanStep> _active = new List<DsPlanStep>();
        int _next;
        DsWorldSnapshot _expected;
        DsPlanStep _failedStep;

        public DsPlanRun(DragonsleepSession session, string goal)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Goal = ContextBuilder.Clip(goal);
        }

        public async Task<AiTrace> ProposeAsync(CancellationToken ct = default)
        {
            var t = await Session.PlanAsync(Goal, null, ct).ConfigureAwait(false);
            Adopt(t);
            return t;
        }

        void Adopt(AiTrace t)
        {
            LatestPlanTrace = t;
            PlanTraces.Add(t);
            if (t.FailureCode == null && t.PlanReport != null && t.PlanReport.Executable)
            {
                Plan = t.PlanReport.Plan;
                Status = "awaiting_confirmation";
            }
            else if (Plan == null) Status = "planning";
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
            var branch = LatestPlanTrace?.PlanReport?.Branches.FirstOrDefault(b => b.Selection.Count == chosen.Count && b.Selection.All(x => chosen.TryGetValue(x.Key, out var v) && v == x.Value));
            if (branch != null && !branch.Feasible) throw new InvalidOperationException("该选项不可行：" + branch.FailedStep + " " + branch.Reason);
            Selection = chosen;
            AcceptedRequestIds.Add(LatestPlanTrace?.RequestId);
            _active = Plan.ActiveSteps(Selection);
            _next = 0;
            _expected = Session.CaptureState();
            var pending = Replans.LastOrDefault(r => r.Decision == "pending");
            if (pending != null) pending.Decision = modified ? "modified" : "accepted";
            Status = "running";
            if (LatestPlanTrace?.Receipt != null && LatestPlanTrace.Receipt.BeforeHash != _expected.Hash)
            {
                var check = DsPlanValidator.Simulate(_expected, _active, Selection);
                if (!check.Feasible) FailBeforeStart("确认前世界已变，计划在 " + check.FailedStep + " 不再可行：" + check.Reason);
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
            if (w.FinalDawn || w.Whelp.WithMother || _next >= _active.Count) { Status = "completed"; return null; }
            var step = _active[_next];
            var r = new DsPlanStepResult { StepId = step.Id, Op = step.Op, Brief = step.Brief(), Clock = w.ClockText };

            // 时间已到深夜：居民照常行动，之后再判断世界是否变了。
            if (Session.NightPending)
            {
                int events = Session.RuleEvents.Count;
                var night = await Session.EndPhaseAsync(ct).ConfigureAwait(false);
                r.RequestIds.AddRange(night.Select(x => x.RequestId));
                r.Receipts.AddRange(night.Select(x => x.Receipt));
                if (CapturedByPatrol(events)) return Fail(r, step, DsPlanFailure.Dice, "换岗巡逻发现了小烬");
                if (Session.World.FinalDawn) { Status = "completed"; return null; }
            }

            var now = Session.CaptureState();
            if (now.Hash != _expected.Hash)
            {
                var changes = RelevantChanges(_expected, now);
                var check = DsPlanValidator.Simulate(now, _active.Skip(_next).ToList(), Selection);
                if (changes.Count > 0 || !check.Feasible)
                    return Fail(r, step, DsPlanFailure.Stale, changes.Count > 0 ? string.Join("；", changes) : "剩余步骤在 " + check.FailedStep + " 不再可行：" + check.Reason, executed: false);
            }

            if (step.Place != "" && step.Place != Session.World.PlayerPlace)
            {
                var ev = Session.EnterPlace(step.Place);
                if (ev == null) return Fail(r, step, DsPlanFailure.Precondition, "会话正忙，未能移动", executed: false);
                r.RequestIds.Add(ev.RequestId); r.Receipts.Add(ev.Receipt); Append(r, ev.Presented);
                if (ev.FailureCode == "stale_state") return Fail(r, step, DsPlanFailure.Stale, "移动时世界已变", executed: false);
                if (ev.WhelpCaptured) return Fail(r, step, DsPlanFailure.Dice, "前往" + Session.World.Places[step.Place] + "时，卫兵发现了小烬");
                if (ev.FailureCode != null || ev.Verdict.Kind == VerdictKind.Rejected) return Fail(r, step, DsPlanFailure.Precondition, ev.Verdict.Reason, executed: false);
                if (ev.Verdict.Kind == VerdictKind.Partial) return Fail(r, step, DsPlanFailure.Dice, ev.Verdict.Reason);
            }

            switch (step.Op)
            {
                case "act":
                {
                    var t = await Session.RunStepAsync(step.ToProposal(Session.World.PlayerPlace), "计划步骤 " + step.Brief(), ct).ConfigureAwait(false);
                    r.RequestIds.Add(t.RequestId); r.Receipts.Add(t.Receipt); Append(r, t.Presented);
                    if (t.FailureCode == "stale_state" || t.FailureCode == "stale_context")
                        return Fail(r, step, DsPlanFailure.Stale, t.Presented, executed: false);
                    if (t.FailureCode != null) return Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason, executed: false);
                    if (t.Changes.Any(c => c.Path == "whelp.captured_by" && c.After == "osric" && c.Before != "osric"))
                        return Fail(r, step, DsPlanFailure.Dice, "换岗巡逻发现了小烬");
                    if (t.Verdict.Kind == VerdictKind.Rejected)
                    {
                        bool rolled = t.Verdict.Roll.HasValue && t.Verdict.Difficulty.HasValue && t.Verdict.Roll.Value < t.Verdict.Difficulty.Value;
                        return rolled ? Fail(r, step, DsPlanFailure.Dice, t.Verdict.Reason)
                                      : Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason, executed: t.Receipt?.Applied == true);
                    }
                    if (t.Verdict.Kind == VerdictKind.Partial && step.Verb == "confide")
                        return Fail(r, step, DsPlanFailure.Precondition, t.Verdict.Reason);
                    break;
                }
                case "wait":
                {
                    DsPlanValidator.TryClock(step.Until, out int day, out var phase);
                    int target = DsPlanValidator.TimeIndex(day, phase);
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
            if (_next >= _active.Count || Session.World.FinalDawn) Status = "completed";
            return r;
        }

        /// <summary>从当前世界重新规划：上下文只含失败类别、玩家看到的原因、已完成步骤与旧计划摘要。</summary>
        public async Task<AiTrace> ReplanAsync(CancellationToken ct = default)
        {
            if (Status != "needs_replan") throw new InvalidOperationException("当前不需要重规划：" + Status);
            var done = Results.Where(x => x.Status == "success").Select(x => (object)x.Brief).ToList();
            string context = MiniJson.Write(new Dictionary<string, object>
            {
                ["failure"] = Label(LastFailure), ["reason"] = LastReason,
                ["failed_step"] = _failedStep?.Brief(), ["done_steps"] = done.Skip(Math.Max(0, done.Count - 8)).ToList(),
                ["old_plan"] = Plan?.Summary ?? "",
            });
            var t = await Session.PlanAsync(Goal, context, ct).ConfigureAwait(false);
            var record = new DsReplanRecord
            {
                Failure = LastFailure, Reason = LastReason, FailedStep = _failedStep?.Id, OldPlan = Plan?.Brief(),
                NewPlan = t.PlanReport?.Plan?.Brief(), NewRequestId = t.RequestId, NewPlanVerdict = t.Presented,
                NewPlanExecutable = t.FailureCode == null && t.PlanReport != null && t.PlanReport.Executable,
            };
            if (!record.NewPlanExecutable) record.Decision = "rejected";
            Replans.Add(record);
            Adopt(t);
            return t;
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

        DsPlanStepResult Fail(DsPlanStepResult r, DsPlanStep step, DsPlanFailure failure, string reason, bool executed = true)
        {
            // 世界已变而未执行的那一步记为作废；其余失败都记为失败（前置条件失败时世界未变）。
            r.Status = failure == DsPlanFailure.Stale && !executed ? "void" : "failed";
            r.Failure = failure; r.Reason = reason ?? "";
            Results.Add(r);
            foreach (var rest in _active.Skip(_next + 1))
                Results.Add(new DsPlanStepResult { StepId = rest.Id, Op = rest.Op, Brief = rest.Brief(), Clock = Session.World.ClockText, Status = "void", Reason = "前面的步骤失败，未执行" });
            LastFailure = failure; LastReason = r.Reason; _failedStep = step;
            Status = "needs_replan";
            return r;
        }

        void FailBeforeStart(string reason)
        {
            var step = _active.FirstOrDefault();
            LastFailure = DsPlanFailure.Stale; LastReason = reason; _failedStep = step;
            foreach (var s in _active)
                Results.Add(new DsPlanStepResult { StepId = s.Id, Op = s.Op, Brief = s.Brief(), Clock = Session.World.ClockText, Status = "void", Failure = s == step ? DsPlanFailure.Stale : DsPlanFailure.None, Reason = s == step ? reason : "计划作废" });
            Status = "needs_replan";
        }

        bool CapturedByPatrol(int fromEvent) =>
            Session.RuleEvents.Skip(fromEvent).Any(e => e.WhelpCaptured);

        static void Append(DsPlanStepResult r, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            r.Presented = r.Presented.Length == 0 ? text : r.Presented + " " + text;
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

        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["goal"] = Goal, ["status"] = Status, ["selection"] = Selection.ToDictionary(x => x.Key, x => (object)x.Value),
            ["plan"] = Plan?.ToRecord(), ["results"] = Results.Select(x => (object)x.ToRecord()).ToList(),
            ["replans"] = Replans.Select(x => (object)x.ToRecord()).ToList(),
            ["plan_requests"] = PlanTraces.Select(x => (object)x.RequestId).ToList(),
        };
    }
}
