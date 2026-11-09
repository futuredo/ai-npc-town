using System.Collections.Generic;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        /// <summary>玩家进入新地点：跟随的雏龙一起移动；总管在场会惊吓暴露的雏龙；随后卫兵巡逻检查。</summary>
        public static void EnterPlace(DsWorld w, string place, AiTrace t, IDsRoll rolls = null)
        {
            t.Verdict.Kind = VerdictKind.Accepted;
            if (!w.Places.ContainsKey(place)) { Fail(t, "unknown_entity", "未知地点：" + place); return; }
            if (w.FinalDawn) { Reject(t, "第三个黎明已至，一切交由苍穹之母裁决", "deadline_passed"); return; }
            if (place == w.PlayerPlace) { t.Verdict.Reason = "仍在原地"; t.Presented = ""; return; }
            var wh = w.Whelp;
            var lines = new List<string>();
            t.Changes.Add(new WorldChange { Path = "player.place", Before = w.PlayerPlace, After = place });
            bool bring = wh.Following && wh.CapturedBy == null && wh.Place == w.PlayerPlace;
            w.PlayerPlace = place;
            if (bring)
            {
                t.Changes.Add(new WorldChange { Path = "whelp.place", Before = wh.Place, After = place, Note = "跟随玩家" });
                wh.Place = place;
                bool exposed = !wh.Hidden;
                if (exposed && w.Actors["osric"].Place == place)
                {
                    Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.2f);
                    if (wh.Fear >= 0.9f)
                    {
                        wh.Following = false; wh.Hidden = true;
                        t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "true", After = "false", Note = "受惊逃走" });
                        t.Changes.Add(new WorldChange { Path = "whelp.place", Before = place, After = "docks", Note = "逃回旧船屋" });
                        wh.Place = "docks";
                        w.Flags["whelp_fled"] = w.ClockText;
                        t.Verdict.Kind = VerdictKind.Partial;
                        t.Verdict.Reason = "雏龙受惊逃回码头";
                        t.Verdict.ReasonCode = new DsReason("whelp_fled");
                        t.Presented = "小烬一看见奥斯里克就尖叫着挣脱，扑腾着朝码头逃去了。";
                        return;
                    }
                    lines.Add("小烬看见奥斯里克，吓得直往你身后躲");
                }
            }
            string patrol = DsGuards.Patrol(w, t, rolls);
            if (patrol != null) lines.Add(patrol);
            if (wh.CapturedBy != null && bring) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "雏龙被卫兵发现"; }
            else t.Verdict.Reason = bring ? "带着雏龙抵达" : "抵达";
            t.Presented = "你来到" + w.Places[place] + (bring ? "，小烬跟在身边" : "") + "。" + (lines.Count > 0 ? string.Join("；", lines) + "。" : "");
        }

        /// <summary>新时段开始：岗位轮换后，对玩家所在地点做一次巡逻检查。</summary>
        public static void PhaseStart(DsWorld w, AiTrace t, IDsRoll rolls = null)
        {
            t.Verdict.Kind = VerdictKind.Accepted;
            t.Verdict.Reason = "换岗";
            string patrol = DsGuards.Patrol(w, t, rolls);
            if (patrol != null && w.Whelp.CapturedBy != null) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "雏龙被卫兵发现"; }
            t.Presented = patrol ?? "";
        }
    }
}
