using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        /// <summary>
        /// 把护送计划托付给在场居民：对方从此知道计划（进入其认知，夜里可能转告他人）。
        /// 梅伦接受已建立的信任或原件保管核验；皮普要看到小烬信任你才肯望风；其他人只会记下。
        /// </summary>
        static string Confide(DsWorld w, DsActor actor, AiTrace t, bool preview = false)
        {
            string id = "b_plan_" + actor.Id;
            if (w.Belief(id) == null)
            {
                w.Beliefs.Add(new DsBelief { Id = id, Owner = actor.Id, RootId = id, RootOwner = actor.Id, Hops = 0, Topic = "plan", RevealTrust = 0.3f, Source = "told:player",
                    Text = "言契师打算把小烬送上龙骨岗，等苍穹之母降临时交还给她。" });
                t.Changes.Add(new WorldChange { Path = actor.Id + ".beliefs", Before = "", After = id, Note = "得知护送计划" });
            }
            w.Flags["confided:" + actor.Id] = w.ClockText;
            var wh = w.Whelp;
            switch (actor.Id)
            {
                case "maren":
                    bool custody = w.Flags.ContainsKey("pact_custody:maren");
                    bool shown = w.Flags.ContainsKey("shown:pact_page:maren");
                    // 试运行只能条件性假设合作：不能用隐藏的信任/远处持有人形成预测反馈。
                    bool willing = preview ? custody || shown
                        : (custody && w.Items["pact_page"].Holder == "maren") || (shown && actor.Trust >= 0.5f);
                    if (willing)
                    {
                        w.Flags["ally:maren"] = w.ClockText;
                        t.Changes.Add(new WorldChange { Path = "flags.ally", Before = "", After = "maren", Note = "以教会名义同行" });
                        t.Verdict.Reason = "梅伦愿意同行";
                        if (preview) t.Verdict.Notes.Add("梅伦是否合作需实际托付确认；此处不读取私有信任");
                        return "梅伦按住那页契约：“教会欠她一个交代。这一程，我以教会的名义陪你走。”";
                    }
                    t.Verdict.Kind = VerdictKind.Partial;
                    t.Verdict.Reason = shown ? "梅伦看过凭据，但仍要求保管原件核验才肯同行" : "梅伦尚未见到契约凭据";
                    t.Verdict.ReasonCode = new DsReason(shown ? "confide_custody_required" : "confide_evidence_required", actor.Id);
                    // 仅记录这次实际说出口的条件，不把私有信任数值变成玩家知识。
                    w.Flags["requested:pact_custody:maren"] = w.ClockText;
                    t.Changes.Add(new WorldChange { Path = "flags.requested", Before = "", After = "pact_custody:maren", Note = "公开提出合作条件" });
                    return shown ? "梅伦摇头：“我认得这页纸，但看过不代表能冒险。把契约原件交我保管核验，我才肯以教会名义同行。”"
                        : "梅伦说：“先让我看到契约，或把原件交我保管核验。没有凭据，我不能以教会名义同行。”";
                case "pip":
                    if (wh.TrustPlayer >= 0.4f && wh.CapturedBy == null)
                    {
                        w.Flags["ally:pip"] = w.ClockText;
                        t.Changes.Add(new WorldChange { Path = "flags.ally", Before = "", After = "pip", Note = "替你望风" });
                        if (wh.Place == w.PlayerPlace) Set(t, "whelp.fear", ref wh.Fear, wh.Fear - 0.2f);
                        t.Verdict.Reason = "皮普愿意望风";
                        return "皮普看着小烬蹭你的手，咬了咬牙：“行。我替你们望风——可别骗我。”";
                    }
                    t.Verdict.Kind = VerdictKind.Partial;
                    t.Verdict.Reason = "皮普不信任你";
                    t.Verdict.ReasonCode = new DsReason("confide_trust_low", actor.Id);
                    return "皮普瞪着你：“小烬都还不信你，我凭什么信？”可他显然把话听进去了。";
                default:
                    t.Verdict.Kind = VerdictKind.Partial;
                    t.Verdict.Reason = actor.Name + "只是记下了";
                    t.Verdict.ReasonCode = new DsReason("confide_recorded_only", actor.Id);
                    return actor.Name + "听完，意味深长地看了你一眼。";
            }
        }

        // ---------------- N5 多智能体 ----------------

        public static void AgentPlan(DsWorld w, string npcId, Dictionary<string, object> p, AiTrace t, IDsRoll rolls = null)
        {
            var a = w.Actors[npcId];
            string verb = p.Str("verb"), place = p.Str("place") ?? a.Place, target = p.Str("target") ?? "";
            var share = p.StrList("share");
            string reason = p.Str("reason") ?? "";
            if (verb == null || share == null) { Fail(t, "schema_invalid", "缺少 verb/share"); return; }
            if (!ContextBuilder.AgentVerbs.Contains(verb)) { Fail(t, "verb_not_allowed", "动词不在白名单：" + verb); return; }
            if (!w.Places.ContainsKey(place)) { Fail(t, "unknown_entity", "未知地点：" + place); return; }
            t.Verdict.Kind = VerdictKind.Accepted;
            string line;
            switch (verb)
            {
                case "move":
                case "guard":
                    Relocate(w, a, place, t);
                    if (verb == "guard") w.Flags["guard:" + place] = npcId;
                    ObserveLocalGuards(w, a.Place, t);
                    line = a.Name + (verb == "guard" ? "守在" : "去了") + w.Places[place];
                    break;
                case "search":
                    Relocate(w, a, place, t);
                    line = a.Name + "在" + w.Places[place] + "四处搜寻";
                    var wh = w.Whelp;
                    if (wh.Place == place && wh.CapturedBy == null && npcId != "pip")
                    {
                        bool knows = w.BeliefsOf(npcId).Any(b => b.Topic == "whelp" && b.Text.Contains("码头"));
                        bool guarded = w.Flags.TryGetValue("guard:" + place, out var g) && g == "pip";
                        bool playerHid = wh.Hidden && w.Flags.ContainsKey("whelp_hidden_by_player");
                        double chance = (wh.Hidden ? 0.25 : 0.45) + (knows ? 0.2 : 0) - (guarded ? 0.2 : 0) - (playerHid ? 0.15 : 0);
                        chance = Math.Max(0.05, chance);
                        double roll = DsRoll.Next(w, rolls);
                        t.Verdict.Roll = roll; t.Verdict.Difficulty = 1 - chance;
                        if (roll < chance)
                        {
                            DsGuards.Capture(w, t, npcId);
                            w.Tension = Math.Min(1, w.Tension + 0.25f);
                            line += "——并且抓住了雏龙！";
                        }
                        else line += "，但什么也没找到";
                    }
                    break;
                case "tell":
                    if (!w.Actors.TryGetValue(target, out var listener) || listener.Id == npcId)
                    { Fail(t, "unknown_entity", "tell 的对象无效：" + target); return; }
                    if (listener.Place != a.Place) { Reject(t, "交谈对象不在身边，需要先寻访", "actor_absent", listener.Id); return; }
                    List<DsBelief> shared;
                    string firstFailure;
                    ShareBeliefs(w, a, listener, share, t, out shared, out firstFailure);
                    if (shared.Count == 0)
                    {
                        t.Verdict.Kind = VerdictKind.Rejected;
                        t.Verdict.ReasonCode = new DsReason(firstFailure ?? "no_beliefs_selected", target);
                        t.Verdict.Reason = "没有可实际传出的认知";
                        line = a.Name + "没有把可转述的消息告诉" + listener.Name;
                        break;
                    }
                    if (firstFailure != null)
                    {
                        t.Verdict.Kind = VerdictKind.Partial;
                        t.Verdict.ReasonCode = new DsReason("partial_hearsay", target);
                        t.Verdict.Reason = "只传出了 " + shared.Count + " 条认知";
                    }
                    else
                    {
                        t.Verdict.Kind = VerdictKind.Accepted;
                        t.Verdict.ReasonCode = new DsReason("hearsay_shared", target);
                        t.Verdict.Reason = "传出了 " + shared.Count + " 条认知";
                    }
                    if (shared.Count > 0)
                    {
                        string key = target + ">" + npcId;
                        float prior = w.Relations.TryGetValue(key, out var trust) ? trust : 0.5f;
                        w.Relations[key] = Math.Min(1f, prior + 0.05f);
                        t.Changes.Add(new WorldChange
                        {
                            Path = "relations." + key, Before = prior.ToString("0.00"),
                            After = w.Relations[key].ToString("0.00"), Note = "收到新信息"
                        });
                    }
                    line = a.Name + (t.Verdict.Kind == VerdictKind.Partial ? "只把以下消息告诉" : "把以下消息告诉") + listener.Name +
                        "：" + string.Join("；", shared.Select(b => b.Text));
                    break;
                default:
                    line = a.Name + "一夜没有出门";
                    break;
            }
            if (verb != "tell") t.Verdict.Reason = reason;
            t.Presented = line + "。";
            w.Chronicle.Add(w.ClockText + "：" + line + "。");
        }
    }
}
