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
    /// 当前 rules-8 黄金与原样保留的 rules-6/7 历史证据。新版本不冒充与旧行为相同。
    /// 不提供自动更新快照开关；预期值的修改必须经过人工审查。
    /// </summary>
    public class DragonsleepGoldenTests
    {
        const string Fingerprint = "bfac3c38bc8b20fd156211217eda55385a4f7875abe37f1075fcb8026ea66147";
        static string Fixture(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", name);

        [Test]
        public async Task rules8冻结起点的计划与全部居民感知_提示与提案字节固定()
            => Check("rules-8/perceptions.json", await CapturePerceptionsAsync());

        public static async Task<Dictionary<string, object>> CapturePerceptionsAsync()
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
            return actual;
        }

        [Test]
        public async Task rules8固定种子完整离线局_全部轨迹回执与终局哈希固定()
            => Check("rules-8/episode.json", await CaptureEpisodeAsync());

        public static async Task<Dictionary<string, object>> CaptureEpisodeAsync()
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
            return actual;
        }

        [Test]
        public async Task 重构前已落盘的rules6录制_当前版本严格拒绝_历史原件保持不变()
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
                Assert.AreEqual("dragonsleep-rules-6", frame.RulesVersion);
                Assert.AreEqual("dragonsleep-rules-8", DsExecutor.RulesVersion);
                Assert.IsFalse(await frame.VerifyAsync(), "不能跨规则版本冒充严格回放成功：" + d.Str("scope"));
                Assert.IsFalse(await frame.VerifyExecutionAsync(), "执行回放也不能跳过版本核对");
                frame.PromptHash = new string('0', 64);
                Assert.IsFalse(await frame.VerifyAsync(), "严格回放必须拒绝提示漂移");
            }
        }

        [Test]
        public void rules6黄金原件保留_版本升级不覆盖历史预期()
        {
            Assert.AreEqual("94cd06ee5f02d19aa5fee1e1604323d84a199173addb4cffe767880300cd06f6", Hash(File.ReadAllText(Fixture("perceptions.json"))));
            Assert.AreEqual("2b9ed4ddd12f3654dd2e7b2baeb36993c394f0ee25b7c2e63882c32ba31a38b8", Hash(File.ReadAllText(Fixture("episode.json"))));
        }

        [Test]
        public async Task rules7工件原样保留_两个回放入口均拒绝跨版本()
        {
            Assert.AreEqual("c1977ab4c652975b874d22437bf5158c062198382fc0100d1adc7b4c79e341cc",
                Hash(File.ReadAllText(Fixture("rules-7/perceptions.json"))));
            Assert.AreEqual("23f40a3b1e331676850c8a955061512fb170c1ff3b45c2670d89885ce0207c84",
                Hash(File.ReadAllText(Fixture("rules-7/episode.json"))));
            string file = Fixture("rules-7/plans.replay.jsonl");
            Assert.AreEqual("dc9ac1565653da3f6ef3f3b41c08bf4f65cce1a8d47e5c2589c56908aa735958", Hash(File.ReadAllText(file)));
            var lines = File.ReadAllLines(file); Assert.AreEqual(14, lines.Length);
            foreach (var line in lines)
            {
                var frame = DsReplayFrame.Parse(line);
                Assert.AreEqual("dragonsleep-rules-7", frame.RulesVersion);
                Assert.IsFalse(await frame.VerifyAsync(), "旧规则不冒充当前严格回放成功");
                Assert.IsFalse(await frame.VerifyExecutionAsync(), "执行回放也不得越过规则版本门禁");
            }
        }

        [Test]
        public async Task rules8新录制严格回放_提示与终态篡改被拒绝()
        {
            string file = Fixture("rules-8/plans.replay.jsonl");
            Assert.AreEqual("3c8c88a9e15aeb5810abbe7251a453375e115f7ffd863cfe9d1a7f59d618ec58", Hash(File.ReadAllText(file)));
            var lines = File.ReadAllLines(file);
            Assert.AreEqual(14, lines.Length);
            foreach (string line in lines)
            {
                var frame = DsReplayFrame.Parse(line);
                Assert.AreEqual("dragonsleep-rules-8", frame.RulesVersion);
                Assert.IsTrue(await frame.VerifyAsync(), frame.Input);
                frame.PromptHash = new string('0', 64);
                Assert.IsFalse(await frame.VerifyAsync());
                var changed = DsReplayFrame.Parse(line);
                var world = changed.After.Restore(); world.Coins++;
                changed.After = DsWorldSnapshot.Capture(world, changed.After.Spent, changed.After.PhaseBudget);
                Assert.IsFalse(await changed.VerifyAsync(), "正确提示也不能接受错误终态");
            }
        }

        [Test]
        public void 规则八升级差分_仅计划感知提示与整局轨迹允许变化_其余预期不漂移()
        {
            var old = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Fixture("rules-7/perceptions.json")));
            var current = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Fixture("rules-8/perceptions.json")));
            foreach (string id in old.Keys)
                foreach (var pair in old.Obj(id).Where(x => x.Key != "plan_perception" && x.Key != "prompt"))
                    Assert.AreEqual(pair.Value, current.Obj(id)[pair.Key], id + ":" + pair.Key);
            var before = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Fixture("rules-7/episode.json")));
            var after = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Fixture("rules-8/episode.json")));
            foreach (var pair in before.Where(x => x.Key != "timeline")) Assert.AreEqual(pair.Value, after[pair.Key], pair.Key);
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
