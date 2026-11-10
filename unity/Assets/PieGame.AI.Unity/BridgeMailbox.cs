using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PieGame.AI.Kernel;

namespace PieGame.AI.Unity
{
    internal sealed class BridgePending
    {
        internal readonly TaskCompletionSource<Preparation> Completion =
            new TaskCompletionSource<Preparation>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class BridgeDelivery
    {
        internal readonly BridgePending Pending;
        internal readonly Preparation Preparation;
        internal readonly Exception Error;
        internal BridgeDelivery(BridgePending pending, Preparation preparation, Exception error)
        { Pending = pending; Preparation = preparation; Error = error; }
    }

    // Pure managed state only. The worker never owns or calls a Unity object.
    internal sealed class BridgeMailbox
    {
        readonly object _gate = new object();
        readonly HashSet<BridgePending> _pending = new HashSet<BridgePending>();
        readonly Queue<BridgeDelivery> _queue = new Queue<BridgeDelivery>();
        bool _closed;
        int _workers;

        internal int Count { get { lock (_gate) return _queue.Count; } }
        internal int Workers { get { lock (_gate) return _workers; } }

        internal BridgePending Add()
        {
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(BridgeMailbox));
                var pending = new BridgePending();
                _pending.Add(pending);
                _workers++;
                return pending;
            }
        }

        internal void Post(BridgeDelivery delivery)
        {
            lock (_gate)
            {
                _workers--;
                if (!_closed) _queue.Enqueue(delivery);
            }
        }

        internal bool TryTake(out BridgeDelivery delivery)
        {
            lock (_gate)
            {
                delivery = null;
                if (_closed || _queue.Count == 0) return false;
                delivery = _queue.Dequeue();
                _pending.Remove(delivery.Pending);
                return true;
            }
        }

        internal BridgePending[] Close()
        {
            lock (_gate)
            {
                _closed = true;
                var pending = new BridgePending[_pending.Count];
                _pending.CopyTo(pending);
                _pending.Clear();
                _queue.Clear();
                return pending;
            }
        }
    }
}
