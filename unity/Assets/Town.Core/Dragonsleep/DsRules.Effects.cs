using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Dragonsleep
{
    public static partial class DsRules
    {
        static string Effects(DsWorld w, string verb, DsItem item, bool whelp, DsActor actor, bool ok, AiTrace t)
        {
            var wh = w.Whelp;
            switch (verb)
            {
                case "buy":
                {
                    var seller = w.Actors[item.Holder];
                    var priceTag = item.Tags.FirstOrDefault(x => x.StartsWith("price:"));
                    if (priceTag == null)
                    { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.ReasonCode = new DsReason("item_not_for_sale", item.Id);
                        t.Verdict.Reason = "不出售"; return seller.Name + "摇头：“这个不卖。”"; }
                    int price = int.Parse(priceTag.Substring(6));
                    if (seller.Trust < 0.15f) price *= 2;
                    if (w.Coins < price)
                    { t.Verdict.Kind = VerdictKind.Rejected; t.Verdict.ReasonCode = new DsReason("coins_insufficient");
                        t.Verdict.Reason = "银币不足"; return "你的银币不够（需要 " + price + " 枚）。"; }
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
                    // 吃饱后安心一些：恐惧不再只升不降，连续失败后仍有补救路径。
                    if (verb == "feed") Set(t, "whelp.fear", ref wh.Fear, wh.Fear - 0.15f);
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
                    if (item.Holder.StartsWith("place:"))
                    { if (ok) Move(t, item, "player"); return ok ? "你拿走了「" + item.Name + "」。" : "你没能拿到「" + item.Name + "」。"; }
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
                    if (potencyTag != null) float.TryParse(potencyTag.Substring(8), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out potency);
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
                    if (ok)
                    {
                        wh.Hidden = true; w.Flags["whelp_hidden_by_player"] = w.ClockText;
                        t.Changes.Add(new WorldChange { Path = "whelp.hidden", Before = "false", After = "true", Note = "玩家藏好" });
                        Set(t, "whelp.fear", ref wh.Fear, wh.Fear - 0.1f);
                    }
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
                    w.Flags["whelp_left_at"] = w.PlayerPlace;
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
                case "confide":
                    return Confide(w, actor, t);
                case "observe":
                    var hints = new List<string>();
                    if (wh.Place == w.PlayerPlace && wh.CapturedBy == null) hints.Add(wh.Hidden ? "旧船屋门缝里飘出一缕细烟" : "雏龙正警惕地盯着你");
                    hints.AddRange(w.Actors.Values.Where(a => a.Place == w.PlayerPlace).Select(a => a.Name + "在附近"));
                    return hints.Count == 0 ? "四周很安静。" : string.Join("；", hints) + "。";
                default:
                    return "时间静静流过。";
            }
        }
    }
}
