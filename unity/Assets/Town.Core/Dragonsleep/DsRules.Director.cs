using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        static readonly HashSet<string> MajorEvents = new HashSet<string> { "omen_roar", "guard_sweep", "mother_descends" };

        public static void Direct(DsWorld w, Dictionary<string, object> p, AiTrace t)
        {
            string ev = p.Str("event");
            double intensity = Clamp(p.Num("intensity") ?? 0.5, 0, 1);
            if (ev == null || !ContextBuilder.GmEvents.Contains(ev)) { Fail(t, "verb_not_allowed", "事件不在白名单：" + ev); return; }
            t.Verdict.Kind = VerdictKind.Accepted;
            t.Verdict.Reason = p.Str("reason") ?? "";
            if (w.FinalDawn && ev != "mother_descends")
            { ev = "mother_descends"; t.Verdict.Kind = VerdictKind.Partial; t.Verdict.Notes.Add("确定的终局期限已到，导演不能无限推迟母龙降临"); }
            if (ev == "mother_descends" && !w.FinalDawn)
            { ev = "omen_roar"; t.Verdict.Kind = VerdictKind.Partial; t.Verdict.Notes.Add("母龙只能在第三个黎明后降临，降级为龙吼征兆"); }
            if (MajorEvents.Contains(ev) && ev != "mother_descends" && w.MajorEventsToday >= 1)
            { ev = "none"; t.Verdict.Kind = VerdictKind.Partial; t.Verdict.Notes.Add("今日大事件额度已用完，改为无事件"); }
            if (MajorEvents.Contains(ev)) w.MajorEventsToday++;
            float before = w.Tension;
            string text;
            switch (ev)
            {
                case "omen_roar":
                    foreach (var a in w.Actors.Values) a.Fear = Math.Min(1, a.Fear + 0.15f * (float)(0.5 + intensity));
                    w.Tension = Math.Min(1, w.Tension + 0.15f);
                    text = "雪峰方向传来一声悠长的龙吼，窗棂嗡嗡作响。";
                    break;
                case "guard_sweep":
                    w.Flags["guard_sweep"] = w.ClockText;
                    // 夜里下令，次日清晨开始搜；白天下令则当天生效。
                    w.Flags["guard_sweep_day"] = (w.Phase == DayPhase.Night ? w.Day + 1 : w.Day).ToString();
                    w.Tension = Math.Min(1, w.Tension + 0.1f);
                    text = "王城的卫兵提着灯，挨家挨户敲门。";
                    break;
                case "snowfall":
                    w.Tension = Math.Max(0, w.Tension - 0.1f);
                    text = "夜里落了一场静雪，镇子安静下来。";
                    break;
                case "rumor_spreads":
                    w.Tension = Math.Min(1, w.Tension + 0.05f);
                    text = "集市上有人在传：码头那边夜里冒烟。";
                    break;
                case "mother_descends":
                    w.Flags["mother_descended"] = w.ClockText;
                    text = "第三个黎明。苍穹之母收拢双翼，落在龙骨岗上。";
                    if (w.Whelp.CapturedBy == null && w.Whelp.Place == "skull_hill")
                    {
                        w.Whelp.WithMother = true; w.Whelp.Following = false;
                        t.Changes.Add(new WorldChange { Path = "whelp.with_mother", Before = "false", After = "true", Note = "在龙骨岗与母亲相会" });
                        text += "小烬从岩石后扑出来，冲着母亲尖叫。";
                    }
                    break;
                default:
                    text = "这一夜无事。";
                    break;
            }
            if (Math.Abs(before - w.Tension) > 1e-4)
                t.Changes.Add(new WorldChange { Path = "world.tension", Before = before.ToString("0.00"), After = w.Tension.ToString("0.00") });
            t.Changes.Add(new WorldChange { Path = "gm.event", Before = "", After = ev });
            t.Presented = text;
            if (ev != "none") { w.Chronicle.Add(w.ClockText + "：" + text); w.PublicNews.Add(w.ClockText + "：" + text); }
        }
    }
}
