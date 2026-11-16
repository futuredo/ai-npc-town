using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;

/// <summary>
/// rules-9 社会信息链的独立研究入口。
/// 不改四机制 rules-8 套题；只复用原题的预注册判据，并把需要真实规则事件的夹具放在本文件。
/// </summary>
internal static class SocialChainResearch
{
    public const string Suite = "ds-social-chain-rules9-1";
    public const int MaximumRequests = 48;
    static readonly string[] CaseIds =
    {
        "d03_custody_return", "d04_repeat_show_claim", "d05_lie_denial", "d06_contradiction",
        "d07_claim_key_transfer", "d08_legal_witness",
        "a01_private_difference", "a02_share_own", "a03_share_unknown", "a04_guard_local_present",
        "a05_guard_local_absent", "a06_second_hop", "a07_listener_absent", "a08_private_escort"
    };

    static string Root(string root) => Path.Combine(root, ".local", "four-mechanisms-rules9", "social-chain");
    static string Hash(string text) => DsWorldSnapshot.Digest(text);
    static string Body(PromptPacket packet) => OpenAiDragonsleepProvider.BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch).ToString(Formatting.None);

    static PromptPacket Packet(DsWorldSnapshot before, FourMechanismCase c)
    {
        return c.Kind == AiTaskKind.Dialogue
            ? ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(before.Restore(), c.Actor), c.Input)
            : ContextBuilder.BuildTask(TaskPerceptionView.Capture(before.Restore(), c.Kind, c.Actor,
                before.PhaseBudget - before.Spent), c.Input);
    }

    static FourMechanismCase Copy(FourMechanismCase source, Action<DragonsleepSession> setup = null) => new FourMechanismCase
    {
        Id = source.Id, Kind = source.Kind, Actor = source.Actor, Input = source.Input,
        Rubric = source.Rubric, Effect = source.Effect, AcceptCraft = source.AcceptCraft,
        Setup = setup ?? source.Setup, EffectCheck = source.EffectCheck, IntentCheck = source.IntentCheck
    };

    static void Require(bool ok, string code)
    {
        if (!ok) throw new InvalidOperationException(code);
    }

    sealed class JsonProvider : IAiProvider
    {
        readonly string _raw;
        public JsonProvider(string raw) { _raw = raw; }
        public string Name => "rules-9 fixture provider";
        public bool IsSimulated => true;
        public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
            Task.FromResult(new ProviderResult { ProviderName = Name, RawText = _raw, Simulated = true, EstimatedUsd = 0m });
    }

    // 离线验收只替换既有 RuleProvider 对 a06 自然语言的启发式误判；
    // 不改变生产规则、原题文本或真实提供方路径。其余案例仍使用原离线规则提供方。
    sealed class OfflineSocialProvider : IAiProvider
    {
        readonly RuleProvider _fallback = new RuleProvider();
        public string Name => "社会链离线规则提供方（固定意图夹具）";
        public bool IsSimulated => true;

        public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
        {
            if (packet.Kind == AiTaskKind.AgentPlan && packet.ActorId == "vesna" &&
                (packet.OriginalPlayerInput ?? "").Contains("继续转述", StringComparison.Ordinal))
            {
                var proposal = new Dictionary<string, object>
                {
                    ["verb"] = "tell", ["place"] = "castle", ["target"] = "osric",
                    ["share"] = new List<object> { "b_marta_saw_pip@vesna" },
                    ["reason"] = "把听玛尔妲讲的消息转告身边的奥斯里克，并保留来源。"
                };
                return Task.FromResult(new ProviderResult
                {
                    ProviderName = Name, RawText = MiniJson.Write(proposal), Simulated = true, EstimatedUsd = 0m
                });
            }
            if (packet.Kind == AiTaskKind.AgentPlan && packet.ActorId == "marta" &&
                (packet.OriginalPlayerInput ?? "").Contains("不在身边", StringComparison.Ordinal))
            {
                var proposal = new Dictionary<string, object>
                {
                    ["verb"] = "move", ["place"] = "castle", ["target"] = "",
                    ["share"] = new List<object>(),
                    ["reason"] = "维丝娜不在身边，先去王城找她。"
                };
                return Task.FromResult(new ProviderResult
                {
                    ProviderName = Name, RawText = MiniJson.Write(proposal), Simulated = true, EstimatedUsd = 0m
                });
            }
            return _fallback.CompleteAsync(packet, ct);
        }
    }

    static AiTrace RuleStep(DragonsleepSession s, Dictionary<string, object> proposal, string label)
    {
        var trace = s.RunStepAsync(proposal, label).GetAwaiter().GetResult();
        Require(trace != null && trace.FailureCode == null && trace.Receipt?.Applied == true, "fixture_rule_step_failed");
        return trace;
    }

    static AiTrace AgentStep(DragonsleepSession s, string actor, Dictionary<string, object> proposal, string label)
    {
        var trace = s.RunWithProviderAsync(AiTaskKind.AgentPlan, actor, label,
            new JsonProvider(MiniJson.Write(proposal))).GetAwaiter().GetResult();
        Require(trace != null && trace.FailureCode == null && trace.Receipt?.Applied == true, "fixture_agent_step_failed");
        return trace;
    }

    static Dictionary<string, object> Action(string verb, string target, string item, string place) =>
        new Dictionary<string, object>
        {
            ["verb"] = verb, ["target"] = target, ["item"] = item, ["place"] = place,
            ["laws"] = new List<object>(), ["difficulty"] = .99, ["narration"] = "规则九真实事件夹具"
        };

    static void ActualCustodyFixture(DragonsleepSession s)
    {
        s.PhaseBudget = 6;
        string place = s.World.Actors["maren"].Place;
        s.World.PlayerPlace = place;
        s.World.Items["pact_page"].Holder = "player";
        RuleStep(s, Action("give", "maren", "pact_page", place), "rules-9 fixture: actual give custody");
        Require(s.World.Flags.ContainsKey("pact_custody:maren") && s.World.Items["pact_page"].Holder == "maren",
            "fixture_custody_not_established");
    }

    static void ActualShowFixture(DragonsleepSession s)
    {
        s.PhaseBudget = 8;
        string place = s.World.Actors["maren"].Place;
        s.World.PlayerPlace = place;
        s.World.Items["pact_page"].Holder = "player";
        RuleStep(s, Action("show", "maren", "pact_page", place), "rules-9 fixture: actual first show");
        RuleStep(s, Action("show", "maren", "pact_page", place), "rules-9 fixture: actual repeated show");
        Require(s.World.Flags.ContainsKey("shown:pact_page:maren"), "fixture_show_not_recorded");
    }

    static void ActualSecondHopFixture(DragonsleepSession s)
    {
        s.World.Phase = DayPhase.Night;
        s.World.Actors["marta"].Place = "market";
        s.World.Actors["vesna"].Place = "market";
        s.World.Actors["osric"].Place = "castle";
        AgentStep(s, "marta", new Dictionary<string, object>
        {
            ["verb"] = "tell", ["place"] = "market", ["target"] = "vesna",
            ["share"] = new List<object> { "b_marta_saw_pip" }, ["reason"] = "真实规则事件夹具的一跳传话"
        }, "rules-9 fixture: actual first hop");
        Require(s.World.Belief("b_marta_saw_pip@vesna") != null, "fixture_first_hop_missing");
        AgentStep(s, "vesna", new Dictionary<string, object>
        {
            ["verb"] = "move", ["place"] = "castle", ["target"] = "", ["share"] = new List<object>(),
            ["reason"] = "真实规则事件夹具：先移动到奥斯里克所在地点"
        }, "rules-9 fixture: actual listener move");
        Require(s.World.Actors["vesna"].Place == s.World.Actors["osric"].Place, "fixture_listener_move_missing");
    }

    static List<FourMechanismCase> Cases()
    {
        var source = FourMechanismCases.New().ToDictionary(c => c.Id, StringComparer.Ordinal);
        var result = new List<FourMechanismCase>();
        foreach (var id in CaseIds)
        {
            if (!source.TryGetValue(id, out var c)) throw new InvalidOperationException("social_case_missing:" + id);
            result.Add(id switch
            {
                "d03_custody_return" => Copy(c, ActualCustodyFixture),
                "d04_repeat_show_claim" => Copy(c, ActualShowFixture),
                "a06_second_hop" => Copy(c, ActualSecondHopFixture),
                _ => Copy(c)
            });
        }
        return result;
    }

    static DragonsleepSession Fixture(FourMechanismCase c)
    {
        var s = new DragonsleepSession(new RuleProvider(), 7, new DsFixedRoll(.9999), requireBoundCraftConfirmation: true);
        c.Setup(s);
        return s;
    }

    static object EventEvidence(DsWorld w) => w.Beliefs
        .Where(b =>
            (b.Source ?? "").StartsWith("seen:event:", StringComparison.Ordinal) ||
            (b.Source ?? "").StartsWith("told:", StringComparison.Ordinal) ||
            b.EffectiveRootId == "b_marta_saw_pip")
        .OrderBy(b => b.Owner, StringComparer.Ordinal).ThenBy(b => b.Id, StringComparer.Ordinal)
        .Select(b => new { b.Id, b.Owner, b.Topic, b.RootId, b.RootOwner, b.Hops, b.Source, b.Text })
        .ToArray();

    static string[] SourcePaths(string root) => new[]
    {
        "dotnet/Town.Research/Program.cs", "dotnet/Town.Research/SocialChainResearch.cs",
        "dotnet/Town.Research/FourMechanismCases.cs", "dotnet/Town.Research/ExperimentCases.cs",
        "dotnet/Town.Research/Journey.cs"
    }
    .Concat(Directory.GetFiles(Path.Combine(root, "unity/Assets/Town.Core/Dragonsleep"), "*.cs")
        .Select(f => Path.GetRelativePath(root, f)))
    .Concat(Directory.GetFiles(Path.Combine(root, "unity/Assets/Town.OpenAI"), "*.cs")
        .Select(f => Path.GetRelativePath(root, f)))
    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public static int Prepare(string root)
    {
        string dir = Root(root); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "pre-registered.json");
        if (File.Exists(file)) throw new InvalidOperationException("pre_registration_already_exists");
        var rows = new List<object>();
        foreach (var c in Cases())
        {
            var s = Fixture(c);
            var before = s.CaptureState();
            var packet = Packet(before, c);
            var privacy = JObject.FromObject(FourMechanismResearch.Privacy(before, c.Kind, c.Actor, c.Input));
            if (!(bool)privacy["same_payload"] || !(bool)privacy["marker_absent"])
                throw new InvalidOperationException("pre_registration_privacy_failed");
            string body = Body(packet);
            rows.Add(new
            {
                id = c.Id, kind = c.Kind.ToString(), actor = c.Actor, input = c.Input,
                accept_craft = c.AcceptCraft, rubric = c.Rubric, effect = c.Effect,
                before = JObject.Parse(before.Json), before_sha256 = before.Hash,
                fixture_event_evidence = EventEvidence(s.World),
                payload = JObject.Parse(body), payload_bytes = Encoding.UTF8.GetByteCount(body),
                payload_sha256 = Hash(body), prompt_sha256 = DsReplayFrame.Fingerprint(packet),
                privacy,
                note = c.Id == "d06_contradiction"
                    ? "保留原题的人工玩家原话夹具；当前协议没有结构化玩家发言事件字段，不把它当作生产事件记忆证据。"
                    : null
            });
        }
        var registration = new
        {
            suite = Suite, rules_version = DsExecutor.RulesVersion, model = OpenAiPolicy.Model,
            maximum_requests = MaximumRequests, planned_requests = CaseIds.Length,
            seed = 7, cases = rows,
            stopping = "服务错误、输入隐私差分失败、请求体超限、严格回放或世界提交不变量失败立即停止；不重试、不补跑、不改原题分数。",
            scope = "069 B 套题社会相关 d03-d08/a01-a08；d03/d04/a06 的前置事实由本文件调用真实规则入口生成。",
            sources = SourcePaths(root).ToDictionary(f => f, f => Hash(File.ReadAllText(Path.Combine(root, f))))
        };
        string text = JsonConvert.SerializeObject(registration, Formatting.Indented);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
        File.WriteAllText(Path.Combine(dir, "pre-registered.sha256"), Hash(text) + "\n");
        Console.WriteLine("预注册：" + file + " SHA-256 " + Hash(text));
        return 0;
    }

    static JObject VerifyRegistration(string root, string dir)
    {
        string file = Path.Combine(dir, "pre-registered.json");
        string text = File.ReadAllText(file);
        if (Hash(text) != File.ReadAllText(Path.Combine(dir, "pre-registered.sha256")).Trim())
            throw new InvalidOperationException("pre_registration_changed");
        var registration = JObject.Parse(text);
        if ((string)registration["suite"] != Suite || (string)registration["rules_version"] != DsExecutor.RulesVersion ||
            (string)registration["model"] != "gpt-6-luna" || OpenAiPolicy.Model != "gpt-6-luna")
            throw new InvalidOperationException("pre_registration_header_changed");
        foreach (var p in ((JObject)registration["sources"]).Properties())
            if (Hash(File.ReadAllText(Path.Combine(root, p.Name))) != (string)p.Value)
                throw new InvalidOperationException("registered_source_changed");
        return registration;
    }

    sealed class LimitedProvider : IAiProvider, IDisposable
    {
        readonly IAiProvider _inner; readonly bool _live; readonly string _dir; readonly FileStream _gate;
        public int Calls { get; private set; }
        public string ExpectedPayloadHash { get; set; }
        public string Name => _inner.Name;
        public bool IsSimulated => _inner.IsSimulated;
        public LimitedProvider(IAiProvider inner, bool live, string dir)
        {
            _inner = inner; _live = live; _dir = dir;
            if (live) _gate = new FileStream(Path.Combine(dir, "live.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
        {
            string stopped = Path.Combine(_dir, "STOPPED");
            if (File.Exists(stopped)) throw new InvalidOperationException("research_stopped");
            string body = Body(packet);
            if (ExpectedPayloadHash != null && Hash(body) != ExpectedPayloadHash)
            { Stop("registered_payload_changed"); throw new InvalidOperationException("registered_payload_changed"); }
            if (Encoding.UTF8.GetByteCount(body) > OpenAiPolicy.MaxRequestBytes)
            { Stop("input_too_large"); throw new InvalidOperationException("input_too_large"); }
            if (_live)
            {
                string file = Path.Combine(_dir, "live-requests.jsonl");
                int count = File.Exists(file) ? File.ReadLines(file).Count() : 0;
                if (count >= MaximumRequests) { Stop("request_limit"); throw new InvalidOperationException("request_limit"); }
                File.AppendAllText(file, JsonConvert.SerializeObject(new
                {
                    number = count + 1, utc = DateTime.UtcNow, kind = packet.Kind.ToString(), actor = packet.ActorId,
                    payload_sha256 = Hash(body), input = packet.OriginalPlayerInput
                }) + "\n");
            }
            Calls++;
            ProviderResult result;
            try { result = await _inner.CompleteAsync(packet, ct).ConfigureAwait(false); }
            catch { Stop("provider_exception"); throw; }
            if (result == null || result.Error != null) Stop(result?.Error ?? "null_provider_response");
            return result;
        }
        public void Stop(string reason) => File.WriteAllText(Path.Combine(_dir, "STOPPED"), reason + "\n");
        public void Dispose() => _gate?.Dispose();
    }

    static async Task<JObject> Record(string output, FourMechanismCase c, DsWorldSnapshot before,
        DragonsleepSession session, AiTrace trace)
    {
        var row = await FourMechanismResearch.Record(output, c, before, session, trace, true, true).ConfigureAwait(false);
        // 保留原 B 套题的自动判据原值；rules-9 门槛另记修复后的规则效果。
        // a06 原判据使用“毛毯”字样，而生产初始事实是“冒烟的毯子”，不能用
        // 文字差异否定已经提交的 root/hops=2 规则事实。
        bool rules9Effect = c.Id == "a06_second_hop"
            ? session.World.Beliefs.Any(x => x.Owner == "osric" && x.Source == "told:vesna" &&
                x.EffectiveRootId == "b_marta_saw_pip" && x.EffectiveHops == 2)
            : c.EffectCheck == null || c.EffectCheck(trace, before.Restore(), session.World);
        var after = session.CaptureState();
        int payloadBytes = trace.Prompt == null ? 0 : Encoding.UTF8.GetByteCount(Body(trace.Prompt));
        row["rules9_rule_effect_match"] = rules9Effect;
        row["social_chain_suite"] = Suite;
        row["fixture_event_evidence"] = JToken.FromObject(EventEvidence(before.Restore()));
        row["after_social_event_evidence"] = JToken.FromObject(EventEvidence(after.Restore()));
        row["model_payload_bytes"] = payloadBytes;
        FourMechanismResearch.Write(Path.Combine(output, "result.json"), row);
        return row;
    }

    public static async Task<int> Run(string root, bool live)
    {
        string dir = Root(root);
        var registration = VerifyRegistration(root, dir);
        string output = Path.Combine(dir, live ? "live" : "offline");
        if (Directory.Exists(output)) throw new InvalidOperationException("batch_already_exists_no_resampling");
        Directory.CreateDirectory(output);
        using var online = live
            ? new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root), new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch)
            : null;
        using var provider = new LimitedProvider(online ?? (IAiProvider)new OfflineSocialProvider(), live, dir);
        var rows = new List<JObject>(); string status = "running";
        try
        {
            foreach (var c in Cases())
            {
                var saved = registration["cases"].Single(x => (string)x["id"] == c.Id);
                var before = DsWorldSnapshot.Parse(saved["before"].ToString(Formatting.None));
                string body = Body(Packet(before, c));
                if (before.Hash != (string)saved["before_sha256"] || Hash(body) != (string)saved["payload_sha256"])
                    throw new InvalidOperationException("registered_fixture_changed");
                var session = new DragonsleepSession(provider, 7, new DsFixedRoll(.9999), requireBoundCraftConfirmation: true);
                session.RestoreState(before);
                provider.ExpectedPayloadHash = (string)saved["payload_sha256"];
                var trace = await session.RunAsync(c.Kind, c.Actor, c.Input).ConfigureAwait(false);
                var row = await Record(Path.Combine(output, c.Id), c, before, session, trace).ConfigureAwait(false);
                rows.Add(row);
                WriteSummary(output, live, rows, provider.Calls, status);
                Console.WriteLine(c.Id + ": structure=" + row["structural_reference_valid"] +
                    " effect=" + row["rule_effect_match"] + " rules9_effect=" + row["rules9_rule_effect_match"] +
                    " intent=" + row["automatic_intent_match"] + " invariant=" + row["invariants"]);
                if (trace.Provider?.Error != null || !(bool)row["invariants"] || !(bool)row["rules9_rule_effect_match"])
                { provider.Stop(trace.Provider?.Error ?? "invariant_failed"); status = "stopped"; return 1; }
            }
            status = "completed"; return 0;
        }
        catch (InvalidOperationException)
        { provider.Stop("research_exception"); status = "stopped"; throw; }
        finally
        { WriteSummary(output, live, rows, provider.Calls, status); Console.WriteLine("报告：" + output); }
    }

    static void WriteSummary(string output, bool live, List<JObject> rows, int calls, string status)
    {
        bool unknown = rows.Any(r => r["estimated_usd"] == null || r["estimated_usd"].Type == JTokenType.Null);
        decimal known = rows.Where(r => r["estimated_usd"] != null && r["estimated_usd"].Type != JTokenType.Null)
            .Sum(r => (decimal)r["estimated_usd"]);
        FourMechanismResearch.Write(Path.Combine(output, "summary.json"), new
        {
            suite = Suite, live, model = live ? OpenAiPolicy.Model : "RuleProvider", rules_version = DsExecutor.RulesVersion,
            status, requested = calls, maximum_requests = MaximumRequests, results = rows,
            max_model_payload_bytes = rows.Count == 0 ? 0 : rows.Max(r => (int)r["model_payload_bytes"]),
            estimated_usd = unknown ? (decimal?)null : known, known_estimated_usd = known,
            note = "按返回 usage 与当前 OpenAiPolicy 价格估算；没有 usage 时费用未知，不记为零。d06 的玩家原话夹具不代表生产事件记忆。"
        });
    }
}
