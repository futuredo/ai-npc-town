using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 一局《龙眠镇》。六类机制都经过 <see cref="RunAsync"/> 这一条七阶段管线，
    /// 每次调用产出一条 <see cref="AiTrace"/>，供织言镜实时显示和研究导出。
    /// </summary>
    public sealed class DragonsleepSession
    {
        public DsWorld World { get; private set; }
        public IAiProvider Provider { get; set; }
        public readonly List<AiTrace> Traces = new List<AiTrace>();
        /// <summary>每个时段的时间预算：对话花 1，行动/造物花 2。</summary>
        public int PhaseBudget = 4;
        public int TimeLeftInPhase => PhaseBudget - _spent;

        /// <summary>阶段推进时触发（UI 用它逐段点亮织机）。</summary>
        public event Action<AiTrace, AiStage> StageChanged;
        public event Action<AiTrace> TraceCompleted;
        /// <summary>无模型参与的世界规则事件（移动、换岗巡逻），各自带实际执行回执。</summary>
        public readonly List<DsRuleEvent> RuleEvents = new List<DsRuleEvent>();
        public event Action<DsRuleEvent> RuleEventCompleted;
        private int _ruleSequence;

        private readonly DsExecutor _executor;
        private int _sequence;
        private int _spent;
        private DsExecutionPlan _pendingCraft;
        private AiTrace _pendingCraftTrace;
        public bool HasPendingCraft => _pendingCraft != null;
        public string PendingCraftDescription
        {
            get
            {
                if (_pendingCraft == null) return "";
                var before = _pendingCraft.Before.Restore(); var after = _pendingCraft.After.Restore();
                var item = after.Items.Values.First(i => !before.Items.ContainsKey(i.Id));
                var consumed = before.Items.Values.Where(i => i.Holder != "consumed" && after.Items[i.Id].Holder == "consumed").Select(i => i.Name);
                return "「" + item.Name + "」\n" + item.Look + "\n\n消耗：" + string.Join("、", consumed) +
                    "\n效果：" + string.Join("、", item.Tags.Select(DsContent.ItemTagName)) + "\n时间：" + _pendingCraft.TimeCost + " 点。";
            }
        }
        private readonly Dictionary<string, string> _latestByActor = new Dictionary<string, string>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public DragonsleepSession(IAiProvider provider, int seed = 20261108,
            IDsRoll rolls = null, DsExecutionMode executionMode = DsExecutionMode.Live)
        {
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _executor = new DsExecutor(rolls, executionMode);
            World = DsContent.NewWorld(seed);
        }

        /// <summary>新局：旧局尚未返回的请求会因 SessionGen 变化被丢弃。</summary>
        public void Restart(int? seed = null)
        {
            int gen = World.SessionGen + 1;
            World = DsContent.NewWorld(seed ?? World.Seed);
            World.SessionGen = gen;
            _spent = 0;
            _latestByActor.Clear();
            Traces.Clear();
            RuleEvents.Clear(); _ruleSequence = 0;
            _executor.Reset();
            _pendingCraft = null; _pendingCraftTrace = null;
        }

        /// <summary>
        /// 玩家走进某个地点（Unity 按实际位置调用）。规则事务：跟随的雏龙同行、巡逻检查。
        /// 会话忙于等待模型时返回 null，调用方下一帧再试，不绕过事务直接改世界。
        /// </summary>
        public DsRuleEvent EnterPlace(string place, IDsSceneRuleTransaction sceneTransaction = null)
        {
            if (World.PlayerPlace == place || !_gate.Wait(0)) return null;
            try { return RunRuleLocked("enter:" + place, (w, t) => _executor.EnterPlace(w, place, t), sceneTransaction); }
            finally { _gate.Release(); }
        }

        DsRuleEvent RunRuleLocked(string label, Action<DsWorld, AiTrace> rule, IDsSceneRuleTransaction sceneTransaction = null)
        {
            var w = World;
            string id = $"S{w.SessionGen}-R{++_ruleSequence}";
            var plan = _executor.PrepareRule(w, _spent, PhaseBudget, id, label, rule);
            var ev = new DsRuleEvent { RequestId = id, Label = label, Clock = w.ClockText };
            ev.Receipt = _executor.Commit(plan, w, ref _spent, PhaseBudget, sceneTransaction);
            ev.Verdict = plan.Evaluation.Verdict;
            ev.FailureCode = plan.FailureCode ?? (ev.Receipt.Status == "stale_state" || ev.Receipt.Status == "request_conflict"
                ? ev.Receipt.Status : null);
            ev.Presented = plan.Evaluation.Presented;
            if (ev.Receipt.Applied) ev.Changes.AddRange(plan.Evaluation.Changes);
            if (ev.Receipt.Applied || !string.IsNullOrEmpty(ev.Presented))
            {
                RuleEvents.Add(ev);
                RuleEventCompleted?.Invoke(ev);
            }
            return ev;
        }

        public bool Busy => _gate.CurrentCount == 0;

        /// <summary>行动把时间推进到了深夜：UI 应调用 EndPhaseAsync 让居民行动。</summary>
        public bool NightPending => World.Phase == DayPhase.Night;

        public Task<AiTrace> SpeakAsync(string npcId, string text, CancellationToken ct = default) =>
            RunAsync(AiTaskKind.Dialogue, npcId, text, ct);

        public Task<AiTrace> ActAsync(string text, CancellationToken ct = default) =>
            RunAsync(AiTaskKind.Adjudicate, "player", text, ct);

        public Task<AiTrace> CraftAsync(string text, CancellationToken ct = default) =>
            RunAsync(AiTaskKind.Craft, "player", text, ct);

        /// <summary>结束当前时段。进入深夜时，五名居民依次自主行动（N5），随后 GM 安排征兆（N3）。</summary>
        public DsWorldSnapshot CaptureState() => DsWorldSnapshot.Capture(World, _spent, PhaseBudget);

        /// <summary>仅用于本地录制回放，绝不把该状态传给模型。</summary>
        public void RestoreState(DsWorldSnapshot snapshot)
        {
            if (Busy) throw new InvalidOperationException("busy");
            World = snapshot.Restore(); _spent = snapshot.Spent; PhaseBudget = snapshot.PhaseBudget;
            _sequence = 0; _latestByActor.Clear(); Traces.Clear(); _executor.Reset();
            RuleEvents.Clear(); _ruleSequence = 0;
            _pendingCraft = null; _pendingCraftTrace = null;
        }

        /// <summary>玩家确认图样后提交；不产生第二次模型调用。过期图样仍会被完整状态检查拒绝。</summary>
        public AiTrace ConfirmCraft(bool accept)
        {
            if (!_gate.Wait(0)) return null;
            try
            {
                if (_pendingCraft == null) return null;
                var plan = _pendingCraft; var t = _pendingCraftTrace;
                _pendingCraft = null; _pendingCraftTrace = null;
                t.CraftDecision = accept ? "confirmed" : "declined";
                t.Receipt = accept ? _executor.Commit(plan, World, ref _spent, PhaseBudget)
                    : new DsExecutionReceipt(plan, "declined", false, CaptureState());
                if (accept && t.Receipt.Applied)
                { t.Changes.AddRange(plan.Evaluation.Changes); t.Presented = plan.Evaluation.Presented; }
                else
                {
                    t.Verdict = new Verdict { Kind = VerdictKind.Rejected, Reason = accept ? "图样已过期，世界未改变" : "玩家放弃图样" };
                    t.Presented = accept ? "材料或地点已改变，这张图样已失效。" : "图样已收起，没有消耗材料和时间。";
                    if (accept) t.FailureCode = t.Receipt.Status;
                }
                t.WorldVersionAfter = World.Version;
                t.Stage(AiStage.Commit).Status = t.Receipt.Applied ? StageStatus.Ok : accept ? StageStatus.Fail : StageStatus.Skipped;
                t.Stage(AiStage.Commit).Detail = t.Receipt.Status + "；实际状态差异 " + t.Receipt.Effects.Count;
                TraceCompleted?.Invoke(t);
                return t;
            }
            finally { _gate.Release(); }
        }

        public async Task<List<AiTrace>> EndPhaseAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new List<AiTrace>();
                var w = World;
                bool night = w.Phase == DayPhase.Night;
                bool advanced = false;
                if (!night) { night = w.Advance(); advanced = true; }
                _spent = 0;
                if (advanced) RunRuleLocked("phase_start", _executor.PhaseStart);
                if (!night) return result;
                foreach (var id in w.Actors.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList())
                {
                    result.Add(await RunLockedAsync(AiTaskKind.AgentPlan, id, null, ct).ConfigureAwait(false));
                    if (World != w || ct.IsCancellationRequested) return result;
                }
                result.Add(await RunLockedAsync(AiTaskKind.Direct, "gm", null, ct).ConfigureAwait(false));
                if (World != w || ct.IsCancellationRequested) return result;
                w.Advance();
                RunRuleLocked("phase_start", _executor.PhaseStart);
                if (w.FinalDawn) result.Add(await RunLockedAsync(AiTaskKind.Direct, "gm", null, ct).ConfigureAwait(false));
                return result;
            }
            finally { _gate.Release(); }
        }

        public async Task<AiTrace> RunAsync(AiTaskKind kind, string actorId, string playerText, CancellationToken ct = default, string planContext = null)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await RunLockedAsync(kind, actorId, playerText, ct, null, planContext).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// 多步计划：把玩家目标交给规划者（模型或离线基线），只做整体校验，不改变世界。
        /// planContext 是系统生成的重规划上下文（失败类别、原因、旧计划），冻结进感知快照。
        /// </summary>
        public Task<AiTrace> PlanAsync(string goal, string planContext = null, CancellationToken ct = default) =>
            RunAsync(AiTaskKind.Plan, "player", goal, ct, planContext);

        internal async Task<AiTrace> PlanWithLocalContextAsync(string goal, string context, DsPlanLocalContext local, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await RunLockedAsync(AiTaskKind.Plan, "player", goal, ct, null, context, local).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// 执行已确认计划中的一步：结构化提案直接进入同一条管线（感知、校验、裁决、单次提交、回执），
        /// 不再调用模型。trace 中提供方标为“计划步骤”，回放与普通行动相同。
        /// </summary>
        public Task<AiTrace> RunStepAsync(Dictionary<string, object> proposal, string label, CancellationToken ct = default) =>
            RunWithProviderAsync(AiTaskKind.Adjudicate, "player", label, new FixedProvider("计划步骤（结构化，无模型调用）", MiniJson.Write(proposal)), ct);

        /// <summary>用指定的提供方跑一次管线（计划步骤、研究扰动）。仍经过全部校验与单次提交。</summary>
        public async Task<AiTrace> RunWithProviderAsync(AiTaskKind kind, string actorId, string text, IAiProvider provider, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await RunLockedAsync(kind, actorId, text, ct, provider, null).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        sealed class FixedProvider : IAiProvider
        {
            readonly string _raw;
            public FixedProvider(string name, string raw) { Name = name; _raw = raw; }
            public string Name { get; }
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
                Task.FromResult(new ProviderResult { ProviderName = Name, RawText = _raw, Simulated = true, EstimatedUsd = 0m });
        }

        async Task<AiTrace> RunLockedAsync(AiTaskKind kind, string actorId, string playerText, CancellationToken ct,
            IAiProvider providerOverride = null, string planContext = null, DsPlanLocalContext localPlanContext = null)
        {
            var total = Stopwatch.StartNew();
            var w = World;
            string requestHash = CaptureState().Hash;
            var t = new AiTrace
            {
                Sequence = ++_sequence, Kind = kind, ActorId = actorId, PlayerInput = playerText,
                RequestId = $"S{w.SessionGen}-{_sequence}", Clock = w.ClockText, SessionGen = w.SessionGen, PlanContext = planContext,
                WorldVersionBefore = w.Version,
                ActorName = w.Actors.TryGetValue(actorId ?? "", out var a) ? a.Name : actorId == "gm" ? "龙母之心" : "言契师",
            };
            foreach (AiStage s in Enum.GetValues(typeof(AiStage))) t.Stage(s);
            Traces.Add(t);
            try
            {
                // ① 感知分区
                var sw = Stopwatch.StartNew();
                Begin(t, AiStage.Context);
                string precondition = Precondition(kind, actorId);
                if (precondition != null)
                {
                    End(t, AiStage.Context, StageStatus.Fail, precondition, sw);
                    t.FailureCode = "precondition";
                    t.Verdict.Reason = precondition;
                    t.Presented = precondition;
                    SkipRest(t, AiStage.Prompt);
                    return t;
                }
                t.Perception = kind == AiTaskKind.Plan ? ContextBuilder.PlanPerception(World, TimeLeftInPhase, planContext) : Perceive(kind, actorId);
                End(t, AiStage.Context, StageStatus.Ok, $"{t.Perception.Count} 个字段；仅含{(kind == AiTaskKind.Direct ? "汇总状态" : "该视角可见信息")}", sw);

                // ② 提示组装
                sw.Restart(); Begin(t, AiStage.Prompt);
                t.Prompt = kind == AiTaskKind.Dialogue
                    ? ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(w, actorId), playerText)
                    : ContextBuilder.BuildTask(TaskPerceptionView.Capture(w, kind, actorId, TimeLeftInPhase, planContext), playerText);
                t.Prompt.LocalPlanContext = localPlanContext;
                bool clipped = playerText != null && playerText.Length > ContextBuilder.MaxPlayerChars;
                End(t, AiStage.Prompt, clipped ? StageStatus.Warn : StageStatus.Ok,
                    $"{t.Prompt.Sections.Count} 段，{t.Prompt.TotalChars} 字符{(clipped ? "；玩家输入已截断" : "")}", sw);

                // ③ 模型调用
                sw.Restart(); Begin(t, AiStage.Provider);
                _latestByActor[actorId ?? ""] = t.RequestId;
                int gen = w.SessionGen;
                ProviderResult r;
                var provider = providerOverride ?? Provider;
                try { r = await provider.CompleteAsync(t.Prompt, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { r = new ProviderResult { ProviderName = provider.Name, Error = "cancelled" }; }
                t.Provider = r;
                if (r.Error != null || string.IsNullOrEmpty(r.RawText))
                {
                    End(t, AiStage.Provider, StageStatus.Fail, r.Error ?? "empty_response", sw);
                    t.FailureCode = r.Error ?? "empty_response";
                    t.Presented = "织言机的丝线断了。（" + t.FailureCode + "）";
                    SkipRest(t, AiStage.Parse);
                    return t;
                }
                End(t, AiStage.Provider, StageStatus.Ok,
                    $"{r.ProviderName}；输入 {r.InputTokens?.ToString() ?? "?"} / 输出 {r.OutputTokens?.ToString() ?? "?"} tokens；" +
                    (r.EstimatedUsd.HasValue ? $"估算 ${r.EstimatedUsd.Value:0.000000}" : "费用未知"), sw);

                if (ct.IsCancellationRequested) { Stale(t, "cancelled", "请求已取消，提案未提交"); return t; }
                // 过期保护：新局或任意权威状态变动 → 丢弃。
                if (World != w || w.SessionGen != gen) { Stale(t, "stale_session", "新局已开始，旧回复丢弃"); return t; }
                if (CaptureState().Hash != requestHash) { Stale(t, "stale_context", "等待回复时世界或玩家地点已改变，旧提案丢弃"); return t; }
                if (_latestByActor.TryGetValue(actorId ?? "", out var latest) && latest != t.RequestId) { Stale(t, "superseded", "已有更新的请求"); return t; }

                // ④ 解析
                sw.Restart(); Begin(t, AiStage.Parse);
                if (!MiniJson.TryParseObject(r.RawText, out var proposal, out string parseError))
                {
                    End(t, AiStage.Parse, StageStatus.Fail, parseError, sw);
                    t.FailureCode = "parse_error";
                    t.Presented = "织言机没听懂这团乱线。";
                    SkipRest(t, AiStage.Validate);
                    return t;
                }
                t.Proposal = proposal;
                End(t, AiStage.Parse, StageStatus.Ok, $"{proposal.Count} 个字段", sw);

                // ⑤⑥ 在私有候选世界中校验和裁决，真实世界此时完全不变。
                sw.Restart(); Begin(t, AiStage.Validate);
                var plan = _executor.Prepare(w, _spent, PhaseBudget, kind, actorId, t.RequestId, proposal);
                t.ValidationIssues.AddRange(plan.Evaluation.ValidationIssues);
                t.Verdict = plan.Evaluation.Verdict;
                t.PlanReport = plan.Evaluation.PlanReport;
                t.Presented = plan.Evaluation.Presented;
                t.FailureCode = plan.FailureCode;
                End(t, AiStage.Validate, plan.FailureCode != null ? StageStatus.Fail : t.ValidationIssues.Count > 0 ? StageStatus.Warn : StageStatus.Ok,
                    t.ValidationIssues.Count == 0 ? "结构、引用与规则前置条件通过" : string.Join("；", t.ValidationIssues), sw);
                if (plan.FailureCode == null)
                {
                    sw.Restart(); Begin(t, AiStage.Adjudicate);
                    End(t, AiStage.Adjudicate,
                        t.Verdict.Kind == VerdictKind.Accepted ? StageStatus.Ok : t.Verdict.Kind == VerdictKind.Partial ? StageStatus.Warn : StageStatus.Fail,
                        t.Verdict.Kind + "：" + t.Verdict.Reason + (t.Verdict.Notes.Count > 0 ? "｜" + string.Join("；", t.Verdict.Notes) : ""), sw);
                }
                else t.Stage(AiStage.Adjudicate).Status = StageStatus.Skipped;

                // ⑦ 单次提交：完整状态比对 + 幂等标识；回执来自实际差异，而非模型宣称。
                sw.Restart(); Begin(t, AiStage.Commit);
                if (kind == AiTaskKind.Plan)
                {
                    // 计划只是提案：不提交、不耗时；玩家确认后逐步执行，每步各自产生回执。
                    t.Receipt = new DsExecutionReceipt(plan, "plan_only", false, plan.Before);
                    End(t, AiStage.Commit, StageStatus.Skipped, "计划不改变世界；等待玩家确认取舍后逐步执行", sw);
                    return t;
                }
                if (kind == AiTaskKind.Craft && plan.FailureCode == null && t.Verdict.Kind != VerdictKind.Rejected)
                {
                    _pendingCraft = plan; _pendingCraftTrace = t; t.CraftDecision = "pending";
                    t.Receipt = new DsExecutionReceipt(plan, "awaiting_confirmation", false, plan.Before);
                    t.Presented = "图样已经画好，确认材料与效果后才能织造。";
                    End(t, AiStage.Commit, StageStatus.Warn, "等待玩家确认；材料、时间与世界均未改变", sw);
                    return t;
                }
                t.Receipt = _executor.Commit(plan, World, ref _spent, PhaseBudget);
                if (t.Receipt.Status == "stale_state" || t.Receipt.Status == "request_conflict")
                {
                    t.FailureCode = t.Receipt.Status; t.Presented = "世界状态已改变，提案未执行。";
                    t.Verdict.Kind = VerdictKind.Rejected;
                }
                if (t.Receipt.Applied) t.Changes.AddRange(plan.Evaluation.Changes);
                End(t, AiStage.Commit, t.FailureCode != null ? StageStatus.Fail : t.Receipt.Applied ? StageStatus.Ok : StageStatus.Skipped,
                    t.Receipt.Status + $"；实际 {t.Receipt.Effects.Count} 处状态差异，时间 {t.Receipt.TimeCost} 点，" +
                    $"版本 {t.Receipt.VersionBefore}→{t.Receipt.VersionAfter}", sw);
                return t;
            }
            finally
            {
                t.WorldVersionAfter = w.Version;
                t.TotalMillis = total.Elapsed.TotalMilliseconds;
                TraceCompleted?.Invoke(t);
            }
        }

        string Precondition(AiTaskKind kind, string actorId)
        {
            var w = World;
            if (w.FinalDawn && kind != AiTaskKind.Direct) return "第三个黎明已至，一切交由苍穹之母裁决。";
            if (kind == AiTaskKind.Dialogue)
            {
                if (!w.Actors.TryGetValue(actorId ?? "", out var a)) return "没有这个人。";
                if (a.Place != w.PlayerPlace) return a.Name + "不在这里（在" + w.Places[a.Place] + "）。";
            }
            if (kind == AiTaskKind.AgentPlan && !w.Actors.ContainsKey(actorId ?? "")) return "没有这个居民。";
            return null;
        }

        Dictionary<string, object> Perceive(AiTaskKind kind, string actorId)
        {
            switch (kind)
            {
                case AiTaskKind.Dialogue: return ContextBuilder.DialoguePerception(World, actorId);
                case AiTaskKind.Adjudicate: return ContextBuilder.AdjudicatePerception(World);
                case AiTaskKind.AgentPlan: return ContextBuilder.AgentPerception(World, actorId);
                case AiTaskKind.Direct: return ContextBuilder.DirectorPerception(World);
                default: return ContextBuilder.CraftPerception(World);
            }
        }

        void Begin(AiTrace t, AiStage s) { StageChanged?.Invoke(t, s); }

        void End(AiTrace t, AiStage s, StageStatus status, string detail, Stopwatch sw)
        {
            var r = t.Stage(s);
            r.Status = status; r.Detail = detail; r.Millis = sw.Elapsed.TotalMilliseconds;
            StageChanged?.Invoke(t, s);
        }

        void SkipRest(AiTrace t, AiStage from)
        {
            foreach (var r in t.Stages.Where(x => x.Stage >= from && x.Status == StageStatus.Pending)) r.Status = StageStatus.Skipped;
        }

        void Stale(AiTrace t, string code, string detail)
        {
            t.FailureCode = code;
            t.Presented = detail;
            t.Verdict.Reason = detail;
            t.Stage(AiStage.Parse).Status = StageStatus.Fail;
            t.Stage(AiStage.Parse).Detail = detail;
            SkipRest(t, AiStage.Validate);
        }

        /// <summary>研究导出：每行一条 trace。</summary>
        public IEnumerable<string> JsonlLines() => Traces.Select(x => MiniJson.Write(x.ToRecord()));

        /// <summary>本局统计：有效率、拒绝分布、平均耗时、估算费用。</summary>
        public Dictionary<string, object> Metrics()
        {
            var done = Traces.Where(x => x.Provider != null).ToList();
            decimal? usd = done.Any(x => !x.Provider.EstimatedUsd.HasValue) ? (decimal?)null : done.Sum(x => x.Provider.EstimatedUsd ?? 0m);
            return new Dictionary<string, object>
            {
                ["calls"] = Traces.Count,
                ["online_calls"] = done.Count(x => !x.Provider.Simulated),
                ["simulated_calls"] = done.Count(x => x.Provider.Simulated),
                ["accepted"] = Traces.Count(x => x.FailureCode == null && x.Verdict.Kind == VerdictKind.Accepted),
                ["partial"] = Traces.Count(x => x.FailureCode == null && x.Verdict.Kind == VerdictKind.Partial),
                ["rejected"] = Traces.Count(x => x.FailureCode == null && x.Verdict.Kind == VerdictKind.Rejected),
                ["failed"] = Traces.Count(x => x.FailureCode != null),
                ["validation_issues"] = Traces.Sum(x => x.ValidationIssues.Count),
                ["avg_ms"] = Traces.Count == 0 ? 0 : Math.Round(Traces.Average(x => x.TotalMillis), 2),
                ["estimated_usd"] = usd,
            };
        }
    }

    /// <summary>一次世界规则事务的记录：叙述只来自规则，状态差异来自回执。</summary>
    public sealed class DsRuleEvent
    {
        public string RequestId;
        public string Label;
        public string Clock;
        public string Presented = "";
        public string FailureCode;
        public Verdict Verdict = new Verdict();
        public DsExecutionReceipt Receipt;
        public readonly List<WorldChange> Changes = new List<WorldChange>();
        public bool WhelpCaptured => Changes.Any(c => c.Path == "whelp.captured_by" && !string.IsNullOrEmpty(c.After));
        /// <summary>是否值得告诉玩家：雏龙状态变化或有巡逻判定。普通走动不打扰。</summary>
        public bool Notable => Changes.Any(c => c.Path.StartsWith("whelp.", StringComparison.Ordinal) && c.Path != "whelp.place") ||
                               Verdict.Notes.Count > 0 || Verdict.Kind != VerdictKind.Accepted;
        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["type"] = "world_rule", ["request_id"] = RequestId, ["label"] = Label, ["clock"] = Clock,
            ["presented"] = Presented, ["failure"] = FailureCode, ["verdict"] = Verdict.Kind.ToString(),
            ["notes"] = Verdict.Notes.Cast<object>().ToList(), ["receipt"] = Receipt?.ToRecord()
        };
    }
}
