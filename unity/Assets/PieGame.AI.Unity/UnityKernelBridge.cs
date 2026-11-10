using System;
using System.Threading;
using System.Threading.Tasks;
using PieGame.AI.Kernel;
using UnityEngine;
using KernelCore = PieGame.AI.Kernel.Kernel;

namespace PieGame.AI.Unity
{
    /// <summary>Explicit preparation and main-thread transactions. Never commits from Update or a worker.</summary>
    [DisallowMultipleComponent]
    public sealed class UnityKernelBridge : MonoBehaviour
    {
        readonly BridgeMailbox _mailbox = new BridgeMailbox();
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        KernelCore _kernel;
        Action<Receipt> _completed;
        int _mainThread;
        bool _destroyed;
        bool _insideLiveCall;

        public event Action<Receipt> Completed
        {
            add { CheckAccess(); _completed += value; }
            remove { CheckAccess(); _completed -= value; }
        }

        void Awake() => Volatile.Write(ref _mainThread, Thread.CurrentThread.ManagedThreadId);

        public void Initialize(IStatePort port, string hostRulesVersion)
        {
            CheckAccess();
            if (_kernel != null) throw new InvalidOperationException("bridge_already_initialized");
            _kernel = new KernelCore(port, hostRulesVersion);
        }

        public void Register(string kind, ITaskHandler detachedHandler) => Live(kernel =>
        {
            kernel.Register(kind, detachedHandler);
            return true;
        });

        public string Capture() => Live(kernel => kernel.Capture());
        public StateStamp CaptureState() => Live(kernel => kernel.CaptureState());
        public string Digest => Live(kernel => kernel.Digest);
        public Request Begin(string id, string kind, FrozenPayload payload) => Live(kernel => kernel.Begin(id, kind, payload));

        public Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default)
        {
            CheckReady();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            var pending = _mailbox.Add();
            var kernel = _kernel;
            var mailbox = _mailbox;
            var lifetime = _lifetime.Token;
            // No component is captured by the worker. Even a synchronous provider/handler runs off-thread.
            _ = Task.Run(() => PrepareAndPostAsync(kernel, mailbox, pending, request, provider, cancellation, lifetime));
            return pending.Completion.Task;
        }

        public Receipt Commit(Preparation preparation, CancellationToken cancellation = default) =>
            Publish(Live(kernel => kernel.Commit(preparation, cancellation)));

        public string IssueConfirmation(Preparation preparation) => Live(kernel => kernel.IssueConfirmation(preparation));

        public Receipt Confirm(Preparation preparation, string token, CancellationToken cancellation = default) =>
            Publish(Live(kernel => kernel.Confirm(preparation, token, cancellation)));

        public Receipt Transact(string id, StateStamp expected, string after, CancellationToken cancellation = default) =>
            Publish(Live(kernel => kernel.Transact(id, expected, after, cancellation)));

        public void Reset(string state) => Live(kernel => { kernel.Reset(state); return true; });

        // Deterministic test observation only; not a public pump or scheduling service.
        internal int PendingCompletionCount => _mailbox.Count;
        internal int PendingPreparationCount => _mailbox.Workers;

        void Update()
        {
            for (int i = 0; i < 16 && _mailbox.TryTake(out var delivery); i++)
            {
                if (delivery.Error != null) delivery.Pending.Completion.TrySetException(delivery.Error);
                else delivery.Pending.Completion.TrySetResult(delivery.Preparation);
            }
        }

        Receipt Publish(Receipt receipt)
        {
            if (_destroyed) return receipt;
            var subscribers = _completed;
            if (subscribers == null) return receipt;
            foreach (Action<Receipt> subscriber in subscribers.GetInvocationList())
            {
                if (_destroyed) break;
                // Observer failures cannot turn an already applied transaction into a reported failure.
                try { subscriber(receipt); }
                catch (Exception) { }
            }
            return receipt;
        }

        T Live<T>(Func<KernelCore, T> action)
        {
            CheckReady();
            _insideLiveCall = true;
            try { return action(_kernel); }
            finally { _insideLiveCall = false; }
        }

        void CheckReady()
        {
            CheckAccess();
            if (_kernel == null) throw new InvalidOperationException("bridge_not_initialized");
        }

        void CheckAccess()
        {
            int owner = Volatile.Read(ref _mainThread);
            if (owner == 0) throw new InvalidOperationException("bridge_not_awake");
            if (Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("bridge_wrong_thread");
            if (_destroyed) throw new ObjectDisposedException(nameof(UnityKernelBridge));
            if (_insideLiveCall) throw new InvalidOperationException("bridge_reentry");
        }

        void OnDestroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            // Close under the enqueue lock, then finish public tasks before asking an uncooperative provider to stop.
            foreach (var pending in _mailbox.Close()) pending.Completion.TrySetCanceled();
            try { _lifetime.Cancel(); }
            catch (AggregateException) { }
            _lifetime.Dispose();
            _completed = null;
            _kernel = null;
        }

        static async Task PrepareAndPostAsync(KernelCore kernel, BridgeMailbox mailbox, BridgePending pending,
            Request request, IProvider provider, CancellationToken user, CancellationToken lifetime)
        {
            try
            {
                var wrapped = new LifetimeProvider(provider, lifetime);
                // Preserve the original user token in Preparation and committed Receipt, including after prepare completes.
                var prepared = await kernel.PrepareAsync(request, wrapped, user).ConfigureAwait(false);
                mailbox.Post(new BridgeDelivery(pending, prepared, null));
            }
            catch (Exception error) { mailbox.Post(new BridgeDelivery(pending, null, error)); }
        }

        sealed class LifetimeProvider : IProvider
        {
            readonly IProvider _provider;
            readonly CancellationToken _lifetime;
            public LifetimeProvider(IProvider provider, CancellationToken lifetime)
            { _provider = provider; _lifetime = lifetime; }

            public async Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken user)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(user, _lifetime))
                {
                    _lifetime.ThrowIfCancellationRequested();
                    var reply = await _provider.CompleteAsync(payload, linked.Token).ConfigureAwait(false);
                    _lifetime.ThrowIfCancellationRequested();
                    return reply;
                }
            }
        }
    }
}
