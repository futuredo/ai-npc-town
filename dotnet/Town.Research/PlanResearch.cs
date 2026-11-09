using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Town.Core.Dragonsleep;
using Town.OpenAI;

/// <summary>
/// M2 多步计划研究：A. 冻结集首个计划（模型 vs 离线基线）；B. 整局执行 + 失败重规划（多种子）；
/// C. 求助取舍对照；D. 扰动（世界已变 / 前置条件）。默认离线；只有 --live 才让规划请求走真实模型，
/// 居民与 GM 始终走离线规则，便于把差异归因到规划者。
/// </summary>
internal static class PlanResearch
{
    const int MaxPlanCalls = 10;

    sealed class Fixed : IAiProvider
    {
        readonly string _raw;
        public Fixed(string raw) { _raw = raw; }
        public string Name => "研究扰动（固定事件）";
        public bool IsSimulated => true;
        public Task<ProviderResult> CompleteAsync(PromptPacket p, CancellationToken ct) =>
            Task.FromResult(new ProviderResult { ProviderName = Name, RawText = _raw, Simulated = true, EstimatedUsd = 0m });
    }

    sealed class Episode
    {
        public string Kind, Scenario, Option, Ending, Status, Note = "";
        public int Seed, PlanCalls, Steps;
        public bool OsricKnew, AllyMaren, AllyPip, Perturbed, ReceiptsConsistent = true;
        public List<DsPlanRun> Runs = new List<DsPlanRun>();
        public List<AiTrace> PlanTraces = new List<AiTrace>();
        /// <summary>仅研究分析用：重规划当时按世界真值是否已无路可走（雏龙被商人带走或来不及）。不进入模型输入。</summary>
        public List<(DsReplanRecord Record, bool DeadEnd)> ReplanTruth = new List<(DsReplanRecord, bool)>();
    }

    public static async Task<int> Run(string root, bool live, int seeds, bool holdout, bool transfer = false)
    {
        string dir = Path.Combine(root, ".local", "dragonsleep", "experiments", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + (live ? "-plan-live" : "-plan-offline"));
        Directory.CreateDirectory(dir);
        var cases = transfer ? PlanTransferCases.All() : holdout ? PlanCases.Holdout() : PlanCases.Frozen();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        OpenAiDragonsleepProvider online = null;
        var first = new List<Dictionary<string, object>>();
        var episodes = new List<Episode>();
        var calls = new List<AiTrace>();
        string status = "running";
        void Observe(DragonsleepSession session, string scope)
        {
            var before = new Dictionary<string, DsWorldSnapshot>();
            session.StageChanged += (t, stage) =>
            { if (t.Kind == AiTaskKind.Plan && stage == AiStage.Context) before[t.RequestId] = session.CaptureState(); };
            session.TraceCompleted += t =>
            {
                if (t.Kind != AiTaskKind.Plan) return;
                calls.Add(t);
                var row = new Dictionary<string, object>
                {
                    ["scope"] = scope, ["trace"] = t.ToRecord(),
                    ["before"] = before.TryGetValue(t.RequestId, out var snap) ? MiniJson.Parse(snap.Json) : null,
                    ["instructions"] = t.Prompt?.Instructions,
                    ["prompt"] = t.Prompt?.Sections.Select(x => (object)new Dictionary<string, object>
                        { ["name"] = x.Name, ["text"] = x.Text, ["untrusted"] = x.Untrusted }).ToList(),
                };
                File.AppendAllText(Path.Combine(dir, "plan-calls.jsonl"), MiniJson.Write(row) + "\n");
            };
        }
        async Task AddEpisode(Task<Episode> pending)
        {
            var e = await pending;
            episodes.Add(e);
            File.AppendAllText(Path.Combine(dir, "episodes.jsonl"), MiniJson.Write(EpisodeRecord(e)) + "\n");
            WriteSummary(dir, live, holdout, cases, first, episodes, calls, status);
            if (e.PlanTraces.Any(t => Fatal(t.Provider?.Error))) throw new InvalidOperationException("provider_failure");
        }
        var sourceFiles = new[]
        {
            "dotnet/Town.Research/PlanCases.cs", "dotnet/Town.Research/PlanTransferCases.cs", "dotnet/Town.Research/PlanResearch.cs",
            "unity/Assets/Town.Core/Dragonsleep/ContextBuilder.cs", "unity/Assets/Town.Core/Dragonsleep/DsPlan.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsPlanRun.cs", "unity/Assets/Town.Core/Dragonsleep/DsRules.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsRules.Core.cs", "unity/Assets/Town.Core/Dragonsleep/DsRules.Dialogue.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsRules.Effects.cs", "unity/Assets/Town.Core/Dragonsleep/DsRules.Social.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsRules.Director.cs", "unity/Assets/Town.Core/Dragonsleep/DsRules.Craft.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsRules.Travel.cs", "unity/Assets/Town.Core/Dragonsleep/BaselinePlanner.cs",
            "unity/Assets/Town.OpenAI/OpenAiDragonsleepProvider.cs"
        };
        File.WriteAllText(Path.Combine(dir, "source-manifest.json"), JsonConvert.SerializeObject(
            sourceFiles.ToDictionary(f => f, f => DsWorldSnapshot.Digest(File.ReadAllText(Path.Combine(root, f)))), Formatting.Indented));
        try
        {
            if (live) online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root), new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            IAiProvider Planner() => online == null ? (IAiProvider)new RuleProvider() : new RoutingProvider(online, new RuleProvider(), new[] { AiTaskKind.Plan }) { FallbackOnBudget = false };
            Console.WriteLine($"归巢计划研究；规划={(live ? OpenAiPolicy.Model + "（仅计划请求联网，居民/GM 离线）" : "离线基线")}；{(transfer ? "状态迁移集" : holdout ? "新措辞集" : "冻结集")} {cases.Count} 例；整局种子 {seeds}。");

            // ---------- A. 首个计划 ----------
            foreach (var c in cases)
            {
                var b = new DragonsleepSession(new RuleProvider(), 7); c.Setup(b);
                if (!live) Observe(b, "first:" + c.Id);
                var bt = await b.PlanAsync(c.Input, null, ct);
                var s = new DragonsleepSession(Planner(), 7); c.Setup(s);
                if (live) Observe(s, "first:" + c.Id);
                var t = live ? await s.PlanAsync(c.Input, null, ct) : bt;
                var row = FirstRow(c, t, bt);
                first.Add(row);
                File.WriteAllText(Path.Combine(dir, c.Id + ".plan.json"), MiniJson.Write(t.ToRecord(), true));
                Console.WriteLine($"{c.Id}: 可执行={row["executable"]} 断言={row["check"]} 团聚={row["reaches_goal"]} 基线可执行={row["baseline_executable"]} 无效引用={row["invalid_references"]} {t.TotalMillis:0}ms {t.FailureCode}");
                WriteSummary(dir, live, holdout, cases, first, episodes, calls, status);
                if (Fatal(t.Provider?.Error)) { status = "provider_failed"; Console.WriteLine("服务故障，批次停止；已保留全部完成记录。"); return 1; }
            }

            // ---------- B/C/D. 整局 ----------
            var goal = "把小烬安全送回母亲身边，避开卫兵。";
            for (int seed = 1; seed <= seeds; seed++)
            {
                if (transfer)
                {
                    foreach (var c in cases)
                        await AddEpisode(Play(Planner, "transfer", c.Id, c.Input, seed + 20, null, null, ct, Observe, setup: c.Setup));
                    Console.WriteLine($"迁移种子 {seed + 20} 完成：累计 {episodes.Count} 局");
                    continue;
                }
                foreach (var sc in new[] { "fresh", "informed", "bonded_docks", "caged" })
                    await AddEpisode(Play(Planner, "full", sc, goal, seed, null, null, ct, Observe));
                foreach (var opt in new[] { "maren", "pip", "none" })
                    await AddEpisode(Play(Planner, "social", "pact_escort", goal, seed, opt, null, ct, Observe));
                // 世界已变：从第 2 步起第一次成功后，GM 下令搜城（公开消息），剩余计划须作废并重规划。
                await AddEpisode(Play(Planner, "perturb_stale", "informed", goal, seed, null, async (run, n) =>
                {
                    if (n < 2) return false;   // 第 2 步起第一次成功之后注入一次
                    var t = await run.Session.RunWithProviderAsync(AiTaskKind.Direct, "gm", null,
                        new Fixed("{\"event\":\"guard_sweep\",\"intensity\":0.8,\"reason\":\"研究扰动：搜城\"}"), ct);
                    return t.Changes.Any(x => x.Path == "gm.event" && x.After == "guard_sweep");
                }, ct, Observe));
                // 前置条件：梅伦对你戒心很重，出示契约后信任仍不足，托付时被拒（掷骰前判定）。
                await AddEpisode(Play(Planner, "perturb_precondition", "pact_escort", goal, seed, "maren", null, ct, Observe,
                    s => s.World.Actors["maren"].Trust = 0.05f));
                Console.WriteLine($"种子 {seed} 完成：累计 {episodes.Count} 局");
            }
            status = "completed";
            WriteSummary(dir, live, holdout, cases, first, episodes, calls, status);
            PrintSummary(first, episodes);
            return 0;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is OperationCanceledException)
        { status = "stopped_" + e.GetType().Name; Console.Error.WriteLine("实验已停止（" + e.GetType().Name + "）；已完成样本保存在报告目录，未输出可能含凭据的异常详情。"); return 2; }
        finally { online?.Dispose(); WriteSummary(dir, live, holdout, cases, first, episodes, calls, status); Console.WriteLine("报告：" + dir); }
    }

    static bool Fatal(string error) => error != null && error != "incomplete_response" && error != "refusal";

    static Dictionary<string, object> FirstRow(PlanCase c, AiTrace t, AiTrace bt)
    {
        var r = t.PlanReport; var br = bt.PlanReport;
        bool exec = t.FailureCode == null && r != null && r.Executable;
        return new Dictionary<string, object>
        {
            ["id"] = c.Id, ["group"] = c.Group, ["scenario"] = c.Scenario, ["expect"] = c.Expect,
            ["executable"] = exec, ["check"] = exec && (c.Check == null || c.Check(r)),
            ["reaches_goal"] = exec && r.Default.ReachesGoal, ["code"] = t.FailureCode ?? r?.Code,
            ["invalid_references"] = r?.InvalidReferences.Count ?? 0, ["issues"] = r?.Issues.Cast<object>().ToList(),
            ["steps"] = r?.Plan?.Steps.Count, ["choices"] = r?.Plan?.Choices.Count,
            ["baseline_executable"] = bt.FailureCode == null && br != null && br.Executable,
            ["baseline_check"] = bt.FailureCode == null && br != null && br.Executable && (c.Check == null || c.Check(br)),
            ["ms"] = t.TotalMillis, ["input_tokens"] = t.Provider?.InputTokens, ["output_tokens"] = t.Provider?.OutputTokens,
            ["estimated_usd"] = t.Provider?.EstimatedUsd, ["provider"] = t.Provider?.ProviderName, ["presented"] = t.Presented,
        };
    }

    static async Task<Episode> Play(Func<IAiProvider> planner, string kind, string scenario, string goal, int seed, string option,
        Func<DsPlanRun, int, Task<bool>> perturb, CancellationToken ct, Action<DragonsleepSession, string> observe, Action<DragonsleepSession> extra = null,
        Action<DragonsleepSession> setup = null)
    {
        var s = new DragonsleepSession(planner(), seed * 7919 + 13);
        (setup ?? PlanCases.Scenarios[scenario])(s);
        extra?.Invoke(s);
        observe(s, kind + ":" + scenario + ":" + option + ":seed=" + seed);
        var e = new Episode { Kind = kind, Scenario = scenario, Seed = seed, Option = option };
        string input = option == "maren" ? goal + "我想请梅伦修女同行。" : option == "pip" ? goal + "请皮普帮忙望风。" : option == "none" ? goal + "谁也别告诉。" : goal;
        var run = new DsPlanRun(s, input, requireHomecoming: true);
        e.Runs.Add(run);
        await run.ProposeAsync(ct);
        int executed = 0;
        bool perturbed = false;
        while (e.PlanCalls < MaxPlanCalls && !ct.IsCancellationRequested)
        {
            e.PlanCalls = run.PlanTraces.Count;
            if (run.LatestPlanTrace?.Provider?.Error != null)
            { e.Note += "提供方失败，不自动重试：" + run.LatestPlanTrace.Provider.Error + "；"; run.Abandon(); break; }
            if (run.Status == "awaiting_confirmation")
            {
                var sel = new Dictionary<string, string>();
                if (option != null && run.Plan.Choices.Any(c => c.Id == "helper" && c.Options.Any(o => o.Id == option))) sel["helper"] = option;
                try { run.Accept(sel); }
                catch (InvalidOperationException ex) { e.Note += "选项不可行，改用默认：" + ex.Message + "；"; run.Accept(); }
            }
            else if (run.Status == "running")
            {
                var r = await run.StepAsync(ct);
                if (r == null) continue;
                executed++;
                if (perturb != null && !perturbed && r.Status == "success" && await perturb(run, executed)) { perturbed = true; e.Perturbed = true; }
            }
            else if (run.Status == "needs_replan")
            {
                var w = s.World;
                bool deadEnd = (w.Whelp.CapturedBy != null && w.Whelp.CapturedBy != "osric") ||
                               (w.Whelp.CapturedBy == "osric" && (w.Day >= 4 || (w.Day == 3 && w.Phase == DayPhase.Night)));
                await run.ReplanAsync(ct);
                e.ReplanTruth.Add((run.Replans.Last(), deadEnd));
                if (StopDrafting(run))
                { run.Abandon(); e.Note += "连续三次草稿不可行或重复同一无效草稿，放弃；"; }
            }
            else if (run.Status == "planning")
            {
                // 独立记账的语义修正；首轮指标不改写。最多三份连续无效草稿，相同无效草稿提前停止。
                e.Note += "首个提案被拒，附预测范围与前序步骤修正；";
                await run.RepairProposalAsync(ct);
                if (StopDrafting(run)) { run.Abandon(); e.Note += "首个提案修正重复同一无效草稿；"; }
            }
            else break;
            e.PlanCalls = run.PlanTraces.Count;
        }
        e.Status = run.Status;
        e.Steps = executed;
        e.PlanTraces.AddRange(run.PlanTraces);
        e.AllyMaren = s.World.Flags.ContainsKey("ally:maren");
        e.AllyPip = s.World.Flags.ContainsKey("ally:pip");
        e.OsricKnew = DsGuards.OsricKnowsPlan(s.World);
        e.ReceiptsConsistent = run.Results.All(Consistent);
        // 计划结束（或放弃）后世界照常运转到终局，以得到结局。
        for (int i = 0; i < 16 && !s.World.FinalDawn; i++) await s.EndPhaseAsync(ct);
        e.Ending = s.World.FinalDawn ? DsEndings.Evaluate(s.World).Title : "未到终局";
        return e;
    }

    static bool StopDrafting(DsPlanRun run)
    {
        var rejected = run.PlanTraces.AsEnumerable().Reverse().TakeWhile(t => !run.MeetsObjective(t)).ToList();
        if (rejected.Count >= 3) return true;
        if (rejected.Count < 2 || rejected[0].PlanReport?.Plan == null || rejected[1].PlanReport?.Plan == null) return false;
        string Key(AiTrace t) => string.Join(";", t.PlanReport.Plan.ActiveSteps(t.PlanReport.Plan.DefaultSelection()).Select(s =>
            string.Join("|", s.Op, s.Verb, s.Target, s.Item, s.Place, s.Until, string.Join(",", s.Laws.OrderBy(x => x)))));
        return Key(rejected[0]) == Key(rejected[1]);
    }

    /// <summary>回执一致：成功步骤的所有回执都是已提交/无变化；失败步骤至少有一份执行回执；作废步骤没有行动回执。</summary>
    static bool Consistent(DsPlanStepResult r)
    {
        var ok = new[] { "committed", "no_change" };
        if (r.Status == "success") return r.Receipts.Count > 0 && r.Receipts.All(x => x != null && ok.Contains(x.Status)) || (r.Op == "wait" && r.Receipts.All(x => x == null || ok.Contains(x.Status)));
        if (r.Status == "failed") return r.Receipts.Count > 0 && r.Receipts.All(x => x != null);
        if (r.Status == "void") return r.Receipts.All(x => x == null || ok.Contains(x.Status)) && (r.Failure == DsPlanFailure.Stale || r.Receipts.Count == 0);
        return false;
    }

    static Dictionary<string, object> EpisodeRecord(Episode e) => new Dictionary<string, object>
    {
        ["kind"] = e.Kind, ["scenario"] = e.Scenario, ["seed"] = e.Seed, ["option"] = e.Option, ["status"] = e.Status,
        ["ending"] = e.Ending, ["plan_calls"] = e.PlanCalls, ["steps"] = e.Steps, ["perturbed"] = e.Perturbed,
        ["osric_knew_plan"] = e.OsricKnew, ["ally_maren"] = e.AllyMaren, ["ally_pip"] = e.AllyPip,
        ["receipts_consistent"] = e.ReceiptsConsistent, ["note"] = e.Note,
        ["plan_traces"] = e.PlanTraces.Select(t => (object)t.ToRecord()).ToList(),
        ["accepted_request_ids"] = e.Runs.SelectMany(r => r.AcceptedRequestIds).Cast<object>().ToList(),
        ["replan_truth"] = e.ReplanTruth.Select(x => (object)new Dictionary<string, object> { ["request_id"] = x.Record.NewRequestId, ["dead_end"] = x.DeadEnd, ["failure"] = x.Record.Failure.ToString() }).ToList(),
        ["runs"] = e.Runs.Select(r => (object)r.ToRecord()).ToList(),
    };

    static void WriteSummary(string dir, bool live, bool holdout, List<PlanCase> cases, List<Dictionary<string, object>> first, List<Episode> episodes, List<AiTrace> calls, string status)
    {
        var steps = episodes.SelectMany(e => e.Runs.SelectMany(r => r.Results)).ToList();
        var planTraces = calls.Select(t => t.TotalMillis).OrderBy(x => x).ToList();
        var usd = calls.Select(t => t.Provider?.EstimatedUsd).ToList();
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonConvert.SerializeObject(new
        {
            utc = DateTime.UtcNow, status, live, holdout, model = live ? OpenAiPolicy.Model : "offline-baseline", rulesVersion = DsExecutor.RulesVersion,
            suite = cases.FirstOrDefault()?.Group,
            caseFingerprint = PlanCases.Fingerprint(cases),
            allCalls = new { count = calls.Count, inputTokens = calls.Sum(t => t.Provider?.InputTokens ?? 0), outputTokens = calls.Sum(t => t.Provider?.OutputTokens ?? 0),
                unknownCosts = usd.Count(x => !x.HasValue), overFiveSeconds = calls.Count(t => t.TotalMillis > 5000),
                errors = calls.Where(t => t.FailureCode != null).GroupBy(t => t.FailureCode).ToDictionary(g => g.Key, g => g.Count()),
                incompleteReasons = calls.Where(t => t.Provider?.IncompleteReason != null).GroupBy(t => t.Provider.IncompleteReason).ToDictionary(g => g.Key, g => g.Count()) },
            firstPlan = new
            {
                total = first.Count, executable = first.Count(r => (bool)r["executable"]), check = first.Count(r => (bool)r["check"]),
                reachesGoal = first.Count(r => (bool)r["reaches_goal"]), baselineExecutable = first.Count(r => (bool)r["baseline_executable"]),
                invalidReferencesIntercepted = first.Sum(r => (int)r["invalid_references"]), rows = first,
            },
            episodes = new
            {
                total = episodes.Count,
                byKind = episodes.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => new { count = g.Count(), endings = g.GroupBy(e => e.Ending).ToDictionary(x => x.Key, x => x.Count()) }),
                failures = steps.Where(r => r.Failure != DsPlanFailure.None).GroupBy(r => r.Failure.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                replans = episodes.SelectMany(e => e.ReplanTruth).GroupBy(r => r.Record.Failure.ToString()).ToDictionary(g => g.Key, g => new
                { count = g.Count(), executable = g.Count(x => x.Record.NewPlanExecutable), deadEnd = g.Count(x => x.DeadEnd),
                  recoverable = g.Count(x => !x.DeadEnd), recoverableExecutable = g.Count(x => !x.DeadEnd && x.Record.NewPlanExecutable) }),
                invalidIntercepted = episodes.SelectMany(e => e.PlanTraces).Count(t => t.PlanReport?.InvalidReferences.Count > 0),
                invalidCommitted = episodes.Sum(e => e.PlanTraces.Count(t => t.PlanReport?.InvalidReferences.Count > 0 && e.Runs.Any(r => r.AcceptedRequestIds.Contains(t.RequestId)))),
                receiptsConsistent = episodes.Count(e => e.ReceiptsConsistent),
                stepsSucceeded = steps.Count(r => r.Status == "success"), stepsFailed = steps.Count(r => r.Status == "failed"), stepsVoid = steps.Count(r => r.Status == "void"),
                social = episodes.Where(e => e.Kind == "social").GroupBy(e => e.Option).ToDictionary(g => g.Key, g => new
                { count = g.Count(), osricKnew = g.Count(e => e.OsricKnew), allyMaren = g.Count(e => e.AllyMaren), allyPip = g.Count(e => e.AllyPip),
                  endings = g.GroupBy(e => e.Ending).ToDictionary(x => x.Key, x => x.Count()) }),
            },
            latencyMs = new { count = planTraces.Count, median = Pct(planTraces, 0.5), p95 = Pct(planTraces, 0.95) },
            estimatedUsd = usd.Any(x => !x.HasValue) ? (decimal?)null : usd.Sum(x => x ?? 0m),
            knownEstimatedUsd = usd.Where(x => x.HasValue).Sum(x => x.Value),
            notes = "离线基线只读计划感知；居民/GM 始终离线。扰动由研究脚本经正式管线注入，并在报告中标明。费用为估算，未知不记零。",
        }, Formatting.Indented));
    }

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)], 1);

    static void PrintSummary(List<Dictionary<string, object>> first, List<Episode> episodes)
    {
        var steps = episodes.SelectMany(e => e.Runs.SelectMany(r => r.Results)).ToList();
        Console.WriteLine($"首个计划可执行 {first.Count(r => (bool)r["executable"])}/{first.Count}，断言 {first.Count(r => (bool)r["check"])}，基线可执行 {first.Count(r => (bool)r["baseline_executable"])}。");
        foreach (var g in steps.Where(r => r.Failure != DsPlanFailure.None).GroupBy(r => r.Failure))
            Console.WriteLine($"失败 {DsPlanRun.Label(g.Key)}：{g.Count()} 次");
        foreach (var g in episodes.SelectMany(e => e.ReplanTruth).GroupBy(r => r.Record.Failure))
            Console.WriteLine($"重规划 {DsPlanRun.Label(g.Key)}：{g.Count()} 次，新计划可执行 {g.Count(x => x.Record.NewPlanExecutable)}；其中已无路可走 {g.Count(x => x.DeadEnd)}，可补救情形可执行 {g.Count(x => !x.DeadEnd && x.Record.NewPlanExecutable)}/{g.Count(x => !x.DeadEnd)}");
        foreach (var g in episodes.GroupBy(e => e.Kind + (e.Option == null ? "" : ":" + e.Option)))
            Console.WriteLine($"{g.Key}：{string.Join("，", g.GroupBy(e => e.Ending).Select(x => x.Key + " " + x.Count()))}；总管得知 {g.Count(e => e.OsricKnew)}；回执一致 {g.Count(e => e.ReceiptsConsistent)}/{g.Count()}");
    }
}
