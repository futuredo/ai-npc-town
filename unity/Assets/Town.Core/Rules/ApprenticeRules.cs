using System;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;
using N = Town.Core.World.Ids.Npc;

namespace Town.Core.Rules
{
    public enum ApprenticeState { None, Refused, Trial, Formal }

    /// <summary>
    /// C 线：芬恩想学、布伦想收。只要有一方知道了对方的心意，两人碰面时就会说开；
    /// 布伦答不答应，取决于他此刻怎么看芬恩（玩法设计 v2 §5 C 线）。
    /// </summary>
    public sealed class ApprenticeRules
    {
        private readonly HammerEventSetup.Session _s;
        private readonly Func<bool> _finnReturnedHimself;
        private readonly Action<string, string> _note;
        private string _lastVerdict;

        public ApprenticeState State { get; private set; } = ApprenticeState.None;
        public string LastReason { get; private set; }

        public ApprenticeRules(HammerEventSetup.Session session, Func<bool> finnReturnedHimself, Action<string, string> note)
        {
            _s = session;
            _finnReturnedHimself = finnReturnedHimself;
            _note = note ?? ((_, __) => { });
        }

        /// <summary>按布伦当前的认知判断，不改变状态（给界面预告用）。</summary>
        public ApprenticeState Evaluate(out string reason)
        {
            var brunn = _s.Knowledge.ViewFor(N.Amu);
            var stole = brunn.Current(Propositions.Stole);
            if (stole != null && stole.Stance && stole.Confidence >= RumorRules.ShareThreshold)
            {
                reason = "布伦沉着脸：「我不收一个偷东西的人。」";
                return ApprenticeState.Refused;
            }
            if (_finnReturnedHimself() || _s.World.GetFlag(Ids.Flag.FinnBadge) == 1 || (brunn.Current(Propositions.Reliable)?.Stance ?? false))
            {
                reason = "布伦拍了拍芬恩的肩膀：「借东西守信的人，靠得住。明天一早来铺子。」";
                return ApprenticeState.Formal;
            }
            if ((brunn.Current(Propositions.Borrowed)?.Stance ?? false) && (brunn.Current(Propositions.Returned)?.Stance ?? false))
            {
                reason = "布伦想了想：「锤子的事算他有交代。灯节后来试几天吧。」";
                return ApprenticeState.Trial;
            }
            reason = "布伦摇头：「锤子到现在还没个说法，这事以后再说。」";
            return ApprenticeState.Refused;
        }

        public void Tick()
        {
            if (State == ApprenticeState.Formal) return;
            var w = _s.World;
            var brunn = w.Npcs[N.Amu];
            var finn = w.Npcs[N.Xiaochuan];
            if (brunn.LocationId == null || brunn.LocationId != finn.LocationId) return;

            bool brunnKnows = _s.Knowledge.ViewFor(N.Amu).Current(Propositions.WantsToLearn)?.Stance ?? false;
            bool finnKnows = _s.Knowledge.ViewFor(N.Xiaochuan).Current(Propositions.WantsApprentice)?.Stance ?? false;
            if (!brunnKnows && !finnKnows) return;

            var verdict = Evaluate(out var reason);
            if (_lastVerdict == verdict + reason) return;   // 同样的结论不重复上演
            _lastVerdict = verdict + reason;
            LastReason = reason;
            if (verdict == ApprenticeState.Refused && State == ApprenticeState.Trial) return; // 已经答应试用就不反悔
            State = verdict;
            w.Commit("apprentice:" + verdict.ToString().ToLowerInvariant(), _ => { });
            _note("apprentice", reason);
        }
    }
}
