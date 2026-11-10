using System.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public sealed class DragonsleepSocialBoundaryTests
    {
        static DsWorld World(string place = "cathedral")
        {
            var world = new DragonsleepSession(new RuleProvider()).World;
            world.PlayerPlace = place;
            world.Items["pact_page"].Holder = "player";
            return world;
        }

        // These matrix cases exercise isolated rule effects, not a claim of committed session receipts.
        static AiTrace Rule(DsWorld world, string verb, string target, string item, double roll = .9999)
        {
            var trace = new AiTrace();
            var step = new DsPlanStep { Verb = verb, Target = target, Item = item };
            DsRules.Adjudicate(world, step.ToProposal(world.PlayerPlace), trace, new DsFixedRoll(roll));
            return trace;
        }

        [TestCase("maren", "cathedral", .3f)]
        [TestCase("osric", "market", .2f)]
        public void 首次失败不抢收益_再次成功后不刷新首见时间(string actor, string place, float gain)
        {
            var world = World(place);
            float before = world.Actors[actor].Trust;
            string key = "shown:pact_page:" + actor;
            Assert.That(Rule(world, "show", actor, "pact_page", 0).Verdict.Kind, Is.EqualTo(VerdictKind.Rejected));
            Assert.That(world.Flags.ContainsKey(key), Is.False);
            Assert.That(world.Actors[actor].Trust, Is.EqualTo(before));
            Rule(world, "show", actor, "pact_page");
            string first = world.Flags[key];
            world.Phase = DayPhase.Noon;
            var repeated = Rule(world, "show", actor, "pact_page");
            Assert.That(world.Flags[key], Is.EqualTo(first));
            Assert.That(world.Actors[actor].Trust, Is.EqualTo(before + gain).Within(.00001f));
            Assert.That(repeated.Changes.Any(c => c.Path == "flags.shown"), Is.False);
        }

        [Test]
        public void 总管重复出示不加信任_后来满足门槛仍可开笼()
        {
            var world = World("market");
            world.Actors["osric"].Trust = .1f;
            world.Whelp.CapturedBy = "osric";
            Rule(world, "show", "osric", "pact_page");
            Assert.That(world.Whelp.CapturedBy, Is.EqualTo("osric"));
            world.Actors["osric"].Trust = .5f; // Independent fixture of a later legitimate trust change.
            Rule(world, "show", "osric", "pact_page");
            Assert.That(world.Actors["osric"].Trust, Is.EqualTo(.5f));
            Assert.That(world.Whelp.CapturedBy, Is.Null);
            Assert.That(world.Whelp.Following, Is.True);
        }

        [Test]
        public void 失败交付与失败取回不改变保管资格()
        {
            var world = World();
            Rule(world, "give", "maren", "pact_page", 0);
            Assert.That(world.Items["pact_page"].Holder, Is.EqualTo("player"));
            Assert.That(world.Flags.ContainsKey("pact_custody:maren"), Is.False);
            Rule(world, "give", "maren", "pact_page");
            Rule(world, "confide", "maren", "");
            Rule(world, "take", "", "pact_page", 0);
            Assert.That(world.Items["pact_page"].Holder, Is.EqualTo("maren"));
            Assert.That(world.Flags.ContainsKey("pact_custody:maren"), Is.True);
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 仅持有人或普通赠物不能伪造玩家交付原件(bool fakeGift)
        {
            var world = World();
            world.Actors["maren"].Trust = .05f;
            if (fakeGift)
            {
                world.Items["iron_lock"].Holder = "player";
                world.Items["iron_lock"].Tags.Add("pact");
                Rule(world, "give", "maren", "iron_lock");
            }
            else world.Items["pact_page"].Holder = "maren";
            Assert.That(world.Flags.ContainsKey("pact_custody:maren"), Is.False);
            Assert.That(Rule(world, "confide", "maren", "").Verdict.Kind, Is.EqualTo(VerdictKind.Partial));
            Assert.That(world.Flags.ContainsKey("ally:maren"), Is.False);
        }

        [Test]
        public void 无旧线索也能从亲历保管继续找梅伦_不再搜索另一份原件()
        {
            var world = World();
            world.Beliefs.RemoveAll(b => b.Id == "b_maren_page");
            Rule(world, "give", "maren", "pact_page");
            var perception = ContextBuilder.PlanPerception(world, 4, null);
            var plan = BaselinePlanner.Plan(perception, "请梅伦帮忙");
            var branch = plan.Steps.Where(s => s.When == "helper=maren").ToList();
            Assert.That(branch.Any(s => s.Verb == "confide" && s.Target == "maren"), Is.True);
            Assert.That(branch.Any(s => s.Verb == "show" || s.Verb == "give" || s.Verb == "search"), Is.False);
            StringAssert.Contains("你已交由梅伦保管核验", MiniJson.Write(perception));
        }

        [Test]
        public void 直接交付也认契约证据_但不能替代真实团聚或新约信任门槛()
        {
            var world = World();
            world.Actors["maren"].Trust = .8f;
            Rule(world, "give", "maren", "pact_page");
            Rule(world, "confide", "maren", "");
            Assert.That(DsEndings.Evaluate(world).Id, Is.EqualTo("unresolved"));
            world.Whelp.WithMother = true;
            Assert.That(DsEndings.Evaluate(world).Id, Is.EqualTo("new_pact"));
            world.Actors["maren"].Trust = .1f;
            Assert.That(DsEndings.Evaluate(world).Id, Is.EqualTo("homecoming"));
            world.Whelp.WithMother = false;
            world.Whelp.CapturedBy = "osric";
            Assert.That(DsEndings.Evaluate(world).Id, Is.EqualTo("iron_cage"));
        }
    }
}
