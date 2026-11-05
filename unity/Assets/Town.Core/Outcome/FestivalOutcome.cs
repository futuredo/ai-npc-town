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

    /// <summary>钟响之后的结算：命名结局、流言溯源、小报头条。全部由规则状态推出，不由模型生成。</summary>
    public static class FestivalOutcome
    {
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
                list.Add(new Ending { Id = "loyal_apprentice", Title = "🔨 守信的学徒", Description = "芬恩亲手还回了符文锤，布伦当场收他为徒。" });
            if (KindLie(r))
                list.Add(new Ending { Id = "kind_lie", Title = "✨ 一个善意的谎言", Description = "你说了一句自己并不确知的话，它在钟声里变成了好事。" });
            if (thief)
                list.Add(new Ending { Id = "thief_mark", Title = "🩸 窃贼印记", Description = "「芬恩偷了符文锤」被两个源头传开，钟响时芬恩手上烙下了印记。" });
            if (evil)
                list.Add(new Ending { Id = "evil_well", Title = "👻 井中恶灵", Description = "镇上的人相信井里有恶灵，于是它真的成了恶灵，灯节的灯火全部熄灭。" });
            if (r.Bell.Manifested.Count == 0 && !arch)
                list.Add(new Ending { Id = "quiet_night", Title = "🕯️ 平静的一夜", Description = "誓钟响了三声，什么都没有改变，拱门也没有亮。" });
            if (list.Count == 0)
                list.Add(new Ending { Id = "ordinary", Title = "🌙 寻常的灯节", Description = "有些事变好了，有些事没赶上。明年的誓钟还会再响。" });
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
            if (w.GetFlag(Ids.Flag.WispGuardian) == 1) lines.Add("旧井升起守护灯灵，全镇灯笼一齐亮起");
            else if (w.GetFlag(Ids.Flag.WispEvil) == 1) lines.Add("旧井恶灵现身！灯节灯火尽数熄灭");
            else lines.Add("旧井的幽光在钟声中悄然离去");
            if (w.GetFlag(Ids.Flag.ThiefMark) == 1) lines.Add("跑腿少年芬恩手现窃贼印记，镇民议论纷纷");
            else if (r.Apprentice.State == ApprenticeState.Formal) lines.Add("符文铁匠布伦收徒，半精灵少年芬恩拜师");
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
