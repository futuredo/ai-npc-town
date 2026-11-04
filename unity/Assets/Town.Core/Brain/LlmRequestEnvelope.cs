using System;
using Town.Core.World;

namespace Town.Core.Brain
{
    /// <summary>
    /// 每次模型请求的信封。回复回来时用它判断是否过期：
    /// 新局/读档（SessionGen 变化）一律丢弃；同一 RequestId 只生效一次。
    /// </summary>
    public sealed class LlmRequestEnvelope
    {
        public string RequestId;
        public string NpcId;
        public int SessionGen;
        public int StateVersion;
        public DateTime SentAtUtc;
    }

    public enum StaleReason
    {
        Fresh,
        SessionChanged,     // 新局或读档后旧回复（0 容忍项：旧局污染新局）
        Superseded,         // 同一 NPC 已有更新的请求
        AlreadyApplied,     // 重复回复（0 容忍项：重复发放物品）
    }

    /// <summary>请求过期检查。每个 NPC 只认最后一次发出的请求。</summary>
    public sealed class StalenessGuard
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _latestByNpc =
            new System.Collections.Generic.Dictionary<string, string>();
        private readonly System.Collections.Generic.HashSet<string> _applied =
            new System.Collections.Generic.HashSet<string>();
        private int _counter;

        public LlmRequestEnvelope Issue(string npcId, WorldState world)
        {
            var env = new LlmRequestEnvelope
            {
                RequestId = $"R{world.SessionGen}-{++_counter}",
                NpcId = npcId,
                SessionGen = world.SessionGen,
                StateVersion = world.Version,
                SentAtUtc = DateTime.UtcNow,
            };
            _latestByNpc[npcId] = env.RequestId;
            return env;
        }

        public StaleReason Check(LlmRequestEnvelope env, WorldState world)
        {
            if (env.SessionGen != world.SessionGen) return StaleReason.SessionChanged;
            if (_applied.Contains(env.RequestId)) return StaleReason.AlreadyApplied;
            if (!_latestByNpc.TryGetValue(env.NpcId, out var latest) || latest != env.RequestId) return StaleReason.Superseded;
            return StaleReason.Fresh;
        }

        /// <summary>校验通过且执行后调用，之后同一 RequestId 再来会被判重复。</summary>
        public void MarkApplied(LlmRequestEnvelope env) => _applied.Add(env.RequestId);
    }
}
