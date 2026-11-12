using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepActionResearchTests
    {
        sealed class Unavailable : IAiProvider
        {
            internal int Calls;
            public string Name => "离线失败桩";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(new ProviderResult { ProviderName = Name, Simulated = true, Error = "test_unavailable" });
            }
        }

        sealed class WrongLightTarget : IAiProvider
        {
            public string Name => "离线错误照明对象反例";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                if (packet.OriginalPlayerInput != "使用我手里的蓝晶路灯，让墓穴本身被照亮，不对任何人或小烬施法。")
                    return new RuleProvider().CompleteAsync(packet, ct);
                return Task.FromResult(new ProviderResult
                {
                    ProviderName = Name, Simulated = true,
                    RawText = MiniJson.Write(new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["verb"] = "use", ["target"] = "whelp", ["item"] = "crafted_1", ["place"] = "catacombs",
                        ["laws"] = new object[0], ["difficulty"] = .5, ["narration"] = "故意对异地雏龙施法的本地验收反例"
                    })
                });
            }
        }

        static string Root()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "dotnet", "Town.sln"))) root = root.Parent;
            Assert.NotNull(root, "须在本仓库构建输出中运行。");
            return root.FullName;
        }

        [Test]
        public void 默认有限行动研究全程离线_拒绝假绿且保存严格回放与隐私差分()
        {
            string root = Root();
            string output = Path.Combine(root, ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.Run(root, false, output));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual("completed", (string)report["status"]);
            Assert.AreEqual(false, (bool)report["live"]);
            Assert.AreEqual(24, (int)report["requested"]);
            Assert.AreEqual(24, (int)report["invariantChecks"]);
            Assert.Less((int)report["intentChecks"], 24, "不得拿内核 committed 冒充离线提供方全部意图正确。");
            Assert.AreEqual(24, (int)report["readyFrames"]);
            var rows = (JArray)report["results"];
            Assert.True(rows.All(row => (bool)row["legacy_exact"] && (bool)row["strict_replay"]));
            Assert.True(rows.All(row => (bool)row["private_markers_absent"]));
            foreach (string id in new[] { "negated_buy", "future_hide", "show_not_give" })
                Assert.False((bool)rows.Single(row => (string)row["id"] == id)["intent_check"], id);
            var privacy = JObject.Parse(File.ReadAllText(Path.Combine(output, "privacy-difference.json")));
            Assert.True((bool)privacy["passed"]);
            var frameFile = Path.Combine(output, "buy_direct", "replay.private.json");
            Assert.AreEqual(1, JArray.Parse(File.ReadAllText(frameFile)).Count);
            Assert.True(File.Exists(Path.Combine(output, "buy_direct", "anchors.local.json")));
            Assert.False(File.ReadAllText(Path.Combine(output, "buy_direct", "payload.json")).Contains("private_maren_731"));
        }

        [Test]
        public void 提供方错误只请求一次即停_无Ready不记回放通过_未知费用不记零()
        {
            string root = Root();
            string output = Path.Combine(root, ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            var provider = new Unavailable();
            Assert.AreEqual(1, ActionKernelResearch.Run(root, false, output, provider));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual(1, (int)report["requested"]);
            Assert.AreEqual(0, (int)report["readyFrames"]);
            Assert.AreEqual(0, (int)report["replayApplicable"]);
            Assert.AreEqual(0, (int)report["replayPassed"]);
            Assert.AreEqual(1, (int)report["unknownCosts"]);
            Assert.AreEqual(JTokenType.Null, report["estimatedUsd"].Type);
            var result = report["results"][0];
            Assert.AreEqual("test_unavailable", (string)result["failure"]);
            Assert.AreEqual("not_ready_no_frame", (string)result["replay_scope"]);
            Assert.AreEqual(JTokenType.Null, result["strict_replay"].Type);
        }

        [Test]
        public void 新措辞套题先冻结实际失败和消耗夹具_四步拒绝链不补写成功()
        {
            string root = Root();
            string output = Path.Combine(root, ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.Run(root, false, output, transfer: true));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual("ds-action-kernel-transfer-1", (string)report["suite"]);
            Assert.AreEqual(24, (int)report["requested"]);
            Assert.AreEqual(24, (int)report["invariantChecks"]);
            var registrations = JArray.Parse(File.ReadAllText(Path.Combine(output, "cases.pre-registered.json")));
            Assert.AreEqual(20, registrations.Count);
            Assert.True(registrations.All(row => (bool)row["exactFields"]));
            var rows = (JArray)report["results"];
            foreach (var row in rows)
            {
                Assert.True((bool)row["exact_fields"]);
                Assert.AreEqual(4, ((JObject)row["expected_proposal"]).Count);
            }
            foreach (string id in new[] { "feed", "show_not_give", "give_not_confide" })
            {
                string path = Path.Combine(output, "transfer_" + id);
                var fixture = JObject.Parse(File.ReadAllText(Path.Combine(path, "fixture-preparation.local.json")));
                var action = fixture["actions"][0];
                Assert.AreEqual("Rejected", (string)action["verdict"]);
                Assert.True((bool)action["receipt"]["applied"]);
                Assert.AreEqual(.3548, (double)action["roll"], .000001);
                var before = DsWorldSnapshot.Parse(File.ReadAllText(Path.Combine(path, "before.local.json")));
                Assert.AreEqual(1, before.Restore().RollCounter);
                Assert.AreEqual(2, before.Spent);
                Assert.AreEqual("player", before.Restore().Items[id == "feed" ? "smoked_fish" : "pact_page"].Holder);
            }
            var consumed = DsWorldSnapshot.Parse(File.ReadAllText(Path.Combine(output, "transfer_nonexistent_food", "before.local.json")));
            Assert.AreEqual("consumed", consumed.Restore().Items["smoked_fish"].Holder);
            var poor = DsWorldSnapshot.Parse(File.ReadAllText(Path.Combine(output, "transfer_insufficient_coins", "before.local.json")));
            Assert.AreEqual(0, poor.Restore().Coins);
            Assert.AreEqual("player", poor.Restore().Items["smoked_fish"].Holder);
            string previous = null;
            foreach (var row in rows.Where(row => ((string)row["id"]).StartsWith("transfer_sequence_")))
            {
                Assert.True((bool)row["sequence_before_matches_previous_after"]);
                if (previous != null) Assert.AreEqual(previous, (string)row["before_hash"]);
                previous = (string)row["after_hash"];
            }
        }

        [Test]
        public void 照明被错对象拒绝不得借两边都是False冒意图通过()
        {
            string output = Path.Combine(Root(), ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.Run(Root(), false, output, new WrongLightTarget(), transfer: true));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            var row = ((JArray)report["results"]).Single(value => (string)value["id"] == "transfer_use_crafted");
            Assert.AreEqual("whelp_absent", (string)row["reason_code"]);
            Assert.False((bool)row["intent_match"]);
            Assert.False((bool)row["expected_effect_match"]);
            Assert.False((bool)row["intent_check"]);
            Assert.True((bool)row["invariants_passed"], "候选与回放一致不能替代意图符合。");
        }

        [Test]
        public void 显式V2套题保留语义判据_旧原文执行对照只允许声明的提示差异()
        {
            string output = Path.Combine(Root(), ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.Run(Root(), false, output, semantics: true,
                actionPromptVersion: DsActionPrompt.Current));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual(DsActionPrompt.Current, (string)report["actionPromptVersion"]);
            Assert.AreEqual(24, (int)report["requested"]);
            Assert.AreEqual(24, (int)report["invariantChecks"]);
            Assert.AreEqual(24, (int)report["replayPassed"]);
            Assert.Less((int)report["intentChecks"], 24, "离线模板未因提示升级自动变成语义全通过。");
            Assert.True(File.Exists(Path.Combine(output, "prompt-version.pre-registered.json")));
            foreach (var row in (JArray)report["results"])
            {
                Assert.AreEqual(DsActionPrompt.Current, (string)row["action_prompt_version"]);
                Assert.True((bool)row["legacy_exact"]);
                if (row["zero_cost_rejection_unchanged"].Type != JTokenType.Null)
                    Assert.True((bool)row["zero_cost_rejection_unchanged"]);
            }
        }
    }
}
