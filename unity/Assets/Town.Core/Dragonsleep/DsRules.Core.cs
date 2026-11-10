using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 候选世界上的确定性规则。Session 只通过 DsExecutor 调用，正式世界由执行器提交。
    /// 所有判断都是确定性的（掷骰来自世界种子），便于重放与对比不同提供方。
    /// </summary>
    public static partial class DsRules
    {
        // ---------------- N4 语义判定 ----------------

        static readonly Dictionary<string, double> BaseDifficulty = new Dictionary<string, double>
        {
            ["buy"] = 0.05, ["lure"] = 0.6, ["feed"] = 0.5, ["give"] = 0.15, ["take"] = 0.35, ["search"] = 0.5,
            ["hide"] = 0.45, ["sneak"] = 0.6, ["show"] = 0.2, ["use"] = 0.15, ["observe"] = 0.1, ["wait"] = 0.05,
            ["lead"] = 0.3, ["release"] = 0.05, ["rescue"] = 0.55, ["confide"] = 0.05,
        };

        public static void Adjudicate(DsWorld w, Dictionary<string, object> p, AiTrace t,
            IDsRoll rolls = null, DsExecutionMode mode = DsExecutionMode.Live)
        {
            string verb = p.Str("verb"), target = p.Str("target") ?? "", itemId = p.Str("item") ?? "", place = p.Str("place") ?? "";
            // 先独立去重再校验，避免剔除未知规律时残留重复 ID 并绕过客观阻碍。
            var laws = p.StrList("laws")?.Distinct(StringComparer.Ordinal).ToList();
            double modelDifficulty = p.Num("difficulty") ?? 0.5;
            string narration = p.Str("narration") ?? "";
            if (verb == null || laws == null) { Fail(t, "schema_invalid", "缺少 verb 或 laws"); return; }
            if (!ContextBuilder.AdjudicateVerbs.Contains(verb)) { Fail(t, "verb_not_allowed", "动词不在白名单：" + verb); return; }
            if (verb == "unsupported") { Reject(t, "现有世界无法执行这个意图，请换一种可行办法", "intent_unsupported"); return; }
            if (place != "" && place != w.PlayerPlace)
            {
                Reject(t, "你此刻在" + w.Places[w.PlayerPlace] + "，做不到发生在"
                    + (w.Places.TryGetValue(place, out var pn) ? pn : place) + "的事", "place_mismatch", place);
                return;
            }

            DsItem item = null;
            if (itemId != "")
            {
                if (!w.Items.TryGetValue(itemId, out item)) { Fail(t, "unknown_entity", "不存在的物件：" + itemId); return; }
                bool reachable = item.Holder == "player" || item.Holder == "place:" + w.PlayerPlace ||
                                 (w.Actors.TryGetValue(item.Holder, out var holder) && holder.Place == w.PlayerPlace);
                if (!reachable) { Reject(t, "你身边没有「" + item.Name + "」", "item_missing", item.Id); return; }
            }
            bool targetWhelp = target == "whelp";
            var wh = w.Whelp;
            if (verb == "rescue")
            {
                if (!targetWhelp) { Reject(t, "救出只针对被关起来的雏龙", "rescue_target_invalid"); return; }
                if (wh.CapturedBy == null) { Reject(t, "小烬没有被关起来，用不着救", "rescue_not_needed"); return; }
                if (wh.CapturedBy != "osric" || wh.Place != w.PlayerPlace) { Reject(t, "小烬不在这里的笼子里", "rescue_target_absent"); return; }
                // 计划试运行不读取未来岗哨（玩家并不知道），这一条到真正执行时才检查。
                if (mode != DsExecutionMode.PlanPreview && DsGuards.IsWatched(w, w.PlayerPlace, out var guarded))
                { Reject(t, guarded + "，现在下手等于自投罗网", "place_guarded", w.PlayerPlace); return; }
            }
            else if (targetWhelp && (wh.Place != w.PlayerPlace || wh.CapturedBy != null))
            {
                bool cagedHere = wh.CapturedBy == "osric" && wh.Place == w.PlayerPlace;
                Reject(t, cagedHere ? "小烬被锁在王城的铁笼里，得先把它救出来" : "这里没有雏龙的踪迹",
                    cagedHere ? "whelp_caged" : "whelp_absent");
                return;
            }
            DsActor targetActor = null;
            if (!targetWhelp && target != "" && !w.Items.ContainsKey(target) && !w.Places.ContainsKey(target))
            {
                if (!w.Actors.TryGetValue(target, out targetActor)) { Fail(t, "unknown_entity", "不存在的对象：" + target); return; }
                if (targetActor.Place != w.PlayerPlace) { Reject(t, targetActor.Name + "不在这里", "actor_absent", targetActor.Id); return; }
            }
            if (verb == "buy" && (item == null || !w.Actors.ContainsKey(item.Holder)))
            { Reject(t, "这里没有人卖那样东西", "seller_absent", itemId); return; }
            if ((verb == "feed" || verb == "give" || verb == "show") && (item == null || item.Holder != "player"))
            { Reject(t, "你得先持有要" + (verb == "show" ? "出示" : "给出") + "的东西", "item_not_held", itemId); return; }
            if (verb == "feed" && (!targetWhelp || !item.Tags.Contains("food")))
            { Reject(t, "需要用可食用的东西喂身边的雏龙", "food_required"); return; }
            if (verb == "lure" && (!targetWhelp || (item != null && item.Holder != "player")))
            { Reject(t, "只能用自己持有的东西引诱身边的雏龙", "lure_target_or_item_invalid"); return; }
            if ((verb == "give" || verb == "show") && targetActor == null)
            { Reject(t, "需要明确一位在场的接收者", "recipient_required"); return; }
            if (verb == "take" && (item == null || item.Holder == "player"))
            { Reject(t, "需要明确一个尚未持有、能够拿取的物件", "take_item_invalid"); return; }
            if (verb == "hide" && !targetWhelp)
            { Reject(t, "当前只能安排雏龙的藏身处", "hide_target_invalid"); return; }
            if (verb == "hide" && wh.TrustPlayer < 0.3f)
            { Reject(t, "遮盖需要雏龙信任至少0.3；先亲近再藏好", "whelp_trust_low"); return; }
            if (verb == "lead")
            {
                if (!targetWhelp) { Reject(t, "只能带上身边的雏龙", "lead_target_invalid"); return; }
                if (wh.Following) { Reject(t, "小烬已经跟着你了", "whelp_already_following"); return; }
                if (wh.Hidden && !w.Flags.ContainsKey("whelp_hidden_by_player")) { Reject(t, "小烬还躲在船屋深处，得先让它现身", "whelp_hidden"); return; }
                if (wh.TrustPlayer < 0.4f) { Reject(t, $"小烬还不够信任你（{wh.TrustPlayer:0.00}，至少要 0.40），不肯跟你走", "whelp_trust_low"); return; }
                if (wh.Fear >= 0.85f) { Reject(t, "小烬吓坏了，缩成一团不肯动", "whelp_scared"); return; }
            }
            if (verb == "confide" && (targetActor == null || targetWhelp))
            { Reject(t, "需要明确一位在场的居民来托付", "confide_target_invalid"); return; }
            if (verb == "release" && (!targetWhelp || !wh.Following))
            { Reject(t, "小烬并没有跟着你", "whelp_not_following"); return; }
            if (verb == "use" && (item == null || item.Holder != "player" || !item.Crafted))
            { Reject(t, "需要持有一件已经织成的物件", "crafted_item_required"); return; }
            if (verb == "use" && !item.Tags.Overlaps(new[] { "light", "quiet", "calm", "lure:dragon" }))
            { Reject(t, "这件物件暂时没有能够主动使用的效果", "item_effect_unavailable"); return; }
            if (verb == "use" && !targetWhelp && !item.Tags.Contains("light") && !item.Tags.Contains("quiet"))
            { Reject(t, "这种效果需要对身边的雏龙使用", "effect_target_invalid"); return; }
            if (verb == "use" && w.Flags.ContainsKey("used:" + item.Id + ":" + w.PlayerPlace + ":" + (targetWhelp ? "whelp" : targetActor?.Id ?? "")))
            { Reject(t, "这件物件在这里的效果已经生效", "item_effect_already_used"); return; }

            // 规律引用：只能用已知的；支持/阻碍标签与物件、动词匹配才算数。
            var tags = new HashSet<string>(item?.Tags ?? new HashSet<string>()) { verb };
            if (verb == "wait" || verb == "observe") tags.Add("patience");
            if (narration.Contains("安静") || narration.Contains("轻") || narration.Contains("耐心")) { tags.Add("quiet"); tags.Add("patience"); }
            double engine = BaseDifficulty[verb];
            if (verb == "sneak" && w.Flags.ContainsKey("quiet:" + w.PlayerPlace)) { engine -= 0.15; t.Verdict.Notes.Add("织物吸收声响，潜行难度 −0.15"); }
            if (verb == "search" && w.Flags.ContainsKey("lit:" + w.PlayerPlace)) { engine -= 0.2; t.Verdict.Notes.Add("织光照亮细节，搜索难度 −0.20"); }
            // 同一规律在一次行动中最多计一次；模型重复引用不能叠加收益或阻碍。
            foreach (var lawId in laws.ToList())
            {
                if (!w.Laws.TryGetValue(lawId, out var law)) { t.ValidationIssues.Add("不存在的规律：" + lawId); laws.Remove(lawId); continue; }
                if (!w.KnowsLaw(lawId)) { t.ValidationIssues.Add("玩家尚未得知该规律，不计入：" + lawId); laws.Remove(lawId); continue; }
                bool helps = law.SupportTags.Any(tags.Contains) && !(law.HinderTags.Any(tags.Contains) && targetWhelp);
                bool hurts = targetWhelp && law.HinderTags.Any(tags.Contains);
                if (helps) { engine -= 0.25; t.Verdict.Notes.Add("依据「" + law.Text + "」难度 −0.25"); }
                if (hurts) { engine += 0.3; t.Verdict.Notes.Add("违背「" + law.Text + "」难度 +0.30"); }
            }
            // 未引用但客观存在的阻碍规律照样生效（世界不会因为你不知道就不成立）。
            if (targetWhelp && item != null && item.Tags.Contains("iron") && !laws.Contains("law_dragon_iron"))
            { engine += 0.3; t.Verdict.Notes.Add("冷铁让雏龙恐惧（未知规律仍然生效）难度 +0.30"); }
            if (targetWhelp && verb == "take") engine += 0.2;
            if (targetWhelp && (verb == "hide" || verb == "feed" || verb == "lure" || verb == "lead") && w.Whelp.TrustPlayer > 0)
            {
                double bonus = Math.Min(0.3, w.Whelp.TrustPlayer * 0.5);
                engine -= bonus;
                t.Verdict.Notes.Add($"小烬信任你（{w.Whelp.TrustPlayer:0.00}）难度 −{bonus:0.00}");
            }

            engine = Clamp(engine, 0.05, 0.95);
            modelDifficulty = Clamp(modelDifficulty, 0, 1);
            double final = Clamp(0.7 * engine + 0.3 * modelDifficulty, 0.05, 0.95);
            if (Math.Abs(engine - modelDifficulty) > 0.35) t.Verdict.Notes.Add($"模型估计 {modelDifficulty:0.00} 与规则 {engine:0.00} 分歧较大");
            double roll = verb == "observe" || verb == "wait" || verb == "buy" || verb == "release" || verb == "confide"
                ? 1.0 : DsRoll.Next(w, rolls);
            bool success = roll >= final;
            t.Verdict.Difficulty = final;
            t.Verdict.Roll = roll;
            t.Verdict.Kind = success ? VerdictKind.Accepted : VerdictKind.Rejected;
            t.Verdict.ReasonCode = new DsReason(success ? "roll_succeeded" : "roll_failed");
            t.Verdict.Reason = success ? $"成功（掷骰 {roll:0.00} ≥ 难度 {final:0.00}）" : $"失败（掷骰 {roll:0.00} < 难度 {final:0.00}）";

            string outcome = Effects(w, verb, item, targetWhelp, targetActor, success, t, mode);
            t.Presented = (narration.Length > 0 ? narration + " " : "") + outcome;
        }

        // ---------------- 工具 ----------------

        static void Relocate(DsWorld w, DsActor a, string place, AiTrace t)
        {
            if (a.Place == place) return;
            t.Changes.Add(new WorldChange { Path = a.Id + ".place", Before = a.Place, After = place });
            a.Place = place;
        }

        static void Move(AiTrace t, DsItem item, string holder)
        {
            t.Changes.Add(new WorldChange { Path = "items." + item.Id + ".holder", Before = item.Holder, After = holder });
            item.Holder = holder;
        }

        static void Set(AiTrace t, string path, ref float field, float value)
        {
            value = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(field - value) < 1e-4) return;
            t.Changes.Add(new WorldChange { Path = path, Before = field.ToString("0.00"), After = value.ToString("0.00") });
            field = value;
        }

        static void Fail(AiTrace t, string code, string detail)
        {
            t.FailureCode = code;
            t.Verdict.Kind = VerdictKind.Rejected;
            t.Verdict.Reason = detail;
            t.Verdict.ReasonCode = new DsReason(code);
            t.ValidationIssues.Add(detail);
            t.Presented = "织言机没能理解这件事。（" + detail + "）";
        }

        static void Reject(AiTrace t, string reason, string code = "precondition", string entity = null)
        {
            t.Verdict.Kind = VerdictKind.Rejected;
            t.Verdict.Reason = reason;
            t.Verdict.ReasonCode = new DsReason(code, entity);
            t.Presented = reason + "。";
        }

        static string Deflect(DsActor a)
        {
            switch (a.Id)
            {
                case "osric": return "王室的事，轮不到外乡人过问。";
                case "maren": return "有些事……还是交给神吧。";
                case "pip": return "我什么都不知道！你别跟着我。";
                case "vesna": return "哎呀，这可是另一笔生意了，朋友。";
                default: return "这个嘛……我不好说。";
            }
        }

        static string Short(string s) => s.Length <= 18 ? s : s.Substring(0, 18) + "…";

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>字符二元组重合率：用于发现“没申报却说漏嘴”。</summary>
        public static double Overlap(string said, string secret)
        {
            if (string.IsNullOrEmpty(said) || string.IsNullOrEmpty(secret) || secret.Length < 2) return 0;
            var a = new HashSet<string>(); for (int i = 0; i + 1 < said.Length; i++) a.Add(said.Substring(i, 2));
            var b = new HashSet<string>(); for (int i = 0; i + 1 < secret.Length; i++) b.Add(secret.Substring(i, 2));
            int hit = b.Count(a.Contains);
            return b.Count == 0 ? 0 : (double)hit / b.Count;
        }
    }
}
