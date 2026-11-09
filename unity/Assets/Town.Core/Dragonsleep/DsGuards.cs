using System;
using System.Collections.Generic;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 王城卫兵的岗位表与视线判定。岗位只由时间、世界旗标推导，不另存状态，
    /// 因此候选世界、快照与回放天然一致。只有规则读写；模型只能看到玩家眼前的卫兵。
    /// </summary>
    public static class DsGuards
    {
        /// <summary>某地此刻是否有王城卫兵把守；reason 是可以展示给玩家的说明。</summary>
        public static bool IsWatched(DsWorld w, string place, out string reason)
        {
            reason = null;
            if (w.FinalDawn) return false;                       // 母龙降临前，卫兵都退回王城
            if (w.Phase == DayPhase.Dusk) return false;           // 黄昏换岗：各处岗哨都有片刻无人
            if (w.Phase == DayPhase.Night)
            {
                if (w.Flags.TryGetValue("guard:" + place, out var g) && g == "osric")
                { reason = "奥斯里克的人连夜守在这里"; return true; }
                return false;
            }
            switch (place)
            {
                case "castle":
                    reason = "铁笼和城门前都有卫兵站岗"; return true;
                case "skull_hill":
                    if (w.Day >= 3) { reason = "奥斯里克在龙骨岗下设了岗哨"; return true; }
                    break;
                case "market":
                    if (w.Phase == DayPhase.Noon) { reason = "正午有卫兵在集市巡逻"; return true; }
                    break;
                case "docks":
                case "cathedral":
                    if (w.Day == 2 && w.Phase == DayPhase.Noon) { reason = "卫兵按总管的命令挨处搜城"; return true; }
                    break;
            }
            if ((place == "docks" || place == "market" || place == "cathedral") &&
                w.Flags.TryGetValue("guard_sweep_day", out var day) && day == w.Day.ToString())
            { reason = "卫兵正提着灯挨家挨户搜查"; return true; }
            return false;
        }

        public static bool IsWatched(DsWorld w, string place) => IsWatched(w, place, out _);

        /// <summary>雏龙是否正跟着玩家、且此刻就在玩家身边。</summary>
        public static bool WhelpWithPlayer(DsWorld w) =>
            w.Whelp.Following && w.Whelp.CapturedBy == null && w.Whelp.Place == w.PlayerPlace;

        /// <summary>被卫兵看见的概率：斗篷遮盖、消声织物与潜行都会降低。</summary>
        public static double SightChance(DsWorld w, out List<string> notes)
        {
            notes = new List<string>();
            var wh = w.Whelp;
            bool cloaked = wh.Hidden && w.Flags.ContainsKey("whelp_hidden_by_player");
            double chance = cloaked ? 0.25 : 0.6;
            notes.Add(cloaked ? "小烬藏在斗篷下，基础暴露 0.25" : "小烬暴露在外，基础暴露 0.60");
            if (w.Flags.ContainsKey("quiet:" + w.PlayerPlace)) { chance -= 0.15; notes.Add("消声织物 −0.15"); }
            if (w.Flags.TryGetValue("sneaking", out var clock) && clock == w.ClockText) { chance -= 0.25; notes.Add("本时段在潜行 −0.25"); }
            return Math.Max(0.05, chance);
        }

        /// <summary>
        /// 巡逻检查：玩家带着雏龙进入有卫兵的地点，或所在地点换上新岗时调用。
        /// 被看见 → 雏龙被押进王城铁笼。返回给玩家的一句话；无事返回 null。
        /// </summary>
        public static string Patrol(DsWorld w, AiTrace t)
        {
            if (!WhelpWithPlayer(w) || !IsWatched(w, w.PlayerPlace, out var reason)) return null;
            double chance = SightChance(w, out var notes);
            double roll = w.NextRoll();
            t.Verdict.Notes.Add(reason + "；" + string.Join("，", notes) + $"；看见概率 {chance:0.00}，掷骰 {roll:0.00}");
            if (roll >= chance) return reason + "，但没人注意到你身边的小烬。";
            Capture(w, t, "osric");
            w.Flags["seen_by_guards"] = w.ClockText;
            w.Tension = Math.Min(1, w.Tension + 0.2f);
            return reason + "——一名卫兵发现了小烬，把它塞进铁笼押回了王城！";
        }

        /// <summary>雏龙被某人抓走：停止跟随，并被带到抓捕者的据点。</summary>
        public static void Capture(DsWorld w, AiTrace t, string by)
        {
            var wh = w.Whelp;
            string before = wh.Place;
            wh.CapturedBy = by;
            t.Changes.Add(new WorldChange { Path = "whelp.captured_by", Before = "", After = by });
            if (wh.Following) { wh.Following = false; t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "true", After = "false" }); }
            string to = by == "osric" ? "castle" : by == "vesna" ? "docks" : wh.Place;
            if (to != before) { wh.Place = to; t.Changes.Add(new WorldChange { Path = "whelp.place", Before = before, After = to, Note = "被押走" }); }
        }
    }
}
