using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.Knowledge;
using Town.Core.Rules;
using Town.Core.Social;
using Town.Core.World;

namespace Town.Core.Outcome
{
    public sealed class Ending
    {
        public string Id;
        public string Title;
        public string Description;
    }

    /// <summary>钟响之后的结算：第七章标题、正文条目、流言溯源。全部由规则状态推出，不由模型生成。</summary>
    public static class FestivalOutcome
    {
        /// <summary>由钟响规则结果选择作者判词，不由模型生成。R1 空白结尾随识破机制接入，不预报已实现。</summary>
        public static string LastPage(HammerEventRules r)
        {
            if (!r.Bell.Rung) throw new InvalidOperationException("钟响前不能读取最后一页。");
            const string opening = "读到这一页的人：我写不完这个镇子了。我写到第七章，发现我笔下的人开始自己说话——他们相信什么，下一页就会写什么。我不敢再写下去，也不忍心合上它。所以我把这一页留给你：替我看一看，一个从页外来的人，会让他们相信什么？";
            bool anyFalse = r.Bell.Manifested.Any(m => Propositions.All[m.PropositionId].TruthInWorld == false);
            string ending = anyFalse
                ? "如果谣言被写成了正文——请把这一页撕掉。有些书，不该自己写完。"
                : KindLie(r)
                    ? "如果你用红墨水替他们写了一件好事——谢谢你。可下一个翻开这本书的人，未必好心。"
                    : r.Session.Ledger.PlayerLies.Any()
                        ? "你曾动用红墨水，只是它没有替这一章写成好事。下一次落笔前，请想一想谁会相信。"
                        : "如果他们写下的仍然只有真话——那这本书已经不需要作者了。合上它吧，它会自己写下去。";
            return opening + "\n\n" + ending;
        }

        public static IReadOnlyList<Ending> Endings(HammerEventRules r)
        {
            var w = r.World;
            var list = new List<Ending>();
            bool arch = w.GetFlag(Ids.Flag.BenchProgress) >= 100;
            bool thief = w.GetFlag(Ids.Flag.ThiefMark) == 1;
            bool evil = w.GetFlag(Ids.Flag.WispEvil) == 1;
            bool guardian = w.GetFlag(Ids.Flag.WispGuardian) == 1;
            bool formal = r.Apprentice.State == ApprenticeState.Formal;
            bool anyFalse = r.Bell.Manifested.Any(m => Propositions.All[m.PropositionId].TruthInWorld == false);

            if (arch && guardian && formal && !anyFalse)
                list.Add(new Ending { Id = "brightest", Title = "🏮 最亮的灯节", Description = "拱门亮了，灯灵守护着广场，芬恩成了布伦的学徒，没有一句谎言变成现实。" });
            if (r.Help.FinnReturnedHimself && formal)
                list.Add(new Ending { Id = "loyal_apprentice", Title = "🔨 被写完整的学徒", Description = "芬恩亲手还回符文锤，布伦收他为徒。介绍里空着的半句话，终于有了下文。" });
            if (KindLie(r))
                list.Add(new Ending { Id = "kind_lie", Title = "🖋️ 红墨水写成的好事", Description = "你说了一句自己并不确知的话，它在墨钟声中被写成了好事。" });
            if (thief)
                list.Add(new Ending { Id = "thief_mark", Title = "🩸 窃贼印记", Description = "「芬恩偷了符文锤」被两个源头传开，钟响时芬恩手上烙下了印记。" });
            if (evil)
                list.Add(new Ending { Id = "evil_well", Title = "👻 废稿井的恶灵", Description = "镇上的人相信井里有恶灵，于是它被写成恶灵，灯节的灯火全部熄灭。" });
            if (r.Bell.Manifested.Count == 0 && !arch)
                list.Add(new Ending { Id = "quiet_night", Title = "📄 空白的一页", Description = "墨钟响了三声，没有新的正文落下，拱门仍未上色。" });
            if (list.Count == 0)
                list.Add(new Ending { Id = "ordinary", Title = "🌙 草草写完的一章", Description = "有些事写好了，有些没赶上。空白页上留下了这一章。" });
            return list;
        }

        /// <summary>你编造（说成亲眼所见但没看到、或添油加醋）的说法里，有一条好事显化了，且有人的信念源头就是你。</summary>
        private static bool KindLie(HammerEventRules r)
        {
            var lies = new HashSet<string>(r.Session.Ledger.PlayerLies.Where(t => t.Adopted).Select(t => t.PropositionId));
            return r.Bell.Manifested.Any(m => Propositions.All[m.PropositionId].TruthInWorld == true &&
                lies.Contains(m.PropositionId) && m.Origins.Contains(Ids.Player));
        }

        /// <summary>流言溯源：每条可显化说法从源头到每个听到的人。</summary>
        public static IReadOnlyList<string> Trace(HammerEventRules r)
        {
            var lines = new List<string>();
            var readings = r.Bell.Rung ? r.Bell.FinalReadings : r.Bell.Read();
            foreach (var reading in readings)
            {
                var p = Propositions.All[reading.PropositionId];
                bool manifested = r.Bell.Manifested.Any(m => m.PropositionId == p.Id);
                lines.Add($"【{p.Text}】{reading.Believers.Count} 人相信 · {reading.Origins.Count} 个源头{(manifested ? " → 显化：" + p.ManifestEffect : "")}");
                var hops = r.Session.Ledger.About(p.Id).Where(t => t.Stance && t.Receiver != Ids.Player && !t.AlreadyKnew).ToList();
                if (hops.Count == 0) lines.Add("    （没有人传过这句话）");
                // 同一人对同一人、同样说法和结果的重复闲聊合并成一行
                foreach (var g in hops.GroupBy(t => (t.Teller, t.Receiver, t.Mode, t.Adopted, t.OriginId, t.IsLie)))
                {
                    var t = g.First();
                    string times = g.Count() > 1 ? $"（共 {g.Count()} 次）" : "";
                    lines.Add($"    {t.At.Hour:00}:{t.At.Minute:00} {Ids.Name(t.Teller)} → {Ids.Name(t.Receiver)}  {ModeLabel(t.Mode)}{(t.IsLie ? "·谎言" : "")}  {(t.Adopted ? "✓ 信了" : "✗ 没信")}  源头:{Ids.Name(t.OriginId)}{times}");
                }
            }
            return lines;
        }

        public static IReadOnlyList<string> Headlines(HammerEventRules r)
        {
            var w = r.World;
            var lines = new List<string>
            {
                w.GetFlag(Ids.Flag.BenchProgress) >= 100 ? "灯节拱门符文重燃，布伦：「锤子回来得正是时候」" : "拱门符文未能修复，灯节在半明半暗中开幕",
            };
            if (w.GetFlag(Ids.Flag.WispGuardian) == 1) lines.Add("废稿井升起守护灯灵，全镇灯笼一齐亮起");
            else if (w.GetFlag(Ids.Flag.WispEvil) == 1) lines.Add("废稿井恶灵现身！灯节灯火尽数熄灭");
            else lines.Add("废稿井的幽光在钟声中悄然离去");
            if (w.GetFlag(Ids.Flag.ThiefMark) == 1) lines.Add("跑腿少年芬恩手现窃贼印记，镇民议论纷纷");
            else if (r.Apprentice.State == ApprenticeState.Formal) lines.Add("布伦收下了芬恩。少年介绍中空着的半句话，终于被写上颜色");
            else if (r.Apprentice.State == ApprenticeState.Trial) lines.Add("布伦答应让芬恩灯节后试做学徒");
            else lines.Add("跑腿少年芬恩今天照常送货");
            return lines;
        }

        public static string ModeLabel(TellMode mode)
        {
            switch (mode)
            {
                case TellMode.Answer: return "回答";
                case TellMode.Faithful: return "如实转述";
                case TellMode.ClaimSeen: return "说成亲眼所见";
                case TellMode.Exaggerate: return "添油加醋";
                case TellMode.Gossip: return "闲聊";
                case TellMode.GossipExaggerated: return "闲聊·添油加醋";
                case TellMode.Overheard: return "偷听";
                case TellMode.Rebuttal: return "反驳";
                case TellMode.Evidence: return "证据";
                case TellMode.Firsthand: return "亲身经历";
                default: return mode.ToString();
            }
        }
    }
}
