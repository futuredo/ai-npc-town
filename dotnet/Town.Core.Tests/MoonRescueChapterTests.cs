using NUnit.Framework;
using Town.Core.Moonlight;

namespace Town.Core.Tests
{
    public sealed class MoonRescueChapterTests
    {
        static MoonWorkshop StartWorkshop()
        {
            var w = new MoonWorkshop(); var r = w.Begin("先试亮，再带大家过去");
            w.Receive(r, RecordedMoonFixture.Example("follow").Read(r)); w.Confirm(); return w;
        }

        [Test]
        public void 没有执行约定不能开局_未走路线或终点不能计数()
        {
            var c = new MoonRescueChapter();
            Assert.IsFalse(c.Start(new MoonWorkshop()));
            Assert.IsFalse(c.ObserveArrival(0, true, 4, 0));
            Assert.IsTrue(c.Start(StartWorkshop())); Assert.IsFalse(c.Start(StartWorkshop()));
            Assert.IsFalse(c.ObserveArrival(0, false, 4, 0));
            Assert.IsFalse(c.ObserveArrival(0, true, 0, -3));
            Assert.IsFalse(c.ObserveArrival(0, true, double.NaN, 0));
            Assert.IsTrue(c.ObserveArrival(0, true, 4, 0));
            Assert.IsFalse(c.ObserveArrival(0, true, 4, 0));
            Assert.AreEqual(1, c.SavedLights); Assert.AreEqual(RescuePhase.Escort, c.Phase);
        }

        [Test]
        public void 三盏灯不替芬恩制造完成_结尾按实际缺口收束()
        {
            var c = new MoonRescueChapter(); var w = StartWorkshop(); c.Start(w);
            for (int i = 0; i < 3; i++) c.ObserveArrival(i, true, 4, 0);
            c.ObserveFinn(w);
            Assert.IsFalse(c.FinnSafe); Assert.AreEqual(RescuePhase.Escort, c.Phase);
            Assert.IsTrue(c.Settle()); Assert.IsFalse(c.Settle());
            StringAssert.Contains("芬恩还留", c.Ending);
            Assert.IsFalse(c.ObserveArrival(1, true, 4, 0));
        }

        [Test]
        public void 无回执撤回不能算成功_重开清空章节()
        {
            var c = new MoonRescueChapter(); var w = StartWorkshop(); c.Start(w); w.Stop(); c.Settle();
            StringAssert.Contains("没有有效到达", c.Ending);
            Assert.AreEqual(0, new MoonRescueChapter().SavedLights);
            Assert.AreEqual(RescuePhase.Preparation, new MoonRescueChapter().Phase);
        }
    }
}
