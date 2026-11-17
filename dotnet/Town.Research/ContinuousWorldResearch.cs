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
using Town.Research.Execution;

/// <summary>固定 T3 批次的研究护栏；不是新的玩法执行器，不接管原预算账本。</summary>
internal static partial class ContinuousWorldResearch
{
    internal const string Suite = "ds-continuous-world-t3-2";
    internal const int Nights = 3, PerSeedLimit = 25, BatchLimit = 75, PayloadLimit = 12 * 1024, OutputLimit = 768;
    // 返回副本，避免调用者改写本批样本。
    internal static int[] Seeds => new[] { 18, 29, 41 };
    internal static string Folder(string root) => Path.Combine(root, ".local", "continuous-world", Suite);
    internal static string Hash(string text) => DsWorldSnapshot.Digest(text);
    internal static string Body(PromptPacket p) => OpenAiDragonsleepProvider.BuildPayload(p,
        OpenAiRunProfile.SixMechanismResearch).ToString(Formatting.None);
    internal static PromptPacket Packet(DsWorldSnapshot before, AiTaskKind kind, string actor, string input) =>
        kind == AiTaskKind.Dialogue
            ? ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(before.Restore(), actor), input)
            : ContextBuilder.BuildTask(TaskPerceptionView.Capture(before.Restore(), kind, actor,
                before.PhaseBudget - before.Spent), input);

    internal static SortedDictionary<string,string> Sources(string root) => T3Snapshot.Sources(root);

    static JObject Artifacts(string root, string dir)
    {
        // 只给链接到测试程序集的纯离线隔离目录使用；生产程序集、正式目录和live绝不接受模拟身份。
        if (typeof(ContinuousWorldResearch).Assembly.GetName().Name != "Town.Research" &&
            Path.GetFullPath(dir) != Path.GetFullPath(Folder(root))) return new JObject { ["test_only"] = true };
        return ContinuousWorldArtifacts.Require(root);
    }

    internal static void Create(string file, object value)
        => CreateText(file, JsonConvert.SerializeObject(value, Formatting.Indented));

    static void CreateText(string file, string text)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
        writer.Flush(); stream.Flush(true);
    }

    internal static void Append(string file, object value)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(JsonConvert.SerializeObject(value, Formatting.None));
        writer.Flush(); stream.Flush(true);
    }

    static void Require(bool condition, string code)
    { if (!condition) throw new InvalidOperationException(code); }

    internal static int Prepare(string root, string directory = null)
    {
        var artifacts = Artifacts(root, directory ?? Folder(root));
        Require(OpenAiPolicy.Model == "gpt-6-luna" && OpenAiPolicy.MaxRequestBytes == PayloadLimit &&
            OpenAiRunProfile.SixMechanismResearch.MaxOutputTokens == OutputLimit, "research_policy_changed");
        string dir = directory ?? Folder(root);
        Directory.CreateDirectory(dir);
        // Prepare 与 live 争用同一把批锁，永不覆写预注册或 STOPPED。
        using var gate = new FileStream(Path.Combine(dir, "live.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Require(!File.Exists(Path.Combine(dir, "STOPPED")), "research_stopped");
        Require(!File.Exists(Path.Combine(dir, "pre-registered.json")) &&
            !File.Exists(Path.Combine(dir, "pre-registered.sha256")) && !File.Exists(Path.Combine(dir, "STARTED")),
            "pre_registration_already_exists");
        var baseline = VerifyBaseline(root, dir);
        var registration = new
        {
            suite = Suite, seeds = Seeds, nights = Nights, per_seed_limit = PerSeedLimit, maximum_requests = BatchLimit,
            request_formula = "6 initial + 5 residents * 3 nights + 3 nightly GM + 1 final-dawn GM = 25 per seed",
            model = OpenAiPolicy.Model, rules_version = DsExecutor.RulesVersion,
            max_output_tokens = OpenAiRunProfile.SixMechanismResearch.MaxOutputTokens,
            max_payload_bytes = OpenAiPolicy.MaxRequestBytes, concurrency = 1, retries = 0,
            artifacts, stop_scope = "batch", craft_dependency_stop_scope = "batch",
            baseline_sha256 = Hash(File.ReadAllText(Path.Combine(dir, "offline", "summary.json"))),
            offline_sources_sha256 = Hash(baseline["sources"].ToString(Formatting.None)),
            stopping = "Any provider Error/null/blank/exception/timeout, payload/privacy/registration/artifact mismatch, limit, strict replay or receipt invariant stops the entire batch. No retry, no next seed, no resampling or resume. A normal craft dependency failure also stops the entire batch, separately classified, without fabricated items. Schema/reference failures retain their denominator; they alone are not invariants.",
            viewpoints = new[] { "Dialogue: frozen NpcPerceptionView", "Adjudicate: player-visible only",
                "Craft: available materials only", "AgentPlan: own beliefs only", "Direct: aggregates/public events only" },
            local_only = "before/after truth, cross-owner root/hops/source/owner chain and differential private canaries never added to model payloads",
            observations = new[] { "each resident and nightly/final GM: raw schema/reference, actual rule verdict/receipt/effects",
                "strict replay, same-payload/private differential", "GM whitelist/quota/final mother separate",
                "local source-chain provenance and ending", "Accepted/committed is not semantic intent correctness; manual review remains unset" },
            reservation_usd_per_attempt = OpenAiPolicy.ReservationUsdPerRequest,
            maximum_reservation_usd = BatchLimit * OpenAiPolicy.ReservationUsdPerRequest,
            cost_rule = "Reservation is not actual cost. Usage + OpenAiPolicy prices yield an estimate only. Missing usage means unknown, never zero; offline simulated usage is not real usage.",
            original_budget = "The existing FileRequestBudget(SixMechanismResearch) remains in the inner online provider, unchanged and additive to these attempt limits.",
            sources = Sources(root)
        };
        Create(Path.Combine(dir, "pre-registered.json"), registration);
        CreateText(Path.Combine(dir, "pre-registered.sha256"), Hash(File.ReadAllText(Path.Combine(dir, "pre-registered.json"))) + "\n");
        Console.WriteLine("连续世界预注册：" + Path.Combine(dir, "pre-registered.json"));
        return 0;
    }

    internal static JObject VerifyRegistration(string root, string dir)
    {
        string text = File.ReadAllText(Path.Combine(dir, "pre-registered.json"));
        Require(Hash(text) == File.ReadAllText(Path.Combine(dir, "pre-registered.sha256")).Trim(),
            "pre_registration_changed");
        var r = JObject.Parse(text);
        Require((string)r["suite"] == Suite && r["seeds"] is JArray seeds && seeds.Values<int>().SequenceEqual(Seeds) &&
            (int?)r["nights"] == Nights && (int?)r["per_seed_limit"] == PerSeedLimit && (int?)r["maximum_requests"] == BatchLimit &&
            (string)r["model"] == "gpt-6-luna" && OpenAiPolicy.Model == "gpt-6-luna" &&
            (string)r["rules_version"] == DsExecutor.RulesVersion && (int?)r["concurrency"] == 1 && (int?)r["retries"] == 0 &&
            (int?)r["max_payload_bytes"] == PayloadLimit && OpenAiPolicy.MaxRequestBytes == PayloadLimit &&
            (int?)r["max_output_tokens"] == OutputLimit && OpenAiRunProfile.SixMechanismResearch.MaxOutputTokens == OutputLimit,
            "pre_registration_header_changed");
        Require(JToken.DeepEquals(r["sources"], JObject.FromObject(Sources(root))), "registered_source_changed");
        Require((string)r["stop_scope"] == "batch" && (string)r["craft_dependency_stop_scope"] == "batch" &&
            JToken.DeepEquals(r["artifacts"], Artifacts(root, dir)), "registered_artifact_or_stop_policy_changed");
        var baseline = VerifyBaseline(root, dir);
        Require((string)r["baseline_sha256"] == Hash(File.ReadAllText(Path.Combine(dir, "offline", "summary.json"))) &&
            (string)r["offline_sources_sha256"] == Hash(baseline["sources"].ToString(Formatting.None)), "registered_baseline_changed");
        return r;
    }

    static JObject VerifyBaseline(string root, string dir)
    {
        var baseline = JObject.Parse(File.ReadAllText(Path.Combine(dir, "offline", "summary.json")));
        Require((string)baseline["suite"] == Suite && (string)baseline["status"] == "baseline_confirmed" &&
            baseline["seeds"] is JArray seeds && seeds.Values<int>().SequenceEqual(Seeds) &&
            (int?)baseline["nights"] == Nights && baseline["comparisons"] is JArray rows && rows.Count == Seeds.Length &&
            rows.Select(x => (int?)x["seed"]).SequenceEqual(Seeds.Select(s => (int?)s)) &&
            rows.All(x => (bool?)x["same_world_and_receipts"] == true && (int?)x["reference_exit"] == 0 &&
                (int?)x["guarded_exit"] == 0), "offline_baseline_required");
        Require(JToken.DeepEquals(baseline["sources"], JObject.FromObject(Sources(root))), "offline_baseline_source_changed");
        Require(JToken.DeepEquals(baseline["artifacts"], Artifacts(root, dir)), "offline_baseline_artifact_changed");
        foreach (var row in (JArray)baseline["comparisons"])
        {
            string name = "seed-" + (int)row["seed"];
            foreach (string arm in new[] { "reference", "guarded" })
            {
                string file = Path.Combine(dir, "offline", arm, name, "journey.local.json");
                Require(Hash(File.ReadAllText(file)) == (string)row[arm + "_sha256"], "offline_baseline_evidence_changed");
            }
        }
        return baseline;
    }

    internal static async Task<int> Run(string root, bool live)
    {
        ContinuousWorldArtifacts.Require(root); // 在任何key/provider/budget构造前拒绝普通--no-build或错误制品。
        if (!live) return await RunOffline(root, Folder(root));
        string dir = Folder(root);
        Require(Directory.Exists(dir), "pre_registration_required");
        // 锁从校验到最后汇总一直持有；重复启动不会读取密钥或创建另一个批目录。
        using var provider = new GuardedProvider(null, true, dir, () => VerifyRegistration(root, dir));
        provider.ClaimBatch();
        var rows = new List<JObject>();
        string status = "stopped";
        try
        {
            using var online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),
                new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            ResponseShapeDiagnostics.Attach(online, dir); // 071：整批只 Attach 一次。
            provider.Bind(online);
            int result = await RunBatch(root, true, Path.Combine(dir, "live"), provider, rows);
            status = result == 0 ? "completed" : "stopped";
            if (result == 0) Create(Path.Combine(dir, "COMPLETED"), new { suite = Suite, utc = DateTime.UtcNow });
            return result;
        }
        catch
        {
            provider.Stop("research_exception");
            throw;
        }
        finally
        {
            // 续跑拒绝时不覆盖第一次运行的汇总。
            string summary = Path.Combine(dir, "live-summary.json");
            if (!File.Exists(summary)) Create(summary, Summary(true, status, provider, rows));
        }
    }

    internal static async Task<int> RunBatch(string root, bool live, string output, GuardedProvider provider, List<JObject> rows)
    {
        Directory.CreateDirectory(output);
        foreach (int seed in Seeds)
        {
            provider.CheckReady();
            provider.BeginSeed(seed);
            string seedDir = Path.Combine(output, "seed-" + seed);
            var audit = new JourneyAudit(provider, live, seed, rows);
            var options = new Journey.RunOptions
            {
                Directory = seedDir, Provider = provider, Context = provider.Context, Observe = audit.Observe
            };
            int result = await Journey.Run(root, live, seed, Nights, options: options);
            Create(Path.Combine(seedDir, "outcome.local.json"), new { outcome = options.Outcome, stop_scope = "batch" });
            if (result != 0 || provider.Stopped)
            {
                provider.Stop(provider.Stopped ? "research_stopped" : options.Outcome ?? "journey_stopped");
                WriteSeedReport(seedDir, seed, rows, provider);
                return 1;
            }
            WriteSeedReport(seedDir, seed, rows, provider);
        }
        return 0;
    }

    internal static async Task<int> RunOffline(string root, string dir)
    {
        var artifacts = Artifacts(root, dir);
        Directory.CreateDirectory(dir);
        string output = Path.Combine(dir, "offline");
        Require(!Directory.Exists(output), "offline_already_exists_no_resampling");
        Directory.CreateDirectory(output);
        var frozenSources = JObject.FromObject(Sources(root));
        using var provider = new GuardedProvider(new RuleProvider(), false, output);
        var rows = new List<JObject>(); var comparisons = new List<object>();
        string status = "stopped";
        try
        {
            foreach (int seed in Seeds)
            {
                provider.CheckReady(); provider.BeginSeed(seed);
                string reference = Path.Combine(output, "reference", "seed-" + seed);
                string guarded = Path.Combine(output, "guarded", "seed-" + seed);
                // 同一个 Journey 流程、同一原生骰序列、同一 RuleProvider；reference 没有新包装/观察器。
                int oldResult = await Journey.Run(root, false, seed, Nights, options: new Journey.RunOptions
                    { Directory = reference, Provider = new RuleProvider() });
                var audit = new JourneyAudit(provider, false, seed, rows);
                int newResult = await Journey.Run(root, false, seed, Nights, options: new Journey.RunOptions
                    { Directory = guarded, Provider = provider, Context = provider.Context, Observe = audit.Observe });
                string left = File.ReadAllText(Path.Combine(reference, "journey.local.json"));
                string right = File.ReadAllText(Path.Combine(guarded, "journey.local.json"));
                bool same = oldResult == newResult && JToken.DeepEquals(JObject.Parse(left), JObject.Parse(right));
                comparisons.Add(new { seed, reference_exit = oldResult, guarded_exit = newResult,
                    same_world_and_receipts = same, reference_sha256 = Hash(left), guarded_sha256 = Hash(right) });
                WriteSeedReport(guarded, seed, rows, provider);
                Require(same && oldResult == 0 && newResult == 0 && !provider.Stopped, "offline_journey_incomplete_or_mismatch");
                // 原流程若造物依赖失败，保留两臂原样并停止，绝不造道具把基线补绿。
            }
            Require(JToken.DeepEquals(frozenSources, JObject.FromObject(Sources(root))), "offline_sources_changed_during_run");
            Require(JToken.DeepEquals(artifacts, Artifacts(root, dir)), "offline_artifacts_changed_during_run");
            status = "baseline_confirmed";
            return 0;
        }
        catch { provider.Stop("offline_baseline_failed"); throw; }
        finally
        {
            Create(Path.Combine(output, "summary.json"), new { suite = Suite, status, live = false,
                seeds = Seeds, nights = Nights, comparisons, sources = frozenSources, artifacts,
                audit = Summary(false, status, provider, rows),
                note = "模拟结果只证明护栏与原流程可比，不代表真实模型成功、真实usage或真实费用为零。" });
            Console.WriteLine("连续世界离线证据：" + output);
        }
    }

    /// <summary>在委托前锁定、校验、持久计次；任何第一次故障都是不可覆写的批次终态。</summary>
    internal sealed class GuardedProvider : IAiProvider, IDisposable
    {
        IAiProvider inner;
        readonly bool live;
        readonly string dir;
        readonly Action verify;
        readonly FileStream gate;
        readonly SemaphoreSlim single = new SemaphoreSlim(1, 1);
        readonly object admission = new object();
        bool stopLatched;
        // 只用在本地Fake屏障测试；生产不设置。位置是早期检查后、最终同步准入前。
        internal Func<Task> BeforeAdmission { get; set; }
        DsWorldSnapshot before;
        AiTrace context;
        int seed;
        public int Calls { get; private set; }
        public int Attempts => File.Exists(Path.Combine(dir, "attempts.jsonl"))
            ? File.ReadLines(Path.Combine(dir, "attempts.jsonl")).Count() : 0;
        public bool Stopped { get { lock(admission)return stopLatched || File.Exists(Path.Combine(dir, "STOPPED")); } }
        public string Name => inner?.Name ?? "continuous-world-unbound";
        public bool IsSimulated => inner?.IsSimulated ?? !live;

        internal GuardedProvider(IAiProvider provider, bool live, string directory, Action verify = null)
        {
            if (!live && provider != null && !provider.IsSimulated) throw new ArgumentException("offline_simulated_only");
            dir = directory; this.live = live; this.verify = verify; inner = provider;
            Directory.CreateDirectory(dir);
            gate = new FileStream(Path.Combine(dir, "live.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        internal void Bind(IAiProvider provider)
        {
            Require(inner == null && provider != null && (live || provider.IsSimulated), "provider_already_bound");
            inner = provider;
        }

        internal void CheckReady()
        {
            Require(!Stopped, "research_stopped");
            try { verify?.Invoke(); }
            catch { Stop("registration_mismatch"); throw; }
        }

        internal void ClaimBatch()
        {
            CheckReady();
            if (File.Exists(Path.Combine(dir, "STARTED")) || File.Exists(Path.Combine(dir, "attempts.jsonl")) ||
                Directory.Exists(Path.Combine(dir, "live")))
            {
                if (!File.Exists(Path.Combine(dir, "COMPLETED"))) Stop("interrupted_batch_no_resume");
                throw new InvalidOperationException("batch_already_started_no_resume");
            }
            Create(Path.Combine(dir, "STARTED"), new { suite = Suite, utc = DateTime.UtcNow });
        }

        internal void BeginSeed(int value)
        {
            CheckReady(); Require(Seeds.Contains(value), "seed_not_registered");
            string starts = Path.Combine(dir, "seed-starts.jsonl");
            int index = File.Exists(starts) ? File.ReadLines(starts).Count() : 0;
            Require(index < Seeds.Length && Seeds[index] == value, "seed_order_or_resume_rejected");
            seed = value; before = null; context = null;
            Append(starts, new { seed, utc = DateTime.UtcNow });
        }

        internal void Context(AiTrace trace, DsWorldSnapshot snapshot) { context = trace; before = snapshot; }

        List<JObject> ReadAttempts()
        {
            string file = Path.Combine(dir, "attempts.jsonl");
            if (!File.Exists(file)) return new List<JObject>();
            var records = File.ReadLines(file).Select(JObject.Parse).ToList();
            Require(records.Select((r, i) => (int?)r["number"] == i + 1 && Seeds.Contains((int?)r["seed"] ?? -1)).All(x => x),
                "attempt_ledger_invalid");
            return records;
        }

        public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
        {
            // 不排队第二次调用：上限与 STOPPED 检查不能与另一个委托交错。
            if (!await single.WaitAsync(0).ConfigureAwait(false))
            { Stop("concurrent_call"); throw new InvalidOperationException("concurrent_call"); }
            try
            {
                CheckReady();
                if (BeforeAdmission != null)
                {
                    Require(!live && inner.IsSimulated, "test_barrier_offline_only");
                    await BeforeAdmission().ConfigureAwait(false);
                }
                Task<ProviderResult> pending;
                try
                {
                    Require(inner != null && Seeds.Contains(seed), "seed_or_provider_not_ready");
                    var attempts = ReadAttempts();
                    if (attempts.Count >= BatchLimit) { Stop("batch_request_limit"); throw new InvalidOperationException("batch_request_limit"); }
                    if (attempts.Count(x => (int)x["seed"] == seed) >= PerSeedLimit)
                    { Stop("seed_request_limit"); throw new InvalidOperationException("seed_request_limit"); }
                    string body = Body(packet);
                    Require(Encoding.UTF8.GetByteCount(body) <= PayloadLimit, "input_too_large");
                    if (before != null)
                    {
                        Require(context.Kind == packet.Kind && context.ActorId == packet.ActorId &&
                            Body(Packet(before, context.Kind, context.ActorId, context.PlayerInput)) == body, "role_payload_changed");
                        var privacy = Privacy(before, packet.Kind, packet.ActorId, packet.OriginalPlayerInput);
                        Require((bool)privacy["same_payload"] && (bool)privacy["marker_absent"] &&
                            (bool)privacy["same_place_private_payload"], "private_payload_changed");
                    }
                    else Require(!live, "live_context_required");
                    lock(admission)
                    {
                        CheckReady(); // Stop与最终委托准入共用短锁，不跨await；STOPPED完成后不可能新启动inner。
                        Append(Path.Combine(dir, "attempts.jsonl"), new { number = attempts.Count + 1, seed,
                            seed_number = attempts.Count(x => (int)x["seed"] == seed) + 1, utc = DateTime.UtcNow,
                            kind = packet.Kind.ToString(), actor = packet.ActorId,
                            payload_sha256 = Hash(body), payload_bytes = Encoding.UTF8.GetByteCount(body) });
                        Calls++;
                        try { pending = inner.CompleteAsync(packet, ct); }
                        catch(Exception e) { pending = Task.FromException<ProviderResult>(e); }
                    }
                }
                catch { Stop("pre_delegate_guard_failed"); throw; }
                ProviderResult result;
                try { result = await pending.ConfigureAwait(false); }
                catch (Exception e)
                {
                    // 不保留异常正文；转为可落盘/可回放的失败结果。真实提供方原 Error 路径不被改码。
                    string code = e is OperationCanceledException ? "provider_cancelled_or_timeout" : "provider_exception";
                    Stop(code);
                    result = new ProviderResult { ProviderName = Name, Error = code, Simulated = IsSimulated };
                }
                if (result == null)
                {
                    Stop("null_provider_response");
                    result = new ProviderResult { ProviderName = Name, Error = "null_provider_response", Simulated = IsSimulated };
                }
                else if (result.Error != null) Stop(result.Error);
                else if (string.IsNullOrWhiteSpace(result.RawText))
                {
                    Stop("empty_response");
                    result.Error = "empty_response";
                }
                // 原Error对象返回给Session，先让第一次失败trace完整落盘；后续调用一律在inner之前拒绝。
                return result;
            }
            finally { single.Release(); }
        }

        internal void Stop(string reason)
        {
            lock(admission)
            {
                stopLatched = true; // 即使写盘失败也禁止本进程再准入。
                string path = Path.Combine(dir, "STOPPED");
                if (File.Exists(path)) return;
                try { Create(path, new { suite = Suite, utc = DateTime.UtcNow, seed, reason, stop_scope = "batch" }); }
                catch (IOException) when (File.Exists(path)) { }
            }
        }

        public void Dispose() { single.Dispose(); gate.Dispose(); }
    }
}
