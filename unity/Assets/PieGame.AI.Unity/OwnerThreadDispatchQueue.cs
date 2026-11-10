using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PieGame.AI.Unity
{
    /// <summary>Queues a synchronous block for explicit execution on its owner thread; never runs inline.</summary>
    public interface IOwnerThreadDispatcher
    {
        Task<T> InvokeAsync<T>(Func<T> synchronousBlock);
    }

    /// <summary>Pure managed FIFO. Only InvokeAsync and PendingCount may be used from any thread.</summary>
    public sealed class OwnerThreadDispatchQueue : IOwnerThreadDispatcher
    {
        readonly object _gate = new object();
        readonly Queue<IWorkItem> _pending = new Queue<IWorkItem>();
        readonly int _ownerThreadId;
        bool _closed;
        bool _draining;

        public OwnerThreadDispatchQueue(int ownerThreadId)
        {
            if (ownerThreadId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerThreadId));
            _ownerThreadId = ownerThreadId;
        }

        /// <summary>Queued items only; excludes the currently executing block.</summary>
        public int PendingCount { get { lock (_gate) return _pending.Count; } }

        /// <summary>Always enqueues, even on the owner. Rejects null blocks and calls after Close synchronously.</summary>
        public Task<T> InvokeAsync<T>(Func<T> synchronousBlock)
        {
            if (synchronousBlock == null) throw new ArgumentNullException(nameof(synchronousBlock));
            var item = new WorkItem<T>(synchronousBlock);
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(OwnerThreadDispatchQueue));
                _pending.Enqueue(item);
            }
            return item.Task;
        }

        /// <summary>Owner only, non-reentrant. Executes up to maxItems blocks; individual faults stay on their tasks.</summary>
        public void Drain(int maxItems = 16)
        {
            CheckOwner();
            if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
            if (_draining) throw new InvalidOperationException("owner_dispatch_reentry");
            _draining = true;
            try
            {
                for (int i = 0; i < maxItems; i++)
                {
                    IWorkItem item;
                    lock (_gate)
                    {
                        if (_closed || _pending.Count == 0) break;
                        item = _pending.Dequeue();
                    }
                    item.Execute();
                }
            }
            finally { _draining = false; }
        }

        /// <summary>Owner only, idempotent. Atomically rejects new work and cancels queued work, never a running block.</summary>
        public void Close()
        {
            CheckOwner();
            IWorkItem[] abandoned;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                abandoned = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var item in abandoned) item.Cancel();
        }

        void CheckOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("owner_dispatch_wrong_thread");
        }

        interface IWorkItem
        {
            void Execute();
            void Cancel();
        }

        sealed class WorkItem<T> : IWorkItem
        {
            readonly Func<T> _block;
            readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal WorkItem(Func<T> block) { _block = block; }
            internal Task<T> Task => _completion.Task;

            public void Execute()
            {
                try { _completion.TrySetResult(_block()); }
                catch (Exception error) { _completion.TrySetException(error); }
            }

            public void Cancel() => _completion.TrySetCanceled();
        }
    }
}
