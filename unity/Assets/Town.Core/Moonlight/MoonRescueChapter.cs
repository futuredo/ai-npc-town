using System;
using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Moonlight
{
    public enum RescuePhase { Preparation, Escort, Settled }

    /// <summary>借光驿站的短局收束。灯群是确定性执行对象，不是额外的语言模型。</summary>
    public sealed class MoonRescueChapter
    {
        readonly bool[] _arrived = new bool[3];
        readonly List<string> _events = new List<string>();
        public RescuePhase Phase { get; private set; }
        public int SavedLights => _arrived.Count(v => v);
        public bool FinnSafe { get; private set; }
        public string Ending { get; private set; }
        public IReadOnlyList<string> Events => _events.ToArray();
        public string Objective => Phase == RescuePhase.Preparation ? "白龙即将翻身。先试亮一条路，再和芬恩确认：把他与三盏漂流灯送到月台。" :
            Phase == RescuePhase.Escort ? "保持约定的光路。灯灭就停；抵达月台的每一盏灯，才会计入救援。" : Ending;

        public bool Start(MoonWorkshop workshop)
        {
            if (workshop == null) throw new ArgumentNullException(nameof(workshop));
            if (Phase != RescuePhase.Preparation || workshop.State != MoonTaskState.Travelling) return false;
            Phase = RescuePhase.Escort;
            _events.Add("芬恩：我跟着试过的光走。也请照顾那些快要褪色的小灯。");
            return true;
        }

        public bool ObserveArrival(int light, bool routeTraversed, double x, double z)
        {
            if (light < 0 || light >= _arrived.Length) throw new ArgumentOutOfRangeException(nameof(light));
            if (Phase != RescuePhase.Escort || _arrived[light] || !routeTraversed ||
                double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(z) || double.IsInfinity(z) ||
                (x - 4) * (x - 4) + z * z > 0.55 * 0.55) return false;
            _arrived[light] = true;
            _events.Add("漂流灯 " + (light + 1) + " 实际到达月台。");
            TrySettle();
            return true;
        }

        public void ObserveFinn(MoonWorkshop workshop)
        {
            if (workshop == null) throw new ArgumentNullException(nameof(workshop));
            if (Phase != RescuePhase.Escort || FinnSafe || workshop.State != MoonTaskState.Completed) return;
            FinnSafe = true;
            _events.Add("芬恩的到达回执已确认；灯群仍需自己走完。");
            TrySettle();
        }

        void TrySettle()
        {
            if (FinnSafe && SavedLights == 3) Settle();
        }

        public bool Settle()
        {
            if (Phase != RescuePhase.Escort) return false;
            Phase = RescuePhase.Settled;
            Ending = FinnSafe && SavedLights == 3 ? "借来的月亮：芬恩与三盏小灯都过了断鳞。没有人被写成灯芯；这条路是你们试出来的。" :
                FinnSafe || SavedLights > 0 ? "尚未写完的航路：救下了 " + SavedLights + " 盏灯，" + (FinnSafe ? "芬恩也在月台。" : "芬恩还留在另一侧。") + "未抵达者没有被旁白算作获救。可以重开，尝试另一种供光办法。" :
                "这一页仍是铅笔稿：没有有效到达回执。大家留在临时避风处；撤回不是死亡，也不是一次成功的救援。";
            _events.Add(Ending);
            return true;
        }
    }
}
