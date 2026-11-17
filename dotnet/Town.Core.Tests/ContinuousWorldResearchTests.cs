using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.Research.Execution;

namespace Town.Core.Tests
{
    // 所有提供方均为本地Fake/RuleProvider；不构造在线提供方、不读密钥或预算账本。
    public class ContinuousWorldResearchTests
    {
        static string Root()
        {
            var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "dotnet", "Town.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repository_required");
        }

        static string Output()
        {
            string dir = Path.Combine(Root(), ".local", "continuous-world-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        sealed class Fake : IAiProvider
        {
            readonly Func<PromptPacket,int,Task<ProviderResult>> reply;
            public int Calls;
            public string Name => "continuous-world-offline-fake";
            public bool IsSimulated => true;
            internal Fake(Func<PromptPacket,int,Task<ProviderResult>> reply = null) { this.reply = reply; }
            public Task<ProviderResult> CompleteAsync(PromptPacket p, CancellationToken ct)
            {
                Calls++;
                return reply == null ? new RuleProvider().CompleteAsync(p, ct) : reply(p, Calls);
            }
        }

        static PromptPacket Packet() => ContextBuilder.BuildDialogue(
            NpcPerceptionView.Capture(DsContent.NewWorld(18), "marta"), "你好");

        [TestCase(1)]
        [TestCase(3)]
        public async Task 夜间首位或中途失败_没有观察器也不能委托同夜下一居民(int failAt)
        {
            string dir = Output();
            var original = new ProviderResult { ProviderName = "fake", Simulated = true, Error = "missing_output" };
            var fake = new Fake((p, n) => n == failAt ? Task.FromResult(original) :
                new RuleProvider().CompleteAsync(p, CancellationToken.None));
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            var s = new DragonsleepSession(guard, 18);
            s.World.Phase = DayPhase.Night;
            var saved = new List<AiTrace>();
            s.TraceCompleted += t =>
            {
                saved.Add(t);
                File.WriteAllText(Path.Combine(dir, t.Sequence + ".trace.json"), MiniJson.Write(t.ToRecord()));
            };
            Assert.ThrowsAsync<InvalidOperationException>(async () => await s.EndPhaseAsync());
            Assert.AreEqual(failAt, fake.Calls);
            Assert.AreEqual(failAt, guard.Attempts);
            Assert.AreSame(original, saved.Single(t => t.Provider?.Error == "missing_output").Provider);
            Assert.True(File.Exists(Path.Combine(dir, "STOPPED")));
            Assert.False(saved.Any(t => t.Kind == AiTaskKind.Direct));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
            Assert.AreEqual(failAt, fake.Calls);
            await Task.CompletedTask;
        }

        [TestCase(7)]
        [TestCase(9)]
        public async Task 原Journey批次夜间失败_先存失败帧_不启动下一seed(int failAt)
        {
            string dir = Output();
            var fake = new Fake((p, n) => n == failAt
                ? Task.FromResult(new ProviderResult { ProviderName = "fake", Simulated = true, Error = "incomplete_response" })
                : new RuleProvider().CompleteAsync(p, CancellationToken.None));
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            var rows = new List<JObject>();
            Assert.AreEqual(1, await ContinuousWorldResearch.RunBatch(Root(), false, Path.Combine(dir, "batch"), guard, rows));
            Assert.AreEqual(failAt, fake.Calls);
            Assert.AreEqual(failAt, guard.Attempts);
            Assert.AreEqual(1, File.ReadLines(Path.Combine(dir, "seed-starts.jsonl")).Count());
            Assert.False(Directory.Exists(Path.Combine(dir, "batch", "seed-29")));
            var failed = rows.Single(r => (string)r["provider_error"] == "incomplete_response");
            string prefix = Path.Combine(dir, "batch", "seed-18", (string)failed["id"]);
            Assert.True(File.Exists(prefix + ".trace.json"));
            Assert.True(File.Exists(prefix + ".replay.json"));
            Assert.True(File.Exists(prefix + ".before.local.json"));
            Assert.True(File.Exists(prefix + ".after.local.json"));
            Assert.True((bool)failed["strict_replay"]);
            Assert.AreEqual(JTokenType.Null, failed["estimated_usd"].Type);
            var report = JObject.Parse(File.ReadAllText(Path.Combine(dir, "batch", "seed-18", "continuous-report.local.json")));
            Assert.True(report["nights"][1]["residents"].All(r => (string)r["status"] == "not_called"));
        }

        [TestCase("null")]
        [TestCase("empty")]
        [TestCase("exception")]
        [TestCase("timeout")]
        [TestCase("error")]
        public async Task 任意提供方故障先计次_STOPPED不改写_重建包装器也不能续跑(string failure)
        {
            string dir = Output();
            var fake = new Fake((p, n) =>
            {
                if (failure == "exception") throw new InvalidOperationException("private exception must not be saved");
                if (failure == "timeout") throw new OperationCanceledException("private timeout must not be saved");
                return Task.FromResult(failure == "null" ? null : new ProviderResult
                {
                    ProviderName = "fake", Simulated = true,
                    RawText = failure == "empty" ? " \n\t" : null,
                    Error = failure == "error" ? "original_error" : null
                });
            });
            string stopped;
            using (var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir))
            {
                guard.BeginSeed(18);
                var result = await guard.CompleteAsync(Packet(), CancellationToken.None);
                Assert.NotNull(result);
                Assert.NotNull(result.Error);
                Assert.AreEqual(1, fake.Calls);
                Assert.AreEqual(1, guard.Attempts);
                stopped = File.ReadAllText(Path.Combine(dir, "STOPPED"));
                guard.Stop("must_not_replace_first_reason");
                Assert.AreEqual(stopped, File.ReadAllText(Path.Combine(dir, "STOPPED")));
                Assert.False(stopped.Contains("private"));
            }
            using var reopened = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            Assert.Throws<InvalidOperationException>(() => reopened.BeginSeed(29));
            Assert.Throws<InvalidOperationException>(() => reopened.ClaimBatch());
            Assert.ThrowsAsync<InvalidOperationException>(async () => await reopened.CompleteAsync(Packet(), CancellationToken.None));
            Assert.AreEqual(1, fake.Calls);
            Assert.AreEqual(1, reopened.Attempts);
            Assert.AreEqual(stopped, File.ReadAllText(Path.Combine(dir, "STOPPED")));
        }

        [Test]
        public async Task 每seed第26次前拒绝_整批上限不替代每seed上限()
        {
            string dir = Output(); var fake = new Fake();
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            for (int i = 0; i < ContinuousWorldResearch.PerSeedLimit; i++)
                await guard.CompleteAsync(Packet(), CancellationToken.None);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
            Assert.AreEqual(25, fake.Calls); Assert.AreEqual(25, guard.Attempts);
            Assert.True(guard.Stopped);
        }

        [Test]
        public async Task 整批第76次前拒绝_持久计数不依赖本进程Calls()
        {
            string dir = Output(); var fake = new Fake();
            for (int i = 0; i < 75; i++)
                ContinuousWorldResearch.Append(Path.Combine(dir, "attempts.jsonl"), new
                { number = i + 1, seed = ContinuousWorldResearch.Seeds[i / 25], seed_number = i % 25 + 1 });
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
            Assert.AreEqual(0, fake.Calls); Assert.AreEqual(75, guard.Attempts);
            Assert.True(guard.Stopped);
            await Task.CompletedTask;
        }

        [Test]
        public void 整批锁互斥_批次开始后不可重启或重排seed()
        {
            string dir = Output();
            using (var guard = new ContinuousWorldResearch.GuardedProvider(new Fake(), false, dir))
            {
                Assert.Throws<IOException>(() => new ContinuousWorldResearch.GuardedProvider(new Fake(), false, dir));
                guard.ClaimBatch();
                guard.BeginSeed(18);
                Assert.Throws<InvalidOperationException>(() => guard.BeginSeed(18));
                Assert.Throws<InvalidOperationException>(() => guard.BeginSeed(41));
            }
            using var reopened = new ContinuousWorldResearch.GuardedProvider(new Fake(), false, dir);
            Assert.Throws<InvalidOperationException>(() => reopened.ClaimBatch());
            Assert.AreEqual(0, reopened.Calls);
        }

        [Test]
        public async Task 同seed原Journey可比一致_预注册不可覆写_源码与注册哈希改动拒绝()
        {
            string root = Output();
            // 用独立源码树验证SHA，不触碰仓库源码或任何历史证据。
            foreach (string folder in new[] { "dotnet/Town.Research", "dotnet/Town.Core", "dotnet/Town.OpenAI",
                "unity/Assets/Town.Core/Dragonsleep", "unity/Assets/Town.OpenAI" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (string project in new[] { "Town.Research", "Town.Core", "Town.OpenAI" })
                File.WriteAllText(Path.Combine(root, "dotnet", project, project + ".csproj"), "fixture-project");
            string source = Path.Combine(root, "dotnet/Town.Research/Journey.cs");
            File.WriteAllText(source, "fixture-source-original");
            string dir = Path.Combine(root, "registered");
            Assert.AreEqual(0, await ContinuousWorldResearch.RunOffline(root, dir));
            Assert.AreEqual(0, ContinuousWorldResearch.Prepare(root, dir));
            var registration = ContinuousWorldResearch.VerifyRegistration(root, dir);
            CollectionAssert.AreEqual(new[] { 18, 29, 41 }, registration["seeds"].Values<int>());
            Assert.AreEqual(75, (int)registration["maximum_requests"]);
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.Prepare(root, dir));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ContinuousWorldResearch.RunOffline(root, dir));
            var baseline = JObject.Parse(File.ReadAllText(Path.Combine(dir, "offline", "summary.json")));
            Assert.True(baseline["comparisons"].All(r => (bool)r["same_world_and_receipts"]));
            Assert.AreEqual(75, (int)baseline["audit"]["persistent_attempts"]);
            Assert.AreEqual(45, baseline["audit"]["results"].Count(r => (string)r["kind"] == "AgentPlan"));
            Assert.AreEqual(12, baseline["audit"]["results"].Count(r => (string)r["kind"] == "Direct"));
            Assert.True(baseline["audit"]["results"].All(r => (bool)r["strict_replay"] && (bool)r["invariants"]));
            Assert.AreEqual(JTokenType.Null, baseline["audit"]["estimated_usd"].Type);
            File.AppendAllText(source, "-changed");
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.VerifyRegistration(root, dir));
            var fake = new Fake();
            using (var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, Path.Combine(root, "guard-check"),
                () => ContinuousWorldResearch.VerifyRegistration(root, dir)))
            {
                Assert.Throws<InvalidOperationException>(() => guard.CheckReady());
                Assert.True(guard.Stopped); Assert.AreEqual(0, fake.Calls);
            }
            File.WriteAllText(source, "fixture-source-original");
            ContinuousWorldResearch.VerifyRegistration(root, dir);
            string registrationFile = Path.Combine(dir, "pre-registered.json");
            string originalRegistration = File.ReadAllText(registrationFile);
            var changedArtifacts = JObject.Parse(originalRegistration);
            changedArtifacts["artifacts"] = new JObject { ["wrong_snapshot"] = true };
            File.WriteAllText(registrationFile, changedArtifacts.ToString());
            File.WriteAllText(Path.Combine(dir, "pre-registered.sha256"), ContinuousWorldResearch.Hash(File.ReadAllText(registrationFile)));
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.VerifyRegistration(root, dir));
            File.WriteAllText(registrationFile, originalRegistration);
            File.WriteAllText(Path.Combine(dir, "pre-registered.sha256"), ContinuousWorldResearch.Hash(originalRegistration));
            File.AppendAllText(Path.Combine(dir, "pre-registered.json"), " ");
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.VerifyRegistration(root, dir));
        }

        [Test]
        public void 缺usage或模拟usage不能报告真实费用零_原始非法引用不能被过滤成绿()
        {
            Assert.IsNull(ContinuousWorldResearch.Cost(new ProviderResult { EstimatedUsd = 0m }, true));
            Assert.IsNull(ContinuousWorldResearch.Cost(new ProviderResult
                { InputTokens = 5, OutputTokens = 3, EstimatedUsd = 0m, Simulated = true }, true));
            Assert.IsNull(ContinuousWorldResearch.Cost(new ProviderResult
                { InputTokens = 5, OutputTokens = 3, EstimatedUsd = .001m }, false));
            Assert.AreEqual(.001m, ContinuousWorldResearch.Cost(new ProviderResult
                { InputTokens = 5, OutputTokens = 3, EstimatedUsd = .001m }, true));
            var raw = JObject.Parse("{\"verb\":\"tell\",\"place\":\"market\",\"target\":\"vesna\",\"share\":[\"b_maren_page\"],\"reason\":\"test\"}");
            Assert.False(ContinuousWorldResearch.RawReferences(raw, AiTaskKind.AgentPlan, "marta", DsContent.NewWorld(18)));
        }

        [Test]
        public async Task 角色payload不符或超12KiB在委托前停止_同地私有认知差分不进入payload()
        {
            string dir = Output(); var fake = new Fake();
            using (var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir))
            {
                guard.BeginSeed(18);
                var world = DsContent.NewWorld(18);
                var before = DsWorldSnapshot.Capture(world, 0, 4);
                guard.Context(new AiTrace { Kind = AiTaskKind.Dialogue, ActorId = "marta", PlayerInput = "原话" }, before);
                Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
                Assert.True(guard.Stopped); Assert.AreEqual(0, fake.Calls); Assert.AreEqual(0, guard.Attempts);
                var privacy = ContinuousWorldResearch.Privacy(before, AiTaskKind.Dialogue, "marta", "原话");
                Assert.True((bool)privacy["same_place_private_payload"]);
                Assert.True(((JArray)privacy["same_place_other_actors_local"]).Count > 0);
            }
            string oversized = Output();
            using var largeGuard = new ContinuousWorldResearch.GuardedProvider(fake, false, oversized);
            largeGuard.BeginSeed(18);
            var largeWorld = DsContent.NewWorld(18);
            largeWorld.Beliefs.Add(new DsBelief { Id = "large", Owner = "marta", Text = new string('界', 20000) });
            var largePacket = ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(largeWorld, "marta"), "你好");
            Assert.ThrowsAsync<InvalidOperationException>(async () => await largeGuard.CompleteAsync(largePacket, CancellationToken.None));
            Assert.True(largeGuard.Stopped); Assert.AreEqual(0, fake.Calls); Assert.AreEqual(0, largeGuard.Attempts);
            await Task.CompletedTask;
        }

        [Test]
        public async Task 原始非法share即使规则部分接受_审计引用仍失败_不把提交当意图正确()
        {
            string dir = Output();
            var fake = new Fake((p, n) => Task.FromResult(new ProviderResult
            {
                ProviderName = "fake", Simulated = true,
                RawText = "{\"verb\":\"tell\",\"place\":\"market\",\"target\":\"vesna\",\"share\":[\"b_marta_saw_pip\",\"b_maren_page\"],\"reason\":\"test\"}"
            }));
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            var s = new DragonsleepSession(guard, 18);
            s.World.Actors["vesna"].Place = s.World.Actors["marta"].Place;
            var before = s.CaptureState();
            var t = await s.RunAsync(AiTaskKind.AgentPlan, "marta", null);
            var rows = new List<JObject>();
            var audit = new ContinuousWorldResearch.JourneyAudit(guard, false, 18, rows);
            audit.Observe(dir, "raw-invalid", DsReplayFrame.Capture(before, s.CaptureState(), t), t);
            Assert.AreEqual(1, rows.Count);
            Assert.True((bool)rows[0]["schema_valid"]);
            Assert.False((bool)rows[0]["references_valid"]);
            Assert.False((bool)rows[0]["structural_reference_valid"]);
            Assert.True((bool)rows[0]["strict_replay"]);
            Assert.True((bool)rows[0]["receipt"]["applied"]);
            Assert.AreEqual("Partial", (string)rows[0]["verdict"]);
            Assert.AreEqual(JTokenType.Null, rows[0]["automatic_intent_match"].Type);
        }

        [Test]
        public async Task 造物正常响应但效果规则拒绝_无providerError也停整批且单列依赖失败()
        {
            string dir = Output();
            var fake = new Fake((p, n) => p.Kind == AiTaskKind.Craft ? Task.FromResult(new ProviderResult
            {
                ProviderName = "fake", Simulated = true,
                RawText = "{\"name\":\"不成立图样\",\"look\":\"test\",\"consume\":[\"ember_ash\"],\"tags\":[\"iron\"],\"potency\":0.3}"
            }) : new RuleProvider().CompleteAsync(p, CancellationToken.None));
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            var rows = new List<JObject>();
            Assert.AreEqual(1, await ContinuousWorldResearch.RunBatch(Root(), false, Path.Combine(dir, "batch"), guard, rows));
            Assert.AreEqual(3, fake.Calls); Assert.True(rows.All(r => r["provider_error"].Type == JTokenType.Null));
            var craft = rows.Single(r => (string)r["kind"] == "Craft");
            Assert.True((bool)craft["structural_reference_valid"]); Assert.True((bool)craft["invariants"]);
            Assert.AreEqual("Rejected", (string)craft["verdict"]);
            Assert.False(Directory.Exists(Path.Combine(dir, "batch", "seed-29")));
            var outcome = JObject.Parse(File.ReadAllText(Path.Combine(dir, "batch", "seed-18", "outcome.local.json")));
            Assert.AreEqual("craft_dependency_failed", (string)outcome["outcome"]);
            var stopped = JObject.Parse(File.ReadAllText(Path.Combine(dir, "STOPPED")));
            Assert.AreEqual("craft_dependency_failed", (string)stopped["reason"]);
            Assert.AreEqual("batch", (string)stopped["stop_scope"]);
        }

        [Test]
        public async Task 并发Stop完成后_早期检查已通过的A不能进入inner_屏障不靠sleep()
        {
            string dir = Output(); var fake = new Fake();
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            guard.BeforeAdmission = () => { reached.SetResult(true); return release.Task; };
            Task<ProviderResult> a = guard.CompleteAsync(Packet(), CancellationToken.None);
            await reached.Task;
            try
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
                Assert.True(guard.Stopped); Assert.AreEqual(0, fake.Calls);
            }
            finally { release.SetResult(true); }
            Assert.ThrowsAsync<InvalidOperationException>(async () => await a);
            Assert.AreEqual(0, fake.Calls); Assert.AreEqual(0, guard.Attempts);
        }

        [Test]
        public async Task 已准入在途A不伪称回滚_B停止后禁止任何新inner()
        {
            string dir = Output();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = new Fake((p, n) => { reached.SetResult(true); return response.Task; });
            using var guard = new ContinuousWorldResearch.GuardedProvider(fake, false, dir);
            guard.BeginSeed(18);
            Task<ProviderResult> a = guard.CompleteAsync(Packet(), CancellationToken.None);
            await reached.Task;
            Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
            Assert.True(guard.Stopped); Assert.AreEqual(1, fake.Calls); Assert.AreEqual(1, guard.Attempts);
            response.SetResult(new ProviderResult { ProviderName = "fake", Simulated = true, RawText = "{}" });
            await a;
            Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.CompleteAsync(Packet(), CancellationToken.None));
            Assert.AreEqual(1, fake.Calls);
        }

        [Test]
        public void 递归source新增修改删除都会改锚_排除binObj和外部UI()
        {
            string root = Output();
            Directory.CreateDirectory(Path.Combine(root, "dotnet/Town.Research/Nested"));
            string nested = Path.Combine(root, "dotnet/Town.Research/Nested/Probe.cs");
            var empty = ContinuousWorldResearch.Sources(root);
            File.WriteAllText(nested, "original"); var before = ContinuousWorldResearch.Sources(root);
            Assert.False(T3Snapshot.Same(empty, before));
            File.AppendAllText(nested, "changed"); Assert.False(T3Snapshot.Same(before, ContinuousWorldResearch.Sources(root)));
            File.WriteAllText(nested, "original");
            foreach (string folder in new[] { "dotnet/Town.Research/bin", "dotnet/Town.Research/Nested/obj", "unity/Assets/Town.Unity" })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.WriteAllText(Path.Combine(root, folder, "Ignored.cs"), "external/generated");
            }
            Assert.True(T3Snapshot.Same(before, ContinuousWorldResearch.Sources(root)));
            File.Delete(nested); Assert.True(T3Snapshot.Same(empty, ContinuousWorldResearch.Sources(root)));
        }

        [Test]
        public void 没有同buffer加载身份不得进入正式offlinePrepareLive_旧suite不准入()
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ContinuousWorldResearch.Run(Root(), false));
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.Prepare(Root()));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ContinuousWorldResearch.Run(Root(), true));
            Assert.AreEqual("ds-continuous-world-t3-2", ContinuousWorldResearch.Suite);
            Assert.False(ContinuousWorldResearch.Folder(Root()).EndsWith("ds-continuous-world-t3-1", StringComparison.Ordinal));
            var manifest = new JObject { ["Files"] = new JObject
            {
                [typeof(ContinuousWorldResearch).Assembly.GetName().Name + ".dll"] = "A",
                ["Town.Core.dll"] = "A", ["Town.OpenAI.dll"] = "A"
            } };
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldArtifacts.ValidateLoaded(manifest, a => "B"));
            Assert.DoesNotThrow(() => ContinuousWorldArtifacts.ValidateLoaded(manifest, a => "A"));
        }

        [Test]
        public void 旧suite离线证据不得用于新prepare_保留原文件不覆写()
        {
            string root = Output(), dir = Path.Combine(root, "isolated-registration");
            Directory.CreateDirectory(Path.Combine(dir, "offline"));
            string file = Path.Combine(dir, "offline", "summary.json");
            const string old = "{\"suite\":\"ds-continuous-world-t3-1\",\"status\":\"baseline_confirmed\"}";
            File.WriteAllText(file, old);
            Assert.Throws<InvalidOperationException>(() => ContinuousWorldResearch.Prepare(root, dir));
            Assert.AreEqual(old, File.ReadAllText(file));
            Assert.False(File.Exists(Path.Combine(dir, "pre-registered.json")));
        }

        [Test]
        public void 实际加载A后磁盘被替换B_不把Location或磁盘B当已加载A的SHA()
        {
            string root = Output();
            string dir = T3Snapshot.Folder(root); Directory.CreateDirectory(Path.Combine(dir, "payload"));
            // 隔离本地夹具；不运行build、不改生产制品。三个依赖真实字节，另两项只满足清单完整性。
            var names = new Dictionary<string,Assembly> { ["Town.Research"] = typeof(ContinuousWorldResearch).Assembly,
                ["Town.Core"] = typeof(DragonsleepSession).Assembly, ["Town.OpenAI"] = typeof(OpenAiDragonsleepProvider).Assembly,
                ["Town.PieGame"] = typeof(ContinuousWorldResearch).Assembly, ["PieGame.AI.Kernel"] = typeof(ContinuousWorldResearch).Assembly };
            var files = new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach (var pair in names)
            {
                byte[] bytes = File.ReadAllBytes(pair.Value.Location);
                string name = pair.Key + ".dll"; File.WriteAllBytes(Path.Combine(dir, "payload", name), bytes);
                files[name] = T3Snapshot.Digest(bytes);
            }
            string text = System.Text.Json.JsonSerializer.Serialize(new T3Snapshot.Manifest
            { Format = T3Snapshot.Format, Suite = T3Snapshot.Suite, Sdk = "test", Sources = T3Snapshot.Sources(root),
                Files = files, Compile = new SortedDictionary<string,string[]>() });
            File.WriteAllText(Path.Combine(dir, "build.json"), text);
            File.WriteAllText(Path.Combine(dir, "build.sha256"), T3Snapshot.Digest(System.Text.Encoding.UTF8.GetBytes(text)));
            var loaded = new T3Snapshot.Loaded(root);
            try
            {
                var assembly = loaded.LoadFromAssemblyName(new AssemblyName("Town.Core"));
                Assert.AreEqual(files["Town.Core.dll"], loaded.ActualHash(assembly));
                File.WriteAllBytes(Path.Combine(dir, "payload", "Town.Core.dll"), new byte[] { 1, 2, 3 });
                Assert.AreEqual(files["Town.Core.dll"], loaded.ActualHash(assembly));
                Assert.Throws<InvalidOperationException>(() => loaded.VerifyUnchanged());
            }
            finally { loaded.Unload(); }
            Assert.Throws<InvalidOperationException>(() => new T3Snapshot.Loaded(root));
            File.WriteAllBytes(Path.Combine(dir, "payload", "Town.Core.dll"), File.ReadAllBytes(typeof(DragonsleepSession).Assembly.Location));
            Directory.CreateDirectory(Path.Combine(root, "dotnet/Town.Research/Nested"));
            File.WriteAllText(Path.Combine(root, "dotnet/Town.Research/Nested/NewSource.cs"), "new source after old build");
            Assert.Throws<InvalidOperationException>(() => new T3Snapshot.Loaded(root), "旧制品不能被新磁盘源码重新标记");
            Assert.ThrowsAsync<InvalidOperationException>(async () => await T3Snapshot.Build(root), "已有目录不能为旧DLL补build凭据");
        }
    }
}
