using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    /// <summary>M1 护送、卫兵、团聚与铁笼补救。全部离线；不读取密钥、不发网络请求。</summary>
    public class DragonsleepEscortTests
    {
        sealed class ScriptedProvider : IAiProvider
        {
            public readonly Queue<string> Replies = new Queue<string>();
            public string Name => "scripted";
            public bool IsSimulated => true;
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct) =>
                Task.FromResult(new ProviderResult { ProviderName = Name, RawText = Replies.Dequeue(), Simulated = true });
        }

        static string Act(string verb, string target = "whelp", string item = "", string laws = "") =>
            "{\"verb\":\"" + verb + "\",\"target\":\"" + target + "\",\"item\":\"" + item + "\",\"place\":\"\",\"laws\":[" + laws +
            "],\"difficulty\":0.3,\"narration\":\"\"}";

        static (DragonsleepSession s, ScriptedProvider p) Scripted(int seed = 20261108)
        {
            var p = new ScriptedProvider();
            return (new DragonsleepSession(p, seed), p);
        }

        static AiTrace Try(DragonsleepSession s, ScriptedProvider p, string json, System.Func<bool> done, int tries = 6)
        {
            AiTrace t = null;
            for (int i = 0; i < tries && !done(); i++) { p.Replies.Enqueue(json); t = s.ActAsync("x").Result; }
            return t;
        }

        // ---------------- 跟随 ----------------

        [Test]
        public void 带上雏龙需要信任_跟随后随玩家移动_放下后留在原地()
        {
            var (s, p) = Scripted();
            var w = s.World;
            w.PlayerPlace = "docks"; w.Whelp.Hidden = false; w.Whelp.TrustPlayer = 0.2f;
            int time = s.TimeLeftInPhase;
            p.Replies.Enqueue(Act("lead"));
            var refused = s.ActAsync("带上它").Result;
            Assert.AreEqual(VerdictKind.Rejected, refused.Verdict.Kind);
            Assert.That(refused.Verdict.Reason, Does.Contain("信任"));
            Assert.IsFalse(w.Whelp.Following);
            Assert.AreEqual(time, s.TimeLeftInPhase, "前置条件不满足不消耗时间");

            w.Whelp.TrustPlayer = 0.6f;
            Try(s, p, Act("lead"), () => w.Whelp.Following);
            Assert.IsTrue(w.Whelp.Following);

            var go = s.EnterPlace("workshop");
            Assert.IsNotNull(go);
            Assert.IsTrue(go.Receipt.Applied);
            Assert.AreEqual("workshop", w.Whelp.Place);
            Assert.IsTrue(go.Receipt.Effects.Any(e => e.Path == "whelp.place" && e.After.Contains("workshop")), "回执来自实际状态差异");
            Assert.That(go.Presented, Does.Contain("小烬跟在身边"));

            p.Replies.Enqueue(Act("release"));
            s.ActAsync("让它待在这").Wait();
            Assert.IsFalse(w.Whelp.Following);
            s.EnterPlace("cathedral");
            Assert.AreEqual("workshop", w.Whelp.Place, "放下的雏龙留在原地");
            Assert.AreEqual("cathedral", w.PlayerPlace);
        }

        [Test]
        public void 还躲在船屋里的雏龙不能直接带走()
        {
            var (s, p) = Scripted();
            var w = s.World;
            w.PlayerPlace = "docks"; w.Whelp.TrustPlayer = 0.6f;   // 初始仍 Hidden
            p.Replies.Enqueue(Act("lead"));
            var t = s.ActAsync("带它走").Result;
            Assert.AreEqual(VerdictKind.Rejected, t.Verdict.Kind);
            Assert.That(t.Verdict.Reason, Does.Contain("现身"));
        }

        // ---------------- 卫兵 ----------------

        [Test]
        public void 岗位表_黄昏换岗_龙骨岗第三天设岗_终局撤岗()
        {
            var w = DsContent.NewWorld();
            Assert.IsTrue(DsGuards.IsWatched(w, "castle"));
            w.Phase = DayPhase.Dusk;
            Assert.IsFalse(DsGuards.IsWatched(w, "castle"), "黄昏换岗");
            w.Phase = DayPhase.Noon;
            Assert.IsTrue(DsGuards.IsWatched(w, "market"));
            Assert.IsFalse(DsGuards.IsWatched(w, "skull_hill"));
            w.Day = 2;
            Assert.IsTrue(DsGuards.IsWatched(w, "docks"), "第二天按总管命令搜城");
            w.Day = 3;
            Assert.IsTrue(DsGuards.IsWatched(w, "skull_hill"));
            w.Phase = DayPhase.Dusk;
            Assert.IsFalse(DsGuards.IsWatched(w, "skull_hill"));
            w.Phase = DayPhase.Night;
            Assert.IsFalse(DsGuards.IsWatched(w, "castle"));
            w.Flags["guard:docks"] = "osric";
            Assert.IsTrue(DsGuards.IsWatched(w, "docks"), "总管夜里亲自守的地方");
            w.Flags["guard:market"] = "pip";
            Assert.IsFalse(DsGuards.IsWatched(w, "market"), "皮普守夜不是卫兵");
            w.Day = 4; w.Phase = DayPhase.Dawn;
            Assert.IsFalse(DsGuards.IsWatched(w, "castle"));
        }

        [Test]
        public void GM搜城令次日生效()
        {
            var w = DsContent.NewWorld();
            w.Phase = DayPhase.Night;
            var t = new AiTrace();
            DsRules.Direct(w, new Dictionary<string, object> { ["event"] = "guard_sweep", ["intensity"] = 0.5, ["reason"] = "" }, t);
            Assert.AreEqual("2", w.Flags["guard_sweep_day"]);
            w.Advance();
            Assert.IsTrue(DsGuards.IsWatched(w, "cathedral"));
        }

        static int Captures(bool cloaked, bool sneaking)
        {
            int caught = 0;
            for (int seed = 1; seed <= 200; seed++)
            {
                var w = DsContent.NewWorld(seed);
                w.Phase = DayPhase.Noon;
                w.PlayerPlace = "workshop";
                w.Whelp.Place = "workshop"; w.Whelp.Following = true; w.Whelp.Hidden = cloaked; w.Whelp.Fear = 0.2f;
                if (cloaked) w.Flags["whelp_hidden_by_player"] = "x";
                if (sneaking) w.Flags["sneaking"] = w.ClockText;
                DsRules.EnterPlace(w, "castle", new AiTrace());
                if (w.Whelp.CapturedBy != null)
                {
                    caught++;
                    Assert.AreEqual("osric", w.Whelp.CapturedBy);
                    Assert.AreEqual("castle", w.Whelp.Place);
                    Assert.IsFalse(w.Whelp.Following);
                }
            }
            return caught;
        }

        [Test]
        public void 带着雏龙闯入岗哨可能被押进铁笼_斗篷和潜行降低风险()
        {
            int exposed = Captures(false, false), cloaked = Captures(true, false), careful = Captures(true, true);
            TestContext.Progress.WriteLine($"[岗哨] 200 种子被发现：暴露 {exposed}，斗篷 {cloaked}，斗篷+潜行 {careful}");
            Assert.Greater(exposed, 80);
            Assert.Less(cloaked, exposed / 2);
            Assert.Less(careful, cloaked);
        }

        [Test]
        public void 黄昏换岗空档带雏龙通过不会被发现()
        {
            var w = DsContent.NewWorld();
            w.Day = 3; w.Phase = DayPhase.Dusk; w.PlayerPlace = "workshop";
            w.Whelp.Place = "workshop"; w.Whelp.Following = true; w.Whelp.Hidden = false;
            int rolls = w.RollCounter;
            var t = new AiTrace();
            DsRules.EnterPlace(w, "skull_hill", t);
            Assert.AreEqual("skull_hill", w.Whelp.Place);
            Assert.IsNull(w.Whelp.CapturedBy);
            Assert.AreEqual(rolls, w.RollCounter, "无岗时不掷骰");
        }

        [Test]
        public void 总管在场会惊吓暴露的雏龙_恐惧过高逃回码头()
        {
            var w = DsContent.NewWorld();
            w.Phase = DayPhase.Dusk;   // 无岗，只看惊吓
            w.PlayerPlace = "workshop";
            w.Whelp.Place = "workshop"; w.Whelp.Following = true; w.Whelp.Hidden = false; w.Whelp.Fear = 0.75f;
            var t = new AiTrace();
            DsRules.EnterPlace(w, "market", t);   // 奥斯里克在集市
            Assert.IsFalse(w.Whelp.Following);
            Assert.AreEqual("docks", w.Whelp.Place);
            Assert.IsTrue(w.Whelp.Hidden);
            Assert.AreEqual(VerdictKind.Partial, t.Verdict.Kind);
            Assert.AreEqual("market", w.PlayerPlace);
        }

        [Test]
        public void 换岗时检查玩家所在地点并留下规则回执()
        {
            int caught = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                var s = new DragonsleepSession(new RuleProvider(), seed);
                var w = s.World;
                w.PlayerPlace = "castle"; w.Phase = DayPhase.Dusk;   // 黄昏进入时无岗
                w.Whelp.Place = "castle"; w.Whelp.Following = true; w.Whelp.Hidden = false;
                s.EndPhaseAsync().Wait();   // 黄昏 → 深夜（无岗）→ 居民与导演 → 第二天清晨：城门重新设岗
                Assert.AreEqual(2, w.Day);
                if (w.Whelp.CapturedBy == "osric")
                {
                    caught++;
                    var ev = s.RuleEvents.Last(e => e.WhelpCaptured);
                    Assert.IsTrue(ev.Receipt.Applied);
                    Assert.IsTrue(ev.Receipt.Effects.Any(e => e.Path == "whelp.captured_by"));
                    Assert.That(ev.Presented, Does.Contain("铁笼"));
                }
            }
            Assert.Greater(caught, 0);
        }

        [Test]
        public void 等待模型时不能绕过事务移动玩家()
        {
            var s = new DragonsleepSession(new BlockingProvider());
            var pending = s.ActAsync("观察四周");
            Assert.IsNull(s.EnterPlace("docks"), "会话忙时拒绝移动，调用方下一帧重试");
            Assert.AreEqual("market", s.World.PlayerPlace);
            ((BlockingProvider)s.Provider).Release.SetResult(true);
            pending.Wait();
            Assert.IsNotNull(s.EnterPlace("docks"));
            Assert.AreEqual("docks", s.World.PlayerPlace);
        }

        sealed class BlockingProvider : IAiProvider
        {
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public string Name => "blocking";
            public bool IsSimulated => true;
            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                await Release.Task.ConfigureAwait(false);
                return new ProviderResult { ProviderName = Name, Simulated = true, RawText = Act("observe", "") };
            }
        }

        // ---------------- 铁笼补救 ----------------

        static (DragonsleepSession, ScriptedProvider) Caged(DayPhase phase)
        {
            var (s, p) = Scripted();
            var w = s.World;
            w.Day = 2; w.Phase = phase; w.PlayerPlace = "castle";
            w.Whelp.CapturedBy = "osric"; w.Whelp.Place = "castle"; w.Whelp.Hidden = false;
            return (s, p);
        }

        [Test]
        public void 铁笼救援_有岗时说明原因且不耗时间_换岗空档可以救出()
        {
            var (s, p) = Caged(DayPhase.Noon);
            int time = s.TimeLeftInPhase;
            p.Replies.Enqueue(Act("rescue"));
            var t = s.ActAsync("撬开铁笼").Result;
            Assert.AreEqual(VerdictKind.Rejected, t.Verdict.Kind);
            Assert.That(t.Verdict.Reason, Does.Contain("卫兵"));
            Assert.AreEqual(time, s.TimeLeftInPhase);
            Assert.AreEqual("osric", s.World.Whelp.CapturedBy);

            var (d, dp) = Caged(DayPhase.Dusk);
            d.World.PlayerKnows.Add("law:law_guards_dusk");
            Try(d, dp, Act("rescue", laws: "\"law_guards_dusk\""), () => d.World.Whelp.CapturedBy == null, 8);
            Assert.IsNull(d.World.Whelp.CapturedBy);
            Assert.IsTrue(d.World.Whelp.Following);
            Assert.IsTrue(d.World.Flags.ContainsKey("whelp_rescued"));
        }

        [Test]
        public void 被关着的雏龙不能直接带走_感知里看得到铁笼()
        {
            var (s, p) = Caged(DayPhase.Dusk);
            var per = ContextBuilder.AdjudicatePerception(s.World);
            Assert.That(MiniJson.Write(per), Does.Contain("铁笼"));
            p.Replies.Enqueue(Act("lead"));
            var t = s.ActAsync("带上它").Result;
            Assert.AreEqual(VerdictKind.Rejected, t.Verdict.Kind);
            Assert.That(t.Verdict.Reason, Does.Contain("救出"));
        }

        [Test]
        public void 向总管出示契约可以说服他开笼()
        {
            var (s, p) = Caged(DayPhase.Noon);
            var w = s.World;
            w.Actors["osric"].Place = "castle"; w.Actors["osric"].Trust = 0.35f;
            w.Items["pact_page"].Holder = "player";
            Try(s, p, Act("show", "osric", "pact_page"), () => w.Whelp.CapturedBy == null);
            Assert.IsNull(w.Whelp.CapturedBy);
            Assert.IsTrue(w.Whelp.Following);
            Assert.IsTrue(w.Flags.ContainsKey("osric_released"));
        }

        // ---------------- 团聚与结局 ----------------

        [Test]
        public void 母龙降临时雏龙在龙骨岗才算团聚()
        {
            foreach (var (place, expect) in new[] { ("skull_hill", true), ("workshop", false) })
            {
                var w = DsContent.NewWorld();
                w.Day = 4; w.Phase = DayPhase.Dawn;
                w.Whelp.Place = place; w.Whelp.Hidden = false;
                DsRules.Direct(w, new Dictionary<string, object> { ["event"] = "none", ["intensity"] = 0.5, ["reason"] = "" }, new AiTrace());
                Assert.AreEqual(expect, w.Whelp.WithMother, place);
                Assert.AreEqual(expect ? "homecoming_wary" : "unresolved", DsEndings.Evaluate(w).Id);
                w.Items["ember_ash"].Holder = "player";
                Assert.AreEqual(expect ? "homecoming" : "unresolved", DsEndings.Evaluate(w).Id);
            }
        }

        [Test]
        public void 终局时被关着的雏龙不会团聚()
        {
            var w = DsContent.NewWorld();
            w.Day = 4; w.Phase = DayPhase.Dawn;
            w.Whelp.Place = "skull_hill"; w.Whelp.CapturedBy = "osric";
            DsRules.Direct(w, new Dictionary<string, object> { ["event"] = "mother_descends", ["intensity"] = 0.5, ["reason"] = "" }, new AiTrace());
            Assert.IsFalse(w.Whelp.WithMother);
            Assert.AreEqual("iron_cage", DsEndings.Evaluate(w).Id);
        }

        // ---------------- 离线整局脚本 ----------------

        enum Strategy { Homecoming, NewPact, StayHome, Castle, CastleRescue }

        static void Until(DragonsleepSession s, int day, DayPhase phase)
        {
            var w = s.World;
            for (int i = 0; i < 20 && !w.FinalDawn && (w.Day < day || (w.Day == day && w.Phase < phase)); i++) s.EndPhaseAsync().Wait();
        }

        static void Repeat(DragonsleepSession s, string text, System.Func<bool> done, int tries = 4)
        {
            for (int i = 0; i < tries && !done() && !s.World.FinalDawn; i++)
            {
                if (s.NightPending) s.EndPhaseAsync().Wait();
                s.ActAsync(text).Wait();
            }
        }

        /// <summary>按固定玩法脚本走完整局，返回结局。只用离线规则提供方。</summary>
        static string Play(int seed, Strategy plan)
        {
            var s = new DragonsleepSession(new RuleProvider(), seed);
            var w = s.World;
            // 每次提交都会换掉 World.Whelp 对象，必须每次重新读取。
            // 第一天：打听、买鱼、赢得皮普、喂食、诱引、藏好、带上。
            s.SpeakAsync("marta", "最近有没有看到冒烟的小龙？").Wait();
            s.ActAsync("向玛尔妲买一串熏鱼").Wait();
            s.EnterPlace("docks");
            while (w.Actors["pip"].Trust < 0.46f && !s.NightPending) s.SpeakAsync("pip", "我想帮小烬回家找妈妈，我保证不骗你，我们做朋友吧。").Wait();
            Repeat(s, "安静地把熏鱼喂给它", () => w.Whelp.TrustPlayer >= 0.3f || w.Items["smoked_fish"].Holder != "player");
            Repeat(s, "安静地蹲下，轻声引它靠近", () => w.Whelp.TrustPlayer >= 0.4f || w.Whelp.Hidden);
            Repeat(s, "用斗篷盖住它藏好", () => w.Flags.ContainsKey("whelp_hidden_by_player") || w.Whelp.TrustPlayer < 0.3f);
            Repeat(s, "带上小烬跟我走", () => w.Whelp.Following || w.Whelp.TrustPlayer < 0.4f);
            if (!w.Whelp.Following) { Until(s, 4, DayPhase.Dawn); return DsEndings.Evaluate(w).Id; }
            s.EnterPlace("workshop");   // 入夜前离开码头：外乡商人迟早会去那里搜
            if (s.NightPending) s.EndPhaseAsync().Wait();

            switch (plan)
            {
                case Strategy.Homecoming:
                    Repeat(s, "拿走那包余烬灰", () => w.Items["ember_ash"].Holder == "player");
                    break;
                case Strategy.NewPact:
                    Until(s, 2, DayPhase.Dawn);
                    s.EnterPlace("catacombs");
                    Repeat(s, "在墓穴里仔细搜找契约页", () => w.Items["pact_page"].Holder == "player", 6);
                    if (w.Actors["maren"].Place == "catacombs")
                        Repeat(s, "把契约页出示给梅伦修女看", () => w.Actors["maren"].Trust >= 0.6f);
                    s.EnterPlace("workshop");
                    break;
                case Strategy.Castle:
                case Strategy.CastleRescue:
                    Until(s, 2, DayPhase.Noon);
                    s.EnterPlace("castle");   // 带着雏龙闯岗哨
                    if (w.Whelp.CapturedBy == null) { s.EnterPlace("workshop"); break; }
                    if (plan == Strategy.CastleRescue && !w.FinalDawn)
                    {
                        // 等到下一个换岗空档（黄昏或深夜无岗），尝试两次。
                        if (w.Phase < DayPhase.Dusk) Until(s, w.Day, DayPhase.Dusk);
                        Repeat(s, "趁黄昏换岗撬开铁笼救出小烬", () => w.Whelp.CapturedBy == null, 2);
                        if (w.Whelp.CapturedBy == null) s.EnterPlace("workshop");
                    }
                    break;
            }
            if (plan != Strategy.StayHome && w.Whelp.CapturedBy == null)
            {
                Until(s, 3, DayPhase.Dusk);
                s.EnterPlace("skull_hill");   // 换岗空档上龙骨岗
            }
            Until(s, 4, DayPhase.Dawn);
            return DsEndings.Evaluate(w).Id;
        }

        [Test]
        public void 离线整局脚本_不同玩法可以走到不同结局()
        {
            var seen = new Dictionary<Strategy, Dictionary<string, int>>();
            foreach (Strategy plan in System.Enum.GetValues(typeof(Strategy)))
            {
                var counts = seen[plan] = new Dictionary<string, int>();
                for (int seed = 1; seed <= 30; seed++)
                {
                    string ending = Play(seed, plan);
                    counts[ending] = counts.TryGetValue(ending, out var n) ? n + 1 : 1;
                }
                TestContext.Progress.WriteLine($"[整局] {plan}: " + string.Join("，", counts.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value)));
            }
            var all = seen.Values.SelectMany(x => x.Keys).ToHashSet();
            foreach (var id in new[] { "homecoming", "new_pact", "homecoming_wary", "iron_cage", "unresolved" })
                Assert.IsTrue(all.Contains(id), "未走到结局：" + id);
            Assert.IsTrue(seen[Strategy.Homecoming].ContainsKey("homecoming"));
            Assert.IsTrue(seen[Strategy.NewPact].ContainsKey("new_pact"));
            Assert.IsTrue(seen[Strategy.StayHome].ContainsKey("unresolved"));
            Assert.IsTrue(seen[Strategy.Castle].ContainsKey("iron_cage"));
            Assert.IsTrue(seen[Strategy.CastleRescue].Keys.Any(k => k.StartsWith("homecoming")), "铁笼救出后仍能归巢");
        }
    }
}
