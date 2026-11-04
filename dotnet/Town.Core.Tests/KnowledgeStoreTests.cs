using System;
using System.Linq;
using NUnit.Framework;
using Town.Core.Knowledge;
using Town.Core.World;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Core.Tests
{
    public class KnowledgeStoreTests
    {
        private static readonly GameTime T = GameTime.At(1, 9);

        [Test]
        public void 视角只包含自己的认知()
        {
            var k = new KnowledgeStore();
            k.Learn("a", P.Location, true, 1f, SourceType.Experienced, T);
            var viewB = k.ViewFor("b");
            Assert.IsFalse(viewB.Knows(P.Location));
            Assert.IsEmpty(viewB.ActiveBeliefs);
            var foreignId = k.ViewFor("a").ActiveBeliefs.Single().Id;
            Assert.IsFalse(viewB.OwnsActive(foreignId), "不能引用别人的认知");
        }

        [Test]
        public void 听说必须记录告知者()
        {
            var k = new KnowledgeStore();
            Assert.Throws<ArgumentException>(() => k.Learn("a", P.Stole, true, 0.5f, SourceType.ToldBy, T));
        }

        [Test]
        public void 看到证据纠正推断_旧认知保留为历史()
        {
            var k = new KnowledgeStore();
            var old = k.Learn("amu", P.Returned, false, 0.7f, SourceType.Inferred, T);
            var fresh = k.Learn("amu", P.Returned, true, 1f, SourceType.SawEvidence, T.AddMinutes(30), evidenceId: "C1");

            var view = k.ViewFor("amu");
            Assert.AreEqual(fresh.Id, view.Current(P.Returned).Id);
            Assert.AreEqual(fresh.Id, old.SupersededBy);
            Assert.IsFalse(view.OwnsActive(old.Id), "已纠正的认知不能再被引用");
            Assert.AreEqual(2, view.History.Count);
        }

        [Test]
        public void 弱来源不覆盖强来源()
        {
            var k = new KnowledgeStore();
            var seen = k.Learn("ahe", P.Took, true, 0.9f, SourceType.Witnessed, T);
            k.Learn("ahe", P.Took, false, 0.5f, SourceType.ToldBy, T.AddMinutes(5), sourceActorId: "player");

            Assert.IsTrue(seen.IsActive);
            Assert.AreEqual(seen.Id, k.ViewFor("ahe").Current(P.Took).Id);
        }
    }
}
