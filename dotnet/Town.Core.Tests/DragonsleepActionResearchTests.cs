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
    }
}
