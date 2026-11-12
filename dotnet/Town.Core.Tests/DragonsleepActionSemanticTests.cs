using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepActionSemanticTests
    {
        static Dictionary<string, object> Proposal(string place) => new Dictionary<string, object>
        {
            ["verb"] = "hide", ["target"] = "whelp", ["item"] = "travel_cloak", ["place"] = place
        };

        [TestCase("", true)]
        [TestCase("docks", true)]
        [TestCase("market", false)]
        [TestCase("unknown_place", false)]
        [TestCase("Docks", false)]
        [TestCase(" ", false)]
        [TestCase(null, false)]
        public void 地点语义仅允许原始空串或本次行动前当前位置(string place, bool expected)
        {
            Assert.AreEqual(expected, ActionKernelResearch.SemanticActionMatches(
                Proposal(place), "hide", "whelp", "travel_cloak", "docks"));
        }

        [TestCase("verb")]
        [TestCase("target")]
        [TestCase("item")]
        [TestCase("place")]
        public void 缺字段或非字符串不能借语义等价当作空串(string field)
        {
            var proposal = Proposal("");
            proposal.Remove(field);
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "hide", "whelp", "travel_cloak", "docks"));
            proposal[field] = 1;
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "hide", "whelp", "travel_cloak", "docks"));
            proposal[field] = null;
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "hide", "whelp", "travel_cloak", "docks"));
        }

        [Test]
        public void 错动词错对象及期望空目标仍严格匹配_不把不支持当原动作拒绝()
        {
            var proposal = Proposal("");
            proposal["verb"] = "unsupported";
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "hide", "whelp", "travel_cloak", "docks"));
            proposal = Proposal("");
            proposal["target"] = "pip";
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "hide", "whelp", "travel_cloak", "docks"));
            proposal = Proposal("");
            proposal["verb"] = "use";
            proposal["item"] = "crafted_1";
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "use", "", "crafted_1", "docks"));
            proposal["target"] = "";
            Assert.True(ActionKernelResearch.SemanticActionMatches(proposal, "use", "", "crafted_1", "docks"));
            Assert.False(ActionKernelResearch.SemanticActionMatches(proposal, "use", "", "crafted_1", ""));
        }

        sealed class EmptyPlace : IAiProvider
        {
            public string Name => "离线地点等价与拒绝分类反例";
            public bool IsSimulated => true;
            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                var result = await new RuleProvider().CompleteAsync(packet, ct);
                var proposal = (Dictionary<string, object>)MiniJson.Parse(result.RawText);
                proposal["place"] = "";
                if (packet.OriginalPlayerInput == "即使它目前不信任我，我仍尝试拿旅行斗篷为小烬安排藏身。")
                {
                    proposal["verb"] = "hide";
                    proposal["target"] = "whelp";
                    proposal["item"] = "travel_cloak";
                }
                if (packet.OriginalPlayerInput == "我尝试拿取自己已经持有的同一把冷铁笼锁，并不要求复制它。")
                {
                    proposal["verb"] = "unsupported";
                    proposal["target"] = "";
                    proposal["item"] = "";
                }
                result.ProviderName = Name;
                result.RawText = MiniJson.Write(proposal);
                return result;
            }
        }

        [Test]
        public void 新语义套题提前落盘_合法空地点不扣分但错误拒绝分类不能假绿()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "dotnet", "Town.sln"))) root = root.Parent;
            Assert.NotNull(root);
            string output = Path.Combine(root.FullName, ".local", "action-research-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.Run(root.FullName, false, output, new EmptyPlace(), semantics: true));
            var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual("ds-action-kernel-semantics-1", (string)report["suite"]);
            Assert.AreEqual(24, (int)report["requested"]);
            Assert.AreEqual(24, (int)report["invariantChecks"]);
            Assert.True(File.Exists(Path.Combine(output, "semantic-contract.pre-registered.json")));
            var rows = (JArray)report["results"];
            Assert.True(rows.All(row => (string)row["place_policy"] == "empty_or_current_before_location"));
            var hidden = rows.Single(row => (string)row["id"] == "semantics_hide_low_trust");
            Assert.True((bool)hidden["intent_check"]);
            Assert.False((bool)hidden["literal_fields_match"]);
            Assert.AreEqual("whelp_trust_low", (string)hidden["reason_code"]);
            Assert.AreEqual(0, (int)hidden["time_cost"]);
            var held = rows.Single(row => (string)row["id"] == "semantics_already_held");
            Assert.AreEqual("intent_unsupported", (string)held["reason_code"]);
            Assert.False((bool)held["intent_match"]);
            Assert.False((bool)held["expected_effect_match"]);
            Assert.False((bool)held["intent_check"]);
            Assert.True(rows.Where(row => ((string)row["id"]).StartsWith("semantics_sequence_"))
                .All(row => (bool)row["sequence_before_matches_previous_after"]));
            Assert.Throws<ArgumentException>(() => ActionKernelResearch.Run(root.FullName, false, output,
                transfer: true, semantics: true));
        }
    }
}
