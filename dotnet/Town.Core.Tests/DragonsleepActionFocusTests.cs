using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.PieGame;

namespace Town.Core.Tests
{
    public sealed class DragonsleepActionFocusTests
    {
        static string Root()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dotnet/Town.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("repository_not_found");
        }

        static PromptPacket Packet(string version, DsWorld world = null)
        {
            return ContextBuilder.BuildTask(TaskPerceptionView.Capture(world ?? DsContent.NewWorld(109),
                AiTaskKind.Adjudicate, "player", actionPromptVersion: version,
                actionPerceptionVersion: DsActionPerception.Current), "先看看，不额外动手");
        }

        [Test]
        public void 候选v3是独立显式版本_v2与默认入口不被默默升级()
        {
            Assert.AreEqual("ds-action-prompt-v2", DsActionPrompt.Current);
            Assert.AreEqual("ds-action-prompt-v3", DsActionPrompt.Focused);
            var baseline = Packet(DsActionPrompt.Current);
            var candidate = Packet(DsActionPrompt.Focused);
            Assert.That(candidate.Instructions, Does.StartWith(baseline.Instructions));
            Assert.That(baseline.Instructions, Does.Not.Contain("字段按动词职责填写"));
            Assert.AreEqual(baseline.TaskSnapshot.Json, candidate.TaskSnapshot.Json);
            Assert.AreEqual(baseline.SchemaName, candidate.SchemaName);
            CollectionAssert.AreEqual(baseline.Sections.Select(s => s.Text), candidate.Sections.Select(s => s.Text));
            Assert.AreNotEqual(ActionPromptCodec.Freeze(baseline).Fingerprint, ActionPromptCodec.Freeze(candidate).Fingerprint);
            Assert.AreEqual(ActionPromptCodec.Binding + "|" + DsActionPrompt.Focused + "|" + DsActionPerception.Current,
                ActionPromptCodec.BindingFor(candidate));
            Assert.AreEqual(DsActionPrompt.Legacy, ContextBuilder.BuildTask(
                TaskPerceptionView.Capture(DsContent.NewWorld(), AiTaskKind.Adjudicate, "player"), "观察").ActionPromptVersion);
            Assert.Throws<ArgumentException>(() => new DragonsleepSession(new RuleProvider()).ActAsync(
                "观察", actionPromptVersion: DsActionPrompt.Focused));
        }

        [Test]
        public void v3使用通用当下语义与类型职责_不把失败题答案写进说明()
        {
            string suffix = Packet(DsActionPrompt.Focused).Instructions.Substring(Packet(DsActionPrompt.Current).Instructions.Length);
            Assert.That(suffix, Does.Contain("否定只约束它所属的动作"));
            Assert.That(suffix, Does.Contain("明确观看/查看用 observe，明确原地等候用 wait"));
            Assert.That(suffix, Does.Contain("只观察不能擅自 search、use 或拿取"));
            Assert.That(suffix, Does.Contain("confide 的 target 是受托居民，item 为空串"));
            foreach (string fixtureSpecific in new[] { "梅伦", "石缝", "蓝晶路灯", "pact_page", "crafted_1", "smoked_fish" })
                Assert.That(suffix, Does.Not.Contain(fixtureSpecific));
        }

        [Test]
        public void 在线安全重建与冻结副本保留v3_不采用可变段落或隐藏世界()
        {
            var world = DsContent.NewWorld(109);
            var packet = Packet(DsActionPrompt.Focused, world);
            var frozen = packet.FreezeCopy();
            string before = OpenAiDragonsleepProvider.BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch)
                .ToString(Formatting.None);
            string fingerprint = ActionPromptCodec.Freeze(frozen).Fingerprint;
            packet.Instructions = "污染说明 secret_private_value_737";
            packet.Sections.Clear();
            world.Actors["maren"].Trust = .12345f;
            world.Actors["maren"].Place = "private_remote_place_737";
            string after = OpenAiDragonsleepProvider.BuildPayload(packet, OpenAiRunProfile.SixMechanismResearch)
                .ToString(Formatting.None);
            Assert.AreEqual(before, after);
            Assert.AreEqual(fingerprint, ActionPromptCodec.Freeze(frozen).Fingerprint);
            Assert.AreEqual(DsActionPrompt.Focused, frozen.ActionPromptVersion);
            Assert.That(after, Does.Not.Contain("private_remote_place_737").And.Not.Contain("secret_private_value_737"));
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(after), 12 * 1024);
        }

        [Test]
        public void 上下文配对只去请求编号_历史动作结果物权时刻均不可归一化()
        {
            const string view = "{\"where\":\"market\",\"recent_player_actions\":[{\"request_id\":\"S1-1\","
                + "\"verb\":\"show\",\"target\":\"maren\",\"item\":\"pact_page\",\"result\":\"Accepted\","
                + "\"at\":\"第1天·清晨\",\"item_outcome\":\"no_transfer_observed\"}]}";
            string expected = ActionKernelResearch.ContextHash(view);
            Assert.AreEqual(expected, ActionKernelResearch.ContextHash(view.Replace("S1-1", "S2-77")));
            foreach (var change in new[] { ("show", "give"), ("maren", "pip"), ("pact_page", "travel_cloak"),
                ("Accepted", "Rejected"), ("清晨", "正午"), ("no_transfer_observed", "handed_over_by_this_action") })
                Assert.AreNotEqual(expected, ActionKernelResearch.ContextHash(view.Replace(change.Item1, change.Item2)));
        }

        [Test]
        public void v3同版本真实Kernel回放通过_同世界同感知跨提示版本仍拒绝()
        {
            var world = DsContent.NewWorld(109);
            string before = DsWorldSnapshot.Capture(world, 0, 4).Json;
            var candidate = Packet(DsActionPrompt.Focused, world);
            var original = new ReplayPort(before);
            var kernel = new Kernel(original, DsExecutor.RulesVersion);
            kernel.Register("action", new ActionTaskHandler());
            var request = kernel.Begin("focus-1", "action", ActionPromptCodec.Freeze(candidate));
            var prepared = Task.Run(() => kernel.PrepareAsync(request, new RecordedObserve())).GetAwaiter().GetResult();
            Assert.AreEqual("ready", prepared.Receipt.Code);
            var frame = ReplayFrame.Capture(prepared);
            string anchor = frame.Digest;
            foreach (string version in new[] { DsActionPrompt.Focused, DsActionPrompt.Current })
            {
                var port = new ReplayPort(before);
                var replay = new Kernel(port, DsExecutor.RulesVersion);
                replay.Register("action", new ActionTaskHandler());
                var replayRequest = replay.Begin("focus-1", "action", ActionPromptCodec.Freeze(Packet(version, world)));
                var result = replay.PrepareReplayAsync(replayRequest, frame, anchor).GetAwaiter().GetResult();
                Assert.AreEqual(version == DsActionPrompt.Focused ? "ready" : "replay_mismatch", result.Receipt.Code);
                if (version == DsActionPrompt.Focused)
                    Assert.AreEqual(frame.AfterDigest, replay.Commit(result).AfterDigest);
                else Assert.AreEqual(before, port.Read());
            }
            var trace = new AiTrace { Kind = AiTaskKind.Adjudicate, Prompt = candidate };
            Assert.AreEqual(DsActionPrompt.Focused, trace.ToRecord()["action_prompt_version"]);
        }

        [Test]
        public void 离线完整失败集对照保留误解_不会把最终目标或同世界当逐步通过()
        {
            string root = Root();
            string output = Path.Combine(root, ".local", "action-focus-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.RunFocus(root, false, output));
            var summary = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual(96, (int)summary["requested"]);
            Assert.AreEqual(96, (int)summary["invariants"]);
            Assert.AreEqual(96, (int)summary["replayPassed"]);
            Assert.AreEqual(24, ((JArray)summary["episodes"]).Count);
            Assert.AreEqual(48, ((JArray)summary["pairs"]).Count);
            foreach (var arm in summary["arms"])
            {
                Assert.AreEqual(48, (int)arm["requested"]);
                Assert.IsFalse((bool)arm["criticalGatePassed"]);
                Assert.Less((int)arm["intent"], 48);
                foreach (var probe in arm["probes"])
                    Assert.AreEqual((string)probe["verb"] == "observe" ? 9 : 3, (int)probe["total"]);
            }
            foreach (var row in summary["results"])
            {
                Assert.AreEqual(DsActionPerception.Current, (string)row["perception_version"]);
                Assert.IsTrue((bool)row["before_matches_previous_after"]);
            }
            Assert.IsTrue(File.Exists(Path.Combine(output, "focus-contract.pre-registered.json")));
        }

        [Test]
        public void 提供方故障一次即停止_无ready不记回放通过()
        {
            string output = Path.Combine(Root(), ".local", "action-focus-tests", Guid.NewGuid().ToString("N"));
            var provider = new Unavailable();
            Assert.AreEqual(1, ActionKernelResearch.RunFocus(Root(), false, output, provider));
            var summary = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(1, (int)summary["requested"]);
            Assert.AreEqual(0, (int)summary["replayApplicable"]);
            Assert.AreEqual(0, (int)summary["replayPassed"]);
            Assert.AreEqual(JTokenType.Null, summary["results"][0]["strict_replay"].Type);
            Assert.AreEqual(JTokenType.Null, summary["estimatedUsd"].Type);
        }

        sealed class Unavailable : IAiProvider
        {
            internal int Calls;
            public string Name => "离线故障";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(new ProviderResult { Error = "offline_test_error", ProviderName = Name, Simulated = true });
            }
        }

        sealed class ReplayPort : IStatePort
        {
            string _state;
            internal ReplayPort(string state) { _state = state; }
            public string Read() => _state;
            public void Write(string state) { _state = state; }
        }

        sealed class RecordedObserve : IProvider
        {
            public Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new ProviderReply("{\"verb\":\"observe\",\"target\":\"\",\"item\":\"\",\"place\":\"market\","
                    + "\"laws\":[],\"difficulty\":0.1,\"narration\":\"观察眼前\"}"));
            }
        }
    }
}
