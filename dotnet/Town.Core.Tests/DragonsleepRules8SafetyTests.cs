using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepRules8SafetyTests
    {
        static DsWorld World(string place = "docks")
        {
            var world = DsContent.NewWorld(7);
            world.PlayerPlace = place;
            return world;
        }

        static Dictionary<string, object> Act(string verb, string target, string item,
            IEnumerable<string> laws, string place = "docks", double difficulty = .3)
        {
            return new Dictionary<string, object>
            {
                ["verb"] = verb, ["target"] = target, ["item"] = item,
                ["place"] = place, ["laws"] = laws.Select(x => (object)x).ToList(),
                ["difficulty"] = difficulty, ["narration"] = "安静地尝试"
            };
        }

        static AiTrace Adjudicate(DsWorld world, IEnumerable<string> laws,
            string item = "smoked_fish")
        {
            var trace = new AiTrace();
            DsRules.Adjudicate(world, Act("lure", "whelp", item, laws, world.PlayerPlace), trace,
                new DsFixedRoll(.4));
            return trace;
        }

        [Test]
        public void 规则版本与支持规律重复引用完全等价()
        {
            Assert.That(DsExecutor.RulesVersion, Is.EqualTo("dragonsleep-rules-8"));
            var singleWorld = World();
            var duplicateWorld = World();
            singleWorld.Items["smoked_fish"].Holder = "player";
            duplicateWorld.Items["smoked_fish"].Holder = "player";
            singleWorld.PlayerKnows.Add("law:law_whelp_fish");
            duplicateWorld.PlayerKnows.Add("law:law_whelp_fish");

            var single = Adjudicate(singleWorld, new[] { "law_whelp_fish" });
            var duplicate = Adjudicate(duplicateWorld,
                new[] { "law_whelp_fish", "law_whelp_fish" });

            Assert.That(duplicate.Verdict.Difficulty, Is.EqualTo(single.Verdict.Difficulty));
            Assert.That(duplicate.Verdict.Roll, Is.EqualTo(single.Verdict.Roll));
            Assert.That(duplicateWorld.RollCounter, Is.EqualTo(singleWorld.RollCounter));
            Assert.That(DsWorldSnapshot.Capture(duplicateWorld, 0, 4).Hash,
                Is.EqualTo(DsWorldSnapshot.Capture(singleWorld, 0, 4).Hash));
            CollectionAssert.AreEqual(single.Changes.Select(x => x.ToString()),
                duplicate.Changes.Select(x => x.ToString()));
            CollectionAssert.AreEqual(single.Verdict.Notes, duplicate.Verdict.Notes);
            Assert.That(duplicate.Verdict.Notes.Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(duplicate.Verdict.Notes.Count));
        }

        [Test]
        public void 未知规律重复不增加判定差异且大小写不归一()
        {
            var plainWorld = World();
            var duplicateWorld = World();
            var upperWorld = World();
            plainWorld.Items["smoked_fish"].Holder = "player";
            duplicateWorld.Items["smoked_fish"].Holder = "player";
            upperWorld.Items["smoked_fish"].Holder = "player";
            foreach (var world in new[] { plainWorld, duplicateWorld, upperWorld })
                world.PlayerKnows.Add("law:law_whelp_fish");

            var plain = Adjudicate(plainWorld, Array.Empty<string>());
            var duplicate = Adjudicate(duplicateWorld,
                new[] { "law_missing", "law_missing" });
            var upper = Adjudicate(upperWorld, new[] { "LAW_WHELP_FISH" });

            Assert.That(duplicate.Verdict.Difficulty, Is.EqualTo(plain.Verdict.Difficulty));
            Assert.That(duplicateWorld.RollCounter, Is.EqualTo(plainWorld.RollCounter));
            Assert.That(DsWorldSnapshot.Capture(duplicateWorld, 0, 4).Hash,
                Is.EqualTo(DsWorldSnapshot.Capture(plainWorld, 0, 4).Hash));
            Assert.That(duplicate.ValidationIssues.Count, Is.EqualTo(1));
            Assert.That(upper.Verdict.Difficulty, Is.EqualTo(plain.Verdict.Difficulty));
            Assert.That(upper.ValidationIssues.Any(x => x.Contains("LAW_WHELP_FISH")), Is.True);
            Assert.That(upper.Verdict.Notes, Is.Empty);
        }

        [Test]
        public void 阻碍规律重复也只计一次并保留单条说明()
        {
            var oneWorld = World();
            var manyWorld = World();
            foreach (var world in new[] { oneWorld, manyWorld })
            {
                world.Items["iron_lock"].Holder = "player";
                world.Laws["law_test_hinder"] = new DsLaw
                {
                    Id = "law_test_hinder", Text = "冷铁会让雏龙害怕。",
                    HinderTags = new[] { "iron" }, Common = true
                };
            }

            var one = Adjudicate(oneWorld, new[] { "law_test_hinder" }, "iron_lock");
            var many = Adjudicate(manyWorld,
                new[] { "law_test_hinder", "law_test_hinder" }, "iron_lock");

            Assert.That(many.Verdict.Difficulty, Is.EqualTo(one.Verdict.Difficulty));
            Assert.That(many.Verdict.Roll, Is.EqualTo(one.Verdict.Roll));
            Assert.That(manyWorld.RollCounter, Is.EqualTo(oneWorld.RollCounter));
            CollectionAssert.AreEqual(one.Changes.Select(x => x.ToString()),
                many.Changes.Select(x => x.ToString()));
            CollectionAssert.AreEqual(one.Verdict.Notes, many.Verdict.Notes);
            Assert.That(many.Verdict.Notes.Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(many.Verdict.Notes.Count));
            Assert.That(DsWorldSnapshot.Capture(manyWorld, 0, 4).Hash,
                Is.EqualTo(DsWorldSnapshot.Capture(oneWorld, 0, 4).Hash));
        }

        [Test]
        public void 当前地点感知分别明示王城与龙骨岗的实际岗哨()
        {
            var castle = World("castle");
            var hill = World("skull_hill");
            hill.Day = 3;

            string castleGuards = (string)ContextBuilder.AdjudicatePerception(castle)["guards_here"];
            string hillGuards = (string)ContextBuilder.AdjudicatePerception(hill)["guards_here"];

            Assert.That(castleGuards, Is.EqualTo("铁笼和城门前都有卫兵站岗"));
            Assert.That(hillGuards, Is.EqualTo("奥斯里克在龙骨岗下设了岗哨"));
            StringAssert.DoesNotContain("龙骨岗", castleGuards);
            StringAssert.DoesNotContain("铁笼", hillGuards);
        }

        [Test]
        public void 异地隐藏岗位与演员信任变化不影响玩家裁剪感知()
        {
            var first = World("workshop");
            var second = World("workshop");
            second.Flags["guard:castle"] = "osric";
            second.Flags["guard:skull_hill"] = "osric";
            second.Actors["osric"].Trust = .99f;
            second.Actors["maren"].Trust = .01f;

            var firstView = ContextBuilder.PlanPerception(first, 4, null);
            var secondView = ContextBuilder.PlanPerception(second, 4, null);
            Assert.That(MiniJson.Write(secondView), Is.EqualTo(MiniJson.Write(firstView)));
            StringAssert.DoesNotContain("guard:castle", MiniJson.Write(secondView));
            StringAssert.DoesNotContain("guard:skull_hill", MiniJson.Write(secondView));
            StringAssert.DoesNotContain("trust_in_player", MiniJson.Write(secondView));
        }

        [Test]
        public void 笼中状态只有王城当前岗哨进入眼前限制()
        {
            var castle = World("castle");
            var hill = World("skull_hill");
            foreach (var world in new[] { castle, hill })
            {
                world.Day = 3;
                world.Whelp.CapturedBy = "osric";
                world.Whelp.Place = "castle";
                world.Flags["seen_by_guards"] = world.ClockText;
            }

            var castleView = ContextBuilder.PlanPerception(castle, 4, null);
            var hillView = ContextBuilder.PlanPerception(hill, 4, null);
            var castleRequirements = ((List<object>)castleView["current_requirements"]).Cast<string>().ToList();
            var hillRequirements = ((List<object>)hillView["current_requirements"]).Cast<string>().ToList();

            Assert.That((string)hillView["guards_here"], Is.Not.EqualTo("此刻看不到卫兵"));
            Assert.That(castleRequirements.Any(x => x.Contains("眼前有卫兵")), Is.True);
            Assert.That(hillRequirements.Any(x => x.Contains("此地不能立即rescue")), Is.False);
            Assert.That(hillRequirements.Any(x => x.Contains("不代表王城的岗位情况")), Is.True);
            Assert.That(hillRequirements.Any(x => x.Contains("当前在笼中")), Is.True);
        }

        [Test]
        public async Task 深夜等待终点被拒后修正上下文保留详细问题且世界不变()
        {
            var bad = new DsPlan { Goal = "归巢", Summary = "错误等待" };
            bad.Steps.Add(new DsPlanStep
            {
                Id = "1", Op = "wait", Place = "workshop", Until = "第3天·深夜"
            });
            var provider = new QueueProvider(new RawProvider(MiniJson.Write(bad.ToRecord())),
                new RuleProvider());
            var session = new DragonsleepSession(provider, 7, new DsFixedRoll(.9999));
            var run = new DsPlanRun(session, "归巢", true);
            string before = session.CaptureState().Hash;

            var rejected = await run.ProposeAsync();
            Assert.That(rejected.FailureCode, Is.EqualTo("schema_invalid"));
            var repaired = await run.RepairProposalAsync();
            var context = (Dictionary<string, object>)MiniJson.Parse(repaired.PlanContext);
            var feedback = (Dictionary<string, object>)context["rejected_proposal"];
            var issues = ((List<object>)feedback["issues"]).Cast<string>().ToList();

            Assert.That(feedback.Str("code"), Is.EqualTo("schema_invalid"));
            StringAssert.Contains("深夜由居民行动，不能作为等待终点", feedback.Str("reason"));
            Assert.That(issues.Count, Is.GreaterThan(0).And.LessThanOrEqualTo(3));
            Assert.That(issues.All(x => x.Length <= 140), Is.True);
            StringAssert.Contains("深夜由居民行动，不能作为等待终点", MiniJson.Write(issues));
            Assert.That(session.CaptureState().Hash, Is.EqualTo(before));
        }

        [Test]
        public async Task 超长未知字段反馈有界且不注入原文或私有状态()
        {
            const string marker = "RAW_UNKNOWN_FIELD_SECRET_8";
            var malformed = new Dictionary<string, object>
            {
                ["goal"] = "归巢", ["summary"] = "坏提案", ["choices"] = new List<object>(),
                ["steps"] = new List<object>(), ["unknown_field"] = marker + new string('x', 5000)
            };
            var provider = new QueueProvider(new RawProvider(MiniJson.Write(malformed)),
                new RuleProvider());
            var session = new DragonsleepSession(provider, 7, new DsFixedRoll(.9999));
            session.World.Actors["maren"].Trust = .99f;
            session.World.Beliefs.Add(new DsBelief
            {
                Id = "private_sentinel", Owner = "maren", Topic = "secret", Text = "私有认知"
            });
            var run = new DsPlanRun(session, "归巢", true);

            await run.ProposeAsync();
            var repaired = await run.RepairProposalAsync();
            var context = (Dictionary<string, object>)MiniJson.Parse(repaired.PlanContext);
            var feedback = (Dictionary<string, object>)context["rejected_proposal"];
            var issues = ((List<object>)feedback["issues"]).Cast<string>().ToList();
            string serialized = MiniJson.Write(feedback);

            Assert.That(feedback.Str("code"), Is.EqualTo("schema_invalid"));
            Assert.That(issues.Count, Is.GreaterThan(0).And.LessThanOrEqualTo(3));
            Assert.That(issues.All(x => x.Length <= 140), Is.True);
            StringAssert.Contains("计划包含未声明的字段", feedback.Str("reason"));
            StringAssert.DoesNotContain(marker, serialized);
            StringAssert.DoesNotContain("private_sentinel", serialized);
            StringAssert.DoesNotContain("trust_in_player", serialized);
            StringAssert.DoesNotContain(marker, repaired.PlanContext);
            StringAssert.DoesNotContain("private_sentinel", repaired.PlanContext);
            StringAssert.DoesNotContain("trust_in_player", repaired.PlanContext);
        }

        sealed class RawProvider : IAiProvider
        {
            readonly string _raw;
            public RawProvider(string raw) => _raw = raw;
            public string Name => "rules-8-坏提案";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
                Task.FromResult(new ProviderResult
                {
                    ProviderName = Name, RawText = _raw, Simulated = true, EstimatedUsd = 0m
                });
        }

        sealed class QueueProvider : IAiProvider
        {
            readonly Queue<IAiProvider> _providers;
            public QueueProvider(params IAiProvider[] providers) => _providers = new Queue<IAiProvider>(providers);
            public string Name => "rules-8-队列";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                var provider = _providers.Count > 1 ? _providers.Dequeue() : _providers.Peek();
                return provider.CompleteAsync(packet, ct);
            }
        }
    }
}
