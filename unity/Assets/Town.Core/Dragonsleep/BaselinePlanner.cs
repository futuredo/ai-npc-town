using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 离线基线规划器：固定模板 + 按感知做前置条件选择。只读取计划感知（与真实模型拿到的一样），
    /// 不偷看世界真值。用于与模型对照，证明 AI 规划带来的增量；也让离线测试与研究零费用跑通。
    /// 模板：取得雏龙信任 → 斗篷遮盖 → 带上 →（可选）托付帮手 → 在工坊躲到第3天黄昏 → 上龙骨岗等母亲。
    /// 被关进铁笼时：等黄昏换岗去王城撬笼，再接回模板。
    /// </summary>
    public static class BaselinePlanner
    {
        static readonly DsMoment DuskD3 = new DsMoment(3, DayPhase.Dusk);
        static readonly DsMoment FinalDawn = new DsMoment(4, DayPhase.Dawn);

        public static Dictionary<string, object> Plan(Dictionary<string, object> per, string input, DsPlanLocalContext local = null)
        {
            string here = (per.Str("where") ?? "").Split('（')[0];
            DsMoment.TryParse(per.Str("time"), out var moment);
            int day = moment.Day, now = moment.Index;
            var phase = moment.Phase;
            int left = (int)(per.Num("time_left_in_phase") ?? 4);
            string status = per.Str("whelp_status") ?? "rumor";
            double bond = per.Num("whelp_bond") ?? 0;
            bool scared = per.Str("whelp_mood") == "吓坏了";
            var inventory = Ids(per, "inventory");
            var inventoryText = (per["inventory"] as List<object>).Cast<string>().ToList();
            var items = Ids(per, "known_items");
            var facts = Ids(per, "known_facts");
            var laws = Ids(per, "known_laws");
            int coins = (int)(per.Num("coins") ?? 0);
            // 只读实际呈现给玩家的失败码，不读中文文案，也不读取静态试运行的异地真值。
            var reason = local?.Reason ?? default;
            string marenAt = reason.Is("actor_absent", "maren") ? "catacombs" : "cathedral";
            string pipAt = reason.Is("actor_absent", "pip") ? "market" : "docks";

            var steps = new List<Dictionary<string, object>>();
            int acts = 0;
            void Add(string op, string verb = "", string target = "", string item = "", string place = "", string until = "",
                string[] useLaws = null, string[] cites = null, string when = "", string why = "")
            {
                var l = (useLaws ?? new string[0]).Where(laws.Contains).Cast<object>().ToList();
                var c = (cites ?? new string[0]).Where(x => facts.Contains(x) || laws.Contains(x) || x.StartsWith("obs_")).Cast<object>().ToList();
                steps.Add(new Dictionary<string, object>
                {
                    ["id"] = "s" + (steps.Count + 1), ["op"] = op, ["verb"] = verb, ["target"] = target, ["item"] = item,
                    ["place"] = place, ["until"] = until, ["laws"] = l, ["cites"] = c, ["when"] = when, ["why"] = why,
                });
                if (op == "act" && when == "") acts++;
            }

            string food = inventoryText.FirstOrDefault(x => x.Contains("food") && !x.Contains("iron"))?.Split('：')[0];
            bool cloak = inventory.Contains("travel_cloak");
            bool exposedAfter = false;

            switch (status)
            {
                case "with_mother":
                    Add("act", "observe", why: "小烬已经团聚");
                    return Wrap(input, "小烬已经回到母亲身边，只需静候。", steps, null);
                case "caged_castle":
                {
                    if (phase != DayPhase.Dusk)
                    {
                        int nextDay = phase == DayPhase.Night ? day + 1 : day;
                        Add("wait", place: "workshop", until: new DsMoment(nextDay, DayPhase.Dusk).ToString(),
                            cites: new[] { "law_guards_dusk", "obs_whelp" }, why: "等黄昏换岗");
                    }
                    Add("act", "rescue", "whelp", place: "castle", useLaws: new[] { "law_guards_dusk" }, cites: new[] { "law_guards_dusk", "obs_whelp" }, why: "趁换岗撬开铁笼");
                    exposedAfter = true;
                    break;
                }
                case "following_cloaked":
                    break;
                case "following":
                    exposedAfter = true;
                    break;
                default:
                {
                    string at = status.StartsWith("left_at:") ? status.Substring(8) : status == "visible_here" ? here : "docks";
                    bool hiddenByPlayer = status.StartsWith("left_at:");
                    if (bond < 0.4 || scared)
                    {
                        if (food == null && items.Contains("smoked_fish") && coins >= 3)
                        { Add("act", "buy", item: "smoked_fish", place: "market", why: "买熏鱼引小烬"); food = "smoked_fish"; }
                        if (!hiddenByPlayer && status != "visible_here")
                            Add("act", "lure", "whelp", food ?? "", place: at, useLaws: new[] { "law_whelp_fish", "law_fear_noise" }, cites: new[] { "b_pip_whelp_place", "b_pip_fish", "obs_whelp" }, why: "用气味引它现身");
                        if (food != null)
                            Add("act", "feed", "whelp", food, place: at, useLaws: new[] { "law_whelp_fish" }, cites: new[] { "b_pip_fish" }, why: "喂它建立信任");
                    }
                    if (!hiddenByPlayer && cloak) Add("act", "hide", "whelp", "travel_cloak", place: at, cites: new[] { "obs_whelp" }, why: "用斗篷盖住它");
                    else if (!hiddenByPlayer) exposedAfter = true;
                    Add("act", "lead", "whelp", place: at, cites: new[] { "obs_whelp" }, why: "带它一起走");
                    break;
                }
            }
            if (exposedAfter && cloak)
            {
                Add("act", "hide", "whelp", "travel_cloak", place: "workshop", cites: new[] { "obs_whelp" }, why: "在工坊用斗篷盖好");
                exposedAfter = false;
            }

            // 关键取舍：找谁帮忙。梅伦需要契约页作凭据；皮普好说话但口风不紧；谁都不说最稳但没人帮。
            bool pactHeld = inventory.Contains("pact_page"), pactKnown = items.Contains("pact_page");
            var options = new List<(string id, string label)>();
            if (input.Contains("皮普")) options.Add(("pip", "托付皮普望风"));
            if (input.Contains("梅伦") && (pactHeld || pactKnown)) options.Add(("maren", "带契约页请梅伦同行"));
            if (input.Contains("谁也别") || input.Contains("不告诉") || input.Contains("别告诉") || input.Contains("谁都不")) options.Add(("none", "谁也不告诉"));
            foreach (var o in new[] { ("none", "谁也不告诉"), ("maren", "带契约页请梅伦同行"), ("pip", "托付皮普望风") })
                if (options.All(x => x.id != o.Item1) && (o.Item1 != "maren" || pactHeld || pactKnown)) options.Add(o);
            if (status != "caged_castle" && status != "with_mother")
            {
                Add("act", "confide", "pip", place: pipAt, cites: new[] { "obs_whelp" }, when: "helper=pip", why: "请皮普望风");
                if (options.Any(o => o.id == "maren"))
                {
                    if (!pactHeld) Add("act", "search", place: "catacombs", cites: new[] { "b_maren_page" }, when: "helper=maren", why: "找回契约页");
                    Add("act", "show", "maren", "pact_page", place: marenAt, cites: new[] { "b_maren_pact", "b_maren_page" }, when: "helper=maren", why: "给梅伦看凭据");
                    Add("act", "confide", "maren", place: marenAt, cites: new[] { "b_maren_pact" }, when: "helper=maren", why: "请梅伦同行");
                }
            }

            // 路线：估算完成上述行动后的时刻；尽量少在外暴露，第3天黄昏换岗时上龙骨岗。
            int estimate = now + (4 - left + 2 * acts) / 4;
            if (estimate < DuskD3.Index)
                Add("wait", place: "workshop", until: DuskD3.ToString(), cites: new[] { "obs_deadline", "law_guards_dusk" }, why: "在工坊躲到最后的黄昏");
            Add("wait", place: "skull_hill", until: FinalDawn.ToString(), cites: new[] { "obs_deadline" }, why: "在龙骨岗等母亲降临");

            var choice = status == "caged_castle" || status == "with_mother" ? null : new Dictionary<string, object>
            {
                ["id"] = "helper", ["question"] = "要把计划托付给谁？",
                ["options"] = options.Take(3).Select(o => (object)new Dictionary<string, object> { ["id"] = o.id, ["label"] = o.label }).ToList(),
            };
            // 没选到的选项对应步骤保留；取舍只有 1 个有效选项时去掉。
            if (choice != null && ((List<object>)choice["options"]).Count < 2) choice = null;
            if (choice == null) steps.RemoveAll(x => (string)x["when"] != "");
            string summary = (status == "caged_castle" ? "趁黄昏换岗撬开铁笼救出小烬，" : bond < 0.4 && !status.StartsWith("following") ? "先用熏鱼取得小烬信任，" : "") +
                             "用斗篷遮好带在身边，躲在工坊，最后的黄昏上龙骨岗等母亲。";
            // 取舍里非首选项对应的步骤，在 when 中已标注；基线最多 14 步。
            if (steps.Count > DsPlan.MaxSteps) steps = steps.Take(DsPlan.MaxSteps).ToList();
            for (int i = 0; i < steps.Count; i++) steps[i]["id"] = "s" + (i + 1);
            return Wrap(input, summary, steps, choice);
        }

        static Dictionary<string, object> Wrap(string input, string summary, List<Dictionary<string, object>> steps, Dictionary<string, object> choice) =>
            new Dictionary<string, object>
            {
                ["goal"] = string.IsNullOrWhiteSpace(input) ? "把小烬安全送回母亲身边" : (input.Length > 200 ? input.Substring(0, 200) : input),
                ["summary"] = summary,
                ["choices"] = choice == null ? new List<object>() : new List<object> { choice },
                ["steps"] = steps.Cast<object>().ToList(),
            };

        static HashSet<string> Ids(Dictionary<string, object> per, string key) =>
            new HashSet<string>(((per.TryGetValue(key, out var v) ? v as List<object> : null) ?? new List<object>()).Cast<string>().Select(x => x.Split('：')[0]));
    }
}
