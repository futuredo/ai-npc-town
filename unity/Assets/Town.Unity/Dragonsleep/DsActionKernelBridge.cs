using System.Threading;
using System.Threading.Tasks;
using PieGame.AI.Kernel;
using PieGame.AI.Unity;
using Town.Core.Dragonsleep;
using Town.PieGame;

namespace Town.Unity.Dragonsleep
{
    /// <summary>只包装实际 Unity 桥的线程与生命周期；算法、提供方和 handler 在无引擎 Town.PieGame。</summary>
    public sealed class DsActionKernelBridge : IPieGameActionKernel
    {
        readonly UnityKernelBridge _bridge;
        readonly DsKernelStatePort _port;
        readonly int _owner = Thread.CurrentThread.ManagedThreadId;
        public int Commits { get; private set; }
        public Receipt LastReceipt { get; private set; }
        public int RetainedRequestCount { get; private set; }
        public long RetainedBeforeUtf8Bytes { get; private set; }
        public string Digest { get { using (_port.OpenRead()) return _bridge.Digest; } }

        public DsActionKernelBridge(UnityKernelBridge bridge, DsKernelStatePort port) { _bridge = bridge; _port = port; }
        public void Register(string kind, ITaskHandler handler) => _bridge.Register(kind, handler);
        public Request Begin(string id, string kind, FrozenPayload payload)
        {
            if (Thread.CurrentThread.ManagedThreadId != _owner) throw new System.InvalidOperationException("bridge_wrong_thread");
            using (_port.OpenRead())
            {
                var request = _bridge.Begin(id, kind, payload);
                RetainedRequestCount++;
                RetainedBeforeUtf8Bytes += _port.LastReadUtf8Bytes;
                return request;
            }
        }
        public Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default) =>
            _bridge.PrepareAsync(request, provider, cancellation);

        public Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken cancellation = default)
        {
            using (_port.OpenAction(write))
            {
                LastReceipt = _bridge.Commit(preparation, cancellation);
                if (LastReceipt.Code == "committed") Commits++;
                return LastReceipt;
            }
        }

        public void ResetCounters()
        { Commits = 0; LastReceipt = null; RetainedRequestCount = 0; RetainedBeforeUtf8Bytes = 0; }
    }
}
