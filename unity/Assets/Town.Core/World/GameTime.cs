using System;

namespace Town.Core.World
{
    /// <summary>游戏内时间：第几天 + 当天第几分钟。不可变值类型。</summary>
    public readonly struct GameTime : IComparable<GameTime>, IEquatable<GameTime>
    {
        public const int MinutesPerDay = 24 * 60;

        public readonly int Day;
        public readonly int MinuteOfDay;

        public GameTime(int day, int minuteOfDay)
        {
            if (minuteOfDay < 0 || minuteOfDay >= MinutesPerDay)
                throw new ArgumentOutOfRangeException(nameof(minuteOfDay));
            Day = day;
            MinuteOfDay = minuteOfDay;
        }

        public static GameTime At(int day, int hour, int minute = 0) => new GameTime(day, hour * 60 + minute);

        public int TotalMinutes => Day * MinutesPerDay + MinuteOfDay;
        public int Hour => MinuteOfDay / 60;
        public int Minute => MinuteOfDay % 60;

        public GameTime AddMinutes(int minutes)
        {
            int total = TotalMinutes + minutes;
            if (total < 0) throw new ArgumentOutOfRangeException(nameof(minutes));
            return new GameTime(total / MinutesPerDay, total % MinutesPerDay);
        }

        public int CompareTo(GameTime other) => TotalMinutes.CompareTo(other.TotalMinutes);
        public bool Equals(GameTime other) => TotalMinutes == other.TotalMinutes;
        public override bool Equals(object obj) => obj is GameTime t && Equals(t);
        public override int GetHashCode() => TotalMinutes;
        public static bool operator <(GameTime a, GameTime b) => a.TotalMinutes < b.TotalMinutes;
        public static bool operator >(GameTime a, GameTime b) => a.TotalMinutes > b.TotalMinutes;
        public static bool operator <=(GameTime a, GameTime b) => a.TotalMinutes <= b.TotalMinutes;
        public static bool operator >=(GameTime a, GameTime b) => a.TotalMinutes >= b.TotalMinutes;
        public override string ToString() => $"第{Day}天 {Hour:00}:{Minute:00}";
    }
}
