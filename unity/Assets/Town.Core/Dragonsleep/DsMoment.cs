using System.Text.RegularExpressions;

namespace Town.Core.Dragonsleep
{
    /// <summary>领域时刻；中文时刻只在感知/计划 JSON 边界解析与格式化。保留原 1–4 天校验范围。</summary>
    public readonly struct DsMoment
    {
        static readonly Regex Pattern = new Regex(@"^第([1-4])天·(清晨|正午|黄昏|深夜)$");
        public int Day { get; }
        public DayPhase Phase { get; }
        public int Index => (Day - 1) * 4 + (int)Phase;

        public DsMoment(int day, DayPhase phase) { Day = day; Phase = phase; }

        public override string ToString() => $"第{Day}天·{PhaseName(Phase)}";

        public static string PhaseName(DayPhase phase) =>
            phase == DayPhase.Dawn ? "清晨" : phase == DayPhase.Noon ? "正午" : phase == DayPhase.Dusk ? "黄昏" : "深夜";

        public static bool TryParse(string text, out DsMoment moment)
        {
            moment = default;
            var match = Pattern.Match(text ?? "");
            if (!match.Success) return false;
            string phase = match.Groups[2].Value;
            moment = new DsMoment(int.Parse(match.Groups[1].Value),
                phase == "清晨" ? DayPhase.Dawn : phase == "正午" ? DayPhase.Noon : phase == "黄昏" ? DayPhase.Dusk : DayPhase.Night);
            return true;
        }
    }
}
