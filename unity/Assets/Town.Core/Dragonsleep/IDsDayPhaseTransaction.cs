using System;

namespace Town.Core.Dragonsleep
{
    /// <summary>只承载清晨→正午、正午→黄昏；世界与已用时间必须在同一 compare/write 内写入。</summary>
    public interface IDsDayPhaseTransaction
    {
        string Commit(string requestId, DsWorldSnapshot before, DsWorldSnapshot after, DsDayPhaseWrite write);
    }

    public sealed class DsDayPhaseWrite
    {
        readonly DragonsleepSession _session;
        readonly DsWorld _world;
        readonly DsWorld _prepared;
        readonly int _spent;
        bool _active = true;
        public bool Applied { get; private set; }

        internal DsDayPhaseWrite(DragonsleepSession session, DsWorld prepared, int spent)
        { _session = session; _world = session.World; _prepared = prepared; _spent = spent; }

        public void Apply()
        {
            if (!_active || Applied || !ReferenceEquals(_world, _session.World))
                throw new InvalidOperationException("day_phase_write_not_active");
            _session.ApplyDayPhaseCandidate(_prepared, _spent);
            Applied = true;
        }

        internal void Close() => _active = false;
    }
}
