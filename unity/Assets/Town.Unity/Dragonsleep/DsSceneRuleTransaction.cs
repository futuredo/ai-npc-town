using System;
using System.Threading;
using PieGame.AI.Kernel;
using PieGame.AI.Unity;
using Town.Core.Dragonsleep;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 场景移动的唯一写入者：SDK 比较/写入锁内调用宿主候选覆盖，不保留第二份权威世界。
    /// 仅由持有 Session 锁的同步 EnterPlace 使用；其它会话事务尚未迁移。
    /// </summary>
    public sealed class DsSceneRuleTransaction : IDsSceneRuleTransaction
    {
        readonly DragonsleepSession _session;
        readonly UnityKernelBridge _bridge;
        readonly int _thread;
        DsSceneRuleWrite _write;
        string _after;
        bool _inside;
        bool _resetting;
        public int Commits { get; private set; }
        public Receipt LastReceipt { get; private set; }

        public DsSceneRuleTransaction(DragonsleepSession session, UnityKernelBridge bridge)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _bridge = bridge != null ? bridge : throw new ArgumentNullException(nameof(bridge));
            _bridge.Initialize(new StatePort(this), DsExecutor.RulesVersion);
            _thread = Thread.CurrentThread.ManagedThreadId;
        }

        public string Commit(string requestId, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write)
        {
            CheckThread();
            if (_inside || _resetting) throw new InvalidOperationException("scene_transaction_reentry");
            if (before == null || after == null || write == null) throw new ArgumentNullException(nameof(write));
            if (before.Spent != after.Spent || before.PhaseBudget != after.PhaseBudget)
                throw new InvalidOperationException("scene_transaction_time_changed");
            _inside = true;
            try
            {
                LastReceipt = null;
                // StateStamp 只在本次同步调用中存活。端口读取会话当前快照，不缓存影子状态。
                var stamp = _bridge.CaptureState();
                if (stamp.State != before.Json || stamp.Digest != before.Hash) return "stale_state";
                _after = after.Json; _write = write;
                LastReceipt = _bridge.Transact(requestId, stamp, after.Json);
                if (LastReceipt.Code == "committed") Commits++;
                return LastReceipt.Code;
            }
            finally { _write = null; _after = null; _inside = false; }
        }

        /// <summary>仅在 Game 空闲并已重建 Session 后调用；失效旧 SDK 请求 ID，不再次写入游戏。</summary>
        public void ResetSession()
        {
            CheckThread();
            if (_inside || _resetting || _session.Busy) throw new InvalidOperationException("scene_transaction_busy");
            _resetting = true;
            try
            {
                _bridge.Reset(_session.CaptureState().Json);
                LastReceipt = null; Commits = 0;
            }
            finally { _resetting = false; }
        }

        void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("scene_transaction_wrong_thread");
        }

        sealed class StatePort : IStatePort
        {
            readonly DsSceneRuleTransaction _owner;
            public StatePort(DsSceneRuleTransaction owner) { _owner = owner; }
            public string Read()
            {
                if (!_owner._inside && !_owner._resetting) throw new InvalidOperationException("scene_transaction_not_active");
                return _owner._session.CaptureState().Json;
            }
            public void Write(string state)
            {
                if (_owner._resetting)
                {
                    // Reset 不写第二次世界，只允许 SDK 对现有完全相同的初态更新自己的代际。
                    if (state != Read()) throw new InvalidOperationException("scene_transaction_not_active");
                    return;
                }
                if (!_owner._inside || _owner._write == null || state != _owner._after)
                    throw new InvalidOperationException("scene_transaction_candidate_changed");
                _owner._write.Apply();
            }
        }
    }
}
