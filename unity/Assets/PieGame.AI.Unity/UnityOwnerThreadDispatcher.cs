using System;
using System.Threading;
using UnityEngine;

namespace PieGame.AI.Unity
{
    /// <summary>Main-thread host. Obtain Dispatcher on the main thread, then let workers retain only that managed queue.</summary>
    [DisallowMultipleComponent]
    public sealed class UnityOwnerThreadDispatcher : MonoBehaviour
    {
        OwnerThreadDispatchQueue _queue;
        int _ownerThreadId;

        void Awake()
        {
            _queue = new OwnerThreadDispatchQueue(Thread.CurrentThread.ManagedThreadId);
            Volatile.Write(ref _ownerThreadId, Thread.CurrentThread.ManagedThreadId);
        }

        public IOwnerThreadDispatcher Dispatcher
        {
            get
            {
                int owner = Volatile.Read(ref _ownerThreadId);
                if (owner == 0) throw new InvalidOperationException("owner_dispatch_not_awake");
                if (Thread.CurrentThread.ManagedThreadId != owner)
                    throw new InvalidOperationException("owner_dispatch_wrong_thread");
                return _queue;
            }
        }

        void Update() => _queue.Drain(16);

        void OnDestroy()
        {
            if (_queue != null) _queue.Close();
        }
    }
}
