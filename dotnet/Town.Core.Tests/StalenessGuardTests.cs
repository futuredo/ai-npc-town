using NUnit.Framework;
using Town.Core.Brain;
using Town.Core.World;

namespace Town.Core.Tests
{
    /// <summary>0 容忍项：旧局回复污染新局、重复回复重复生效。</summary>
    public class StalenessGuardTests
    {
        private WorldState _w;
        private StalenessGuard _g;

        [SetUp]
        public void SetUp()
        {
            _w = new WorldState(GameTime.At(1, 9));
            _g = new StalenessGuard();
        }

        [Test]
        public void 新请求是有效的()
        {
            var env = _g.Issue("amu", _w);
            Assert.AreEqual(StaleReason.Fresh, _g.Check(env, _w));
        }

        [Test]
        public void 重开后旧回复被丢弃()
        {
            var env = _g.Issue("amu", _w);
            _w.BumpSession();
            Assert.AreEqual(StaleReason.SessionChanged, _g.Check(env, _w));
        }

        [Test]
        public void 同一NPC的新请求让旧请求失效()
        {
            var first = _g.Issue("amu", _w);
            var second = _g.Issue("amu", _w);
            Assert.AreEqual(StaleReason.Superseded, _g.Check(first, _w));
            Assert.AreEqual(StaleReason.Fresh, _g.Check(second, _w));
        }

        [Test]
        public void 同一回复只生效一次()
        {
            var env = _g.Issue("amu", _w);
            _g.MarkApplied(env);
            Assert.AreEqual(StaleReason.AlreadyApplied, _g.Check(env, _w));
        }

        [Test]
        public void 世界每次修改版本号加一()
        {
            int v = _w.Version;
            _w.AdvanceTime(10);
            Assert.AreEqual(v + 1, _w.Version);
            Assert.AreEqual(GameTime.At(1, 9, 10), _w.Now);
        }
    }
}
