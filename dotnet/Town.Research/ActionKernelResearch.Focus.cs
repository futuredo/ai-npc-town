using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.OpenAI;

internal static partial class ActionKernelResearch
{
    internal const string FocusSuite = "ds-action-current-intent-1";

    // 复用066已知失败集，不作为未见措辞泛化；两臂保留相同记忆版本与真实连续状态。
    public static int RunFocus(string root, bool live, string outputDirectory = null, IAiProvider offlineProvider = null,
        bool transfer = false)
    {
        if (live && offlineProvider != null) throw new ArgumentException("live_provider_override_forbidden");
        if (!live && offlineProvider?.IsSimulated == false) throw new ArgumentException("offline_provider_must_be_simulated");
        string dir = outputDirectory ?? Path.Combine(root, ".local", "dragonsleep", "experiments",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")
            + (transfer ? "-action-focus-transfer" : "-action-focus") + (live ? "-live" : "-offline"));
        Directory.CreateDirectory(dir);
        var rows = new List<Dictionary<string, object>>();
        var episodes = new List<object>();
        OpenAiDragonsleepProvider online = null;
        string status = "running";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Manifest(root, dir, live, false, false, DsActionPrompt.Current);
            string manifestPath = Path.Combine(dir, "source-manifest.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            manifest["suite"] = transfer ? FocusTransferSuite : FocusSuite;
            manifest["maximumRequests"] = 96;
            manifest["promptArms"] = JArray.FromObject(new[] { DsActionPrompt.Current, DsActionPrompt.Focused });
            manifest["perceptionVersion"] = DsActionPerception.Current;
            if (transfer) manifest["worldSeeds"] = JArray.FromObject(new[] { 131, 137 });
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            var fixtures = (transfer ? FocusTransferChains() : MemoryChains())
                .SelectMany(chain => (transfer ? new[] { 131, 137 } : new[] { 109, 110, 111 }).Select(seed =>
            {
                var session = new DragonsleepSession(new RuleProvider(), seed);
                chain.Setup(session);
                return new { chain, seed, before = session.CaptureState() };
            })).ToArray();
            string fixtureDirectory = Path.Combine(dir, "fixtures.local-only");
            Directory.CreateDirectory(fixtureDirectory);
            foreach (var fixture in fixtures)
                File.WriteAllText(Path.Combine(fixtureDirectory, fixture.chain.Id + "-" + fixture.seed + ".json"), fixture.before.Json);
            File.WriteAllText(Path.Combine(dir, "focus-contract.pre-registered.json"), JsonConvert.SerializeObject(new
            {
                suite = transfer ? FocusTransferSuite : FocusSuite, maximumRequests = 96, automaticRetries = 0, concurrency = 1,
                promptArms = new[] { DsActionPrompt.Current, DsActionPrompt.Focused },
                perceptionVersion = DsActionPerception.Current,
                scope = transfer ? "提示v3冻结后的独立新措辞及两个新控制链；熟悉机制，不是全新玩法或总体泛化证明。"
                    : "066已知失败集修复复验；非未见泛化、非自主下一步选择、非完整归巢或真人试玩。",
                order = "夹具序号偶数先v2，奇数先v3；固定交替而非随机；链中不重置、补资源或重抽。",
                intent = "verb/target/item严格字符串；place只能原空串或当前ID；最终目标不能抵销错误。",
                criticalGate = transfer ? FocusTransferGate
                    : "每臂observe9、wait3、confide3、无食物lure3分别独立报告；候选仅全部通过才考虑生产启用。",
                contextMatch = "比较世界before及完整冻结感知；仅去recent_player_actions.request_id，不去任何语义历史字段。",
                effect = "observe/wait必须实际Accepted、Roll=1、耗时2且Applied；confide实际尝试耗时1，不以此保证合作。lure仅验意图不保证骰成。",
                repeat = transfer ? "首步须正确动词及真实生效后果；buy还须实际扣三银币，take得鱼不算购买。未形成挑战不补成通过。"
                    : "首步未生效则第二步重复挑战不适用，不补资源制造挑战；与最终目标分开。",
                successfulIntent = transfer ? "四槽及安全引用符合，且FailureCode为空、Kernel committed；结构或宿主拒绝不能计为成功意图。"
                    : null,
                promotion = transfer ? "只记录候选资格，研究器不切换Game；生产启用仍须单独集成与Player验证。" : null,
                fixtures = fixtures.Select(x => new { x.chain.Id, x.seed, beforeHash = x.before.Hash,
                    inputs = x.chain.Inputs, verbs = x.chain.Verbs, targets = x.chain.Targets, items = x.chain.Items })
            }, Formatting.Indented));
            if (live)
            {
                if (OpenAiPolicy.Model != "gpt-6-luna") throw new InvalidOperationException("research_model_mismatch");
                online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),
                    new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            }
            IAiProvider provider = online ?? offlineProvider ?? (IAiProvider)new RuleProvider();
            for (int index = 0; index < fixtures.Length; index++)
            {
                var fixture = fixtures[index];
                string[] arms = index % 2 == 0 ? new[] { DsActionPrompt.Current, DsActionPrompt.Focused }
                    : new[] { DsActionPrompt.Focused, DsActionPrompt.Current };
                foreach (string arm in arms)
                {
                    using var host = new Host(provider, fixture.before, arm, DsActionPerception.Current);
                    string previousAfter = fixture.before.Hash;
                    string episode = fixture.chain.Id + "-" + fixture.seed + "-" + arm;
                    bool repeatPrefixVerified = false;
                    for (int step = 0; step < 4; step++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var before = host.Session.CaptureState();
                        if (before.Hash != previousAfter) throw new InvalidOperationException("focus_sequence_external_mutation");
                        var row = ExecuteMemory(dir, episode, step, fixture.chain, host, cancellation.Token);
                        row["seed"] = fixture.seed;
                        row["action_prompt_version"] = arm;
                        row["perception_version"] = DsActionPerception.Current;
                        row["before_matches_previous_after"] = true;
                        row["expected_verb"] = fixture.chain.Verbs[step];
                        row["context_hash"] = ContextHash(host.Provider.Packet.TaskSnapshot.Json);
                        var trace = host.Session.Traces.Last();
                        bool safeIntent = (bool)row["intent_match"] && (bool)row["raw_laws_known"]
                            && (bool)row["raw_private_markers_absent"];
                        row["focused_effect_match"] = FocusedEffect(fixture.chain.Verbs[step], trace, safeIntent);
                        if (transfer)
                        {
                            var after = host.Session.CaptureState();
                            if (step == 0) repeatPrefixVerified = TransferRepeatPrefix(fixture.chain.RepeatKind,
                                before, after, trace, safeIntent);
                            AnnotateFocusTransfer(row, fixture.chain, step, before, after, trace, safeIntent, repeatPrefixVerified);
                        }
                        rows.Add(row);
                        previousAfter = (string)row["after_hash"];
                        string path = Path.Combine(dir, episode, "step-" + (step + 1), "result.json");
                        File.WriteAllText(path, JsonConvert.SerializeObject(row, Formatting.Indented));
                        WriteFocusSummary(dir, rows, episodes, live, status, transfer);
                        if (!(bool)row["invariants_passed"] || row["provider_error"] != null)
                        { status = "stopped_invariant_or_provider_error"; return 1; }
                    }
                    bool achieved = fixture.chain.Goal(host.Session.World);
                    episodes.Add(new { id = episode, family = fixture.chain.Id, fixture.seed, arm, achieved,
                        finalHash = host.Session.CaptureState().Hash });
                    Console.WriteLine(episode + " 实际目标=" + achieved);
                }
            }
            status = "completed";
            return 0;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException
            || error is OperationCanceledException || error is TimeoutException || error is ArgumentException)
        {
            status = "stopped_" + error.GetType().Name;
            Console.Error.WriteLine("当下动作实验停止；完成样本已保留，不输出异常原文或凭据。");
            return 2;
        }
        finally
        {
            online?.Dispose();
            Console.CancelKeyPress -= cancel;
            WriteFocusSummary(dir, rows, episodes, live, status, transfer);
            Console.WriteLine("报告：" + dir);
        }
    }

    static void WriteFocusSummary(string dir, List<Dictionary<string, object>> rows, List<object> episodes,
        bool live, string status, bool transfer)
    {
        FocusSummary(dir, rows, episodes, live, status);
        if (transfer) CompleteFocusTransferSummary(dir, rows);
    }

    internal static string ContextHash(string perception)
    {
        var view = JObject.Parse(perception);
        foreach (var record in (view["recent_player_actions"] as JArray ?? new JArray()).OfType<JObject>())
            record.Remove("request_id");
        return Protocol.Hash(view.ToString(Formatting.None));
    }

    static bool FocusedEffect(string verb, AiTrace trace, bool intent)
    {
        if (!intent) return false;
        if (verb == "observe" || verb == "wait")
            return trace.FailureCode == null && trace.Verdict.Kind == VerdictKind.Accepted
                && trace.Verdict.Roll == 1 && trace.Receipt?.TimeCost == 2 && trace.Receipt.Applied;
        if (verb == "confide")
            return trace.FailureCode == null && trace.Receipt?.TimeCost == 1 && trace.Receipt.Applied;
        return true; // 其余动作意图、真实规则对照及条件性重复挑战各自单列，不以此布尔值保证骰成。
    }

    static void FocusSummary(string dir, List<Dictionary<string, object>> rows, List<object> episodes, bool live, string status)
    {
        var pairs = new List<object>();
        foreach (var current in rows.Where(r => (string)r["action_prompt_version"] == DsActionPrompt.Focused))
        {
            var previous = rows.SingleOrDefault(r => (string)r["action_prompt_version"] == DsActionPrompt.Current
                && (string)r["family"] == (string)current["family"] && (int)r["seed"] == (int)current["seed"]
                && (int)r["step"] == (int)current["step"]);
            if (previous == null) continue;
            bool sameWorld = (string)previous["before_hash"] == (string)current["before_hash"];
            bool sameContext = (string)previous["context_hash"] == (string)current["context_hash"];
            pairs.Add(new { family = current["family"], seed = current["seed"], step = current["step"], sameWorld, sameContext,
                category = !sameWorld ? "world_diverged" : sameContext ? "same_world_and_history" : "history_diverged" });
        }
        var arms = new[] { DsActionPrompt.Current, DsActionPrompt.Focused }.Select(version =>
        {
            var subset = rows.Where(r => (string)r["action_prompt_version"] == version).ToArray();
            var probes = new[] { "observe", "wait", "confide", "lure" }.Select(verb => new
            {
                verb, total = subset.Count(r => (string)r["expected_verb"] == verb),
                passed = subset.Count(r => (string)r["expected_verb"] == verb && (bool)r["focused_effect_match"])
            }).ToArray();
            return new { version, requested = subset.Length,
                intent = subset.Count(r => (bool)r["intent_match"] && (bool)r["raw_laws_known"] && (bool)r["raw_private_markers_absent"]),
                probes, criticalGatePassed = subset.Length == 48 && probes.All(p => p.total > 0 && p.total == p.passed) };
        }).ToArray();
        int unknown = rows.Count(r => r["estimated_usd"] == null);
        decimal known = rows.Where(r => r["estimated_usd"] != null).Sum(r => (decimal)r["estimated_usd"]);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonConvert.SerializeObject(new
        {
            suite = FocusSuite, live, status, maximumRequests = 96, model = OpenAiPolicy.Model, rules = DsExecutor.RulesVersion,
            requested = rows.Count, invariants = rows.Count(r => (bool)r["invariants_passed"]),
            replayApplicable = rows.Count(r => r["strict_replay"] != null),
            replayPassed = rows.Count(r => r["strict_replay"] is bool passed && passed),
            inputTokens = rows.Sum(r => (int?)r["input_tokens"] ?? 0), outputTokens = rows.Sum(r => (int?)r["output_tokens"] ?? 0),
            knownEstimatedUsd = known, unknownCosts = unknown, estimatedUsd = unknown == 0 ? (decimal?)known : null,
            arms, pairs, episodes, results = rows,
            scope = "已知失败集的有限连续玩法对照；同世界且同语义历史节点才作同上下文比较，不抵销逐步误操作。"
        }, Formatting.Indented));
    }
}
