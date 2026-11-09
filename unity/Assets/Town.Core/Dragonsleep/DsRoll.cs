namespace Town.Core.Dragonsleep
{
    /// <summary>只依赖种子与调用序号的掷骰源；不在提供方中保存可变随机状态。</summary>
    public interface IDsRoll
    {
        double Next(int seed, int counter);
    }

    public sealed class DsSeededRoll : IDsRoll
    {
        public static readonly DsSeededRoll Instance = new DsSeededRoll();

        public double Next(int seed, int counter)
        {
            unchecked
            {
                uint x = (uint)(seed * 2654435761u) ^ (uint)(counter * 40503u);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                return (x % 10000u) / 10000.0;
            }
        }
    }

    /// <summary>计划可行性试运行或测试使用；仍由规则逐次增长候选世界的计数。</summary>
    public sealed class DsFixedRoll : IDsRoll
    {
        public double Value { get; }
        public DsFixedRoll(double value) { Value = value; }
        public double Next(int seed, int counter) => Value;
    }

    public enum DsExecutionMode { Live, PlanPreview }

    internal static class DsRoll
    {
        internal static double Next(DsWorld world, IDsRoll source = null)
        {
            unchecked { world.RollCounter++; }
            return (source ?? DsSeededRoll.Instance).Next(world.Seed, world.RollCounter);
        }
    }
}
