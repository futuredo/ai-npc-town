using Town.Core.World;

namespace Town.Core.Tasks
{
    public enum RepairTaskState { WaitingForHammer, Running, Completed, Expired }

    /// <summary>首个任务的生命周期；世界规则负责判定启动、推进与截止。</summary>
    public sealed class RepairTask
    {
        public const int DurationMinutes = 60;
        public RepairTaskState State { get; internal set; } = RepairTaskState.WaitingForHammer;
        public GameTime? StartedAt { get; internal set; }
    }
}
