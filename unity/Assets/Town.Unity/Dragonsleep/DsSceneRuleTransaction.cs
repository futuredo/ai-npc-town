using System;
using System.Threading;
using PieGame.AI.Kernel;
using PieGame.AI.Unity;
using Town.Core.Dragonsleep;
using Town.PieGame;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 场景移动的唯一写入者：SDK 比较/写入锁内调用宿主候选覆盖，不保留第二份权威世界。
    /// 仅由持有 Session 锁的同步 EnterPlace 使用；其它会话事务尚未迁移。
    /// </summary>
    public sealed class DsSceneRuleTransaction : IDsSceneRuleTransaction, IDsDayPhaseTransaction
    {
        readonly DragonsleepSession _session;
        readonly UnityKernelBridge _bridge;
        readonly int _thread;
        readonly DsKernelStatePort _port;
        public DsActionKernelBridge ActionKernel { get; }
        public KernelActionTransaction ActionTransaction { get; }
        bool _inside;
        bool _resetting;
        public int Commits { get; private set; }
        public int DayCommits { get; private set; }
        public Receipt LastReceipt { get; private set; }

        public DsSceneRuleTransaction(DragonsleepSession session, UnityKernelBridge bridge)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _bridge = bridge != null ? bridge : throw new ArgumentNullException(nameof(bridge));
            _port = new DsKernelStatePort(session.CaptureState);
            _bridge.Initialize(_port, DsExecutor.RulesVersion);
            ActionKernel = new DsActionKernelBridge(_bridge, _port);
            ActionTransaction = new KernelActionTransaction(ActionKernel);
            _session.StateReplaced += ResetSession;
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
                StateStamp stamp;
                using (_port.OpenRead()) stamp = _bridge.CaptureState();
                if (stamp.State != before.Json || stamp.Digest != before.Hash) return "stale_state";
                using (_port.OpenScene(write, after.Json)) LastReceipt = _bridge.Transact(requestId, stamp, after.Json);
                if (LastReceipt.Code == "committed") Commits++;
                return LastReceipt.Code;
            }
            finally { _inside = false; }
        }

        public string Commit(string requestId, DsWorldSnapshot before, DsWorldSnapshot after, DsDayPhaseWrite write)
        {
            CheckThread();
            if (_inside || _resetting) throw new InvalidOperationException("scene_transaction_reentry");
            if (before == null || after == null || write == null) throw new ArgumentNullException(nameof(write));
            var a = before.Restore(); var b = after.Restore();
            if ((a.Phase != DayPhase.Dawn && a.Phase != DayPhase.Noon) || b.Day != a.Day ||
                b.Phase != a.Phase + 1 || after.Spent != 0 || before.PhaseBudget != after.PhaseBudget || a.FinalDawn || b.FinalDawn)
                throw new InvalidOperationException("day_phase_transaction_scope");
            _inside = true;
            try
            {
                LastReceipt = null;
                StateStamp stamp;
                using (_port.OpenRead()) stamp = _bridge.CaptureState();
                if (stamp.State != before.Json || stamp.Digest != before.Hash) return "stale_state";
                using (_port.OpenDay(write, after.Json)) LastReceipt = _bridge.Transact(requestId, stamp, after.Json);
                if (LastReceipt.Code == "committed") { Commits++; DayCommits++; }
                return LastReceipt.Code;
            }
            finally { _inside = false; }
        }

        /// <summary>仅在 Game 空闲并已重建 Session 后调用；失效旧 SDK 请求 ID，不再次写入游戏。</summary>
        public void ResetSession()
        {
            CheckThread();
            if (_inside || _resetting || (_session.Busy && !_session.KernelActionInFlight))
                throw new InvalidOperationException("scene_transaction_busy");
            _resetting = true;
            try
            {
                using (_port.OpenReset()) _bridge.Reset(_session.CaptureState().Json);
                LastReceipt = null; Commits = 0; DayCommits = 0;
                ActionKernel.ResetCounters();
            }
            finally { _resetting = false; }
        }

        void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("scene_transaction_wrong_thread");
        }

        public void Detach() { CheckThread(); _session.StateReplaced -= ResetSession; }

    }
}
