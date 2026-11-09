using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>
    /// rules-6 重构基线。只屏蔽轨迹的 total_ms 和阶段 ms，保留提示字节、键序、回执和全部世界后果。
    /// 不提供自动更新快照开关；预期值的修改必须经过人工审查。
    /// </summary>
    public class DragonsleepGoldenTests
    {
        const string Fingerprint = "bfac3c38bc8b20fd156211217eda55385a4f7875abe37f1075fcb8026ea66147";
        static string Fixture(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", name);

        [Test]
        public async Task 冻结起点的计划与全部居民感知_提示与提案字节不变()
        {
            var cases = PlanCases.Frozen();
            Assert.AreEqual(Fingerprint, PlanCases.Fingerprint(cases), "冻结测试集不得修改");
            var actual = new Dictionary<string, object>();
            foreach (var c in cases)
            {
                var s = new DragonsleepSession(new RuleProvider(), 7);
                c.Setup(s);
                var hashes = new Dictionary<string, object>
                {
                    ["plan_perception"] = Hash(TaskPerceptionView.Capture(s.World, AiTaskKind.Plan, "player").Json),
                };
                foreach (var id in s.World.Actors.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    hashes["dialogue:" + id] = Hash(NpcPerceptionView.Capture(s.World, id).Json);
                    hashes["agent:" + id] = Hash(TaskPerceptionView.Capture(s.World, AiTaskKind.AgentPlan, id).Json);
                }
                var t = await s.PlanAsync(c.Input);
                hashes["prompt"] = DsReplayFrame.Fingerprint(t.Prompt);
                hashes["proposal"] = Hash(t.Provider.RawText);
                hashes["plan_record"] = Hash(MiniJson.Write(t.PlanReport.Plan.ToRecord()));
                actual[c.Id] = hashes;
            }
            Check("perceptions.json", actual);
        }

        [Test]
        public async Task 固定种子完整离线局_全部轨迹回执与终局哈希不变()
        {
            var s = new DragonsleepSession(new RuleProvider(), 7932);
            var run = new DsPlanRun(s, "把小烬安全送回母亲身边，避开卫兵。", requireHomecoming: true);
            var timeline = new List<object>();
            s.TraceCompleted += t => timeline.Add(new Dictionary<string, object>
            {
                ["type"] = "ai", ["trace"] = StableTrace(t), ["prompt_hash"] = DsReplayFrame.Fingerprint(t.Prompt),
            });
            s.RuleEventCompleted += e => timeline.Add(new Dictionary<string, object>
            {
                ["type"] = "rule", ["event"] = e.ToRecord(),
            });
            await run.ProposeAsync();
            int operations = 0;
            while (operations++ < 120 && run.PlanTraces.Count < 10)
            {
                if (run.Status == "awaiting_confirmation") run.Accept();
                else if (run.Status == "running") await run.StepAsync();
                else if (run.Status == "planning") await run.RepairProposalAsync();
                else if (run.Status == "needs_replan")
                {
                    await run.ReplanAsync();
                    if (run.PlanTraces.AsEnumerable().Reverse().TakeWhile(t => !run.MeetsObjective(t)).Count() >= 3)
                        run.Abandon();
                }
                else break;
            }
            Assert.Less(operations, 120, "黄金局必须有界结束，不能靠挂起隐瞒差异");
            for (int i = 0; i < 16 && !s.World.FinalDawn; i++) await s.EndPhaseAsync();
            Assert.IsTrue(s.World.FinalDawn);
            Assert.IsTrue(s.Traces.All(t => t.Provider == null || t.Provider.Simulated));
            var actual = new Dictionary<string, object>
            {
                ["timeline"] = Hash(MiniJson.Write(timeline)),
                ["run"] = Hash(MiniJson.Write(run.ToRecord())),
                ["world"] = s.CaptureState().Hash,
                ["ending"] = DsEndings.Evaluate(s.World).Title,
                ["traces"] = s.Traces.Count,
                ["rule_events"] = s.RuleEvents.Count,
            };
            Check("episode.json", actual);
        }

        [Test]
        public async Task 重构前已落盘的rules6录制_全部严格回放()
        {
            Assert.AreEqual("815b8abd9dba89492876ebca98b7090ff1ba3055720acbe89f0951c8425f922e",
                Hash(File.ReadAllText(Fixture("recorded-plan-calls.jsonl"))), "既有录制不可重生成");
            var lines = File.ReadAllLines(Fixture("recorded-plan-calls.jsonl"));
            Assert.AreEqual(14, lines.Length);
            foreach (var line in lines)
            {
                var d = (Dictionary<string, object>)MiniJson.Parse(line);
                var t = d.Obj("trace");
                var before = DsWorldSnapshot.Parse(MiniJson.Write(d.Obj("before")));
                var sections = ((IEnumerable<object>)d["prompt"]).Cast<Dictionary<string, object>>().ToList();
                var prompt = new PromptPacket
                {
                    Kind = AiTaskKind.Plan, ActorId = "player", SchemaName = "ds_plan_v1",
                    Instructions = d.Str("instructions"),
                };
                foreach (var section in sections)
                    prompt.Sections.Add(new PromptSection
                    {
                        Name = section.Str("name"), Text = section.Str("text"), Untrusted = (bool)section["untrusted"],
                    });
                var frame = new DsReplayFrame
                {
                    RulesVersion = t.Str("rules_version"), Before = before, After = before,
                    Kind = AiTaskKind.Plan, ActorId = t.Str("actor"), Input = t.Str("input"),
                    PlanContext = t.Str("plan_context"), PromptHash = DsReplayFrame.Fingerprint(prompt),
                    Presented = t.Str("presented"), Failure = t.Str("failure"),
                    Verdict = (VerdictKind)Enum.Parse(typeof(VerdictKind), t.Str("verdict")),
                    Response = new ProviderResult { RawText = t.Str("raw"), Simulated = true },
                };
                Assert.AreEqual(before.Hash, t.Obj("receipt").Str("after_hash"), "规划不能改变录制世界");
                Assert.IsTrue(await frame.VerifyAsync(), d.Str("scope"));
                frame.PromptHash = new string('0', 64);
                Assert.IsFalse(await frame.VerifyAsync(), "严格回放必须拒绝提示漂移");
            }
        }

        static string Hash(string json) => DsWorldSnapshot.Digest(json);

        static Dictionary<string, object> StableTrace(AiTrace t)
        {
            var record = t.ToRecord();
            record.Remove("total_ms");
            foreach (var stage in ((IEnumerable<object>)record["stages"]).Cast<Dictionary<string, object>>()) stage.Remove("ms");
            return record;
        }

        static void Check(string file, Dictionary<string, object> actual)
        {
            string json = MiniJson.Write(actual);
            var expected = MiniJson.Write(MiniJson.Parse(File.ReadAllText(Fixture(file))));
            Assert.AreEqual(expected, json, file);
        }
    }
}
