using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Town.Core.Dragonsleep;
using Town.OpenAI;

/// <summary>独立的 v2 合作意图整局实验。复用固定夹具，不属于冻结 v1 研究，也不代表从新局开始。</summary>
internal static class PlanIntentResearch
{
    const int MaxPlanCalls = 6;
    const int MaxLoops = 100;
    const string Suite = "ds-plan-intent-v2";
    static readonly string[] Modes = { "solo", "maren", "pip" };

    sealed class Episode
    {
        public readonly string Mode, Directory, Input;
        public readonly int Seed, SessionSeed;
        public readonly DragonsleepSession Session;
        public readonly DsPlanRun Run;
        public readonly DsWorldSnapshot Initial;
        public readonly List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
        public readonly List<object> Decisions = new List<object>();
        public readonly Dictionary<string, DsWorldSnapshot> Before = new Dictionary<string, DsWorldSnapshot>();
        public readonly Dictionary<string, object> ProviderPrompts = new Dictionary<string, object>();
        public readonly Dictionary<string, string> Operations = new Dictionary<string, string>();
        public string Operation = "idle", StopReason = "running", ExceptionType;
        public int Loops;

        public Episode(string directory, string mode, int seed, IAiProvider provider)
        {
            Mode = mode;
            Seed = seed;
            SessionSeed = (seed + 30) * 7919 + 13;
            Directory = Path.Combine(directory, mode + "-seed-" + seed);
            System.IO.Directory.CreateDirectory(Directory);
            Session = new DragonsleepSession(provider, SessionSeed);
            PlanCases.Scenarios["pact_escort"](Session);
            Initial = Session.CaptureState();
            var constraints = mode == "solo" ? DsPlanConstraints.Solo : DsPlanConstraints.WithHelper(mode);
            string intent = mode == "solo" ? "我独自护送，不向任何居民托付，也不把契约原件交给居民。"
                : mode == "maren" ? "请梅伦修女实际答应帮忙护送；不得改请其他人，也不得改为独行。"
                : "请皮普实际答应帮忙望风；不向其他人托付，也不把契约原件交给居民，不得改为独行。";
            Input = "把小烬安全送回母亲身边，避开卫兵。" + intent;
            Run = new DsPlanRun(Session, Input, true, constraints);
        }

        public List<AiTrace> PlanTraces => Session.Traces.Where(t => t.Kind == AiTaskKind.Plan).ToList();
        public bool ProviderFailed => Session.Traces.Any(t => t.Provider?.Error != null);
    }

    public static async Task<int> Run(string root, bool live, int seeds)
    {
        if (seeds < 1 || seeds > 3) throw new ArgumentOutOfRangeException(nameof(seeds), "每种模式只允许 1 至 3 个种子。");
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
        string dir = Path.Combine(root, ".local", "dragonsleep", "experiments", stamp + (live ? "-intent-live" : "-intent-offline"));
        Directory.CreateDirectory(dir);
        var episodes = new List<Episode>();
        string status = "running";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        OpenAiDragonsleepProvider online = null;
        try
        {
            WriteManifest(root, dir, live, seeds);
            if (live)
            {
                if (!string.Equals(OpenAiPolicy.Model, "gpt-6-luna", StringComparison.Ordinal))
                    throw new InvalidOperationException("research_model_mismatch");
                online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),
                    new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            }
            IAiProvider provider = online == null ? (IAiProvider)new RuleProvider()
                : new RoutingProvider(online, new RuleProvider(), new[] { AiTaskKind.Plan }) { FallbackOnBudget = false };
            Console.WriteLine("v2 合作意图研究：固定 pact_escort 夹具，非新局；仅计划可联网，其他机制离线。");
            WriteSummary(dir, live, seeds, status, episodes);
            foreach (string mode in Modes)
            {
                for (int seed = 1; seed <= seeds; seed++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var episode = new Episode(dir, mode, seed, provider);
                    episodes.Add(episode);
                    Observe(episode, live);
                    await Play(episode, cancellation.Token);
                    WriteSummary(dir, live, seeds, status, episodes);
                    var evidence = Outcome(episode);
                    Console.WriteLine(mode + " 种子 " + seed + "：状态=" + episode.Run.Status
                        + "；实际目标达成=" + evidence["objective_achieved"] + "；计划调用=" + episode.PlanTraces.Count);
                    if (episode.ProviderFailed)
                    {
                        status = "provider_failed";
                        Console.Error.WriteLine("提供方返回错误，停止整个批次；不重试、不切换提供方。");
                        return 1;
                    }
                }
            }
            status = "completed";
            return 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException
            || ex is OperationCanceledException || ex is ArgumentException || ex is JsonException)
        {
            status = ex is OperationCanceledException ? "cancelled" : "stopped_" + ex.GetType().Name;
            Console.Error.WriteLine("实验停止；已保留完成及中断样本，不输出可能含凭据的异常详情。");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            online?.Dispose();
            WriteSummary(dir, live, seeds, status, episodes);
            Console.WriteLine("报告：" + dir);
        }
    }

    static async Task Play(Episode e, CancellationToken ct)
    {
        try
        {
            for (int loop = 0; loop < MaxLoops; loop++)
            {
                ct.ThrowIfCancellationRequested();
                e.Loops = loop + 1;
                if (e.ProviderFailed)
                {
                    e.StopReason = "provider_error";
                    e.Run.Abandon();
                    break;
                }
                if (Terminal(e.Run.Status))
                {
                    e.StopReason = "terminal_" + e.Run.Status;
                    break;
                }
                switch (e.Run.Status)
                {
                    case "awaiting_confirmation":
                        Accept(e);
                        break;
                    case "running":
                        e.Operation = "step";
                        await e.Run.StepAsync(ct);
                        break;
                    case "planning":
                    case "needs_replan":
                        if (e.PlanTraces.Count >= MaxPlanCalls)
                        {
                            e.StopReason = "plan_call_limit";
                            e.Run.Abandon();
                            break;
                        }
                        await Plan(e, ct);
                        break;
                    default:
                        throw new InvalidOperationException("unknown_plan_status");
                }
                SaveEpisode(e);
                if (Terminal(e.Run.Status))
                {
                    if (e.StopReason == "running") e.StopReason = "terminal_" + e.Run.Status;
                    break;
                }
            }
            if (!Terminal(e.Run.Status))
            {
                e.StopReason = e.ProviderFailed ? "provider_error" : "loop_limit";
                e.Run.Abandon();
            }
        }
        catch (Exception ex)
        {
            e.ExceptionType = ex.GetType().Name;
            e.StopReason = ex is OperationCanceledException ? "cancelled" : "exception";
            if (!Terminal(e.Run.Status)) e.Run.Abandon();
            throw;
        }
        finally
        {
            // 只观察当前状态。绝不补跑 EndPhase，把放弃或失败后的世界演化记成规划成功。
            SaveEpisode(e);
        }
    }

    static async Task Plan(Episode e, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (e.Run.Status == "needs_replan")
        {
            e.Operation = "replan";
            await e.Run.ReplanAsync(ct);
        }
        else if (e.Run.LatestPlanTrace == null)
        {
            e.Operation = "propose";
            await e.Run.ProposeAsync(ct);
        }
        else
        {
            e.Operation = "semantic_repair";
            await e.Run.RepairProposalAsync(ct);
        }
        if (e.ProviderFailed)
        {
            e.StopReason = "provider_error";
            e.Run.Abandon();
        }
    }

    static void Accept(Episode e)
    {
        e.Operation = "accept_default";
        string beforeHash = e.Session.CaptureState().Hash;
        var decision = new Dictionary<string, object>
        {
            ["request_id"] = e.Run.LatestPlanTrace?.RequestId, ["operation"] = e.Operation,
            ["selection"] = e.Run.Plan.DefaultSelection(), ["before_hash"] = beforeHash,
        };
        try
        {
            // 明确意图已在 constraints 中，不能失败后改选默认或另一个合作对象。
            e.Run.Accept();
            decision["decision"] = "accepted";
        }
        catch (InvalidOperationException ex)
        {
            decision["decision"] = "rejected_then_abandoned";
            decision["reason"] = ex.Message;
            e.StopReason = "accept_rejected";
            e.Run.Abandon();
        }
        decision["after_hash"] = e.Session.CaptureState().Hash;
        decision["world_unchanged"] = beforeHash == e.Session.CaptureState().Hash;
        e.Decisions.Add(decision);
    }

    static bool Terminal(string status) => status == "completed" || status == "failed" || status == "abandoned";

    static void Observe(Episode e, bool live)
    {
        e.Session.StageChanged += (trace, stage) =>
        {
            if (stage == AiStage.Context && !e.Before.ContainsKey(trace.RequestId))
            {
                e.Before.Add(trace.RequestId, e.Session.CaptureState());
                e.Operations.Add(trace.RequestId, e.Operation);
            }
            if (stage == AiStage.Provider && !e.ProviderPrompts.ContainsKey(trace.RequestId))
            {
                // 这是实际交给提供方的完整分段；线上 payload 另从冻结的安全感知重建，与真实提供方一致。
                var prompt = PromptRecord(trace.Prompt);
                if (live && trace.Kind == AiTaskKind.Plan)
                    prompt["wire_payload"] = OpenAiDragonsleepProvider.BuildPayload(trace.Prompt, OpenAiRunProfile.SixMechanismResearch);
                e.ProviderPrompts.Add(trace.RequestId, prompt);
            }
        };
        e.Session.TraceCompleted += trace =>
        {
            var row = TraceRecord(e, trace);
            string file = trace.Kind == AiTaskKind.Plan ? "plan-calls.jsonl" : "action-traces.jsonl";
            if (trace.Kind == AiTaskKind.Plan) e.Calls.Add(row);
            File.AppendAllText(Path.Combine(e.Directory, file), JsonConvert.SerializeObject(row) + "\n");
        };
        e.Session.RuleEventCompleted += rule =>
            File.AppendAllText(Path.Combine(e.Directory, "rule-events.jsonl"), MiniJson.Write(rule.ToRecord()) + "\n");
    }

    static Dictionary<string, object> PromptRecord(PromptPacket prompt) => new Dictionary<string, object>
    {
        ["kind"] = prompt?.Kind.ToString(), ["actor"] = prompt?.ActorId, ["schema_name"] = prompt?.SchemaName,
        ["instructions"] = prompt?.Instructions, ["original_player_input"] = prompt?.OriginalPlayerInput,
        ["sections"] = prompt?.Sections.Select(s => (object)new Dictionary<string, object>
        {
            ["name"] = s.Name, ["text"] = s.Text, ["untrusted"] = s.Untrusted,
        }).ToList(),
        ["scope"] = "仅 Session 生成的角色感知；完整世界仅在本地 before/initial/final，不进入提示词",
    };

    static Dictionary<string, object> TraceRecord(Episode e, AiTrace trace)
    {
        e.Before.TryGetValue(trace.RequestId, out var before);
        e.ProviderPrompts.TryGetValue(trace.RequestId, out var prompt);
        return new Dictionary<string, object>
        {
            ["request_id"] = trace.RequestId, ["scope"] = e.Mode + ":seed=" + e.Seed,
            ["operation"] = e.Operations.TryGetValue(trace.RequestId, out var operation) ? operation : "unknown",
            ["before_local_only"] = before == null ? null : MiniJson.Parse(before.Json), ["before_hash"] = before?.Hash,
            ["provider_prompt"] = prompt, ["raw"] = trace.Provider?.RawText, ["provider_error"] = trace.Provider?.Error,
            ["trace"] = trace.ToRecord(), ["proposal"] = trace.Proposal, ["perception"] = trace.Perception,
            ["draft_audit"] = trace.Kind == AiTaskKind.Plan ? DraftAudit(e, trace, before) : null,
        };
    }

    static Dictionary<string, object> DraftAudit(Episode e, AiTrace trace, DsWorldSnapshot before)
    {
        var plan = trace.PlanReport?.Plan;
        if (plan == null && trace.Proposal != null) DsPlan.TryParse(trace.Proposal, out plan);
        bool hasSteps = trace.Proposal != null && trace.Proposal.TryGetValue("steps", out var value) && value is List<object>;
        var rawSteps = hasSteps ? (List<object>)trace.Proposal["steps"] : new List<object>();
        var steps = rawSteps.OfType<Dictionary<string, object>>().ToList();
        bool helperAlreadyAllied = before != null && e.Run.Constraints.CooperationAchieved(before.Restore());
        var active = plan?.ActiveSteps(plan.DefaultSelection());
        string issue = plan == null ? null : e.Run.Constraints.Check(plan, plan.DefaultSelection(), helperAlreadyAllied);
        return new Dictionary<string, object>
        {
            ["parsed_plan"] = plan != null, ["all_steps_auditable"] = hasSteps && steps.Count == rawSteps.Count,
            ["constraints"] = e.Run.Constraints.ToRecord(), ["constraint_issue"] = issue,
            ["helper_already_allied_before"] = helperAlreadyAllied,
            ["missing_required_helper_in_default"] = plan != null && e.Run.Constraints.RequiredHelper != null && !helperAlreadyAllied
                && !active.Any(s => s.Op == "act" && s.Verb == "confide" && s.Target == e.Run.Constraints.RequiredHelper),
            ["all_forbidden_confide"] = steps.Where(s => ForbiddenConfide(e, s, true)).Cast<object>().ToList(),
            ["all_forbidden_pact_give"] = steps.Where(s => ForbiddenPactGive(e, s, true)).Cast<object>().ToList(),
            ["default_forbidden_confide"] = active?.Where(s => ForbiddenConfide(e, s.ToRecord(), true))
                .Select(s => (object)s.ToRecord()).ToList(),
            ["default_forbidden_pact_give"] = active?.Where(s => ForbiddenPactGive(e, s.ToRecord(), true))
                .Select(s => (object)s.ToRecord()).ToList(),
            ["invalid_references"] = trace.PlanReport?.InvalidReferences.Cast<object>().ToList(),
            ["scope"] = "所有草稿（含拒绝、修正、重规划）；全分支违规与默认分支违规分开，不能当成实际执行",
        };
    }

    static string Text(Dictionary<string, object> proposal, string key) =>
        proposal != null && proposal.TryGetValue(key, out var value) ? value as string : null;

    static bool ForbiddenConfide(Episode e, Dictionary<string, object> proposal, bool draft) =>
        (!draft || Text(proposal, "op") == "act") && Text(proposal, "verb") == "confide"
        && !e.Run.Constraints.Allows(new DsPlanStep { Op = "act", Verb = "confide", Target = Text(proposal, "target") });

    static bool ForbiddenPactGive(Episode e, Dictionary<string, object> proposal, bool draft) =>
        (!draft || Text(proposal, "op") == "act") && Text(proposal, "verb") == "give" && Text(proposal, "item") == "pact_page"
        && !e.Run.Constraints.Allows(new DsPlanStep
        {
            Op = "act", Verb = "give", Item = "pact_page", Target = Text(proposal, "target"),
        });

    static List<AiTrace> ForbiddenActions(Episode e, bool confide) => e.Session.Traces.Where(t => t.Kind == AiTaskKind.Adjudicate
        && (confide ? ForbiddenConfide(e, t.Proposal, false) : ForbiddenPactGive(e, t.Proposal, false))).ToList();

    static Dictionary<string, object> Outcome(Episode e)
    {
        var world = e.Session.World;
        var confide = ForbiddenActions(e, true);
        var pactGive = ForbiddenActions(e, false);
        bool requiredAlly = e.Run.Constraints.CooperationAchieved(world);
        string holder = world.Items.TryGetValue("pact_page", out var pact) ? pact.Holder : null;
        bool achieved = e.Run.Status == "completed" && world.Whelp.WithMother && requiredAlly
            && confide.Count == 0 && pactGive.Count == 0;
        return new Dictionary<string, object>
        {
            ["actual_with_mother"] = world.Whelp.WithMother, ["final_dawn"] = world.FinalDawn,
            ["required_helper"] = e.Run.Constraints.RequiredHelper, ["required_ally"] = requiredAlly,
            ["ally_maren"] = world.Flags.ContainsKey("ally:maren"), ["ally_pip"] = world.Flags.ContainsKey("ally:pip"),
            ["objective_achieved"] = achieved, ["osric_knew_plan"] = DsGuards.OsricKnowsPlan(world),
            ["held_pact"] = holder == "player", ["pact_holder"] = holder,
            ["forbidden_confide"] = confide.Count, ["forbidden_pact_give"] = pactGive.Count,
            ["forbidden_confide_request_ids"] = confide.Select(t => t.RequestId).ToList(),
            ["forbidden_pact_give_request_ids"] = pactGive.Select(t => t.RequestId).ToList(),
            ["forbidden_confide_applied"] = confide.Count(t => t.Receipt?.Applied == true),
            ["forbidden_pact_give_applied"] = pactGive.Count(t => t.Receipt?.Applied == true),
            ["clock"] = world.ClockText,
            ["ending"] = world.FinalDawn ? DsEndings.Evaluate(world).Title : "未到终局（未额外推进时段）",
            ["evidence_scope"] = "仅当前实际世界、状态、Session.Traces 与回执；草稿声称、试运行或事后补跑均不算成功",
        };
    }

    static Dictionary<string, object> Guards(Episode e)
    {
        var accepted = e.PlanTraces.Where(t => e.Run.AcceptedRequestIds.Contains(t.RequestId)).ToList();
        bool acceptedAllowed = accepted.All(t =>
        {
            var row = e.Calls.FirstOrDefault(c => (string)c["request_id"] == t.RequestId);
            if (row == null) return false;
            var audit = (Dictionary<string, object>)row["draft_audit"];
            return (bool)audit["parsed_plan"] && audit["constraint_issue"] == null;
        });
        var receipts = e.Session.Traces.Select(t => t.Receipt).Concat(e.Session.RuleEvents.Select(r => r.Receipt))
            .Where(r => r != null).ToList();
        return new Dictionary<string, object>
        {
            ["accepted_defaults_allowed"] = acceptedAllowed,
            ["accepted_request_ids_found"] = accepted.Count == e.Run.AcceptedRequestIds.Count,
            ["accepted_invalid_reference_count"] = accepted.Sum(t => t.PlanReport?.InvalidReferences.Count ?? 0),
            ["actual_actions_allowed"] = ForbiddenActions(e, true).Count == 0 && ForbiddenActions(e, false).Count == 0,
            ["remaining_active_steps_allowed"] = e.Run.ActiveSteps.All(e.Run.Constraints.Allows),
            ["one_receipt_per_request"] = receipts.GroupBy(r => r.RequestId).All(g => g.Count() == 1),
            ["all_results_receipts_match_traces"] = e.Run.Results.All(r => ResultReceiptsMatch(e, r)),
            ["plan_calls_within_limit"] = e.PlanTraces.Count <= MaxPlanCalls,
            ["loops_within_limit"] = e.Loops <= MaxLoops,
            ["only_plan_online"] = e.Session.Traces.All(t => t.Provider == null || t.Provider.Simulated || t.Kind == AiTaskKind.Plan),
            ["same_constraints_for_entire_run"] = e.Run.Constraints.HasCooperationIntent
                && e.Run.Constraints.RequiredHelper == (e.Mode == "solo" ? null : e.Mode),
        };
    }

    static bool ResultReceiptsMatch(Episode e, DsPlanStepResult result)
    {
        if (result.RequestIds.Count != result.Receipts.Count) return false;
        for (int i = 0; i < result.Receipts.Count; i++)
        {
            var receipt = result.Receipts[i];
            if (receipt == null || receipt.RequestId != result.RequestIds[i]) return false;
            var source = e.Session.Traces.Where(t => t.RequestId == receipt.RequestId).Select(t => t.Receipt)
                .Concat(e.Session.RuleEvents.Where(r => r.RequestId == receipt.RequestId).Select(r => r.Receipt)).ToList();
            if (source.Count != 1 || !ReferenceEquals(source[0], receipt)) return false;
            if (!receipt.Applied && (receipt.Effects.Count != 0 || receipt.TimeCost != 0)) return false;
        }
        return true;
    }

    static Dictionary<string, object> FullResult(Episode e, DsPlanStepResult result)
    {
        var record = result.ToRecord();
        record["reason_code"] = result.ReasonCode.Code;
        record["receipts"] = result.Receipts.Select(r => (object)r?.ToRecord()).ToList();
        record["receipts_match_traces"] = ResultReceiptsMatch(e, result);
        return record;
    }

    static void SaveEpisode(Episode e)
    {
        var final = e.Session.CaptureState();
        var record = new Dictionary<string, object>
        {
            ["suite"] = Suite, ["mode"] = e.Mode, ["seed"] = e.Seed, ["session_seed"] = e.SessionSeed,
            ["fixture"] = "pact_escort", ["is_fresh_game"] = false, ["input"] = e.Input,
            ["status"] = e.Run.Status, ["stop_reason"] = e.StopReason, ["exception_type"] = e.ExceptionType,
            ["plan_calls"] = e.PlanTraces.Count, ["loops"] = e.Loops, ["outcome"] = Outcome(e),
            ["initial_local_only"] = MiniJson.Parse(e.Initial.Json), ["initial_hash"] = e.Initial.Hash,
            ["final_local_only"] = MiniJson.Parse(final.Json), ["final_hash"] = final.Hash,
            ["run"] = e.Run.ToRecord(), ["last_reason"] = e.Run.LastReason, ["last_reason_code"] = e.Run.LastReasonCode.Code,
            ["all_results"] = e.Run.Results.Select(r => (object)FullResult(e, r)).ToList(),
            ["accepted_request_ids"] = e.Run.AcceptedRequestIds, ["confirmation_decisions"] = e.Decisions,
            ["plan_traces"] = e.PlanTraces.Select(t => (object)t.ToRecord()).ToList(), ["plan_call_evidence"] = e.Calls,
            ["action_traces"] = e.Session.Traces.Where(t => t.Kind != AiTaskKind.Plan).Select(t => (object)TraceRecord(e, t)).ToList(),
            ["rule_events"] = e.Session.RuleEvents.Select(r => (object)r.ToRecord()).ToList(),
            ["allow_guards"] = Guards(e), ["drafts"] = DraftSummary(e.Calls), ["usage"] = Usage(e.PlanTraces),
        };
        WriteJson(Path.Combine(e.Directory, "episode.json"), record);
    }

    static Dictionary<string, object> Usage(IEnumerable<AiTrace> traces)
    {
        var calls = traces.ToList();
        var returned = calls.Where(t => t.Provider != null).ToList();
        var online = returned.Where(t => !t.Provider.Simulated).ToList();
        bool Known(AiTrace t) => t.Provider?.EstimatedUsd != null
            && (t.Provider.Simulated || (t.Provider.InputTokens.HasValue && t.Provider.OutputTokens.HasValue));
        int unknown = calls.Count(t => !Known(t));
        return new Dictionary<string, object>
        {
            ["plan_requests"] = calls.Count, ["provider_results"] = returned.Count, ["online_provider_results"] = online.Count,
            ["input_tokens_known"] = returned.Sum(t => (long)(t.Provider.InputTokens ?? 0)),
            ["output_tokens_known"] = returned.Sum(t => (long)(t.Provider.OutputTokens ?? 0)),
            ["unknown_input_usage"] = online.Count(t => !t.Provider.InputTokens.HasValue),
            ["unknown_output_usage"] = online.Count(t => !t.Provider.OutputTokens.HasValue),
            ["unknown_costs"] = unknown, ["no_provider_result"] = calls.Count - returned.Count,
            ["known_estimated_usd"] = returned.Where(Known).Sum(t => t.Provider.EstimatedUsd.Value),
            ["estimated_usd"] = unknown > 0 ? (decimal?)null : returned.Where(Known).Sum(t => t.Provider.EstimatedUsd.Value),
            ["provider_errors"] = returned.Where(t => t.Provider.Error != null).GroupBy(t => t.Provider.Error)
                .ToDictionary(g => g.Key, g => g.Count()),
            ["cost_note"] = "费用仅为 usage 估算；线上没有完整 usage 则未知，不记零；提供方结果数不等于已发送 HTTP 数",
        };
    }

    static Dictionary<string, object> DraftSummary(IEnumerable<Dictionary<string, object>> calls)
    {
        var audits = calls.Select(c => (Dictionary<string, object>)c["draft_audit"]).ToList();
        int Count(string key) => audits.Sum(a => (a[key] as List<object>)?.Count ?? 0);
        return new Dictionary<string, object>
        {
            ["total"] = audits.Count, ["unparsed_plans"] = audits.Count(a => !(bool)a["parsed_plan"]),
            ["unauditable_all_steps"] = audits.Count(a => !(bool)a["all_steps_auditable"]),
            ["constraint_violating_defaults"] = audits.Count(a => a["constraint_issue"] != null),
            ["missing_required_helper_defaults"] = audits.Count(a => (bool)a["missing_required_helper_in_default"]),
            ["all_forbidden_confide"] = Count("all_forbidden_confide"),
            ["all_forbidden_pact_give"] = Count("all_forbidden_pact_give"),
            ["default_forbidden_confide"] = Count("default_forbidden_confide"),
            ["default_forbidden_pact_give"] = Count("default_forbidden_pact_give"),
            ["invalid_references"] = Count("invalid_references"),
        };
    }

    static void WriteSummary(string dir, bool live, int seeds, string status, List<Episode> episodes)
    {
        var modes = new Dictionary<string, object>();
        foreach (string mode in Modes)
        {
            var group = episodes.Where(e => e.Mode == mode).ToList();
            var outcomes = group.Select(Outcome).ToList();
            modes[mode] = new Dictionary<string, object>
            {
                ["planned_episodes"] = seeds, ["recorded_episodes"] = group.Count,
                ["objective_achieved"] = outcomes.Count(o => (bool)o["objective_achieved"]),
                ["actual_with_mother"] = outcomes.Count(o => (bool)o["actual_with_mother"]),
                ["required_ally"] = outcomes.Count(o => (bool)o["required_ally"]),
                ["final_dawn"] = outcomes.Count(o => (bool)o["final_dawn"]),
                ["forbidden_confide"] = outcomes.Sum(o => (int)o["forbidden_confide"]),
                ["forbidden_pact_give"] = outcomes.Sum(o => (int)o["forbidden_pact_give"]),
                ["held_pact"] = outcomes.Count(o => (bool)o["held_pact"]),
                ["statuses"] = group.GroupBy(e => e.Run.Status).ToDictionary(g => g.Key, g => g.Count()),
                ["stop_reasons"] = group.GroupBy(e => e.StopReason).ToDictionary(g => g.Key, g => g.Count()),
                ["allow_guards"] = group.ToDictionary(e => e.Seed.ToString(), e => (object)Guards(e)),
                ["drafts"] = DraftSummary(group.SelectMany(e => e.Calls)),
                ["usage"] = Usage(group.SelectMany(e => e.PlanTraces)),
                ["episodes"] = group.Select(e => new { seed = e.Seed, session_seed = e.SessionSeed,
                    path = Path.GetFileName(e.Directory) + "/episode.json", outcome = Outcome(e) }).ToList(),
            };
        }
        WriteJson(Path.Combine(dir, "summary.json"), new Dictionary<string, object>
        {
            ["suite"] = Suite, ["utc"] = DateTime.UtcNow, ["status"] = status, ["live"] = live,
            ["model"] = live ? OpenAiPolicy.Model : "offline-baseline", ["rules_version"] = DsExecutor.RulesVersion,
            ["fixture"] = "PlanCases.Scenarios[pact_escort]", ["is_fresh_game"] = false,
            ["seed_formula"] = "(seed + 30) * 7919 + 13", ["seeds_per_mode"] = seeds,
            ["max_plan_calls_per_episode"] = MaxPlanCalls, ["max_loops_per_episode"] = MaxLoops,
            ["planned_episodes"] = Modes.Length * seeds, ["recorded_episodes"] = episodes.Count,
            ["by_mode"] = modes, ["usage"] = Usage(episodes.SelectMany(e => e.PlanTraces)),
            ["protocol"] = "v2 显式约束；默认确认一次，拒绝即放弃；语义修正独立计次；提供方任意错误停止批次；不补跑终局",
            ["notes"] = "无预定成功数。objective_achieved 仅报告实际完成且合作条件满足、无实际禁用行动的样本；世界真值只留本地。",
        });
    }

    static void WriteManifest(string root, string dir, bool live, int seeds)
    {
        // 开跑前冻结全部共享 Core/OpenAI 源文件清单与字节哈希；不读密钥文件、环境或预算账本。
        var files = new[] { "unity/Assets/Town.Core", "unity/Assets/Town.OpenAI" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".asmdef", StringComparison.Ordinal))
            .Concat(new[] { "dotnet/Town.Core/Town.Core.csproj", "dotnet/Town.OpenAI/Town.OpenAI.csproj",
                "dotnet/Town.Research/Town.Research.csproj", "dotnet/Town.Research/PlanCases.cs",
                "dotnet/Town.Research/PlanResearch.cs", "dotnet/Town.Research/PlanIntentResearch.cs" }.Select(f => Path.Combine(root, f)))
            .OrderBy(file => file, StringComparer.Ordinal).ToList();
        var sources = files.ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), FileHash);
        var core = typeof(DragonsleepSession).Assembly;
        var openAi = typeof(OpenAiDragonsleepProvider).Assembly;
        WriteJson(Path.Combine(dir, "source-manifest.json"), new Dictionary<string, object>
        {
            ["suite"] = Suite, ["frozen_utc"] = DateTime.UtcNow, ["hash_algorithm"] = "SHA256(file bytes)",
            ["live"] = live, ["seeds_per_mode"] = seeds, ["source_sha256"] = sources,
            ["loaded_town_core_assembly"] = new { name = core.FullName, path = core.Location, sha256 = FileHash(core.Location) },
            ["loaded_town_openai_assembly"] = new { name = openAi.FullName, path = openAi.Location, sha256 = FileHash(openAi.Location) },
            ["fixture"] = "PlanCases.Scenarios[pact_escort]", ["is_fresh_game"] = false,
            ["constraints_version"] = DsPlanConstraints.CooperationVersion, ["rules_version"] = DsExecutor.RulesVersion,
            ["max_plan_calls_per_episode"] = MaxPlanCalls, ["max_loops_per_episode"] = MaxLoops,
            ["seed_formula"] = "(seed + 30) * 7919 + 13", ["modes"] = Modes,
            ["only_online_kind"] = "Plan", ["fallback_on_budget"] = false, ["automatic_http_retries"] = 0,
            ["profile"] = "SixMechanismResearch", ["max_output_tokens"] = OpenAiRunProfile.SixMechanismResearch.MaxOutputTokens,
            ["max_request_bytes"] = OpenAiPolicy.MaxRequestBytes, ["concurrency"] = 1,
        });
    }

    static string FileHash(string file)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").ToLowerInvariant();
    }

    static void WriteJson(string file, object record) =>
        File.WriteAllText(file, JsonConvert.SerializeObject(record, Formatting.Indented));
}
