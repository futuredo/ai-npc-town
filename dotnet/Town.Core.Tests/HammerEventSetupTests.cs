using NUnit.Framework;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;
using P = Town.Core.Knowledge.Propositions;

namespace Town.Core.Tests
{
    /// <summary>初始认知必须与策划大纲 v0.2 §5.3 的表格一致。</summary>
    public class HammerEventSetupTests
    {
        private HammerEventSetup.Session _s;

        [SetUp]
        public void SetUp() => _s = HammerEventSetup.Create();

        [Test]
        public void 锤子真实位置在旧哨塔石亭()
        {
            var hammer = _s.World.Items[Ids.Item.Hammer];
            Assert.AreEqual(Ids.Loc.Shelter, hammer.LocationId);
            Assert.IsNull(hammer.HolderId);
            Assert.AreEqual(Ids.Npc.Amu, hammer.OwnerId);
        }

        [TestCase(Ids.Npc.Amu, P.Took, true)]
        [TestCase(Ids.Npc.Amu, P.Borrowed, true)]
        [TestCase(Ids.Npc.Amu, P.Location, false)]
        [TestCase(Ids.Npc.Ahe, P.Took, true)]
        [TestCase(Ids.Npc.Ahe, P.Borrowed, false)]
        [TestCase(Ids.Npc.Ahe, P.Returned, false)]
        [TestCase(Ids.Npc.Ahe, P.Location, false)]
        [TestCase(Ids.Npc.Xiaochuan, P.Borrowed, true)]
        [TestCase(Ids.Npc.Xiaochuan, P.Returned, true)]
        [TestCase(Ids.Npc.Xiaochuan, P.Location, true)]
        public void 初始知识边界(string npc, string prop, bool shouldKnow)
        {
            Assert.AreEqual(shouldKnow, _s.Knowledge.ViewFor(npc).Knows(prop));
        }

        [Test]
        public void 阿木认为锤子没还_是推断()
        {
            var b = _s.Knowledge.ViewFor(Ids.Npc.Amu).Current(P.Returned);
            Assert.IsFalse(b.Stance);
            Assert.AreEqual(SourceType.Inferred, b.Source);
        }

        [Test]
        public void 梅芙对偷窃只是推断_刚好够她传出去()
        {
            var b = _s.Knowledge.ViewFor(Ids.Npc.Ahe).Current(P.Stole);
            Assert.IsTrue(b.Stance);
            Assert.AreEqual(SourceType.Inferred, b.Source);
            Assert.LessOrEqual(b.Confidence, 0.5f);
        }
    }
}
