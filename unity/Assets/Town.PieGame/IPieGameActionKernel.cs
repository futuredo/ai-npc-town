using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using KernelCore = PieGame.AI.Kernel.Kernel;

namespace Town.PieGame
{
    /// <summary>Unity 桥只需转接这四个操作；Commit 必须在 Kernel port.Write 中消费 write，不能先写或补写。</summary>
    public interface IPieGameActionKernel
    {
        void Register(string kind, ITaskHandler handler);
        Request Begin(string id, string kind, FrozenPayload payload);
        Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default);
        Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken cancellation = default);
    }

    /// <summary>
    /// 真实 Kernel 的无引擎实现；与 Unity 共用 DsKernelStatePort，不创建缓存世界或另一条写入算法。
    /// </summary>
    public sealed class ActionKernelAdapter : IPieGameActionKernel
    {
        readonly object _gate = new object();
        readonly DsKernelStatePort _port;
        readonly KernelCore _kernel;
        // 仅本地保留量统计；从 Begin 时实际读取的 JSON 记账，不反射访问 Kernel 内部 Request.Before。
        readonly Dictionary<string, int> _requests = new Dictionary<string, int>(StringComparer.Ordinal);
        long _retainedBeforeUtf8Bytes;
        public int RetainedRequestCount { get { lock (_gate) return _requests.Count; } }
        public long RetainedBeforeUtf8Bytes { get { lock (_gate) return _retainedBeforeUtf8Bytes; } }

        public ActionKernelAdapter(DsKernelStatePort port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _kernel = new KernelCore(_port, DsExecutor.RulesVersion);
        }

        /// <summary>兼容纯 .NET 状态源，只使用它读取当前权威快照；写入必须通过 Core 能力，不能再调用其 Write。</summary>
        public ActionKernelAdapter(IStatePort port) : this(AsScopedPort(port)) { }

        static DsKernelStatePort AsScopedPort(IStatePort port)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            return port as DsKernelStatePort ?? new DsKernelStatePort(() => DsWorldSnapshot.Parse(port.Read()));
        }

        public void Register(string kind, ITaskHandler handler) { lock (_gate) _kernel.Register(kind, handler); }
        public Request Begin(string id, string kind, FrozenPayload payload)
        {
            lock (_gate) using (_port.OpenRead())
            {
                var request = _kernel.Begin(id, kind, payload);
                int bytes = _port.LastReadUtf8Bytes;
                _requests.Add(request.Id, bytes);
                _retainedBeforeUtf8Bytes += bytes;
                return request;
            }
        }
        public Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default) =>
            Task.Run(() => _kernel.PrepareAsync(request, provider, cancellation));

        /// <summary>
        /// 仅 .NET 本地回放：真实 Kernel 先同步核对当前状态，再用内置 RecordedProvider 重算候选。
        /// 不扩展 Unity bridge，不调用真实提供方；常规 worker PrepareAsync 仍没有 port scope。
        /// </summary>
        public Task<Preparation> PrepareReplayAsync(Request request, ReplayFrame frame, string trustedDigest,
            CancellationToken cancellation = default)
        {
            lock (_gate) using (_port.OpenRead())
                return _kernel.PrepareReplayAsync(request, frame, trustedDigest, cancellation);
        }

        public Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken cancellation = default)
        {
            lock (_gate)
            {
                bool appliedBefore = write?.Applied == true;
                using (_port.OpenAction(write))
                {
                    var receipt = _kernel.Commit(preparation, cancellation);
                    if ((receipt.Code == "committed") != (!appliedBefore && write?.Applied == true))
                        throw new InvalidOperationException("action_transaction_write_contract");
                    return receipt;
                }
            }
        }

        public string Capture() { lock (_gate) using (_port.OpenRead()) return _kernel.Capture(); }
        public StateStamp CaptureState() { lock (_gate) using (_port.OpenRead()) return _kernel.CaptureState(); }
        /// <summary>宿主先替换世界，再通知 Reset；端口只核对字节，不复制第二份世界。</summary>
        public void Reset(string state)
        {
            lock (_gate) using (_port.OpenReset())
            {
                _kernel.Reset(state);
                _requests.Clear(); _retainedBeforeUtf8Bytes = 0;
            }
        }
        public Receipt TransactScene(string id, StateStamp expected, string after, DsSceneRuleWrite write,
            CancellationToken cancellation = default)
        {
            lock (_gate) using (_port.OpenScene(write, after)) return _kernel.Transact(id, expected, after, cancellation);
        }
        public Receipt TransactDay(string id, StateStamp expected, string after, DsDayPhaseWrite write,
            CancellationToken cancellation = default)
        {
            lock (_gate) using (_port.OpenDay(write, after)) return _kernel.Transact(id, expected, after, cancellation);
        }
    }
}
