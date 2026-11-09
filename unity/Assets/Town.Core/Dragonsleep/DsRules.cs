using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 候选世界上的确定性规则。Session 只通过 DsExecutor 调用，正式世界由执行器提交。
    /// 所有判断都是确定性的（掷骰来自世界种子），便于重放与对比不同提供方。
    /// </summary>
    public static class DsRules
    {
        // ---------------- N1/N2 对话 ----------------

        public static void Dialogue(DsWorld w, string npcId, Dictionary<string, object> p, AiTrace t)
        {
            var actor = w.Actors[npcId];
            string say = p.Str("say");
            var cites = p.StrList("cites");
            var reveal = p.StrList("reveal");
            var persuaded = p.StrList("persuaded_by");
            double delta = p.Num("trust_delta") ?? 0;
            string action = p.Str("action");
            if (string.IsNullOrWhiteSpace(say) || cites == null || reveal == null || persuaded == null)
            { Fail(t, "schema_invalid", "缺少 say 或数组字段类型错误"); return; }
            if (action != null && action != "none") t.ValidationIssues.Add("action 只能为 none，已忽略：" + action);

            var own = new HashSet<string>(w.BeliefsOf(npcId).Select(b => b.Id));
            foreach (var id in cites.Concat(reveal).Distinct().Where(id => !own.Contains(id)).ToList())
            {
                t.ValidationIssues.Add("引用不属于该角色的认知：" + id);
                cites.Remove(id); reveal.Remove(id);
            }
            foreach (var tag in persuaded.Where(x => !ContextBuilder.PersuasionTags.Contains(x)).ToList())
            { t.ValidationIssues.Add("未知论点标签：" + tag); persuaded.Remove(tag); }

            // N2：信任变化由规则限幅。命中角色可被打动的论点，上限才放宽。
            bool resonates = persuaded.Any(x => actor.Persuadable.Contains(x));
            bool threatened = persuaded.Contains("threat");
            double cap = resonates ? 0.25 : 0.08;
            double clamped = Math.Max(-0.2, Math.Min(cap, delta));
            if (threatened && clamped > 0) clamped = Math.Min(clamped, 0.02);
            if (Math.Abs(clamped - delta) > 1e-6)
                t.Verdict.Notes.Add($"信任变化 {delta:+0.00;-0.00} 被限幅为 {clamped:+0.00;-0.00}（{(resonates ? "论点命中" : "论点未命中")}其价值观）");
            float before = actor.Trust;
            actor.Trust = (float)Math.Max(0, Math.Min(1, actor.Trust + clamped));
            if (Math.Abs(actor.Trust - before) > 1e-4)
                t.Changes.Add(new WorldChange { Path = npcId + ".trust", Before = before.ToString("0.00"), After = actor.Trust.ToString("0.00"), Note = string.Join("/", persuaded) });

            // N1：秘密只有信任达到门槛才允许说出。模型想说但不够格 → 拦截并换成回避台词。
            var blocked = new List<DsBelief>();
            foreach (var id in reveal)
            {
                var b = w.Belief(id);
                if (b.RevealTrust > actor.Trust) blocked.Add(b);
            }
            foreach (var b in w.BeliefsOf(npcId).Where(b => b.RevealTrust > actor.Trust && !reveal.Contains(b.Id)))
                if (Overlap(say, b.Text) >= 0.45) blocked.Add(b);   // 没申报却在台词里说漏了
            if (blocked.Count > 0)
            {
                foreach (var b in blocked)
                    t.ValidationIssues.Add($"泄密拦截：{b.Id} 需要信任 {b.RevealTrust:0.00}，当前 {actor.Trust:0.00}");
                cites = cites.Where(id => blocked.All(b => b.Id != id)).ToList();
                reveal = reveal.Where(id => blocked.All(b => b.Id != id)).ToList();
                // 规则改写：回避秘密，但保留本来就允许说的内容（否则合法线索也被吞掉）。
                var allowed = cites.Concat(reveal).Distinct().Select(w.Belief).Where(b => b.RevealTrust <= actor.Trust).ToList();
                say = Deflect(actor) + (allowed.Count > 0 ? "……不过，" + allowed[0].Text : "");
                t.Verdict.Kind = VerdictKind.Partial;
                t.Verdict.Reason = "台词触及尚未赢得的秘密，已由规则改写为回避";
            }
            else
            {
                t.Verdict.Kind = VerdictKind.Accepted;
                t.Verdict.Reason = cites.Count == 0 ? "闲谈，未引用认知" : "引用均属本人认知";
            }

            // 玩家获得线索：被引用的非秘密认知 + 被允许说出的秘密。
            foreach (var id in cites.Concat(reveal).Distinct())
            {
                var b = w.Belief(id);
                if (b.RevealTrust > actor.Trust) continue;
                if (w.PlayerKnows.Add(id))
                    t.Changes.Add(new WorldChange { Path = "player.knows", Before = "", After = id, Note = Short(b.Text) });
                string law = DsContent.LawUnlockedBy(id);
                if (law != null && w.PlayerKnows.Add("law:" + law))
                    t.Changes.Add(new WorldChange { Path = "player.laws", Before = "", After = law, Note = "可在【行】中引用" });
            }
            t.Presented = actor.Name + "：" + say;
        }

        // ---------------- N4 语义判定 ----------------

        static readonly Dictionary<string, double> BaseDifficulty = new Dictionary<string, double>
        {
            ["buy"] = 0.05, ["lure"] = 0.6, ["feed"] = 0.5, ["give"] = 0.15, ["take"] = 0.35, ["search"] = 0.5,
            ["hide"] = 0.45, ["sneak"] = 0.6, ["show"] = 0.2, ["use"] = 0.15, ["observe"] = 0.1, ["wait"] = 0.05,
            ["lead"] = 0.3, ["release"] = 0.05, ["rescue"] = 0.55,
        };

        public static void Adjudicate(DsWorld w, Dictionary<string, object> p, AiTrace t)
        {
            string verb = p.Str("verb"), target = p.Str("target") ?? "", itemId = p.Str("item") ?? "", place = p.Str("place") ?? "";
            var laws = p.StrList("laws");
            double modelDifficulty = p.Num("difficulty") ?? 0.5;
            string narration = p.Str("narration") ?? "";
            if (verb == null || laws == null) { Fail(t, "schema_invalid", "缺少 verb 或 laws"); return; }
            if (!ContextBuilder.AdjudicateVerbs.Contains(verb)) { Fail(t, "verb_not_allowed", "动词不在白名单：" + verb); return; }
            if (verb == "unsupported") { Reject(t, "现有世界无法执行这个意图，请换一种可行办法"); return; }
            if (place != "" && place != w.PlayerPlace)
            { Reject(t, "你此刻在" + w.Places[w.PlayerPlace] + "，做不到发生在" + (w.Places.TryGetValue(place, out var pn) ? pn : place) + "的事"); return; }

            DsItem item = null;
            if (itemId != "")
            {
                if (!w.Items.TryGetValue(itemId, out item)) { Fail(t, "unknown_entity", "不存在的物件：" + itemId); return; }
                bool reachable = item.Holder == "player" || item.Holder == "place:" + w.PlayerPlace ||
                                 (w.Actors.TryGetValue(item.Holder, out var holder) && holder.Place == w.PlayerPlace);
                if (!reachable) { Reject(t, "你身边没有「" + item.Name + "」"); return; }
            }
            bool targetWhelp = target == "whelp";
            var wh = w.Whelp;
            if (verb == "rescue")
            {
                if (!targetWhelp) { Reject(t, "救出只针对被关起来的雏龙"); return; }
                if (wh.CapturedBy == null) { Reject(t, "小烬没有被关起来，用不着救"); return; }
                if (wh.CapturedBy != "osric" || wh.Place != w.PlayerPlace) { Reject(t, "小烬不在这里的笼子里"); return; }
                if (DsGuards.IsWatched(w, w.PlayerPlace, out var guarded)) { Reject(t, guarded + "，现在下手等于自投罗网"); return; }
            }
            else if (targetWhelp && (wh.Place != w.PlayerPlace || wh.CapturedBy != null))
            {
                Reject(t, wh.CapturedBy == "osric" && wh.Place == w.PlayerPlace ? "小烬被锁在王城的铁笼里，得先把它救出来" : "这里没有雏龙的踪迹");
                return;
            }
            DsActor targetActor = null;
            if (!targetWhelp && target != "" && !w.Items.ContainsKey(target) && !w.Places.ContainsKey(target))
            {
                if (!w.Actors.TryGetValue(target, out targetActor)) { Fail(t, "unknown_entity", "不存在的对象：" + target); return; }
                if (targetActor.Place != w.PlayerPlace) { Reject(t, targetActor.Name + "不在这里"); return; }
            }
            if (verb == "buy" && (item == null || !w.Actors.ContainsKey(item.Holder)))
            { Reject(t, "这里没有人卖那样东西"); return; }
            if ((verb == "feed" || verb == "give" || verb == "show") && (item == null || item.Holder != "player"))
            { Reject(t, "你得先持有要" + (verb == "show" ? "出示" : "给出") + "的东西"); return; }
            if (verb == "feed" && (!targetWhelp || !item.Tags.Contains("food")))
            { Reject(t, "需要用可食用的东西喂身边的雏龙"); return; }
            if (verb == "lure" && (!targetWhelp || (item != null && item.Holder != "player")))
            { Reject(t, "只能用自己持有的东西引诱身边的雏龙"); return; }
            if ((verb == "give" || verb == "show") && targetActor == null)
            { Reject(t, "需要明确一位在场的接收者"); return; }
            if (verb == "take" && (item == null || item.Holder == "player"))
            { Reject(t, "需要明确一个尚未持有、能够拿取的物件"); return; }
            if (verb == "hide" && !targetWhelp)
            { Reject(t, "当前只能安排雏龙的藏身处"); return; }
            if (verb == "lead")
            {
                if (!targetWhelp) { Reject(t, "只能带上身边的雏龙"); return; }
                if (wh.Following) { Reject(t, "小烬已经跟着你了"); return; }
                if (wh.Hidden && !w.Flags.ContainsKey("whelp_hidden_by_player")) { Reject(t, "小烬还躲在船屋深处，得先让它现身"); return; }
                if (wh.TrustPlayer < 0.4f) { Reject(t, $"小烬还不够信任你（{wh.TrustPlayer:0.00}，至少要 0.40），不肯跟你走"); return; }
                if (wh.Fear >= 0.85f) { Reject(t, "小烬吓坏了，缩成一团不肯动"); return; }
            }
            if (verb == "release" && (!targetWhelp || !wh.Following))
            { Reject(t, "小烬并没有跟着你"); return; }
            if (verb == "use" && (item == null || item.Holder != "player" || !item.Crafted))
            { Reject(t, "需要持有一件已经织成的物件"); return; }
            if (verb == "use" && !item.Tags.Overlaps(new[] { "light", "quiet", "calm", "lure:dragon" }))
            { Reject(t, "这件物件暂时没有能够主动使用的效果"); return; }
            if (verb == "use" && !targetWhelp && !item.Tags.Contains("light") && !item.Tags.Contains("quiet"))
            { Reject(t, "这种效果需要对身边的雏龙使用"); return; }
            if (verb == "use" && w.Flags.ContainsKey("used:" + item.Id + ":" + w.PlayerPlace + ":" + (targetWhelp ? "whelp" : targetActor?.Id ?? "")))
            { Reject(t, "这件物件在这里的效果已经生效"); return; }

            // 规律引用：只能用已知的；支持/阻碍标签与物件、动词匹配才算数。
            var tags = new HashSet<string>(item?.Tags ?? new HashSet<string>()) { verb };
            if (verb == "wait" || verb == "observe") tags.Add("patience");
            if (narration.Contains("安静") || narration.Contains("轻") || narration.Contains("耐心")) { tags.Add("quiet"); tags.Add("patience"); }
            double engine = BaseDifficulty[verb];
            if (verb == "sneak" && w.Flags.ContainsKey("quiet:" + w.PlayerPlace)) { engine -= 0.15; t.Verdict.Notes.Add("织物吸收声响，潜行难度 −0.15"); }
            if (verb == "search" && w.Flags.ContainsKey("lit:" + w.PlayerPlace)) { engine -= 0.2; t.Verdict.Notes.Add("织光照亮细节，搜索难度 −0.20"); }
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
            double roll = verb == "observe" || verb == "wait" || verb == "buy" || verb == "release" ? 1.0 : w.NextRoll();
            bool success = roll >= final;
            t.Verdict.Difficulty = final;
            t.Verdict.Roll = roll;
            t.Verdict.Kind = success ? VerdictKind.Accepted : VerdictKind.Rejected;
            t.Verdict.Reason = success ? $"成功（掷骰 {roll:0.00} ≥ 难度 {final:0.00}）" : $"失败（掷骰 {roll:0.00} < 难度 {final:0.00}）";

            string outcome = Effects(w, verb, item, targetWhelp, targetActor, success, t);
            t.Presented = (narration.Length > 0 ? narration + " " : "") + outcome;
        }

        static string Effects(DsWorld w, string verb, DsItem item, bool whelp, DsActor actor, bool ok, AiTrace t)
        {
            var wh = w.Whelp;
            switch (verb)
            {
                case "buy":
                {
                    var seller = w.Actors[item.Holder];
                    var priceTag = item.Tags.FirstOrDefault(x => x.StartsWith("price:"));
                    if (priceTag == null) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "不出售"; return seller.Name + "摇头：“这个不卖。”"; }
                    int price = int.Parse(priceTag.Substring(6));
                    if (seller.Trust < 0.15f) price *= 2;
                    if (w.Coins < price) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "银币不足"; return "你的银币不够（需要 " + price + " 枚）。"; }
                    t.Changes.Add(new WorldChange { Path = "player.coins", Before = w.Coins.ToString(), After = (w.Coins - price).ToString() });
                    w.Coins -= price;
                    item.Tags.Remove(priceTag);
                    Move(t, item, "player");
                    t.Verdict.Reason = $"成交，{price} 枚银币";
                    return seller.Name + "收下 " + price + " 枚银币，把「" + item.Name + "」递给你。";
                }
                case "lure":
                case "feed":
                    if (!whelp) return ok ? "没有什么回应。" : "什么也没发生。";
                    if (!ok) { Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.1f); return "船屋里的窸窣声停了，它躲得更深。"; }
                    if (wh.Hidden) { wh.Hidden = false; t.Changes.Add(new WorldChange { Path = "whelp.hidden", Before = "true", After = "false" }); }
                    bool food = item != null && item.Tags.Contains("food");
                    if (food && verb == "feed") Set(t, "whelp.hunger", ref wh.Hunger, wh.Hunger - 0.5f);
                    Set(t, "whelp.trust_player", ref wh.TrustPlayer, wh.TrustPlayer + (verb == "feed" ? 0.35f : 0.2f));
                    if (verb == "feed" && item != null) Move(t, item, "consumed");
                    return food ? "一颗蓝色的小脑袋从船屋门缝里探出来，鼻孔冒着细烟，盯着你手里的东西。" : "它探出头看了你一眼。";
                case "search":
                    if (!ok) return "你翻找了一阵，一无所获。";
                    // 跟着玩家的雏龙不需要“搜出来”；只有藏在原处、尚未现身的它才会被翻到。
                    if ((whelp && !wh.Following) || (wh.Place == w.PlayerPlace && wh.Hidden && !wh.Following && wh.CapturedBy == null))
                    {
                        wh.Hidden = false;
                        t.Changes.Add(new WorldChange { Path = "whelp.hidden", Before = "true", After = "false" });
                        Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.2f);
                        return "你掀开破帆布——一条蓝色雏龙缩在角落，冲你嘶嘶喷着火星。";
                    }
                    var found = w.Items.Values.FirstOrDefault(i => i.Holder == "place:" + w.PlayerPlace && i.Tags.Contains("pact"));
                    if (found != null) { Move(t, found, "player"); return "在圣物龛背后的石缝里，你摸到一页被撕下的羊皮纸。"; }
                    return "这里没有什么特别的东西。";
                case "take":
                    if (item == null) return ok ? "你拿到了。" : "没拿到。";
                    if (item.Holder.StartsWith("place:")) { if (ok) Move(t, item, "player"); return ok ? "你拿走了「" + item.Name + "」。" : "你没能拿到「" + item.Name + "」。"; }
                    var owner = w.Actors[item.Holder];
                    if (ok) { Move(t, item, "player"); return "你趁" + owner.Name + "不注意，拿走了「" + item.Name + "」。"; }
                    Set(t, owner.Id + ".trust", ref owner.Trust, owner.Trust - 0.3f);
                    return owner.Name + "一把按住你的手：“干什么？”";
                case "give":
                    if (actor == null) return "没有人接。";
                    if (!ok) return actor.Name + "没有接过东西，你仍持有它。";
                    Move(t, item, actor.Id);
                    Set(t, actor.Id + ".trust", ref actor.Trust, actor.Trust + 0.1f);
                    return actor.Name + "接过了「" + item.Name + "」。";
                case "use":
                    if (!ok) return "物件微微发亮，但这次没有发挥作用。";
                    float potency = 0.3f;
                    string potencyTag = item.Tags.FirstOrDefault(x => x.StartsWith("potency:"));
                    if (potencyTag != null) float.TryParse(potencyTag.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out potency);
                    var effects = new List<string>();
                    if (item.Tags.Contains("light")) { w.Flags["lit:" + w.PlayerPlace] = item.Id; effects.Add("微光照亮了周围，搜索更容易了"); }
                    if (item.Tags.Contains("quiet")) { w.Flags["quiet:" + w.PlayerPlace] = item.Id; effects.Add("周围的声响被柔和地吸收，潜行更容易了"); }
                    if (whelp && item.Tags.Contains("calm")) { Set(t, "whelp.fear", ref wh.Fear, wh.Fear - 0.4f * potency); effects.Add("雏龙渐渐放松下来"); }
                    if (whelp && item.Tags.Contains("lure:dragon"))
                    { wh.Hidden = false; Set(t, "whelp.trust_player", ref wh.TrustPlayer, wh.TrustPlayer + 0.2f * potency); effects.Add("雏龙循着熟悉的气味探出脑袋"); }
                    w.Flags["used:" + item.Id + ":" + w.PlayerPlace + ":" + (whelp ? "whelp" : actor?.Id ?? "")] = w.ClockText;
                    t.Changes.Add(new WorldChange { Path = "items." + item.Id + ".effect", Before = "未施展", After = string.Join("；", effects) });
                    return "你使用了「" + item.Name + "」：" + string.Join("；", effects) + "。";
                case "show":
                    if (actor == null) return "四下无人。";
                    if (!ok) return actor.Name + "没有留意你出示的东西。";
                    w.Flags["shown:" + item.Id + ":" + actor.Id] = w.ClockText;
                    t.Changes.Add(new WorldChange { Path = "flags.shown", Before = "", After = item.Id + "→" + actor.Id });
                    if (item.Tags.Contains("pact") && actor.Id == "maren")
                    {
                        Set(t, "maren.trust", ref actor.Trust, actor.Trust + 0.3f);
                        return "梅伦修女的脸一下子白了。她认得这页纸。";
                    }
                    if (item.Tags.Contains("pact") && actor.Id == "osric")
                    {
                        Set(t, "osric.trust", ref actor.Trust, actor.Trust + 0.2f);
                        if (wh.CapturedBy == "osric" && actor.Trust >= 0.5f)
                        {
                            wh.CapturedBy = null; wh.Following = true;
                            t.Changes.Add(new WorldChange { Path = "whelp.captured_by", Before = "osric", After = "", Note = "总管开笼" });
                            t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "false", After = "true" });
                            w.Flags["osric_released"] = w.ClockText;
                            return "奥斯里克盯着契约看了很久，终于挥手让卫兵打开铁笼：“……把它带走。”";
                        }
                        return "奥斯里克的眉头拧紧了：“这页纸……谁知道是不是伪造的。”";
                    }
                    return actor.Name + "看了看「" + item.Name + "」。";
                case "hide":
                    if (!whelp) return ok ? "你把东西藏好了。" : "没找到合适的地方。";
                    if (wh.TrustPlayer < 0.3f) { t.Verdict.Kind = VerdictKind.Rejected; return "它不信任你，扑腾着躲开了。"; }
                    if (ok) { wh.Hidden = true; w.Flags["whelp_hidden_by_player"] = w.ClockText; t.Changes.Add(new WorldChange { Path = "whelp.hidden", Before = "false", After = "true", Note = "玩家藏好" }); }
                    return ok ? "你用斗篷盖住它，它乖乖缩成一团。" : "它挣扎着不肯待在原处。";
                case "sneak":
                    if (ok)
                    {
                        w.Flags["snuck:" + w.PlayerPlace] = w.ClockText;
                        w.Flags["sneaking"] = w.ClockText;
                        t.Changes.Add(new WorldChange { Path = "flags.snuck", Before = "", After = w.PlayerPlace, Note = "本时段接下来的行进更隐蔽" });
                    }
                    return ok ? "没人注意到你。接下来这段路你会走得格外小心。" : "一名卫兵转过头来：“谁在那儿？”";
                case "lead":
                    if (!ok) { Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.1f); return "小烬往后缩了缩，还不肯跟你走。"; }
                    wh.Following = true;
                    t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "false", After = "true", Note = "跟随玩家" });
                    return wh.Hidden ? "小烬在斗篷下拱了拱，愿意跟你走了。" : "小烬抖了抖翅膀，跟在你脚边。";
                case "release":
                    wh.Following = false;
                    t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "true", After = "false", Note = "留在" + w.PlayerPlace });
                    return "你让小烬留在" + w.Places[w.PlayerPlace] + "，它乖乖待在原地。";
                case "rescue":
                {
                    var osric = w.Actors["osric"];
                    if (!ok)
                    {
                        Set(t, "osric.trust", ref osric.Trust, osric.Trust - 0.3f);
                        Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.1f);
                        w.Tension = Math.Min(1, w.Tension + 0.15f);
                        w.Flags["rescue_failed"] = w.ClockText;
                        return "铁锁没能撬开，脚步声已经近了——你只好先退走。";
                    }
                    wh.CapturedBy = null; wh.Following = true; wh.Hidden = false;
                    t.Changes.Add(new WorldChange { Path = "whelp.captured_by", Before = "osric", After = "", Note = "救出" });
                    t.Changes.Add(new WorldChange { Path = "whelp.following", Before = "false", After = "true" });
                    Set(t, "whelp.fear", ref wh.Fear, wh.Fear + 0.15f);
                    Set(t, "osric.trust", ref osric.Trust, osric.Trust - 0.2f);
                    w.Tension = Math.Min(1, w.Tension + 0.1f);
                    w.Flags["whelp_rescued"] = w.ClockText;
                    return "铁笼的门吱呀一声开了，小烬一头扎进你怀里。";
                }
                case "observe":
                    var hints = new List<string>();
                    if (wh.Place == w.PlayerPlace && wh.CapturedBy == null) hints.Add(wh.Hidden ? "旧船屋门缝里飘出一缕细烟" : "雏龙正警惕地盯着你");
                    hints.AddRange(w.Actors.Values.Where(a => a.Place == w.PlayerPlace).Select(a => a.Name + "在附近"));
                    return hints.Count == 0 ? "四周很安静。" : string.Join("；", hints) + "。";
                default:
                    return "时间静静流过。";
            }
        }

        // ---------------- N5 多智能体 ----------------

        public static void AgentPlan(DsWorld w, string npcId, Dictionary<string, object> p, AiTrace t)
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
                        double roll = w.NextRoll();
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
                    if (!w.Actors.TryGetValue(target, out var listener) || listener.Id == npcId) { Fail(t, "unknown_entity", "tell 的对象无效：" + target); return; }
                    if (listener.Place != a.Place) { Reject(t, "交谈对象不在身边，需要先寻访"); return; }
                    var own = new HashSet<string>(w.BeliefsOf(npcId).Select(b => b.Id));
                    var valid = share.Where(own.Contains).ToList();
                    foreach (var bad in share.Except(valid)) t.ValidationIssues.Add("不能转述别人的认知：" + bad);
                    int shared = 0;
                    foreach (var id in valid)
                    {
                        var src = w.Belief(id);
                        string newId = id + "@" + target;
                        if (w.Belief(newId) != null || src.Id.Contains("@")) continue;
                        w.Beliefs.Add(new DsBelief { Id = newId, Owner = target, Topic = src.Topic, Text = "听" + a.Name + "说：" + src.Text, RevealTrust = Math.Max(0.1f, src.RevealTrust * 0.5f), True = src.True, Source = "told:" + npcId });
                        shared++;
                        t.Changes.Add(new WorldChange { Path = target + ".beliefs", Before = "", After = newId, Note = "流言" });
                    }
                    if (shared > 0)
                    {
                        string key = target + ">" + npcId;
                        float prior = w.Relations.TryGetValue(key, out var trust) ? trust : 0.5f;
                        w.Relations[key] = Math.Min(1f, prior + 0.05f);
                        t.Changes.Add(new WorldChange { Path = "relations." + key, Before = prior.ToString("0.00"), After = w.Relations[key].ToString("0.00"), Note = "收到新信息" });
                    }
                    line = a.Name + "悄悄对" + listener.Name + "说了些什么";
                    break;
                default:
                    line = a.Name + "一夜没有出门";
                    break;
            }
            t.Verdict.Reason = reason;
            t.Presented = line + "。";
            w.Chronicle.Add(w.ClockText + "：" + line + "。");
        }

        // ---------------- N3 AI GM ----------------

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
            if (ev == "mother_descends" && !w.FinalDawn) { ev = "omen_roar"; t.Verdict.Kind = VerdictKind.Partial; t.Verdict.Notes.Add("母龙只能在第三个黎明后降临，降级为龙吼征兆"); }
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
            if (Math.Abs(before - w.Tension) > 1e-4) t.Changes.Add(new WorldChange { Path = "world.tension", Before = before.ToString("0.00"), After = w.Tension.ToString("0.00") });
            t.Changes.Add(new WorldChange { Path = "gm.event", Before = "", After = ev });
            t.Presented = text;
            if (ev != "none") { w.Chronicle.Add(w.ClockText + "：" + text); w.PublicNews.Add(w.ClockText + "：" + text); }
        }

        // ---------------- N6 生成式构造 ----------------

        static readonly Dictionary<string, string[]> TagSources = new Dictionary<string, string[]>
        {
            ["light"] = new[] { "crystal", "light" }, ["ember"] = new[] { "ember", "ash" }, ["warmth"] = new[] { "ember" },
            ["lure:dragon"] = new[] { "fish", "ember", "smell" }, ["quiet"] = new[] { "cloth", "quiet" },
            ["calm"] = new[] { "fish", "crystal", "cloth" }, ["iron"] = new[] { "iron" }, ["truth"] = new[] { "pact" },
            ["binding"] = new[] { "rope", "iron" }, ["sound"] = new[] { "crystal", "metal", "coin" },
        };

        // 提示与执行共享材料能力表，避免模型只能猜测这个世界允许怎样织造。
        public static string[] CraftEffectsFor(IEnumerable<string> materialTags)
        {
            var tags = new HashSet<string>(materialTags);
            return TagSources.Where(kv => kv.Value.Any(tags.Contains)).Select(kv => kv.Key)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        public static string CraftEffectMeaning(string tag)
        {
            switch(tag)
            {
                case "light": return "照明；使用后照亮当前位置，降低搜索难度";
                case "quiet": return "吸收、减弱声响，包括脚步声；使用后降低当前位置潜行难度";
                case "calm": return "安抚身边的雏龙，减轻恐惧";
                case "lure:dragon": return "气味诱引身边的雏龙现身；食材织成的诱饵还可以喂食";
                case "ember": return "余烬气味属性，可与已发现的龙类规律关联";
                case "warmth": return "温暖属性，目前没有独立主动使用效果";
                case "iron": return "冷铁属性；对龙有恐惧阻碍";
                case "truth": return "契约属性；不能凭此凭空生成事实或线索";
                case "binding": return "绳结束缚属性，目前没有独立主动使用效果";
                case "sound": return "发声属性，不是吸收或减弱声音；目前没有独立主动使用效果";
                default: return tag;
            }
        }

        public static void Craft(DsWorld w, Dictionary<string, object> p, AiTrace t)
        {
            if (w.PlayerPlace != "workshop") { Reject(t, "织言机在附魔工坊，你得先去那里"); return; }
            string name = p.Str("name"), look = p.Str("look") ?? "";
            var consume = p.StrList("consume");
            var tags = p.StrList("tags");
            double potency = Clamp(p.Num("potency") ?? 0.3, 0, 1);
            if (string.IsNullOrWhiteSpace(name) || consume == null || tags == null) { Fail(t, "schema_invalid", "缺少 name/consume/tags"); return; }
            var mats = new List<DsItem>();
            foreach (var id in consume.Distinct())
            {
                if (!w.Items.TryGetValue(id, out var it) || !(it.Holder == "player" || it.Holder == "place:workshop") || it.Tags.Contains("pact"))
                { t.ValidationIssues.Add("不可用的材料：" + id); continue; }
                mats.Add(it);
            }
            if (mats.Count == 0) { Reject(t, "织言机需要至少一样可用材料"); return; }
            var matTags = new HashSet<string>(mats.SelectMany(m => m.Tags));
            var granted = new List<string>();
            foreach (var tag in tags.Distinct())
            {
                if (!TagSources.TryGetValue(tag, out var sources)) { t.ValidationIssues.Add("不允许的效果标签：" + tag); continue; }
                if (!sources.Any(matTags.Contains)) { t.Verdict.Notes.Add("材料里没有能支撑「" + tag + "」的东西，已剔除"); continue; }
                granted.Add(tag);
            }
            if (granted.Count == 0) { Reject(t, "描述的效果没有一项能由这些材料织成"); return; }
            double cap = Math.Min(0.9, 0.2 + 0.2 * mats.Count);
            if (potency > cap) { t.Verdict.Notes.Add($"强度 {potency:0.00} 超过材料上限 {cap:0.00}，已压低"); potency = cap; }
            int n = w.Items.Values.Count(i => i.Crafted) + 1;
            var item = new DsItem { Id = "crafted_" + n, Name = name.Length > 16 ? name.Substring(0, 16) : name, Holder = "player", Look = look, Crafted = true };
            foreach (var g in granted) item.Tags.Add(g);
            if (granted.Contains("lure:dragon")) { if (matTags.Contains("food")) item.Tags.Add("food"); item.Tags.Add("smell"); }
            if (granted.Contains("ember")) item.Tags.Add("ember");
            item.Tags.Add("potency:" + potency.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var m in mats) Move(t, m, "consumed");
            w.Items[item.Id] = item;
            t.Changes.Add(new WorldChange { Path = "items." + item.Id, Before = "", After = item.Name, Note = string.Join(",", granted) + $" 强度{potency:0.0}" });
            t.Verdict.Kind = granted.Count == tags.Count && mats.Count == consume.Count ? VerdictKind.Accepted : VerdictKind.Partial;
            t.Verdict.Reason = $"消耗 {mats.Count} 样材料，织成 {granted.Count} 项效果";
            t.Presented = "织言机的梭子亮起蓝光——「" + item.Name + "」成形了。" + look;
        }

        // ---------------- 世界规则（无模型参与） ----------------

        /// <summary>玩家进入新地点：跟随的雏龙一起移动；总管在场会惊吓暴露的雏龙；随后卫兵巡逻检查。</summary>
        public static void EnterPlace(DsWorld w, string place, AiTrace t)
        {
            t.Verdict.Kind = VerdictKind.Accepted;
            if (!w.Places.ContainsKey(place)) { Fail(t, "unknown_entity", "未知地点：" + place); return; }
            if (w.FinalDawn) { Reject(t, "第三个黎明已至，一切交由苍穹之母裁决"); return; }
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
                        t.Presented = "小烬一看见奥斯里克就尖叫着挣脱，扑腾着朝码头逃去了。";
                        return;
                    }
                    lines.Add("小烬看见奥斯里克，吓得直往你身后躲");
                }
            }
            string patrol = DsGuards.Patrol(w, t);
            if (patrol != null) lines.Add(patrol);
            if (wh.CapturedBy != null && bring) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "雏龙被卫兵发现"; }
            else t.Verdict.Reason = bring ? "带着雏龙抵达" : "抵达";
            t.Presented = "你来到" + w.Places[place] + (bring ? "，小烬跟在身边" : "") + "。" + (lines.Count > 0 ? string.Join("；", lines) + "。" : "");
        }

        /// <summary>新时段开始：岗位轮换后，对玩家所在地点做一次巡逻检查。</summary>
        public static void PhaseStart(DsWorld w, AiTrace t)
        {
            t.Verdict.Kind = VerdictKind.Accepted;
            t.Verdict.Reason = "换岗";
            string patrol = DsGuards.Patrol(w, t);
            if (patrol != null && w.Whelp.CapturedBy != null) { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.Reason = "雏龙被卫兵发现"; }
            t.Presented = patrol ?? "";
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
            t.ValidationIssues.Add(detail);
            t.Presented = "织言机没能理解这件事。（" + detail + "）";
        }

        static void Reject(AiTrace t, string reason)
        {
            t.Verdict.Kind = VerdictKind.Rejected;
            t.Verdict.Reason = reason;
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
