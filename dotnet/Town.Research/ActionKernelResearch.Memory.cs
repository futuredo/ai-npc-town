using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.PieGame;

internal static partial class ActionKernelResearch
{
    internal const string MemorySuite = "ds-action-experience-memory-1";

    sealed class MemoryChain
    {
        internal string Id;
        internal string RepeatKind;
        internal string[] Inputs, Verbs, Targets, Items;
        internal Action<DragonsleepSession> Setup;
        internal Func<DsWorld, bool> Goal;
    }

    static MemoryChain[] MemoryChains() => new[]
    {
        new MemoryChain
        {
            Id = "feeding",
            Inputs = new[] { "把我随身的熏鱼喂给小烬吃。", "再把刚才同一串鱼喂给它，不是取另一份。",
                "这回不用任何食物，安静退开，轻声耐心招呼小烬靠近。", "现在带上小烬跟我同行。" },
            Verbs = new[] { "feed", "feed", "lure", "lead" },
            Targets = new[] { "whelp", "whelp", "whelp", "whelp" },
            Items = new[] { "smoked_fish", "smoked_fish", "", "" },
            Setup = s =>
            {
                s.World.PlayerPlace = "docks";
                s.World.Whelp.Hidden = false;
                s.World.Whelp.Fear = .2f;
                s.World.Items["smoked_fish"].Holder = "player";
                s.World.PlayerKnows.Add("law:law_whelp_fish");
            },
            Goal = w => w.Whelp.Following && w.Whelp.CapturedBy == null
        },
        new MemoryChain
        {
            Id = "custody",
            Inputs = new[] { "把随身的契约原件交给梅伦保管核验。", "把刚才同一份原件展开给她看；我没有从她那里拿回来。",
                "现在不额外托付，只观察眼前情况。", "现在向梅伦托付护送小烬，请她帮忙。" },
            Verbs = new[] { "give", "show", "observe", "confide" },
            Targets = new[] { "maren", "maren", "", "maren" },
            Items = new[] { "pact_page", "pact_page", "", "" },
            Setup = s =>
            {
                s.World.PlayerPlace = "cathedral";
                s.World.Items["pact_page"].Holder = "player";
                s.World.PlayerKnows.Add("b_maren_page");
                s.World.Actors["maren"].Trust = .05f;
                s.World.Whelp.Place = "cathedral";
                s.World.Whelp.Hidden = true;
                s.World.Whelp.Following = true;
                s.World.Whelp.TrustPlayer = .6f;
                s.World.Flags["whelp_hidden_by_player"] = "玩家亲历夹具";
            },
            Goal = w => w.Flags.ContainsKey("ally:maren") && w.Items["pact_page"].Holder == "maren"
        },
        new MemoryChain
        {
            Id = "illumination",
            Inputs = new[] { "使用随身蓝晶路灯照亮墓穴石缝。", "再使用刚才同一盏灯照亮这里，不换地方或对象。",
                "先观察石缝，不再施放灯的效果。", "安静耐心搜索刚才照亮的石缝。" },
            Verbs = new[] { "use", "use", "observe", "search" },
            Targets = new[] { "", "", "", "" }, Items = new[] { "crafted_1", "crafted_1", "", "" },
            Setup = s =>
            {
                s.World.PlayerPlace = "catacombs";
                s.World.Items["crafted_1"] = new DsItem { Id = "crafted_1", Name = "蓝晶路灯", Holder = "player", Crafted = true,
                    Tags = new HashSet<string> { "light", "potency:0.4" } };
            },
            Goal = w => w.Flags.ContainsKey("lit:catacombs") && w.Items["pact_page"].Holder == "player"
        },
        new MemoryChain
        {
            Id = "purchase_control",
            Inputs = new[] { "付三银币，向玛尔妲买走待售熏鱼。", "再向她买刚才同一串鱼，不是新的一份。",
                "现在只看看摊位，不继续付钱。", "在原地等候片刻，不做别的。" },
            Verbs = new[] { "buy", "buy", "observe", "wait" },
            Targets = new[] { "marta", "marta", "", "" }, Items = new[] { "smoked_fish", "smoked_fish", "", "" },
            Setup = _ => { }, Goal = w => w.Coins == 9 && w.Items["smoked_fish"].Holder == "player"
        }
    };

    public static int RunMemory(string root, bool live, string outputDirectory = null, IAiProvider offlineProvider = null,
        int startFixture = 0)
    {
        if (startFixture < 0 || startFixture > 11) throw new ArgumentOutOfRangeException(nameof(startFixture));
        if (live && offlineProvider != null) throw new ArgumentException("live_provider_override_forbidden");
        if (!live && offlineProvider?.IsSimulated == false) throw new ArgumentException("offline_provider_must_be_simulated");
        string dir = outputDirectory ?? Path.Combine(root, ".local", "dragonsleep", "experiments",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")
            + "-action-memory" + (startFixture == 0 ? "" : "-continuation") + (live ? "-live" : "-offline"));
        Directory.CreateDirectory(dir);
        var rows = new List<Dictionary<string, object>>();
        var episodes = new List<object>();
        var snapshots = new Dictionary<string, string>();
        OpenAiDragonsleepProvider online = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string status = "running";
        try
        {
            Manifest(root, dir, live, false, false, DsActionPrompt.Current);
            string manifestPath = Path.Combine(dir, "source-manifest.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            manifest["suite"] = MemorySuite;
            manifest["maximumRequests"] = (12 - startFixture) * 8;
            manifest["startFixture"] = startFixture;
            manifest["perceptionVersions"] = JArray.FromObject(new[] { DsActionPerception.Legacy, DsActionPerception.Current });
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            CheckHiddenDifference(dir, DsActionPrompt.Current);
            var chains = MemoryChains();
            var fixtures = chains.SelectMany(chain => new[] { 109, 110, 111 }.Select(seed =>
            {
                var fixture = new DragonsleepSession(new RuleProvider(), seed);
                chain.Setup(fixture);
                string fixturePath = Path.Combine(dir, "fixtures.local-only");
                Directory.CreateDirectory(fixturePath);
                File.WriteAllText(Path.Combine(fixturePath, chain.Id + "-" + seed + ".json"), fixture.CaptureState().Json);
                return new { chain, seed, before = fixture.CaptureState() };
            })).ToArray();
            File.WriteAllText(Path.Combine(dir, "memory-contract.pre-registered.json"), JsonConvert.SerializeObject(new
            {
                suite = MemorySuite, maximumRequests = (12 - startFixture) * 8, startFixture,
                callsPerChain = 4, automaticRetries = 0, concurrency = 1,
                comparison = "host_rejected仅按公开Kernel映射核对：原Failure一致、原执行器评估一致、Receipt仅归一status；世界及其余回执逐字一致。",
                actionPromptVersion = DsActionPrompt.Current,
                control = DsActionPerception.Legacy, memory = DsActionPerception.Current,
                intervention = "感知v2的四条亲历记录加配套历史说明指令；不是只增减数据字段的纯因果对照。",
                order = "偶数链先无记忆，奇数链先记忆；固定种子，不按响应重排、不补资源、不重置链中世界。",
                intent = "verb/target/item严格字符串；place为空或当前ID。意图、规则不变量和真实玩法目标分开。",
                matched = "每个后续节点核对两臂before是否相同；分叉后的差异不单归因于记忆。",
                repeatProbe = "第二步仅在首步实际消耗/交付/施效/买到后算有效重复挑战；未形成仍保留，不能用最终目标代替挑战成绩。",
                goals = new
                {
                    feeding = "实际Following且CapturedBy为空",
                    custody = "实际ally:maren且契约仍由maren持有",
                    illumination = "实际lit:catacombs且契约已归player",
                    purchase_control = "实际Coins为9且熏鱼归player"
                },
                fixtures = fixtures.Select(x => new { id = x.chain.Id, x.seed, beforeHash = x.before.Hash,
                    inputs = x.chain.Inputs, verbs = x.chain.Verbs, targets = x.chain.Targets, items = x.chain.Items })
            }, Formatting.Indented));
            if (live)
            {
                if (OpenAiPolicy.Model != "gpt-6-luna") throw new InvalidOperationException("research_model_mismatch");
                online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),
                    new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            }
            IAiProvider provider = online ?? offlineProvider ?? (IAiProvider)new RuleProvider();
            for (int index = startFixture; index < fixtures.Length; index++)
            {
                var fixture = fixtures[index];
                string[] arms = index % 2 == 0 ? new[] { DsActionPerception.Legacy, DsActionPerception.Current }
                    : new[] { DsActionPerception.Current, DsActionPerception.Legacy };
                foreach (string arm in arms)
                {
                    using var host = new Host(provider, fixture.before, DsActionPrompt.Current, arm);
                    string previousAfter = fixture.before.Hash;
                    string episodeId = fixture.chain.Id + "-" + fixture.seed + "-" + arm;
                    for (int step = 0; step < 4; step++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var before = host.Session.CaptureState();
                        if (before.Hash != previousAfter) throw new InvalidOperationException("memory_sequence_external_mutation");
                        var row = ExecuteMemory(dir, episodeId, step, fixture.chain, host, cancellation.Token);
                        row["seed"] = fixture.seed;
                        row["perception_version"] = arm;
                        row["before_matches_previous_after"] = true;
                        rows.Add(row);
                        previousAfter = (string)row["after_hash"];
                        snapshots[fixture.chain.Id + "-" + fixture.seed + "-" + step + "-" + arm] = (string)row["before_hash"];
                        MemorySummary(dir, rows, episodes, snapshots, live, status, startFixture);
                        if (!(bool)row["invariants_passed"] || row["provider_error"] != null)
                        { status = "stopped_invariant_or_provider_error"; return 1; }
                    }
                    bool achieved = fixture.chain.Goal(host.Session.World);
                    episodes.Add(new { id = episodeId, family = fixture.chain.Id, fixture.seed, arm, achieved,
                        finalHash = host.Session.CaptureState().Hash, final_dawn = host.Session.World.FinalDawn,
                        scope = "四步中途玩法链，不是完整归巢或真人试玩。" });
                    Console.WriteLine(episodeId + " 实际目标=" + achieved);
                }
            }
            status = "completed";
            return 0;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException
            || error is OperationCanceledException || error is TimeoutException || error is ArgumentException)
        {
            status = "stopped_" + error.GetType().Name;
            Console.Error.WriteLine("记忆实验停止；不输出异常原文或凭据，保留已完成样本。");
            return 2;
        }
        finally
        {
            online?.Dispose(); Console.CancelKeyPress -= cancel;
            MemorySummary(dir, rows, episodes, snapshots, live, status, startFixture);
            Console.WriteLine("报告：" + dir);
        }
    }

    static Dictionary<string, object> ExecuteMemory(string dir, string episode, int step, MemoryChain chain, Host host, CancellationToken ct)
    {
        string path = Path.Combine(dir, episode, "step-" + (step + 1));
        Directory.CreateDirectory(path);
        var before = host.Session.CaptureState();
        int recordsBefore = host.Transaction.PrivateRecordList.Count;
        int callsBefore = host.Provider.Calls;
        var trace = host.Act(chain.Inputs[step], ct);
        var after = host.Session.CaptureState();
        var frames = host.Transaction.PrivateRecordList.Skip(recordsBefore).ToArray();
        File.WriteAllText(Path.Combine(path, "anchors.local.json"), JsonConvert.SerializeObject(frames.Select(f => f.Digest)));
        File.WriteAllText(Path.Combine(path, "replay.private.json"), JsonConvert.SerializeObject(frames, Formatting.Indented));
        var anchors = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(Path.Combine(path, "anchors.local.json")));
        var reloaded = JsonConvert.DeserializeObject<PieGame.AI.Kernel.ReplayFrame[]>(File.ReadAllText(Path.Combine(path, "replay.private.json")));
        bool replay = reloaded.Select((frame, i) => Replay(before, after, host.Provider.Packet, frame, anchors[i])).All(x => x);
        host.Recorded.Result = trace.Provider;
        var legacy = host.Owner.Pump(host.Legacy.ActAsync(chain.Inputs[step]));
        bool oldRules = host.Legacy.CaptureState().Json == after.Json && MemoryRulesMatch(before, legacy, trace);
        bool references = trace.Proposal == null || trace.Proposal.StrList("laws").All(law =>
            host.Provider.Packet.TaskSnapshot.ToDictionary().StrList("known_laws").Any(text => text.StartsWith(law + "：", StringComparison.Ordinal)));
        bool rawSafe = new[] { HiddenActor, HiddenWhelp, HiddenBelief }.All(marker => !(trace.Provider?.RawText ?? "").Contains(marker));
        bool noCostUnchanged = trace.Verdict.Kind != VerdictKind.Rejected || trace.Verdict.Roll.HasValue || trace.Receipt?.TimeCost != 0
            || before.Json == after.Json && trace.Changes.Count == 0 && trace.Receipt?.Applied == false;
        bool invariant = host.Provider.Calls == callsBefore + 1 && host.Provider.PrivateMarkersAbsent && oldRules && replay
            && host.Kernel.Capture() == after.Json && noCostUnchanged
            && (trace.KernelCode != "committed" || frames.Length == 1);
        bool intent = SemanticActionMatches(trace.Proposal, chain.Verbs[step], chain.Targets[step], chain.Items[step], before.Restore().PlayerPlace);
        var prior = before.Restore();
        string repeatKind = chain.RepeatKind ?? chain.Id;
        bool repeatApplicable = step == 1 && (repeatKind == "feeding" ? prior.Items["smoked_fish"].Holder == "consumed"
            : repeatKind == "custody" ? prior.Items["pact_page"].Holder == "maren"
            : repeatKind == "illumination" ? prior.Flags.ContainsKey("used:crafted_1:catacombs:")
            : repeatKind == "purchase_control" && prior.Items["smoked_fish"].Holder == "player");
        string repeatReason = repeatKind == "feeding" ? "item_missing" : repeatKind == "custody" ? "item_not_held"
            : repeatKind == "illumination" ? "item_effect_already_used" : "seller_absent";
        bool? repeatPassed = repeatApplicable ? intent && trace.Verdict.Kind == VerdictKind.Rejected
            && trace.Verdict.ReasonCode.Code == repeatReason && !trace.Verdict.Roll.HasValue && trace.Receipt?.TimeCost == 0
            && before.Json == after.Json && trace.Changes.Count == 0 : (bool?)null;
        File.WriteAllText(Path.Combine(path, "before.local.json"), before.Json);
        File.WriteAllText(Path.Combine(path, "after.local.json"), after.Json);
        File.WriteAllText(Path.Combine(path, "trace.json"), MiniJson.Write(trace.ToRecord(), true));
        File.WriteAllText(Path.Combine(path, "payload.json"), JsonConvert.SerializeObject(host.Provider.Payload, Formatting.Indented));
        File.WriteAllText(Path.Combine(path, "frozen-prompt.json"), Encoding.UTF8.GetString(ActionPromptCodec.Encode(host.Provider.Packet)));
        var row = new Dictionary<string, object>
        {
            ["id"] = episode + "/step-" + (step + 1), ["family"] = chain.Id, ["step"] = step + 1,
            ["input"] = chain.Inputs[step], ["before_hash"] = before.Hash, ["after_hash"] = after.Hash,
            ["intent_match"] = intent, ["raw_laws_known"] = references, ["raw_private_markers_absent"] = rawSafe,
            ["repeat_probe_applicable"] = repeatApplicable, ["repeat_probe_passed"] = repeatPassed,
            ["zero_cost_rejection_unchanged"] = noCostUnchanged,
            ["invariants_passed"] = invariant, ["same_raw_rules_exact"] = oldRules, ["ready_frames"] = frames.Length,
            ["strict_replay"] = frames.Length == 0 ? (bool?)null : replay,
            ["provider_error"] = trace.Provider?.Error, ["kernel_code"] = trace.KernelCode,
            ["verdict"] = trace.Verdict.Kind.ToString(), ["reason_code"] = trace.Verdict.ReasonCode.StableCode,
            ["roll"] = trace.Verdict.Roll, ["time_cost"] = trace.Receipt?.TimeCost,
            ["fish_holder"] = host.Session.World.Items["smoked_fish"].Holder,
            ["pact_holder"] = host.Session.World.Items["pact_page"].Holder,
            ["coins"] = host.Session.World.Coins, ["following"] = host.Session.World.Whelp.Following,
            ["ally_maren"] = host.Session.World.Flags.ContainsKey("ally:maren"),
            ["private_replay_scope"] = "ready候选严格重算；不是玩家意图、历史提交来源或Unity实况回放证明。",
            ["input_tokens"] = trace.Provider?.InputTokens, ["output_tokens"] = trace.Provider?.OutputTokens,
            ["estimated_usd"] = trace.Provider?.EstimatedUsd,
            ["memory_count"] = host.Provider.Packet.TaskSnapshot.ToDictionary().TryGetValue("recent_player_actions", out var history)
                ? ((List<object>)history).Count : 0
        };
        File.WriteAllText(Path.Combine(path, "result.json"), JsonConvert.SerializeObject(row, Formatting.Indented));
        return row;
    }

    internal static bool MemoryRulesMatch(DsWorldSnapshot before, AiTrace legacy, AiTrace trace)
    {
        if (trace.KernelCode != "host_rejected")
            return MiniJson.Write(legacy.Receipt?.ToRecord()) == MiniJson.Write(trace.Receipt?.ToRecord())
                && legacy.FailureCode == trace.FailureCode && legacy.Verdict.Kind == trace.Verdict.Kind
                && legacy.Verdict.Reason == trace.Verdict.Reason;

        // 061 已声明的失败映射不是规则差异；不能宽泛忽略错误码、回执或真实后果。
        if (string.IsNullOrEmpty(legacy.FailureCode) || legacy.FailureCode != trace.FailureCode ||
            legacy.Verdict.Kind != VerdictKind.Rejected || trace.Verdict.Kind != VerdictKind.Rejected ||
            trace.Verdict.Reason != "行动未通过结构或规则校验，未提交。" ||
            legacy.Receipt?.Status != legacy.FailureCode || trace.Receipt?.Status != "host_rejected" ||
            legacy.Receipt.Applied || trace.Receipt.Applied || legacy.Receipt.TimeCost != 0 || trace.Receipt.TimeCost != 0 ||
            legacy.Changes.Count != 0 || trace.Changes.Count != 0 || trace.Proposal == null)
            return false;
        var evaluation = new DsExecutor().Prepare(before.Restore(), before.Spent, before.PhaseBudget,
            AiTaskKind.Adjudicate, "player", trace.RequestId, trace.Proposal).EvaluationSnapshot;
        if (evaluation.FailureCode != legacy.FailureCode || evaluation.Reason != legacy.Verdict.Reason ||
            evaluation.Kind != legacy.Verdict.Kind || evaluation.Roll != legacy.Verdict.Roll ||
            evaluation.Difficulty != legacy.Verdict.Difficulty || trace.Verdict.Roll != legacy.Verdict.Roll ||
            trace.Verdict.Difficulty != legacy.Verdict.Difficulty ||
            evaluation.ReasonCode.StableCode != legacy.Verdict.ReasonCode.StableCode ||
            trace.Verdict.ReasonCode.StableCode != legacy.Verdict.ReasonCode.StableCode ||
            MiniJson.Write(evaluation.Notes.ToArray()) != MiniJson.Write(legacy.Verdict.Notes) ||
            MiniJson.Write(legacy.Verdict.Notes) != MiniJson.Write(trace.Verdict.Notes) ||
            MiniJson.Write(legacy.ValidationIssues) != MiniJson.Write(trace.ValidationIssues)) return false;
        var projected = trace.Receipt.ToRecord();
        projected["status"] = legacy.Receipt.Status;
        return MiniJson.Write(legacy.Receipt.ToRecord()) == MiniJson.Write(projected);
    }

    static void MemorySummary(string dir, List<Dictionary<string, object>> rows, List<object> episodes,
        Dictionary<string, string> snapshots, bool live, string status, int startFixture)
    {
        var matched = new List<object>();
        foreach (var row in rows.Where(row => (string)row["perception_version"] == DsActionPerception.Current))
        {
            string prefix = row["family"] + "-" + row["seed"] + "-" + ((int)row["step"] - 1);
            if (snapshots.TryGetValue(prefix + "-" + DsActionPerception.Legacy, out var old))
                matched.Add(new { id = prefix, sameBefore = old == (string)row["before_hash"] });
        }
        int unknown = rows.Count(row => row["estimated_usd"] == null);
        decimal known = rows.Where(row => row["estimated_usd"] != null).Sum(row => (decimal)row["estimated_usd"]);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonConvert.SerializeObject(new
        {
            suite = MemorySuite, live, status, maximumRequests = (12 - startFixture) * 8, startFixture,
            model = OpenAiPolicy.Model, rules = DsExecutor.RulesVersion,
            requested = rows.Count, invariants = rows.Count(row => (bool)row["invariants_passed"]),
            intentChecks = rows.Count(row => (bool)row["intent_match"] && (bool)row["raw_laws_known"] && (bool)row["raw_private_markers_absent"]),
            replayApplicable = rows.Count(row => row["strict_replay"] != null),
            replayPassed = rows.Count(row => row["strict_replay"] is bool passed && passed),
            inputTokens = rows.Sum(row => (int?)row["input_tokens"] ?? 0), outputTokens = rows.Sum(row => (int?)row["output_tokens"] ?? 0),
            knownEstimatedUsd = known, unknownCosts = unknown, estimatedUsd = unknown == 0 ? (decimal?)known : null,
            matchedStates = matched, episodes, results = rows,
            scope = "两臂真实连续四步；亲历记忆不是当前物权。分叉后不作为同状态因果对照，不代表完整玩法或普遍正确率。"
        }, Formatting.Indented));
    }
}
