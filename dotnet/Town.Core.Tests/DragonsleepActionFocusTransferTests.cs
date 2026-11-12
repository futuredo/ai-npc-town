using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepActionFocusTransferTests
    {
        static string Root()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dotnet/Town.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("repository_not_found");
        }

        [Test]
        public void 结构拒绝不能借四槽匹配成为意图与诱引通过()
        {
            var session = new DragonsleepSession(new RuleProvider(), 131);
            session.World.PlayerPlace = "docks";
            var proposal = new Dictionary<string, object>
            {
                ["verb"] = "lure", ["target"] = "whelp", ["item"] = "", ["place"] = "docks",
                ["laws"] = new List<object>(), ["narration"] = "轻声招呼"
            };
            var trace = session.RunStepAsync(proposal, "结构负例").GetAwaiter().GetResult();
            Assert.AreEqual("schema_invalid", trace.FailureCode);
            trace.KernelCode = "host_rejected"; // 单独测试晋升计分，不伪装为内核集成回执。
            Assert.IsTrue(ActionKernelResearch.SemanticActionMatches(proposal, "lure", "whelp", "", "docks"));
            Assert.IsFalse(ActionKernelResearch.TransferSuccessfulIntent(trace, true));
        }

        [Test]
        public void 拿走鱼不冒购买成功前缀_真正购买必须具有三银币代价()
        {
            foreach (string verb in new[] { "take", "buy" })
            {
                var session = new DragonsleepSession(new RuleProvider(), 131);
                var before = session.CaptureState();
                var proposal = new Dictionary<string, object>
                {
                    ["verb"] = verb, ["target"] = verb == "buy" ? "marta" : "", ["item"] = "smoked_fish",
                    ["place"] = "market", ["laws"] = new List<object>(), ["difficulty"] = .1, ["narration"] = "取鱼"
                };
                var trace = session.RunStepAsync(proposal, "购买前缀负例").GetAwaiter().GetResult();
                Assert.AreEqual(VerdictKind.Accepted, trace.Verdict.Kind);
                Assert.AreEqual("player", session.World.Items["smoked_fish"].Holder);
                Assert.AreEqual(verb == "buy" ? 9 : 12, session.World.Coins);
                trace.KernelCode = "committed"; // 评分函数以真实宿主后果测试，内核契约另有独立集成测试。
                Assert.AreEqual(verb == "buy", ActionKernelResearch.TransferRepeatPrefix("purchase_control",
                    before, session.CaptureState(), trace, true));
            }
        }

        [Test]
        public void 候选资格必须等待完整成功批次_不完整对照和后续故障不能变绿()
        {
            var rows = new List<Dictionary<string, object>>();
            string[][] verbs = {
                new[] { "feed", "feed", "lure", "lead" }, new[] { "give", "show", "observe", "confide" },
                new[] { "use", "use", "observe", "search" }, new[] { "buy", "buy", "observe", "wait" },
                new[] { "show", "confide", "observe", "wait" }, new[] { "observe", "wait", "observe", "wait" }
            };
            string[] repeats = { "feeding", "custody", "illumination", "purchase_control", "none", "none" };
            foreach (string version in new[] { DsActionPrompt.Current, DsActionPrompt.Focused })
                for (int family = 0; family < 6; family++)
                    for (int seed = 0; seed < 2; seed++)
                        for (int step = 0; step < 4; step++)
                            rows.Add(new Dictionary<string, object>
                            {
                                ["action_prompt_version"] = version, ["expected_verb"] = verbs[family][step],
                                ["focused_effect_match"] = true, ["successful_intent"] = true,
                                ["invariants_passed"] = true, ["provider_error"] = null,
                                ["repeat_kind"] = repeats[family], ["step"] = step + 1,
                                ["repeat_probe_applicable"] = family < 4 && step == 1,
                                ["repeat_probe_passed"] = family < 4 && step == 1 ? (object)true : null,
                                ["slot_control"] = family == 4
                            });
            string output = Path.Combine(Root(), ".local", "action-focus-transfer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            foreach (string state in new[] { "incomplete", "stopped", "error", "completed" })
            {
                var selected = state == "incomplete" ? rows.Where((_, index) => index < 44 || index >= 48).ToList() : rows;
                bool completed = state != "stopped";
                var initial = new JObject
                {
                    ["status"] = completed ? "completed" : "stopped_invariant_or_provider_error",
                    ["arms"] = new JArray(new[] { DsActionPrompt.Current, DsActionPrompt.Focused }.Select(v => new JObject
                    {
                        ["version"] = v, ["requested"] = selected.Count(r => (string)r["action_prompt_version"] == v),
                        ["probes"] = JArray.FromObject(new Dictionary<string, int>
                        { ["observe"] = 12, ["wait"] = 8, ["confide"] = 4, ["lure"] = 2 }
                            .Select(p => new { verb = p.Key, total = p.Value, passed = p.Value }))
                    }))
                };
                rows[0]["provider_error"] = state == "error" ? "offline_fault" : null;
                string path = Path.Combine(output, "summary.json");
                File.WriteAllText(path, initial.ToString());
                ActionKernelResearch.CompleteFocusTransferSummary(output, selected);
                Assert.AreEqual(state == "completed", (bool)JObject.Parse(File.ReadAllText(path))["candidateEligible"], state);
            }
        }

        [Test]
        public void 独立新措辞对照冻结种子与门槛_不把规则离线或空集合当候选通过()
        {
            string output = Path.Combine(Root(), ".local", "action-focus-transfer-tests", Guid.NewGuid().ToString("N"));
            Assert.AreEqual(0, ActionKernelResearch.RunFocus(Root(), false, output, transfer: true));
            var summary = JObject.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
            Assert.AreEqual(ActionKernelResearch.FocusTransferSuite, (string)summary["suite"]);
            Assert.AreEqual(96, (int)summary["requested"]);
            Assert.AreEqual(96, (int)summary["invariants"]);
            Assert.AreEqual(96, (int)summary["replayPassed"]);
            Assert.AreEqual(24, ((JArray)summary["episodes"]).Count);
            Assert.AreEqual(48, ((JArray)summary["pairs"]).Count);
            Assert.IsFalse((bool)summary["candidateEligible"]);
            foreach (var arm in summary["arms"])
            {
                Assert.AreEqual(48, (int)arm["requested"]);
                Assert.IsFalse((bool)arm["criticalGatePassed"]);
                foreach (var repeat in arm["repeatGates"])
                    Assert.AreEqual(2, (int)repeat["total"]);
                foreach (var probe in arm["probes"])
                {
                    string verb = (string)probe["verb"];
                    int expected = verb == "observe" ? 12 : verb == "wait" ? 8 : verb == "confide" ? 4 : 2;
                    Assert.AreEqual(expected, (int)probe["total"]);
                }
            }
            foreach (var row in summary["results"])
            {
                Assert.That((int)row["seed"], Is.EqualTo(131).Or.EqualTo(137));
                Assert.IsTrue((bool)row["before_matches_previous_after"]);
                Assert.AreEqual(DsActionPerception.Current, (string)row["perception_version"]);
                Assert.That(row["expected_target"].Type, Is.EqualTo(JTokenType.String));
                Assert.That(row["expected_item"].Type, Is.EqualTo(JTokenType.String));
                if ((string)row["repeat_kind"] == "none")
                {
                    Assert.IsFalse((bool)row["repeat_probe_applicable"]);
                    Assert.AreEqual(JTokenType.Null, row["repeat_probe_passed"].Type);
                }
            }
            var contract = JObject.Parse(File.ReadAllText(Path.Combine(output, "focus-contract.pre-registered.json")));
            Assert.AreEqual(ActionKernelResearch.FocusTransferSuite, (string)contract["suite"]);
            Assert.AreEqual(ActionKernelResearch.FocusTransferGate, (string)contract["criticalGate"]);
            Assert.AreEqual(12, ((JArray)contract["fixtures"]).Count);
            Assert.AreEqual(24, contract["fixtures"].SelectMany(f => f["inputs"]).Select(x => (string)x).Distinct().Count());
            Assert.AreEqual(12, Directory.GetFiles(Path.Combine(output, "fixtures.local-only"), "*.json").Length);
            Assert.AreEqual("ds-action-prompt-v2", DsActionPrompt.Current);
            Assert.AreEqual("ds-action-prompt-v3", DsActionPrompt.Focused);
        }
    }
}
