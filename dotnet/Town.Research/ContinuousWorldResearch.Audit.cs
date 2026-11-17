using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;

internal static partial class ContinuousWorldResearch
{
    // 真实费用只有非模拟、input/output usage俱全且提供方给出估算时才可累计。
    internal static decimal? Cost(ProviderResult result, bool live) => live && result != null && !result.Simulated &&
        result.InputTokens.HasValue && result.OutputTokens.HasValue ? result.EstimatedUsd : null;

    internal static JObject Privacy(DsWorldSnapshot before, AiTaskKind kind, string actor, string input)
    {
        var result = JObject.FromObject(FourMechanismResearch.Privacy(before, kind, actor, input));
        var changed = before.Restore();
        string place = changed.Actors.TryGetValue(actor, out var owner) ? owner.Place : changed.PlayerPlace;
        var samePlace = changed.Actors.Values.Where(a => a.Id != actor && a.Place == place)
            .Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var b in changed.Beliefs.Where(b => samePlace.Contains(b.Owner)))
        { b.Text = FourMechanismCases.PrivateCanary + "_SAME_PLACE_" + b.Id; b.True = !b.True; }
        string original = Body(Packet(before, kind, actor, input));
        string altered = Body(Packet(DsWorldSnapshot.Capture(changed, before.Spent, before.PhaseBudget), kind, actor, input));
        result["same_place_private_payload"] = original == altered;
        result["same_place_other_actors_local"] = JArray.FromObject(samePlace);
        result["same_place_probe_scope"] = samePlace.Length == 0 ? "no_other_actor_here (vacuous)" : "other actors here: private text/True only";
        return result;
    }

    internal static object Summary(bool live, string status, GuardedProvider provider, List<JObject> rows)
    {
        var costs = rows.Select(r => (decimal?)r["estimated_usd"]).ToArray();
        bool known = live && rows.Count == provider.Attempts && rows.Count > 0 && costs.All(c => c.HasValue);
        return new
        {
            suite = Suite, live, status, seeds = Seeds, nights = Nights, stop_scope = "batch", craft_dependency_stop_scope = "batch", per_seed_limit = PerSeedLimit,
            maximum_requests = BatchLimit, persistent_attempts = provider.Attempts, inner_calls_this_process = provider.Calls,
            model = live ? OpenAiPolicy.Model : "RuleProvider (simulated)",
            estimated_usd = known ? (decimal?)costs.Sum(c => c.Value) : null,
            known_estimated_usd = costs.Where(c => c.HasValue).Select(c => c.Value).DefaultIfEmpty().Sum(),
            unknown_costs = rows.Count(r => r["estimated_usd"].Type == JTokenType.Null) + Math.Max(0, provider.Attempts - rows.Count),
            input_tokens = live && rows.Count == provider.Attempts && rows.Count > 0 && rows.All(r => r["input_tokens"].Type != JTokenType.Null)
                ? (int?)rows.Sum(r => (int)r["input_tokens"]) : null,
            output_tokens = live && rows.Count == provider.Attempts && rows.Count > 0 && rows.All(r => r["output_tokens"].Type != JTokenType.Null)
                ? (int?)rows.Sum(r => (int)r["output_tokens"]) : null,
            results = rows,
            max_model_payload_bytes = rows.Count == 0 ? 0 : rows.Max(r => (int?)r["model_payload_bytes"] ?? 0),
            note = "按返回usage与OpenAiPolicy价格估算，非平台账单或余额证明；无usage为未知。known_estimated_usd仅是已知部分，不是整批费用。模拟usage另列，不作真实usage。Accepted/committed不是意图正确。"
        };
    }

    internal static void WriteSeedReport(string dir, int seed, List<JObject> rows, GuardedProvider provider)
    {
        var own = rows.Where(r => (int)r["seed"] == seed).ToArray();
        var residents = DsContent.NewWorld(seed).Actors.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var nights = Enumerable.Range(1, Nights).Select(day => new
        {
            day,
            residents = residents.Select(actor => new { actor,
                result = own.FirstOrDefault(r => (int)r["day"] == day && (string)r["kind"] == "AgentPlan" && (string)r["actor"] == actor),
                status = own.Any(r => (int)r["day"] == day && (string)r["kind"] == "AgentPlan" && (string)r["actor"] == actor)
                    ? "observed" : "not_called" }),
            gm = own.FirstOrDefault(r => (int)r["day"] == day && (string)r["kind"] == "Direct" && !(bool)r["final_dawn"]),
            gm_status = own.Any(r => (int)r["day"] == day && (string)r["kind"] == "Direct" && !(bool)r["final_dawn"])
                ? "observed" : "not_called"
        });
        Create(Path.Combine(dir, "continuous-report.local.json"), new
        {
            suite = Suite, seed, stopped = provider.Stopped, nights,
            final_dawn_gm = own.Where(r => (string)r["kind"] == "Direct" && (bool)r["final_dawn"]),
            final_dawn_gm_status = own.Any(r => (string)r["kind"] == "Direct" && (bool)r["final_dawn"])
                ? "observed" : "not_called",
            initial_steps = own.Where(r => (string)r["kind"] != "AgentPlan" && (string)r["kind"] != "Direct"),
            note = "未调用项不是失败样本或成功，保持not_called；因依赖链停止不造补充道具。母龙终局与每夜GM分列。"
        });
    }

    internal static bool RawReferences(JObject raw, AiTaskKind kind, string actor, DsWorld world)
    {
        if (raw == null) return false;
        if (kind != AiTaskKind.Adjudicate)
            return FourMechanismResearch.ReferencesValid(raw, kind, actor, world) &&
                (kind != AiTaskKind.AgentPlan || string.IsNullOrEmpty((string)raw["target"]) ||
                    world.Actors.ContainsKey((string)raw["target"]));
        var visible = JObject.FromObject(ContextBuilder.AdjudicatePerception(world));
        IEnumerable<string> Ids(string key) => ((JArray)visible[key]).Values<string>().Select(x => x.Split('：')[0]);
        var items = new HashSet<string>(Ids("inventory").Concat(Ids("for_sale")).Concat(Ids("things_here")), StringComparer.Ordinal);
        var targets = new HashSet<string>(Ids("people_here").Concat(items), StringComparer.Ordinal);
        string item = (string)raw["item"], target = (string)raw["target"], place = (string)raw["place"];
        var laws = new HashSet<string>(Ids("known_laws"), StringComparer.Ordinal);
        return (string.IsNullOrEmpty(item) || items.Contains(item)) &&
            (string.IsNullOrEmpty(target) || targets.Contains(target)) &&
            (string.IsNullOrEmpty(place) || world.Places.ContainsKey(place)) &&
            ((JArray)raw["laws"]).Values<string>().All(laws.Contains);
    }

    static object Chain(DsWorld world) => world.Beliefs.OrderBy(b => b.Owner, StringComparer.Ordinal)
        .ThenBy(b => b.Id, StringComparer.Ordinal).Select(b => new
        {
            id = b.Id, owner = b.Owner, root = b.EffectiveRootId, hops = b.EffectiveHops,
            source = b.Source, root_owner = b.RootOwner ?? world.Belief(b.EffectiveRootId)?.Owner ??
                (b.EffectiveHops == 0 ? b.Owner : null)
        }).ToArray();

    internal sealed class JourneyAudit
    {
        readonly GuardedProvider provider;
        readonly bool live;
        readonly int seed;
        readonly List<JObject> rows;
        internal JourneyAudit(GuardedProvider provider, bool live, int seed, List<JObject> rows)
        { this.provider = provider; this.live = live; this.seed = seed; this.rows = rows; }

        internal void Observe(string dir, string id, DsReplayFrame frame, AiTrace trace)
        {
            // Journey 已先保存 trace/replay；所有后续校验与停止在持久证据之后。
            File.WriteAllText(Path.Combine(dir, id + ".before.local.json"), frame.Before.Json);
            File.WriteAllText(Path.Combine(dir, id + ".after.local.json"), frame.After.Json);
            string body = trace.Prompt == null ? null : Body(trace.Prompt);
            if (body != null) File.WriteAllText(Path.Combine(dir, id + ".payload.json"), body);
            JObject raw = null;
            try { raw = JObject.Parse(trace.Provider?.RawText ?? ""); }
            catch (JsonException) { }
            var bw = frame.Before.Restore(); var aw = frame.After.Restore();
            bool schema = raw != null && FourMechanismResearch.SchemaValid(raw, OpenAiDragonsleepProvider.Schema(trace.Kind));
            bool references = schema && RawReferences(raw, trace.Kind, trace.ActorId, bw);
            bool replay = frame.VerifyAsync().GetAwaiter().GetResult();
            var privacy = trace.Prompt == null ? new JObject { ["same_payload"] = false, ["marker_absent"] = false,
                ["same_place_private_payload"] = false } : Privacy(frame.Before, trace.Kind, trace.ActorId, trace.PlayerInput);
            bool samePacket = body != null && body == Body(Packet(frame.Before, trace.Kind, trace.ActorId, trace.PlayerInput));
            bool receipt = trace.Receipt == null ? frame.Before.Hash == frame.After.Hash :
                trace.Receipt.BeforeHash == frame.Before.Hash && trace.Receipt.AfterHash == frame.After.Hash;
            bool privateCanaryAbsent = !(trace.Provider?.RawText ?? "").Contains(FourMechanismCases.PrivateCanary) &&
                !(trace.Presented ?? "").Contains(FourMechanismCases.PrivateCanary);
            bool invariant = replay && receipt && samePacket && (bool)privacy["same_payload"] &&
                (bool)privacy["marker_absent"] && (bool)privacy["same_place_private_payload"] && privateCanaryAbsent;
            var actualEvent = trace.Changes.FirstOrDefault(c => c.Path == "gm.event")?.After;
            string proposedEvent = raw?["event"]?.Type == JTokenType.String ? (string)raw["event"] : null;
            bool? whitelist = trace.Kind == AiTaskKind.Direct ? (bool?)ContextBuilder.GmEvents.Contains(proposedEvent) : null;
            bool eventCommitted = trace.Kind == AiTaskKind.Direct && actualEvent != null;
            bool? actualWhitelist = eventCommitted ? (bool?)ContextBuilder.GmEvents.Contains(actualEvent) : null;
            bool major = new[] { "omen_roar", "guard_sweep", "mother_descends" }.Contains(actualEvent);
            bool? quota = eventCommitted ? (bool?)(aw.MajorEventsToday == bw.MajorEventsToday + (major ? 1 : 0) &&
                (bw.FinalDawn && actualEvent == "mother_descends" || aw.MajorEventsToday <= 1 &&
                    !(bw.MajorEventsToday >= 1 && major))) : null;
            bool? mother = eventCommitted ? (bool?)(bw.FinalDawn ?
                actualEvent == "mother_descends" : actualEvent != "mother_descends") : null;
            var added = aw.Beliefs.Where(b => !bw.Beliefs.Any(old => old.Id == b.Id && old.Owner == b.Owner)).ToArray();
            var row = JObject.FromObject(new
            {
                id, seed, day = bw.Day, phase = bw.Phase.ToString(), final_dawn = bw.FinalDawn,
                kind = trace.Kind.ToString(), actor = trace.ActorId,
                schema_valid = schema, references_valid = references, structural_reference_valid = schema && references,
                verdict = trace.Verdict.Kind.ToString(), reason = trace.Verdict.Reason,
                verdict_notes = trace.Verdict.Notes,
                reason_code = trace.Verdict.ReasonCode.ToString(), issues = trace.ValidationIssues,
                provider_error = trace.Provider?.Error, failure = trace.FailureCode,
                receipt = trace.Receipt?.ToRecord(), actual_rule_effects = DsWorldSnapshot.Diff(frame.Before, frame.After),
                automatic_intent_match = (bool?)null, manual_semantic_review = (bool?)null,
                strict_replay = replay, receipt_hashes_match = receipt, privacy, same_role_payload = samePacket,
                raw_and_presented_canary_absent = privateCanaryAbsent, invariants = invariant,
                model_payload_bytes = body == null ? (int?)null : Encoding.UTF8.GetByteCount(body),
                before_hash = frame.Before.Hash, after_hash = frame.After.Hash,
                gm = trace.Kind == AiTaskKind.Direct ? new
                {
                    proposed_event = proposedEvent, actual_event = actualEvent, whitelist,
                    actual_whitelist = actualWhitelist,
                    major_before = bw.MajorEventsToday, major_after = aw.MajorEventsToday,
                    quota_valid = quota, final_mother_valid = mother, final_dawn = bw.FinalDawn,
                    mother_proposed = proposedEvent == "mother_descends",
                    mother_committed = actualEvent == "mother_descends", with_mother_after = aw.Whelp.WithMother
                } : null,
                new_beliefs = added.Select(b => new { id = b.Id, owner = b.Owner, root = b.EffectiveRootId,
                    hops = b.EffectiveHops, source = b.Source, root_owner = b.RootOwner }),
                input_tokens = live && trace.Provider?.Simulated == false ? trace.Provider.InputTokens : null,
                output_tokens = live && trace.Provider?.Simulated == false ? trace.Provider.OutputTokens : null,
                estimated_usd = Cost(trace.Provider, live),
                simulated_usage = !live ? new { input = trace.Provider?.InputTokens, output = trace.Provider?.OutputTokens } : null,
                note = "结构/引用读取未过滤raw。规则结果只报告实际回执与世界差异，不等于意图正确。root/hops/source/owner仅供本地链审计，不增加到任何模型输入。"
            });
            Create(Path.Combine(dir, id + ".chain.local.json"), new { before = Chain(bw), after = Chain(aw) });
            Create(Path.Combine(dir, id + ".audit.local.json"), row);
            rows.Add(row);
            bool failure = trace.Provider == null || trace.Provider.Error != null ||
                string.IsNullOrWhiteSpace(trace.Provider.RawText);
            if (failure || !invariant || actualWhitelist == false || quota == false || mother == false)
            {
                provider.Stop(trace.Provider?.Error ?? (failure ? "provider_response_missing" : "journey_invariant_failed"));
                throw new InvalidOperationException("continuous_world_stopped_after_trace_saved");
            }
        }
    }
}
