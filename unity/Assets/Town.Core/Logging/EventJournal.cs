using System.Collections.Generic;
using Town.Core.World;

namespace Town.Core.Logging
{
    public sealed class JournalEntry
    {
        public int Sequence { get; }
        public int SessionGen { get; }
        public int StateVersion { get; }
        public string GameTime { get; }
        public string Kind { get; }
        public string Detail { get; }

        internal JournalEntry(int sequence, WorldState world, string kind, string detail)
        {
            Sequence = sequence;
            SessionGen = world.SessionGen;
            StateVersion = world.Version;
            GameTime = world.Now.ToString();
            Kind = kind;
            Detail = detail;
        }
    }

    /// <summary>每局有界审计日志；不记录密钥、完整提示或原始 API 响应。不是确定性回放文件。</summary>
    public sealed class EventJournal
    {
        private readonly Queue<JournalEntry> _entries = new Queue<JournalEntry>();
        private int _sequence;
        public IReadOnlyList<JournalEntry> Entries => _entries.ToArray();

        public void Append(WorldState world, string kind, string detail)
        {
            _entries.Enqueue(new JournalEntry(++_sequence, world, kind, detail));
            while (_entries.Count > 256) _entries.Dequeue();
        }
    }
}
