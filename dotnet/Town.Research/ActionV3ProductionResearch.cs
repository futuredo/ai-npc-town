using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.PieGame;

/// <summary>
/// Game 显式自由行动 v3 的独立生产前置验证套题。
/// 不复用旧 Focus/Memory 套题的成绩；离线和 live 都经过同一个 Host/KernelActionTransaction。
/// </summary>
internal static partial class ActionKernelResearch
{
    internal const string ActionV3ProductionSuite = "ds-action-v3-production-1";
    const int ActionV3ProductionMaximumRequests = 48;
    const string ActionV3ProductionRootName = "action-v3-production-1";

    sealed class ProductionCase
    {
        internal string Id;
        internal string Episode;
        internal int Step;
        internal int Seed;
        internal string Input;
        internal string Verb;
        internal string Target;
        internal string Item;
        internal string Place;
        internal string Group;
        internal string ExpectedEffect;
        internal string Regression;
        internal Action<DragonsleepSession> Setup;
    }

    sealed class ProductionOfflineProvider : IAiProvider
    {
        readonly Dictionary<string, Dictionary<string, object>> _proposals;
        internal ProductionOfflineProvider(IEnumerable<ProductionCase> cases)
        {
            // 同一固定措辞会在三个种子中复用；proposal只由注册输入决定，不能让重复种子
            // 触发ToDictionary异常，也不能回读世界状态来消歧。
            _proposals = cases.GroupBy(c => c.Input, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Proposal(group.First()), StringComparer.Ordinal);
        }

        public string Name => "任务三生产套题离线确定性提供方";
        public bool IsSimulated => true;

        public System.Threading.Tasks.Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
        {
            if (packet.Kind != AiTaskKind.Adjudicate || packet.ActorId != "player")
                throw new InvalidOperationException("action_v3_production_action_only");
            if (!_proposals.TryGetValue(packet.OriginalPlayerInput ?? "", out var proposal))
                throw new InvalidOperationException("action_v3_production_unregistered_input");
            string raw = MiniJson.Write(proposal);
            return System.Threading.Tasks.Task.FromResult(new ProviderResult
            {
                ProviderName = Name,
                RawText = raw,
                Simulated = true,
                // 离线确定性提供方没有服务端 usage，不能把本地字符数或零报成真实费用。
                InputTokens = null,
                OutputTokens = null,
                EstimatedUsd = null
            });
        }

        static Dictionary<string, object> Proposal(ProductionCase c) => new Dictionary<string, object>
        {
            ["verb"] = c.Verb, ["target"] = c.Target ?? "", ["item"] = c.Item ?? "",
            ["place"] = c.Place ?? "", ["laws"] = new List<object>(), ["difficulty"] = .1,
            ["narration"] = c.Input.TrimEnd('。', '！', '!') + "。"
        };
    }

    internal static int PrepareActionV3Production(string root)
    {
        string contractRoot = Path.Combine(root, ".local", "dragonsleep", ActionV3ProductionRootName);
        Directory.CreateDirectory(contractRoot);
        string registration = Path.Combine(contractRoot, "pre-registered.json");
        if (File.Exists(registration)) throw new InvalidOperationException("action_v3_production_already_registered");

        var cases = ProductionCases();
        if (cases.Count != ActionV3ProductionMaximumRequests)
            throw new InvalidOperationException("action_v3_production_case_count");
        var fixtures = new List<object>();
        foreach (var episode in cases.GroupBy(c => c.Episode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var seed = episode.First().Seed;
            var fixture = Fixture(episode.First());
            fixtures.Add(new
            {
                id = episode.Key,
                seed,
                before_hash = fixture.Hash,
                before = JObject.Parse(fixture.Json),
                cases = episode.OrderBy(c => c.Step).Select(CaseRecord).ToArray()
            });
        }
        var contract = new
        {
            suite = ActionV3ProductionSuite,
            model = OpenAiPolicy.Model,
            rules = DsExecutor.RulesVersion,
            action_prompt = DsActionPrompt.Focused,
            action_perception = DsActionPerception.Current,
            maximum_requests = ActionV3ProductionMaximumRequests,
            planned_requests = cases.Count,
            concurrency = 1,
            automatic_retries = 0,
            output_tokens = 768,
            max_request_bytes = OpenAiPolicy.MaxRequestBytes,
            gate = new
            {
                intent_minimum = "44/48",
                key_probes = "observe/wait/confide/lure/repeat_show/repeat_buy/take_after_delivery each non-zero",
                invariants = "48/48",
                replay = "48/48",
                privacy = "48/48",
                non_regression_effects = "全部非回归动作效果必须通过；已知两个历史反例只单列观察，不计入该阻断。",
                failure_policy = "不达标回退Game到v2并停止；不改词、不重采样、不以目标达成抵消意图或规则失败。"
            },
            regressions = new[]
            {
                new { id = "custody-110", observation = "重复出示已交付且不再由玩家持有的契约；只记录模型/规则实际结果，不改判。" },
                new { id = "purchase_control-109", observation = "重复购买同一已买物件；只记录卖方/item槽位和实际规则结果，不改判。" }
            },
            scope = "生产Game同构：ActAsync + KernelActionTransaction + ds-action-prompt-v3 + ds-action-perception-v2；离线夹具不发送请求。",
            source_hashes = SourceHashes(root),
            fixtures
        };
        string json = JsonConvert.SerializeObject(contract, Formatting.Indented);
        File.WriteAllText(registration, json, new UTF8Encoding(false));
        File.WriteAllText(registration + ".sha256", ProductionHash(Encoding.UTF8.GetBytes(json)) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(contractRoot, "README.md"),
            "本目录锁定任务三 ds-action-v3-production-1 的48条输入、种子、规则版本、v3/v2感知绑定和源文件哈希。\n" +
            "预注册只证明离线夹具及发送边界；真实晋升必须在独立 --live 批次中按固定门槛判定。\n" +
            "067/068 两个已知反例仅记录实际观察，不改词、不改判、不重抽。\n", new UTF8Encoding(false));
        Console.WriteLine("任务三生产套题预注册完成：" + registration);
        return 0;
    }

    internal static async System.Threading.Tasks.Task<int> RunActionV3Production(string root, bool live)
    {
        string contractRoot = Path.Combine(root, ".local", "dragonsleep", ActionV3ProductionRootName);
        string registration = Path.Combine(contractRoot, "pre-registered.json");
        if (!File.Exists(registration) || !File.Exists(registration + ".sha256"))
            throw new InvalidOperationException("action_v3_production_pre_registration_required");
        VerifyRegistration(root, registration);
        string run = Path.Combine(contractRoot, "runs",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + (live ? "-live" : "-offline"));
        Directory.CreateDirectory(run);

        var cases = ProductionCases();
        var rows = new List<Dictionary<string, object>>();
        OpenAiDragonsleepProvider online = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string status = "running";
        string stopPhase = null;
        try
        {
            stopPhase = "provider";
            if (live)
            {
                if (OpenAiPolicy.Model != "gpt-6-luna") throw new InvalidOperationException("research_model_mismatch");
                online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),
                    new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch),
                    profile: OpenAiRunProfile.SixMechanismResearch);
                ResponseShapeDiagnostics.Attach(online, run);
            }
            IAiProvider provider = online ?? (IAiProvider)new ProductionOfflineProvider(cases);
            foreach (var episode in cases.GroupBy(c => c.Episode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var first = episode.First();
                stopPhase = "fixture";
                var before = Fixture(first);
                stopPhase = "host";
                using var host = new Host(provider, before, DsActionPrompt.Focused, DsActionPerception.Current,
                    first.Seed, new DsFixedRoll(.9999), requireBoundCraftConfirmation: true);
                string previousAfter = host.Session.CaptureState().Hash;
                foreach (var item in episode.OrderBy(c => c.Step))
                {
                    if (host.Session.CaptureState().Hash != previousAfter)
                        throw new InvalidOperationException("action_v3_production_sequence_state_changed");
                    stopPhase = "action";
                    var row = ExecuteActionV3(run, item, host, cancellation.Token);
                    rows.Add(row);
                    previousAfter = (string)row["after_hash"];
                    WriteActionV3Summary(run, rows, live, status);
                    if (!(bool)row["invariants_passed"] || !(bool)row["replay_passed"] ||
                        !(bool)row["privacy_passed"] || row["provider_error"] != null)
                    {
                        status = "stopped_invariant_replay_privacy_or_provider_error";
                        WriteActionV3Summary(run, rows, live, status);
                        return 1;
                    }
                }
            }
            status = "completed";
            WriteActionV3Summary(run, rows, live, status);
            var final = JObject.Parse(File.ReadAllText(Path.Combine(run, "summary.json")));
            bool promoted = (bool)final["promotion_gate_passed"];
            Console.WriteLine("任务三生产套题：" + (promoted ? "达到晋升门槛" : "未达到晋升门槛，必须回退v2并停止") + "；报告=" + run);
            return promoted ? 0 : 1;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
            error is InvalidOperationException || error is OperationCanceledException || error is TimeoutException ||
            error is ArgumentException)
        {
            status = "stopped_" + error.GetType().Name;
            WriteActionV3Summary(run, rows, live, status, stopPhase);
            Console.Error.WriteLine("任务三生产套题停止；已保留完成证据，不输出异常正文或凭据。");
            return 2;
        }
        catch (Exception error)
        {
            // 仅保留安全的异常类型和阶段，避免把路径、请求体、密钥或模型原文写入研究证据。
            status = "stopped_" + error.GetType().Name;
            WriteActionV3Summary(run, rows, live, status, stopPhase);
            Console.Error.WriteLine("任务三生产套题停止；已保留完成证据，不输出异常正文或凭据。");
            return 2;
        }
        finally
        {
            online?.Dispose();
            Console.CancelKeyPress -= cancel;
            WriteActionV3Summary(run, rows, live, status, stopPhase);
            Console.WriteLine("报告：" + run);
        }
    }

    static Dictionary<string, object> ExecuteActionV3(string dir, ProductionCase item, Host host, CancellationToken ct)
    {
        string path = Path.Combine(dir, item.Episode, "step-" + (item.Step + 1));
        Directory.CreateDirectory(path);
        var before = host.Session.CaptureState();
        int recordsBefore = host.Transaction.PrivateRecordList.Count;
        int callsBefore = host.Provider.Calls;
        var trace = host.Act(item.Input, ct);
        var after = host.Session.CaptureState();
        var frames = host.Transaction.PrivateRecordList.Skip(recordsBefore).ToArray();
        bool literalIntent = trace.Proposal != null && trace.Proposal.Str("verb") == item.Verb
            && trace.Proposal.Str("target") == (item.Target ?? "")
            && trace.Proposal.Str("item") == (item.Item ?? "")
            && (trace.Proposal.Str("place") ?? "") == (item.Place ?? "");
        bool effect = EffectMatch(item, before.Restore(), after.Restore(), trace);
        bool regressionObserved = item.Regression != null && RegressionObserved(item, before.Restore(), after.Restore(), trace);
        bool privacy = host.Provider.PrivateMarkersAbsent && PayloadIsBound(host);
        bool replay = false;
        string replayReason = frames.Length == 1 ? "ready_frame" : "no_ready_frame";
        if (frames.Length == 1)
        {
            string anchorsPath = Path.Combine(path, "anchors.local.json");
            string replayPath = Path.Combine(path, "replay.private.json");
            File.WriteAllText(anchorsPath, JsonConvert.SerializeObject(frames.Select(f => f.Digest).ToArray()));
            File.WriteAllText(replayPath, JsonConvert.SerializeObject(frames, Formatting.Indented));
            var anchors = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(anchorsPath));
            var reloaded = JsonConvert.DeserializeObject<ReplayFrame[]>(File.ReadAllText(replayPath));
            replay = reloaded.Length == 1 && Replay(before, after, host.Provider.Packet, reloaded[0], anchors[0]);
        }
        bool invariant = callsBefore + 1 == host.Provider.Calls && host.Provider.PrivateMarkersAbsent
            && host.Kernel.Capture() == after.Json && trace.KernelCode != null
            && frames.Length == (trace.KernelCode == "committed" ? 1 : 0)
            && frames.All(frame => frame.PromptFingerprint == host.Provider.Fingerprint && frame.AfterDigest == after.Hash)
            && trace.Prompt?.ActionPromptVersion == DsActionPrompt.Focused
            && trace.Prompt?.ActionPerceptionVersion == DsActionPerception.Current;
        // 任务要求将两个历史反例单独观察，不能用它们的规则后果伪造生产通过。
        bool effectGate = item.Regression == null ? effect : true;
        File.WriteAllText(Path.Combine(path, "before.local.json"), before.Json);
        File.WriteAllText(Path.Combine(path, "after.local.json"), after.Json);
        File.WriteAllText(Path.Combine(path, "trace.json"), MiniJson.Write(trace.ToRecord(), true));
        File.WriteAllText(Path.Combine(path, "payload.json"), JsonConvert.SerializeObject(host.Provider.Payload, Formatting.Indented));
        var row = new Dictionary<string, object>
        {
            ["id"] = item.Id, ["episode"] = item.Episode, ["step"] = item.Step + 1, ["group"] = item.Group,
            ["input"] = item.Input, ["expected_verb"] = item.Verb, ["expected_target"] = item.Target,
            ["expected_item"] = item.Item, ["expected_place"] = item.Place,
            ["intent_match"] = literalIntent, ["effect_match"] = effect, ["effect_gate"] = effectGate,
            ["regression"] = item.Regression, ["regression_observed"] = regressionObserved,
            ["kernel_code"] = trace.KernelCode, ["failure"] = trace.FailureCode,
            ["provider_error"] = trace.Provider?.Error, ["verdict"] = trace.Verdict.Kind.ToString(),
            ["reason_code"] = trace.Verdict.ReasonCode.StableCode, ["time_cost"] = trace.Receipt?.TimeCost,
            ["before_hash"] = before.Hash, ["after_hash"] = after.Hash,
            ["action_prompt_version"] = trace.Prompt?.ActionPromptVersion,
            ["action_perception_version"] = trace.Prompt?.ActionPerceptionVersion,
            ["binding"] = host.Provider.Packet == null ? null : ActionPromptCodec.BindingFor(host.Provider.Packet),
            ["fingerprint"] = host.Provider.Fingerprint, ["ready_records"] = frames.Length,
            ["replay_passed"] = replay, ["replay_scope"] = replayReason,
            ["privacy_passed"] = privacy, ["private_markers_absent"] = host.Provider.PrivateMarkersAbsent,
            ["invariants_passed"] = invariant, ["strict_replay"] = replay,
            ["input_tokens"] = trace.Provider?.InputTokens, ["output_tokens"] = trace.Provider?.OutputTokens,
            ["estimated_usd"] = trace.Provider?.EstimatedUsd
        };
        File.WriteAllText(Path.Combine(path, "result.json"), JsonConvert.SerializeObject(row, Formatting.Indented));
        Console.WriteLine(item.Id + ": intent=" + literalIntent + " effect=" + effect +
            " replay=" + replay + " privacy=" + privacy + " kernel=" + trace.KernelCode);
        return row;
    }

    static bool PayloadIsBound(Host host)
    {
        return host.Provider.Packet != null && host.Provider.Packet.Kind == AiTaskKind.Adjudicate
            && host.Provider.Packet.ActorId == "player"
            && ActionPromptCodec.BindingFor(host.Provider.Packet).Contains(DsActionPrompt.Focused)
            && ActionPromptCodec.BindingFor(host.Provider.Packet).Contains(DsActionPerception.Current);
    }

    static bool EffectMatch(ProductionCase item, DsWorld before, DsWorld after, AiTrace trace)
    {
        if (item.Regression != null) return false;
        if (trace.FailureCode != null) return false;
        switch (item.ExpectedEffect)
        {
            case "lure": return after.Whelp.TrustPlayer > before.Whelp.TrustPlayer
                && after.Items["smoked_fish"].Holder == before.Items["smoked_fish"].Holder;
            case "feed": return after.Items["smoked_fish"].Holder == "consumed"
                && after.Whelp.Hunger < before.Whelp.Hunger;
            case "give": return after.Items["pact_page"].Holder == "maren"
                && after.Flags.ContainsKey("pact_custody:maren");
            case "take": return after.Items["pact_page"].Holder == "player"
                && !after.Flags.ContainsKey("pact_custody:maren");
            case "confide": return after.Flags.ContainsKey("confided:" + item.Target)
                && (trace.Verdict.Kind == VerdictKind.Accepted || trace.Verdict.Kind == VerdictKind.Partial);
            case "buy": return after.Items["smoked_fish"].Holder == "player"
                && before.Coins - after.Coins == 3;
            case "observe":
            case "wait": return trace.Verdict.Kind == VerdictKind.Accepted;
            default: return false;
        }
    }

    static bool RegressionObserved(ProductionCase item, DsWorld before, DsWorld after, AiTrace trace)
    {
        if (item.Regression == "custody-110")
            return trace.Proposal?.Str("verb") == "show" && trace.Proposal.Str("target") == "maren"
                && trace.Proposal.Str("item") == "pact_page" && before.Items["pact_page"].Holder == "maren"
                && trace.Verdict.Kind == VerdictKind.Rejected;
        if (item.Regression == "purchase_control-109")
            return trace.Proposal?.Str("verb") == "buy" && trace.Proposal.Str("target") == "marta"
                && trace.Proposal.Str("item") == "smoked_fish" && before.Items["smoked_fish"].Holder == "player"
                && trace.Verdict.Kind == VerdictKind.Rejected;
        return false;
    }

    static void WriteActionV3Summary(string dir, List<Dictionary<string, object>> rows, bool live, string status,
        string stopPhase = null)
    {
        int intent = rows.Count(r => (bool)r["intent_match"]);
        int invariants = rows.Count(r => (bool)r["invariants_passed"]);
        int replay = rows.Count(r => (bool)r["replay_passed"]);
        int privacy = rows.Count(r => (bool)r["privacy_passed"]);
        int nonRegressionTotal = rows.Count(r => r["regression"] == null);
        int nonRegressionEffects = rows.Count(r => r["regression"] == null && (bool)r["effect_match"]);
        int nonRegressionEffectFailures = nonRegressionTotal - nonRegressionEffects;
        var nonRegressionFailureReasons = rows.Where(r => r["regression"] == null && !(bool)r["effect_match"])
            .GroupBy(r => (string)r["reason_code"] ?? "unknown", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var groups = rows.GroupBy(r => (string)r["group"], StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => new { requested = g.Count(), intent = g.Count(r => (bool)r["intent_match"]), effect = g.Count(r => (bool)r["effect_match"]),
                regression_observed = g.Count(r => (bool)r["regression_observed"]) });
        bool keyProbes = new[] { "observe", "wait", "confide", "lure", "repeat_show", "repeat_buy", "take_after_delivery" }
            .All(group => rows.Any(r => (string)r["group"] == group));
        bool promotion = status == "completed" && rows.Count == ActionV3ProductionMaximumRequests
            && intent >= 44 && keyProbes && invariants == 48 && replay == 48 && privacy == 48
            && nonRegressionEffectFailures == 0;
        decimal known = rows.Where(r => r["estimated_usd"] != null).Sum(r => (decimal)r["estimated_usd"]);
        int unknown = rows.Count(r => r["estimated_usd"] == null);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonConvert.SerializeObject(new
        {
            suite = ActionV3ProductionSuite, live, status, model = OpenAiPolicy.Model,
            rules = DsExecutor.RulesVersion, action_prompt = DsActionPrompt.Focused,
            action_perception = DsActionPerception.Current, requested = rows.Count,
            intent, intent_minimum = 44, effects = rows.Count(r => (bool)r["effect_match"]),
            non_regression_total = nonRegressionTotal,
            non_regression_effects = nonRegressionEffects,
            non_regression_effect_failures = nonRegressionEffectFailures,
            non_regression_failure_reasons = nonRegressionFailureReasons,
            invariants, replay, privacy, key_probes = keyProbes,
            promotion_gate_passed = promotion,
            known_estimated_usd = known, unknown_costs = unknown, estimated_usd = unknown == 0 ? (decimal?)known : null,
            stop_phase = stopPhase, groups, results = rows,
            scope = "Game生产接线同构的有限批；反例独立记录，不用最终目标抵消模型意图或规则效果；不代表普遍可靠性。"
        }, Formatting.Indented));
    }

    static object CaseRecord(ProductionCase c) => new
    {
        id = c.Id, episode = c.Episode, step = c.Step + 1, seed = c.Seed, input = c.Input,
        verb = c.Verb, target = c.Target, item = c.Item, place = c.Place, group = c.Group,
        expected_effect = c.ExpectedEffect, regression = c.Regression
    };

    static DsWorldSnapshot Fixture(ProductionCase c)
    {
        var session = new DragonsleepSession(new RuleProvider(), c.Seed, new DsFixedRoll(.9999),
            requireBoundCraftConfirmation: true);
        c.Setup(session);
        return session.CaptureState();
    }

    static List<ProductionCase> ProductionCases()
    {
        var result = new List<ProductionCase>();
        void Add(string episode, int seed, int step, string id, string input, string verb, string target,
            string item, string place, string group, string expected, string regression, Action<DragonsleepSession> setup)
        {
            result.Add(new ProductionCase { Episode = episode, Seed = seed, Step = step, Id = id, Input = input,
                Verb = verb, Target = target ?? "", Item = item ?? "", Place = place ?? "", Group = group,
                ExpectedEffect = expected, Regression = regression, Setup = setup });
        }
        Action<DragonsleepSession> Feed = s =>
        {
            s.World.PlayerPlace = "docks"; s.World.Whelp.Hidden = false; s.World.Whelp.TrustPlayer = .45f;
            s.World.Items["smoked_fish"].Holder = "player"; s.World.PlayerKnows.Add("law:law_whelp_fish");
        };
        Action<DragonsleepSession> Pact = s =>
        {
            s.World.PlayerPlace = "cathedral"; s.World.Items["pact_page"].Holder = "player";
            s.World.PlayerKnows.Add("b_maren_page");
        };
        Action<DragonsleepSession> Buy = s => s.World.PlayerPlace = "market";
        Action<DragonsleepSession> Control = s =>
        {
            s.World.PlayerPlace = "docks"; s.World.Whelp.Hidden = false; s.World.Whelp.TrustPlayer = .45f;
            s.World.Items["smoked_fish"].Holder = "player"; s.World.PlayerKnows.Add("law:law_whelp_fish");
            s.World.Actors["marta"].Place = "docks";
        };
        for (int i = 0; i < 3; i++)
        {
            int seed = 401 + i;
            string suffix = (i + 1).ToString("00");
            Add("feeding-" + suffix, seed, 0, "feeding-" + suffix + "-lure", "让小烬循着手里的熏鱼气味靠近，不把鱼喂掉。", "lure", "whelp", "smoked_fish", "docks", "lure", "lure", null, Feed);
            Add("feeding-" + suffix, seed, 1, "feeding-" + suffix + "-feed", "把同一串熏鱼递到小烬嘴边，让它吃下去。", "feed", "whelp", "smoked_fish", "docks", "feed", "feed", null, Feed);
            Add("feeding-" + suffix, seed, 2, "feeding-" + suffix + "-observe", "不追加喂食，现在只观察船屋周围。", "observe", "", "", "docks", "observe", "observe", null, Feed);
            Add("feeding-" + suffix, seed, 3, "feeding-" + suffix + "-wait", "暂时不做别的，留在原处等候片刻。", "wait", "", "", "docks", "wait", "wait", null, Feed);

            Add("custody-" + suffix, seed, 0, "custody-" + suffix + "-give", "把契约原件交给梅伦保管核验。", "give", "maren", "pact_page", "cathedral", "give", "give", null, Pact);
            Add("custody-" + suffix, seed, 1, "custody-" + suffix + "-show-regression", "我没有取回原件，现在仍向梅伦出示这份契约。", "show", "maren", "pact_page", "cathedral", "repeat_show", "show", "custody-110", Pact);
            Add("custody-" + suffix, seed, 2, "custody-" + suffix + "-take", "把刚才交给梅伦保管的契约原件取回。", "take", "", "pact_page", "cathedral", "take_after_delivery", "take", null, Pact);
            Add("custody-" + suffix, seed, 3, "custody-" + suffix + "-confide", "现在向梅伦托付护送小烬的计划，请她帮忙。", "confide", "maren", "", "cathedral", "confide", "confide", null, Pact);

            Add("purchase-" + suffix, seed, 0, "purchase-" + suffix + "-buy", "付三枚银币，向玛尔妲买下摊上的熏鱼。", "buy", "marta", "smoked_fish", "market", "buy", "buy", null, Buy);
            Add("purchase-" + suffix, seed, 1, "purchase-" + suffix + "-buy-regression", "我刚买下的仍是同一串鱼，再向玛尔妲购买一次。", "buy", "marta", "smoked_fish", "market", "repeat_buy", "buy", "purchase_control-109", Buy);
            Add("purchase-" + suffix, seed, 2, "purchase-" + suffix + "-observe", "不再付钱，先观察眼前的摊位。", "observe", "", "", "market", "observe", "observe", null, Buy);
            Add("purchase-" + suffix, seed, 3, "purchase-" + suffix + "-wait", "事情先放一放，我在原地等候片刻。", "wait", "", "", "market", "wait", "wait", null, Buy);

            Add("control-" + suffix, seed, 0, "control-" + suffix + "-confide", "现在向玛尔妲说明护送小烬的计划，请她帮忙。", "confide", "marta", "", "docks", "confide", "confide", null, Control);
            Add("control-" + suffix, seed, 1, "control-" + suffix + "-lure", "用手里的熏鱼引小烬出来，但不要喂掉它。", "lure", "whelp", "smoked_fish", "docks", "lure", "lure", null, Control);
            Add("control-" + suffix, seed, 2, "control-" + suffix + "-observe", "现在只观察眼前的人和船屋，不开始搜索。", "observe", "", "", "docks", "observe", "observe", null, Control);
            Add("control-" + suffix, seed, 3, "control-" + suffix + "-wait", "暂不买卖或交付，留在这里安静等候。", "wait", "", "", "docks", "wait", "wait", null, Control);
        }
        // 上述四组每组3个episode、每个4步，共48次；返回时按固定episode/step顺序。
        return result.OrderBy(c => c.Episode, StringComparer.Ordinal).ThenBy(c => c.Step).ToList();
    }

    static Dictionary<string, string> SourceHashes(string root)
    {
        string[] paths =
        {
            "dotnet/Town.Research/ActionV3ProductionResearch.cs",
            "dotnet/Town.Research/ActionKernelResearch.cs",
            "dotnet/Town.Research/Program.cs",
            "unity/Assets/Town.Unity/Dragonsleep/DragonsleepGame.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsActionPrompt.cs",
            "unity/Assets/Town.Core/Dragonsleep/DsActionPerception.cs",
            "unity/Assets/Town.PieGame/KernelActionTransaction.cs"
        };
        return paths.ToDictionary(p => p, p => ProductionHash(File.ReadAllBytes(Path.Combine(root, p))), StringComparer.Ordinal);
    }

    static void VerifyRegistration(string root, string path)
    {
        string json = File.ReadAllText(path);
        string expected = File.ReadAllText(path + ".sha256").Trim();
        if (!string.Equals(ProductionHash(Encoding.UTF8.GetBytes(json)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("action_v3_production_registration_hash_mismatch");
        var contract = JObject.Parse(json);
        if ((string)contract["suite"] != ActionV3ProductionSuite || (string)contract["model"] != "gpt-6-luna"
            || (string)contract["action_prompt"] != DsActionPrompt.Focused
            || (string)contract["action_perception"] != DsActionPerception.Current
            || (int)contract["planned_requests"] != ActionV3ProductionMaximumRequests)
            throw new InvalidOperationException("action_v3_production_registration_mismatch");
        var current = SourceHashes(root);
        foreach (var property in ((JObject)contract["source_hashes"]).Properties())
            if (!current.TryGetValue(property.Name, out var value) || value != (string)property.Value)
                throw new InvalidOperationException("action_v3_production_source_changed");
    }

    static string ProductionHash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
    }
}
