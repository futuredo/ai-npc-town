using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Town.Core.Moonlight;

namespace Town.Core.Tests
{
    public sealed class MoonWorkshopTests
    {
        static MoonCandidate Candidate(string fixture) => RecordedMoonFixture.Example(fixture).Read(null);
        static MoonWorkshop Start(string fixture)
        {
            var workshop = new MoonWorkshop();
            Assert.That(workshop.Receive(workshop.Begin("我有一个过断鳞的新办法"), Candidate(fixture)).Success, Is.True);
            return workshop;
        }
        static void Warm(MoonWorkshop w) { for (int i = 0; i < 21; i++) w.Observe(-4, 0, 0.1, true); }
        static void Arrive(MoonWorkshop w)
        {
            double x = -4, z = 0;
            foreach (var point in w.Route.Skip(1))
            {
                while (Math.Sqrt(Math.Pow(point[0] - x, 2) + Math.Pow(point[1] - z, 2)) > 0.1)
                {
                    double distance = Math.Sqrt(Math.Pow(point[0] - x, 2) + Math.Pow(point[1] - z, 2));
                    x += (point[0] - x) / distance * 0.1; z += (point[1] - z) / distance * 0.1;
                    if (w.State == MoonTaskState.Travelling) Assert.That(w.Observe(x, z, 0.1, true).Success, Is.True);
                }
            }
        }

        [TestCase("follow", -3)]
        [TestCase("split", 3)]
        public void 两种候选保留真实不同路线_同意确认和实际到达分开(string fixture, int middleZ)
        {
            var w = Start(fixture);
            Assert.That(w.Route[1][1], Is.EqualTo(middleZ));
            Assert.That(w.State, Is.EqualTo(MoonTaskState.AwaitingConfirmation));
            Assert.That(w.FreeEnergy, Is.EqualTo(2));
            Assert.That(w.Confirm().Success, Is.True);
            Assert.That(w.FreeEnergy, Is.Zero);
            Assert.That(w.Confirm().Success, Is.False, "重复确认不能重复扣材或完成");
            Assert.That(w.Observe(4, 0, 0.1, true).Success, Is.False, "点击终点/瞬移不代表到达");
            Warm(w); Arrive(w);
            Assert.That(w.State, Is.EqualTo(MoonTaskState.Completed));
            Assert.That(w.FreeEnergy, Is.EqualTo(2));
            Assert.That(w.History.Count(r => r.Code == "completed"), Is.EqualTo(1));
        }

        [TestCase("clarify", "clarify")]
        [TestCase("decline", "decline")]
        public void 澄清和拒绝不产生约定也不消耗资源(string fixture, string code)
        {
            var w = new MoonWorkshop();
            Assert.That(w.Receive(w.Begin("如何过去"), Candidate(fixture)).Code, Is.EqualTo(code));
            Assert.That(w.State, Is.EqualTo(MoonTaskState.None));
            Assert.That(w.Agreement, Is.Null);
            Assert.That(w.FreeEnergy, Is.EqualTo(2));
        }

        [Test]
        public void 越权引用_缺独立同意_超资源_未知能力全部拒绝()
        {
            foreach (var error in new[] { "evidence", "consent", "resource", "capability", "schema" })
            {
                var w = new MoonWorkshop(); var c = Candidate("follow");
                if (error == "evidence") c.Evidence = new[] { "maeve:private_secret" };
                if (error == "consent") c.ActorConsents = false;
                if (error == "resource") c.Energy = 200;
                if (error == "capability") c.Method = (LightMethod)999;
                if (error == "schema") c.Summary = null;
                Assert.That(w.Receive(w.Begin("试验"), c).Code, Is.EqualTo(error));
                Assert.That(w.State, Is.EqualTo(MoonTaskState.None));
                Assert.That(w.FreeEnergy, Is.EqualTo(2));
            }
        }

        [Test]
        public void 过期_跨局_重复_取消后的候选不会执行()
        {
            var w = new MoonWorkshop(); var request = w.Begin("试验");
            w.SetLight(false);
            Assert.That(w.Receive(request, Candidate("follow")).Code, Is.EqualTo("stale"));
            Assert.That(w.Receive(request, Candidate("follow")).Code, Is.EqualTo("request"));
            var next = w.Begin("再试"); w.CancelRequest();
            Assert.That(w.Receive(next, Candidate("follow")).Success, Is.False);
            var other = new MoonWorkshop(2); other.Begin("新局");
            Assert.That(other.Receive(next, Candidate("follow")).Code, Is.EqualTo("generation"));
            Assert.That(other.HasPending, Is.True);
        }

        [Test]
        public void 相同代际的另一实例和篡改副本不能夺取当前请求()
        {
            var a = new MoonWorkshop(); var b = new MoonWorkshop();
            var request = a.Begin("a"); b.Begin("b");
            Assert.That(b.Receive(request, Candidate("follow")).Success, Is.False);
            var c = Candidate("follow"); a.Receive(request, c); c.Energy = 999;
            a.Agreement.Energy = 100;
            a.History.First(r => r.Kind == "candidate").Candidate.ActorConsents = false;
            Assert.That(a.Agreement.Energy, Is.EqualTo(2));
            Assert.That(a.Agreement.ActorConsents, Is.True);
        }

        [Test]
        public void 光被遮挡或切断时不能完成_分光必须重新稳定两秒()
        {
            var w = Start("split"); w.Confirm();
            w.Observe(-4, 0, 0.5, false);
            Assert.That(w.CanMove, Is.False);
            Warm(w); Assert.That(w.CanMove, Is.True);
            w.SetLight(false); Assert.That(w.CanMove, Is.False);
            Assert.That(w.Observe(-3.5, 0, 0.5, false).Code, Is.EqualTo("unlit_movement"));
            w.SetLight(true); w.Observe(-4, 0, 0.5, true);
            Assert.That(w.CanMove, Is.False);
            Warm(w); Assert.That(w.CanMove, Is.True);
            w.Observe(-4, 0, 0.1, false); Assert.That(w.CanMove, Is.False);
        }

        [Test]
        public void 撤回和失败释放预留_不允许无限重复任务或乱姿态()
        {
            var w = Start("follow"); w.Confirm(); Warm(w);
            Assert.That(w.Observe(double.NaN, 0, 0.1).Success, Is.False);
            Assert.That(w.Observe(-4, 0, 10).Success, Is.False);
            w.Stop(true);
            Assert.That(w.State, Is.EqualTo(MoonTaskState.Failed)); Assert.That(w.FreeEnergy, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => w.Begin("重来"));
        }

        [TestCase("follow")]
        [TestCase("split")]
        public void 途中回放不调用候选源_可继续到达且旧存档不混用(string fixture)
        {
            var w = Start(fixture); w.Confirm(); Warm(w);
            var bytes = MoonReplay.Encode(w); var loaded = MoonReplay.Restore(bytes, 5);
            Assert.That(loaded.Generation, Is.EqualTo(5)); Assert.That(loaded.State, Is.EqualTo(w.State));
            Assert.That(loaded.Version, Is.EqualTo(w.Version)); Assert.That(loaded.Route[1], Is.EqualTo(w.Route[1]));
            Arrive(w); Arrive(loaded);
            Assert.That(MoonReplay.Encode(loaded), Is.EqualTo(MoonReplay.Encode(w)));
            var broken = Encoding.UTF8.GetString(bytes).Replace("moonlight-slice-1", "unfinished-town-v3.1");
            Assert.Throws<InvalidDataException>(() => MoonReplay.Restore(Encoding.UTF8.GetBytes(broken), 1));
        }

        [Test]
        public void 等候回复不能保存_坏存档拒绝_取消后可保存()
        {
            var w = new MoonWorkshop(); w.Begin("test");
            Assert.Throws<InvalidOperationException>(() => MoonReplay.Encode(w));
            w.CancelRequest(); var loaded = MoonReplay.Restore(MoonReplay.Encode(w), 2);
            Assert.That(loaded.HasPending, Is.False);
            Assert.Throws<InvalidDataException>(() => MoonReplay.Restore(Encoding.UTF8.GetBytes("not json"), 2));
        }
    }
}
