using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 第一天的目标、提示与日终总结。目标完成与否只读世界状态（不读 AI 台词），
    /// 所以无论用离线提供方还是真实模型，判定标准一致。
    /// </summary>
    public static class DsObjectives
    {
        public sealed class Objective
        {
            public string Id;
            public string Text;
            public bool Done;
            public string Hint;
            public bool Optional;
        }

        public static List<Objective> DayOne(DsWorld w)
        {
            var wh = w.Whelp;
            bool safe = wh.CapturedBy == null;
            bool knowsWhere = w.PlayerKnows.Contains("b_marta_saw_pip") || w.PlayerKnows.Contains("b_pip_whelp_place") || !wh.Hidden;
            bool hasFood = w.Items.Values.Any(i => i.Holder == "player" && i.Tags.Contains("food")) || wh.TrustPlayer > 0;
            bool pipTrust = w.Actors["pip"].Trust >= 0.4f;
            bool whelpTrust = wh.TrustPlayer >= 0.3f;
            bool hidden = safe && wh.Hidden && w.Flags.ContainsKey("whelp_hidden_by_player");
            return new List<Objective>
            {
                new Objective { Id = "find", Text = "打听雏龙的下落", Done = knowsWhere,
                    Hint = "集市的厨娘玛尔妲什么都看在眼里——【言】问问她最近有没有见到冒烟的东西。" },
                new Objective { Id = "food", Text = "准备雏龙爱吃的东西", Done = hasFood,
                    Hint = "玛尔妲在卖熏鱼。在她身边用【行】：“花三枚银币买一串熏鱼”。" },
                new Objective { Id = "pip", Text = "赢得皮普的信任", Done = pipTrust,
                    Hint = "皮普在码头。他在乎朋友、怜悯和诚实——告诉他你想帮小烬回家，并保证不骗他。威胁只会适得其反。" },
                new Objective { Id = "whelp", Text = "让小烬现身并信任你", Done = whelpTrust,
                    Hint = "知道它爱吃什么之后，在码头用【行】描述你怎样安静地引它出来或喂它。" },
                new Objective { Id = "hide", Text = "入夜前把小烬藏好", Done = hidden,
                    Hint = "它信任你之后，用【行】描述你怎样把它藏起来（比如用斗篷盖住它）。外乡商人也在打听它。" },
                new Objective { Id = "egg", Text = "（可选）弄清龙蛋为什么会提前孵化", Optional = true,
                    Done = w.PlayerKnows.Contains("b_osric_egg_moved"),
                    Hint = "王室总管奥斯里克似乎知道些什么，但他只在乎王室的利益和自己的功劳。" },
            };
        }

        /// <summary>当前最该做的一步（第一项未完成的必做目标）。</summary>
        public static Objective Next(DsWorld w) => DayOne(w).FirstOrDefault(o => !o.Done && !o.Optional);

        /// <summary>需要立即提醒玩家的风险。</summary>
        public static string Warning(DsWorld w)
        {
            var wh = w.Whelp;
            if (wh.CapturedBy != null) return null;
            if (!wh.Hidden && (w.Phase == DayPhase.Dusk || w.Phase == DayPhase.Night))
                return "天快黑了，小烬还暴露在码头上。今晚会有人去搜寻——趁现在把它藏好！";
            if (w.Phase == DayPhase.Dusk && w.Day == 1 && !w.Flags.ContainsKey("whelp_hidden_by_player"))
                return "黄昏了。入夜后，镇上的居民会各自行动。";
            return null;
        }

        public sealed class DayRecap
        {
            public int Day;
            public string Title;
            public List<string> Completed = new List<string>();
            public List<string> Missed = new List<string>();
            public List<string> Night = new List<string>();
            public string Whelp;
            public int AiCalls;
            public int Corrections;
        }

        /// <summary>日终总结：今天完成了什么、夜里发生了什么、雏龙现状。</summary>
        public static DayRecap Recap(DsWorld w, int day, IEnumerable<AiTrace> traces)
        {
            var list = traces.ToList();
            var r = new DayRecap { Day = day, AiCalls = list.Count, Corrections = list.Sum(t => t.ValidationIssues.Count) };
            foreach (var o in DayOne(w))
                (o.Done ? r.Completed : r.Missed).Add(o.Text);
            r.Night.AddRange(list.Where(t => t.Kind == AiTaskKind.AgentPlan || t.Kind == AiTaskKind.Direct).Select(t => t.Presented));
            var wh = w.Whelp;
            r.Whelp = wh.CapturedBy == "osric" && wh.Place == "castle" ? "小烬被关进了王城的铁笼。也许还来得及把它救出来。"
                : wh.CapturedBy != null ? "小烬被" + w.Actors[wh.CapturedBy].Name + "抓走了。"
                : wh.Following ? "小烬跟在你身边，在" + w.Places[wh.Place] + "。"
                : wh.Hidden && w.Flags.ContainsKey("whelp_hidden_by_player") ? "小烬安全地藏在你的斗篷下，还活着，还在等妈妈。"
                : wh.Hidden ? "小烬仍躲在旧船屋里，没人找到它——暂时。"
                : "小烬暴露在外面，但侥幸没人发现它。";
            r.Title = wh.CapturedBy != null ? "第" + day + "天结束 · 失去了小烬" : "第" + day + "天结束";
            return r;
        }
    }
}
