using System;
using System.Threading;
using System.Text;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;

namespace Town.PieGame
{
    /// <summary>各接缝共用的实时端口；无影子状态，写能力只在一次同步 Kernel 调用期间有效。</summary>
    public sealed class DsKernelStatePort : IStatePort
    {
        readonly Func<DsWorldSnapshot> _capture;
        readonly int _owner = Thread.CurrentThread.ManagedThreadId;
        Action<string> _write;
        bool _active;
        bool _insideCallback;
        bool _written;
        public int LastReadUtf8Bytes { get; private set; }

        public DsKernelStatePort(Func<DsWorldSnapshot> capture) => _capture = capture ?? throw new ArgumentNullException(nameof(capture));

        public string Read()
        {
            CheckScope();
            _insideCallback = true;
            try
            {
                string state = _capture().Json;
                LastReadUtf8Bytes = Encoding.UTF8.GetByteCount(state);
                return state;
            }
            finally { _insideCallback = false; }
        }

        public void Write(string state)
        {
            CheckScope();
            if (_write == null) throw new InvalidOperationException("kernel_port_write_outside_scope");
            if (_written) throw new InvalidOperationException("kernel_port_write_once");
            _written = true;
            _insideCallback = true;
            try { _write(state); }
            finally { _insideCallback = false; }
        }

        public IDisposable OpenRead() => Open(null);

        public IDisposable OpenAction(DsActionWrite write) => Open(state =>
        {
            if (write == null) throw new InvalidOperationException("action_write_required");
            write.Apply(state);
        });

        public IDisposable OpenScene(DsSceneRuleWrite write, string expectedAfter) => Open(state =>
        {
            if (write == null || !string.Equals(state, expectedAfter, StringComparison.Ordinal))
                throw new InvalidOperationException("scene_candidate_mismatch");
            write.Apply();
        });

        public IDisposable OpenDay(DsDayPhaseWrite write, string expectedAfter) => Open(state =>
        {
            if (write == null || !string.Equals(state, expectedAfter, StringComparison.Ordinal))
                throw new InvalidOperationException("day_candidate_mismatch");
            write.Apply();
        });

        public IDisposable OpenReset() => Open(state =>
        {
            if (!string.Equals(state, _capture().Json, StringComparison.Ordinal))
                throw new InvalidOperationException("reset_candidate_mismatch");
            // 世界已由 Session 替换；只失效内核代际，不再次复制世界。
        });

        IDisposable Open(Action<string> write)
        {
            CheckThread();
            if (_active || _insideCallback) throw new InvalidOperationException("kernel_port_reentry");
            _active = true; _written = false; _write = write;
            return new Scope(this);
        }

        void CheckScope()
        {
            CheckThread();
            if (!_active) throw new InvalidOperationException("kernel_port_outside_scope");
            if (_insideCallback) throw new InvalidOperationException("kernel_port_reentry");
        }

        void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _owner) throw new InvalidOperationException("kernel_port_wrong_thread");
        }

        sealed class Scope : IDisposable
        {
            DsKernelStatePort _port;
            internal Scope(DsKernelStatePort port) { _port = port; }
            public void Dispose()
            {
                if (_port == null) return;
                _port.CheckThread();
                if (_port._insideCallback) throw new InvalidOperationException("kernel_port_reentry");
                _port._write = null; _port._active = false; _port = null;
            }
        }
    }
}
