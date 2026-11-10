using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Kernel;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PieGame.AI.Unity.Tests.PlayMode
{
    /// <summary>真实桥接加离线字符串端口；时序只依赖 TCS、队列计数与完成屏障，不加载 Town 宿主。</summary>
    public sealed class OwnerThreadDispatchPlayModeTests
    {
        const string Initial = "state:initial";
        const string Kind = "dispatch-string";
        readonly List<GameObject> _objects = new List<GameObject>();
        readonly List<OwnerThreadDispatchQueue> _queues = new List<OwnerThreadDispatchQueue>();
        readonly List<ControlledProvider> _providers = new List<ControlledProvider>();
        readonly List<CancellationTokenSource> _cancellations = new List<CancellationTokenSource>();
        int _mainThread;

        [SetUp]
        public void SetUp() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        [TearDown]
        public void TearDown()
        {
            foreach (var queue in _queues) queue.Close();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            foreach (var provider in _providers) provider.Release("state:teardown");
            foreach (var cancellation in _cancellations) cancellation.Dispose();
            _queues.Clear(); _objects.Clear(); _providers.Clear(); _cancellations.Clear();
        }

        [UnityTest]
        public IEnumerator 工作线程只投递同步块_Update派发前不读写_端口和Completed都在主线程()
        {
            var bridge = Create(out var port);
            var host = Host(out var queue);
            host.enabled = false;
            IOwnerThreadDispatcher dispatcher = host.Dispatcher;
            var stamp = bridge.CaptureState();
            var events = new EventLog();
            int throwingThread = 0, blockThread = 0;
            bridge.Completed += _ =>
            {
                throwingThread = Thread.CurrentThread.ManagedThreadId;
                throw new InvalidOperationException("observer-isolated");
            };
            bridge.Completed += events.Record;
            int reads = port.Reads;
            var submitted = Signal<int>();
            Func<Receipt> ownerBlock = () =>
            {
                blockThread = Thread.CurrentThread.ManagedThreadId;
                return bridge.Transact("worker", stamp, "state:worker");
            };
            var submission = EnqueueFromWorker(dispatcher, ownerBlock, submitted);
            yield return Until(() => queue.PendingCount == 1);
            yield return Finished(submission);
            var task = Result(submission);
            Assert.That(Result(submitted.Task), Is.Not.EqualTo(_mainThread));
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(blockThread, Is.Zero);
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Codes, Is.Empty);

            host.enabled = true;
            yield return Finished(task);
            Assert.That(Result(task).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(blockThread, Is.EqualTo(_mainThread));
            Assert.That(throwingThread, Is.EqualTo(_mainThread));
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed" }));
            AssertOwner(port, events);
        }

        [UnityTest]
        public IEnumerator 真实异步提供方和解析返回工作线程_准备不写入_仅队列Commit产生唯一写入()
        {
            var handler = new RecordingHandler();
            var bridge = Create(out var port, handler);
            var queue = Queue();
            var events = new EventLog();
            bridge.Completed += events.Record;
            var provider = Provider();
            var preparation = bridge.PrepareAsync(Begin(bridge, "async"), provider);
            int reads = port.Reads;
            var resumed = Signal<int>();
            Func<Preparation, Receipt> ownerCommit = candidate => bridge.Commit(candidate);
            var task = CommitAfterPreparation(queue, preparation, ownerCommit, resumed);
            yield return Finished(provider.Entered.Task);
            Assert.That(Result(provider.Entered.Task), Is.Not.EqualTo(_mainThread));
            Assert.That(preparation.IsCompleted, Is.False);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(handler.Calls, Is.Zero);
            Assert.That(port.Writes, Is.Zero);

            provider.Release("state:async");
            yield return Until(() => queue.PendingCount == 1);
            Assert.That(Result(preparation).Receipt.Code, Is.EqualTo("ready"));
            Assert.That(Result(provider.Returned.Task), Is.Not.EqualTo(_mainThread));
            Assert.That(Result(resumed.Task), Is.Not.EqualTo(_mainThread));
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(handler.Thread, Is.Not.EqualTo(_mainThread));
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Codes, Is.Empty);
            queue.Drain();
            yield return Finished(task);
            Assert.That(Result(task).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed" }));
            AssertOwner(port, events);
        }

        [UnityTest]
        public IEnumerator 旧候选及重复提交仍走桥校验_派发前零写入_拒绝项不增加写入()
        {
            var bridge = Create(out var port);
            var queue = Queue();
            var events = new EventLog();
            bridge.Completed += events.Record;
            var oldTask = bridge.PrepareAsync(Begin(bridge, "old"), Provider("state:old"));
            var freshTask = bridge.PrepareAsync(Begin(bridge, "fresh"), Provider("state:fresh"));
            yield return Finished(oldTask);
            yield return Finished(freshTask);
            var old = Result(oldTask);
            var fresh = Result(freshTask);
            var winner = queue.InvokeAsync(() => bridge.Commit(fresh));
            var stale = queue.InvokeAsync(() => bridge.Commit(old));
            var duplicate = queue.InvokeAsync(() => bridge.Commit(fresh));
            Assert.That(port.Writes, Is.Zero, "owner 调用也不能内联执行");
            Assert.That(queue.PendingCount, Is.EqualTo(3));
            queue.Drain(1);
            Assert.That(Result(winner).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(stale.IsCompleted || duplicate.IsCompleted, Is.False);
            queue.Drain();
            Assert.That(Result(stale).Code, Is.EqualTo("stale_state"));
            Assert.That(Result(duplicate).Code, Is.EqualTo("duplicate"));
            Assert.That(port.Writes, Is.EqualTo(1), "stale 和 duplicate 均不得再次写入");
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed", "stale_state", "duplicate" }));
            AssertOwner(port, events);
        }

        [UnityTest]
        public IEnumerator 排队后原始token取消拒绝写入_提交返回后取消保留committed与动态标记()
        {
            var bridge = Create(out var port);
            var queue = Queue();
            var before = Cancellation();
            var after = Cancellation();
            var cancelledTask = bridge.PrepareAsync(Begin(bridge, "before"), Provider("state:before"), before.Token);
            var committedTask = bridge.PrepareAsync(Begin(bridge, "after"), Provider("state:after"), after.Token);
            yield return Finished(cancelledTask);
            yield return Finished(committedTask);
            var cancelled = Result(cancelledTask);
            var prepared = Result(committedTask);
            var rejected = queue.InvokeAsync(() => bridge.Commit(cancelled));
            Assert.That(queue.PendingCount, Is.EqualTo(1));
            before.Cancel();
            Assert.That(port.Writes, Is.Zero);
            queue.Drain();
            Assert.That(Result(rejected).Code, Is.EqualTo("cancelled"));
            Assert.That(port.Writes, Is.Zero, "Commit 未额外传 token，仍须保留准备时的原始 token");
            var accepted = queue.InvokeAsync(() => bridge.Commit(prepared));
            queue.Drain();
            var receipt = Result(accepted);
            Assert.That(receipt.Code, Is.EqualTo("committed"));
            Assert.That(receipt.CancellationAfterCommit, Is.False);
            after.Cancel();
            Assert.That(receipt.Code, Is.EqualTo("committed"));
            Assert.That(receipt.CancellationAfterCommit, Is.True);
            var duplicate = queue.InvokeAsync(() => bridge.Commit(prepared));
            queue.Drain();
            Assert.That(Result(duplicate).Code, Is.EqualTo("duplicate"));
            Assert.That(Result(duplicate).CancellationAfterCommit, Is.True);
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
        }

        [UnityTest]
        public IEnumerator 禁用MonoUpdate并确认Pending屏障后销毁_排队Task立即取消且闭包不执行()
        {
            var bridge = Create(out var port);
            var host = Host(out var queue);
            host.enabled = false;
            IOwnerThreadDispatcher dispatcher = host.Dispatcher;
            var stamp = bridge.CaptureState();
            var events = new EventLog();
            bridge.Completed += events.Record;
            int calls = 0, reads = port.Reads;
            Func<Receipt> ownerBlock = () => { calls++; return bridge.Transact("destroy", stamp, "state:forbidden"); };
            var submission = EnqueueFromWorker(dispatcher, ownerBlock);
            yield return Until(() => queue.PendingCount == 1);
            yield return Finished(submission);
            var task = Result(submission);
            Assert.That(task.IsCompleted, Is.False);
            Object.DestroyImmediate(host.gameObject);
            Assert.That(task.IsCanceled, Is.True, "OnDestroy 返回前就应取消真正的派发 Task");
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.DoesNotThrow(() => queue.Drain(), "关闭后的合法 owner Drain 应为空操作");
            Assert.That(calls, Is.Zero);
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Codes, Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => dispatcher.InvokeAsync(ownerBlock));
        }

        [UnityTest]
        public IEnumerator 错误线程Drain及Close都先拒绝_不取出闭包也不触达真实端口()
        {
            var bridge = Create(out var port);
            var queue = Queue();
            var stamp = bridge.CaptureState();
            int reads = port.Reads;
            var task = queue.InvokeAsync(() => bridge.Transact("owner-only", stamp, "state:owner"));
            // worker 仅持有托管 queue，不持有或访问 MonoBehaviour。
            var worker = WrongThreadCalls(queue);
            yield return Finished(worker);
            foreach (var error in Result(worker)) Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(queue.PendingCount, Is.EqualTo(1));
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            queue.Drain();
            Assert.That(Result(task).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
        }

        [UnityTest]
        public IEnumerator 单项异常不阻断队列_委托内Close取消余项但保留自身已提交结果()
        {
            var bridge = Create(out var port);
            var queue = Queue();
            var events = new EventLog();
            bridge.Completed += events.Record;
            int abandonedCalls = 0;
            var fault = queue.InvokeAsync<int>(() => throw new InvalidOperationException("one-item-only"));
            var following = queue.InvokeAsync(() => bridge.Transact("following", bridge.CaptureState(), "state:following"));
            var closing = queue.InvokeAsync(() =>
            {
                Assert.That(queue.PendingCount, Is.EqualTo(1), "仅剩余排队项，不包含当前正在执行项");
                var receipt = bridge.Transact("closing", bridge.CaptureState(), "state:closing");
                queue.Close();
                Assert.That(queue.PendingCount, Is.Zero);
                return receipt;
            });
            var abandoned = queue.InvokeAsync(() => { abandonedCalls++; return bridge.Capture(); });
            queue.Drain();
            Assert.That(fault.IsFaulted, Is.True);
            Assert.That(fault.Exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(fault.Exception.InnerException.Message, Is.EqualTo("one-item-only"));
            Assert.That(Result(following).Code, Is.EqualTo("committed"));
            Assert.That(Result(closing).Code, Is.EqualTo("committed"));
            Assert.That(abandoned.IsCanceled, Is.True);
            Assert.That(abandonedCalls, Is.Zero);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(port.Writes, Is.EqualTo(2));
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed", "committed" }));
            AssertOwner(port, events);
            queue.Close(); // 幂等；关闭不会回头取消运行中已经返回的结果。
            Assert.That(Result(closing).Code, Is.EqualTo("committed"));
            Assert.Throws<ObjectDisposedException>(() => queue.InvokeAsync(() => 1));
            yield break;
        }

        [UnityTest]
        public IEnumerator 非法参数不取队列_重入Drain拒绝_默认限额16且完成续体不会内联到owner()
        {
            var bridge = Create(out var port);
            var queue = Queue();
            Assert.Throws<ArgumentOutOfRangeException>(() => new OwnerThreadDispatchQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OwnerThreadDispatchQueue(-1));
            Assert.Throws<ArgumentNullException>(() => queue.InvokeAsync<int>(null));
            Exception reentry = null;
            var task = queue.InvokeAsync(() =>
            {
                reentry = Failure(() => queue.Drain());
                return bridge.Transact("reentry", bridge.CaptureState(), "state:reentry");
            });
            var order = new List<int>();
            for (int i = 0; i < 16; i++)
            {
                int index = i;
                _ = queue.InvokeAsync(() => { order.Add(index); return index; });
            }
            var continuation = task.ContinueWith(_ => Thread.CurrentThread.ManagedThreadId,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.Drain(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.Drain(-1));
            Assert.That(queue.PendingCount, Is.EqualTo(17));
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(port.Writes, Is.Zero);
            queue.Drain();
            Assert.That(Result(task).Code, Is.EqualTo("committed"));
            Assert.That(reentry, Is.TypeOf<InvalidOperationException>());
            Assert.That(queue.PendingCount, Is.EqualTo(1));
            Assert.That(order.Count, Is.EqualTo(15));
            queue.Drain(1);
            Assert.That(queue.PendingCount, Is.Zero);
            Assert.That(order.Count, Is.EqualTo(16));
            for (int i = 0; i < order.Count; i++) Assert.That(order[i], Is.EqualTo(i));
            yield return Finished(continuation);
            Assert.That(Result(continuation), Is.Not.EqualTo(_mainThread), "ExecuteSynchronously 也不能内联续体");
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
        }

        UnityKernelBridge Create(out RecordingPort port, RecordingHandler handler = null)
        {
            var go = new GameObject("Owner dispatch bridge fixture");
            _objects.Add(go);
            var bridge = go.AddComponent<UnityKernelBridge>();
            port = new RecordingPort(_mainThread);
            bridge.Initialize(port, "offline-owner-dispatch-1");
            bridge.Register(Kind, handler ?? new RecordingHandler());
            return bridge;
        }

        OwnerThreadDispatchQueue Queue() => Track(_queues, new OwnerThreadDispatchQueue(_mainThread));
        UnityOwnerThreadDispatcher Host(out OwnerThreadDispatchQueue queue)
        {
            var go = new GameObject("Owner dispatch Update fixture");
            _objects.Add(go);
            var host = go.AddComponent<UnityOwnerThreadDispatcher>();
            queue = Track(_queues, (OwnerThreadDispatchQueue)host.Dispatcher);
            return host;
        }

        static Request Begin(UnityKernelBridge bridge, string id) =>
            bridge.Begin(id, Kind, new FrozenPayload("dispatch-test-role", Encoding.UTF8.GetBytes("offline-visible-input")));

        ControlledProvider Provider(string state = null)
        {
            var provider = Track(_providers, new ControlledProvider());
            if (state != null) provider.Release(state);
            return provider;
        }
        CancellationTokenSource Cancellation() => Track(_cancellations, new CancellationTokenSource());
        static T Track<T>(List<T> cleanup, T item) { cleanup.Add(item); return item; }

        // 单独的 worker 提交闭包只捕获接口与未执行的委托；不访问 Mono，不等待需要 Update 的任务。
        static Task<Task<T>> EnqueueFromWorker<T>(IOwnerThreadDispatcher dispatcher, Func<T> ownerBlock,
            TaskCompletionSource<int> submitted = null) => Task.Factory.StartNew(() =>
            {
                submitted?.TrySetResult(Thread.CurrentThread.ManagedThreadId);
                return dispatcher.InvokeAsync(ownerBlock);
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        static Task<Exception[]> WrongThreadCalls(OwnerThreadDispatchQueue queue) =>
            Task.Run(() => new[] { Failure(() => queue.Drain()), Failure(queue.Close) });

        static async Task<Receipt> CommitAfterPreparation(IOwnerThreadDispatcher dispatcher, Task<Preparation> preparation,
            Func<Preparation, Receipt> ownerCommit, TaskCompletionSource<int> resumed)
        {
            var candidate = await preparation.ConfigureAwait(false);
            resumed.TrySetResult(Thread.CurrentThread.ManagedThreadId);
            return await dispatcher.InvokeAsync(() => ownerCommit(candidate)).ConfigureAwait(false);
        }

        void AssertOwner(RecordingPort port, EventLog events)
        {
            Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
            Assert.That(events.Threads, Has.All.EqualTo(_mainThread));
        }

        static TaskCompletionSource<T> Signal<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        static Exception Failure(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        static IEnumerator Finished(Task task) => Until(() => task.IsCompleted);
        static IEnumerator Until(Func<bool> completed)
        {
            var timeout = Stopwatch.StartNew();
            while (!completed())
            {
                Assert.That(timeout.Elapsed.TotalSeconds, Is.LessThan(10), "完成/入队屏障超时（仅防挂）");
                yield return null;
            }
        }

        static T Result<T>(Task<T> task)
        {
            Assert.That(task.IsCompleted, Is.True, "只能在完成屏障之后读取结果");
            Assert.That(task.IsCanceled, Is.False);
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            return task.GetAwaiter().GetResult();
        }

        sealed class RecordingPort : IStatePort
        {
            readonly int _owner;
            readonly List<int> _threads = new List<int>();
            string _state = Initial;
            int _reads, _writes;
            public int Reads => Volatile.Read(ref _reads);
            public int Writes => Volatile.Read(ref _writes);
            public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }
            public RecordingPort(int owner) => _owner = owner;
            public string Read() { Record(); Interlocked.Increment(ref _reads); return _state; }
            public void Write(string state) { Record(); _state = state; Interlocked.Increment(ref _writes); }
            void Record()
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                lock (_threads) _threads.Add(thread);
                if (thread != _owner) throw new InvalidOperationException("test_port_wrong_thread");
            }
        }

        sealed class RecordingHandler : ITaskHandler
        {
            int _calls, _thread;
            public int Calls => Volatile.Read(ref _calls);
            public int Thread => Volatile.Read(ref _thread);
            public Candidate Prepare(string before, string raw)
            {
                Volatile.Write(ref _thread, System.Threading.Thread.CurrentThread.ManagedThreadId);
                Interlocked.Increment(ref _calls);
                return new Candidate("dispatch-proposal:" + raw, raw, false);
            }
        }

        sealed class ControlledProvider : IProvider
        {
            readonly TaskCompletionSource<ProviderReply> _reply = Signal<ProviderReply>();
            public readonly TaskCompletionSource<int> Entered = Signal<int>();
            public readonly TaskCompletionSource<int> Returned = Signal<int>();
            public async Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
            {
                Entered.TrySetResult(Thread.CurrentThread.ManagedThreadId);
                var reply = await _reply.Task.ConfigureAwait(false);
                Returned.TrySetResult(Thread.CurrentThread.ManagedThreadId);
                return reply;
            }
            public void Release(string state) => _reply.TrySetResult(new ProviderReply(state));
        }

        sealed class EventLog
        {
            readonly List<string> _codes = new List<string>();
            readonly List<int> _threads = new List<int>();
            public string[] Codes { get { lock (_threads) return _codes.ToArray(); } }
            public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }
            public void Record(Receipt receipt)
            {
                lock (_threads) { _codes.Add(receipt.Code); _threads.Add(Thread.CurrentThread.ManagedThreadId); }
            }
        }
    }
}
