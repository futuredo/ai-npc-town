using System.Collections.Generic;
using System.Linq;

namespace Town.Core.Memory
{
    public sealed class DialogueMemoryEntry
    {
        public string NpcId { get; }
        public string PlayerText { get; }
        public string Reply { get; }
        public DialogueMemoryEntry(string npcId, string playerText, string reply)
        { NpcId = npcId; PlayerText = playerText; Reply = reply; }
    }

    /// <summary>按角色隔离的近期对话记录。仅供界面查看；尚未接入 B/C 模型记忆实验。</summary>
    public sealed class DialogueMemory
    {
        private readonly Dictionary<string, Queue<DialogueMemoryEntry>> _byNpc =
            new Dictionary<string, Queue<DialogueMemoryEntry>>();

        public void Record(string npcId, string playerText, string reply)
        {
            if (!_byNpc.TryGetValue(npcId, out var entries))
                _byNpc[npcId] = entries = new Queue<DialogueMemoryEntry>();
            entries.Enqueue(new DialogueMemoryEntry(npcId, playerText, reply));
            while (entries.Count > 20) entries.Dequeue();
        }

        public IReadOnlyList<DialogueMemoryEntry> For(string npcId) =>
            _byNpc.TryGetValue(npcId, out var entries) ? entries.ToArray() : new DialogueMemoryEntry[0];
    }
}
