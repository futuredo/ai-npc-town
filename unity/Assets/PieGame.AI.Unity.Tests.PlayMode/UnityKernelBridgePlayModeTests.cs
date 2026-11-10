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
    /// <summary>
    /// 独立 SDK 桥接契约：仅使用测试内的字符串端口，不加载 Town 场景或生产宿主。
    /// 所有时序由 TCS、线程 ID、端口计数及已入队计数建立；超时仅防止失败时悬挂。
    /// </summary>
    public sealed class UnityKernelBridgePlayModeTests
    {
        const string Initial = "state:initial";
        const string Kind = "replace-detached-string";
        const double TimeoutSeconds = 10;

        readonly List<GameObject> _objects = new List<GameObject>();
        readonly List<ControlledProvider> _providers = new List<ControlledProvider>();
        readonly List<CancellationTokenSource> _cancellations = new List<CancellationTokenSource>();
        int _mainThread;

        [SetUp]
        public void SetUp() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        [TearDown]
        public void TearDown()
        {
            // 先关闭桥，再释放故意忽略取消的离线 provider，避免失败分支遗留待处理写入。
            foreach (var gameObject in _objects)
                if (gameObject != null) Object.DestroyImmediate(gameObject);
            foreach (var provider in _providers) provider.Release("state:teardown");
            foreach (var cancellation in _cancellations) cancellation.Dispose();
            _objects.Clear();
            _providers.Clear();
            _cancellations.Clear();
        }

        [UnityTest]
        public IEnumerator 同步与异步提供方及解析都在工作线程_端口和事件只在主线程()
        {
            foreach (bool asynchronous in new[] { false, true })
            {
                var handler = new RecordingHandler();
                var bridge = Create(handler, out var port);
                var events = new EventLog();
                bridge.Completed += events.Record;
                var provider = Provider(asynchronous);
                var request = Begin(bridge, asynchronous ? "async" : "sync");
                int readsAfterBegin = port.Reads;
                var task = bridge.PrepareAsync(request, provider);

                yield return Until(() => provider.Entered.Task.IsCompleted, "provider 未进入");
                Assert.That(provider.InvocationThread, Is.Not.EqualTo(_mainThread));
                Assert.That(port.Reads, Is.EqualTo(readsAfterBegin), "准备不应再次读取实况端口");
                Assert.That(port.Writes, Is.Zero);
                if (asynchronous)
                {
                    Assert.That(task.IsCompleted, Is.False, "未释放 TCS 时不能完成准备");
                    Assert.That(handler.Calls, Is.Zero);
                    provider.Release("state:asynchronous");
                }

                yield return Finished(task);
                var prepared = Result(task);
                Assert.That(prepared.Receipt.Code, Is.EqualTo("ready"));
                Assert.That(handler.Calls, Is.EqualTo(1));
                Assert.That(handler.Threads, Has.All.Not.EqualTo(_mainThread));
                Assert.That(handler.Before, Is.EqualTo(Initial));
                Assert.That(port.Writes, Is.Zero, "公开准备 Task 完成也不等于已提交");
                Assert.That(events.Count, Is.Zero);

                Assert.That(bridge.Commit(prepared).Code, Is.EqualTo("committed"));
                Assert.That(port.Writes, Is.EqualTo(1));
                Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
                Assert.That(events.Threads, Is.EqualTo(new[] { _mainThread }));
                Object.DestroyImmediate(bridge.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator 所有实况入口从工作线程调用均先拒绝_不读写端口也不启动提供方()
        {
            var bridge = Create(new RecordingHandler(true), out var port);
            var preparedTask = bridge.PrepareAsync(Begin(bridge, "prepared"), Provider(false));
            yield return Finished(preparedTask);
            var prepared = Result(preparedTask);
            var stamp = bridge.CaptureState();
            var unused = Begin(bridge, "unused");
            var provider = Provider(false);
            var events = new EventLog();
            bridge.Completed += events.Record;
            int reads = port.Reads;
            int writes = port.Writes;

            // 传入 null 的入口也应先做线程校验，而不是触达参数、端口或 Kernel。
            var entries = new Action[]
            {
                () => bridge.Initialize(null, null),
                () => bridge.Register(null, null),
                () => { bridge.Capture(); },
                () => { bridge.CaptureState(); },
                () => { bridge.Begin(null, null, null); },
                () => { bridge.PrepareAsync(unused, provider); },
                () => { bridge.Commit(prepared); },
                () => { bridge.IssueConfirmation(prepared); },
                () => { bridge.Confirm(prepared, "not-a-capability"); },
                () => { bridge.Transact("worker", stamp, "state:illegal"); },
                () => bridge.Reset("state:illegal")
            };
            var worker = Task.Run(() =>
            {
                var failures = new List<Exception>();
                foreach (var entry in entries)
                {
                    try { entry(); failures.Add(null); }
                    catch (Exception error) { failures.Add(error); }
                }
                return failures;
            });
            yield return Finished(worker);
            var errors = Result(worker);
            Assert.That(errors.Count, Is.EqualTo(entries.Length));
            foreach (var error in errors)
            {
                Assert.That(error, Is.TypeOf<InvalidOperationException>(), "每个入口都必须同步拒绝工作线程");
            }
            Assert.That(provider.Invocations, Is.Zero);
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.EqualTo(writes));
            Assert.That(events.Count, Is.Zero);
            Assert.That(bridge.Capture(), Is.EqualTo(Initial));
        }

        [UnityTest]
        public IEnumerator 提供方TCS未释放时外部事务可提交_旧候选随后因状态变化拒绝()
        {
            var handler = new RecordingHandler();
            var bridge = Create(handler, out var port);
            var events = new EventLog();
            bridge.Completed += events.Record;
            var provider = Provider(true);
            var task = bridge.PrepareAsync(Begin(bridge, "candidate"), provider);
            yield return Until(() => provider.Entered.Task.IsCompleted, "provider 未进入等待");

            Assert.That(provider.ReplyReleased, Is.False);
            Assert.That(bridge.Transact("external", bridge.CaptureState(), "state:external").Code,
                Is.EqualTo("committed"));
            Assert.That(task.IsCompleted, Is.False, "外部提交不能依赖 provider 收尾");
            Assert.That(handler.Calls, Is.Zero);
            provider.Release("state:old-candidate");
            yield return Finished(task);

            Assert.That(bridge.Commit(Result(task)).Code, Is.EqualTo("stale_state"));
            Assert.That(bridge.Capture(), Is.EqualTo("state:external"));
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed", "stale_state" }));
            Assert.That(events.Threads, Has.All.EqualTo(_mainThread));
        }

        [UnityTest]
        public IEnumerator 已准备后取消不写入_忽略取消的迟到结果标记late且不进入解析()
        {
            var handler = new RecordingHandler();
            var bridge = Create(handler, out var port);
            var preparedCancellation = Cancellation();
            var preparedTask = bridge.PrepareAsync(Begin(bridge, "prepared-cancel"),
                Provider(false), preparedCancellation.Token);
            yield return Finished(preparedTask);
            var prepared = Result(preparedTask);
            preparedCancellation.Cancel();
            Assert.That(bridge.Commit(prepared).Code, Is.EqualTo("cancelled"));
            Assert.That(port.Writes, Is.Zero);

            var lateCancellation = Cancellation();
            var lateProvider = Provider(true);
            var lateTask = bridge.PrepareAsync(Begin(bridge, "late-cancel"), lateProvider,
                lateCancellation.Token);
            yield return Until(() => lateProvider.Entered.Task.IsCompleted, "迟到 provider 未进入");
            Assert.That(lateProvider.SeenCancellation.CanBeCanceled, Is.True);
            int calls = handler.Calls;
            lateCancellation.Cancel();
            Assert.That(lateProvider.SeenCancellation.IsCancellationRequested, Is.True,
                "实际提供方必须同时观察用户取消与桥生命周期；内核保留原始用户 token");
            lateProvider.Release("state:must-not-parse");
            yield return Finished(lateTask);
            var late = Result(lateTask);
            Assert.That(late.Receipt.Code, Is.EqualTo("late_after_cancel"));
            Assert.That(bridge.Commit(late).Code, Is.EqualTo("late_after_cancel"));
            Assert.That(handler.Calls, Is.EqualTo(calls));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(bridge.Capture(), Is.EqualTo(Initial));
        }

        [UnityTest]
        public IEnumerator 端口写入前回调取消及提交返回后取消都保留committed并可观察取消标记()
        {
            var bridge = Create(new RecordingHandler(), out var port);
            var duringWrite = Cancellation();
            var firstTask = bridge.PrepareAsync(Begin(bridge, "cancel-in-write"), Provider(false), duringWrite.Token);
            yield return Finished(firstTask);
            port.BeforeWrite = duringWrite.Cancel;
            var first = bridge.Commit(Result(firstTask));
            port.BeforeWrite = null;
            Assert.That(first.Code, Is.EqualTo("committed"));
            Assert.That(first.CancellationAfterCommit, Is.True);
            Assert.That(port.Writes, Is.EqualTo(1));

            var afterReturn = Cancellation();
            var secondTask = bridge.PrepareAsync(Begin(bridge, "cancel-after-return"), Provider(false), afterReturn.Token);
            yield return Finished(secondTask);
            var second = bridge.Commit(Result(secondTask));
            var duplicate = bridge.Commit(Result(secondTask));
            Assert.That(second.Code, Is.EqualTo("committed"));
            Assert.That(second.CancellationAfterCommit, Is.False);
            afterReturn.Cancel();
            Assert.That(second.Code, Is.EqualTo("committed"));
            Assert.That(second.CancellationAfterCommit, Is.True);
            Assert.That(duplicate.Code, Is.EqualTo("duplicate"));
            Assert.That(duplicate.CancellationAfterCommit, Is.True);
            Assert.That(port.Writes, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator 销毁时提供方仍未返回_公开Task立即取消_迟到提供方不读写端口不发事件()
        {
            var handler = new RecordingHandler();
            var bridge = Create(handler, out var port);
            var provider = Provider(true);
            var events = new EventLog();
            bridge.Completed += events.Record;
            var task = bridge.PrepareAsync(Begin(bridge, "destroy-pending"), provider);
            yield return Until(() => provider.Entered.Task.IsCompleted, "provider 未进入等待");
            Assert.That(provider.SeenCancellation.CanBeCanceled, Is.True);
            int reads = port.Reads;

            Object.DestroyImmediate(bridge.gameObject);
            Assert.That(provider.SeenCancellation.IsCancellationRequested, Is.True);
            Assert.That(task.IsCanceled, Is.True, "OnDestroy 返回前就必须取消公开 Task，不能等 provider");
            Assert.That(provider.Returned.Task.IsCompleted, Is.False, "销毁取消不能靠 provider 配合");
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Count, Is.Zero);

            provider.Release("state:late-after-destroy");
            yield return Until(() => provider.Returned.Task.IsCompleted, "离线 provider 未交付迟到结果");
            yield return Until(() => bridge.PendingPreparationCount == 0, "迟到回复尚未经过生命周期拦截");
            Assert.That(handler.Calls, Is.Zero, "销毁后的 provider 回复不得再进入解析");
            Assert.That(task.IsCanceled, Is.True);
            Assert.That(port.Reads, Is.EqualTo(reads), "迟到准备不得访问实况端口");
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Count, Is.Zero);
            Assert.That(handler.Threads, Has.All.Not.EqualTo(_mainThread));
            Assert.That(port.Threads, Has.All.EqualTo(_mainThread));
        }

        [UnityTest]
        public IEnumerator 禁用Update后确定完成已入队再销毁_队列中的公开Task取消且不悬挂()
        {
            var handler = new RecordingHandler();
            var bridge = Create(handler, out var port);
            bridge.enabled = false;
            var events = new EventLog();
            bridge.Completed += events.Record;
            var task = bridge.PrepareAsync(Begin(bridge, "destroy-queued"), Provider(false));
            yield return Until(() => bridge.PendingCompletionCount > 0, "完成结果未进入可观察队列");

            Assert.That(handler.Calls, Is.EqualTo(1), "已确实解析完成，而不只是 provider 返回");
            Assert.That(task.IsCompleted, Is.False, "禁用 Update 时不能在工作线程完成公开 Task");
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Count, Is.Zero);
            int reads = port.Reads;
            Object.DestroyImmediate(bridge.gameObject);
            Assert.That(task.IsCanceled, Is.True, "销毁必须终结已入队但未派发的公开 Task");
            Assert.That(port.Reads, Is.EqualTo(reads));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(events.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator 重置到完全相同状态也使旧候选和旧状态戳失效_新局可复用请求标识()
        {
            var bridge = Create(new RecordingHandler(), out var port);
            var oldStamp = bridge.CaptureState();
            var task = bridge.PrepareAsync(Begin(bridge, "same-id"), Provider(false));
            yield return Finished(task);
            var oldPrepared = Result(task);
            bridge.Reset(oldStamp.State);
            int writes = port.Writes;

            Assert.That(bridge.Capture(), Is.EqualTo(oldStamp.State));
            Assert.That(bridge.Commit(oldPrepared).Code, Is.EqualTo("stale_session"));
            Assert.That(bridge.Transact("old-event", oldStamp, "state:illegal-old-event").Code,
                Is.EqualTo("stale_session"));
            Assert.That(port.Writes, Is.EqualTo(writes));
            var current = bridge.CaptureState();
            Assert.That(current.Generation, Is.GreaterThan(oldStamp.Generation));
            var newTask = bridge.PrepareAsync(Begin(bridge, "same-id"), Provider(false));
            yield return Finished(newTask);
            Assert.That(bridge.Commit(Result(newTask)).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(writes + 1));
        }

        [UnityTest]
        public IEnumerator 确认绑定确切候选_改态或Reset撤销_一次使用_写失败重试区分原子与违约端口()
        {
            var bridge = Create(new RecordingHandler(true), out var port);
            var request = Begin(bridge, "confirmation");
            var firstTask = bridge.PrepareAsync(request, Provider(false));
            yield return Finished(firstTask);
            var first = Result(firstTask);
            Assert.That(bridge.Commit(first).Code, Is.EqualTo("confirmation_required"));
            string token = bridge.IssueConfirmation(first);
            Assert.That(token, Is.Not.Null.And.Not.Empty);
            var secondTask = bridge.PrepareAsync(request, Provider(false));
            yield return Finished(secondTask);
            Assert.That(bridge.Confirm(Result(secondTask), token).Code, Is.EqualTo("confirmation_invalid"),
                "即使同请求同结果，也不能把另一 Preparation 当成展示过的候选");
            Assert.That(bridge.Confirm(first, "forged").Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(port.Writes, Is.Zero);

            var cancelled = Cancellation();
            cancelled.Cancel();
            Assert.That(bridge.Confirm(first, token, cancelled.Token).Code, Is.EqualTo("cancelled"));
            port.BeforeWrite = () => throw new InvalidOperationException("atomic-write-rejected");
            Assert.Throws<InvalidOperationException>(() => bridge.Confirm(first, token));
            Assert.That(port.Writes, Is.Zero, "测试端口必须在完整替换前抛出，不能部分写入");
            Assert.That(bridge.Capture(), Is.EqualTo(Initial));
            port.BeforeWrite = null;
            Assert.That(bridge.Confirm(first, token).Code, Is.EqualTo("committed"));
            Assert.That(bridge.Confirm(first, token).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(bridge.Commit(first).Code, Is.EqualTo("duplicate"));
            Assert.That(port.Writes, Is.EqualTo(1));

            var stateTask = bridge.PrepareAsync(Begin(bridge, "state-token"), Provider(false));
            yield return Finished(stateTask);
            var statePrepared = Result(stateTask);
            string stateToken = bridge.IssueConfirmation(statePrepared);
            string original = bridge.Capture();
            Assert.That(bridge.Transact("away", bridge.CaptureState(), "state:away").Code, Is.EqualTo("committed"));
            Assert.That(bridge.Transact("back", bridge.CaptureState(), original).Code, Is.EqualTo("committed"));
            Assert.That(bridge.Confirm(statePrepared, stateToken).Code, Is.EqualTo("confirmation_invalid"));
            string resetToken = bridge.IssueConfirmation(statePrepared);
            Assert.That(resetToken, Is.Not.Null.And.Not.Empty);
            bridge.Reset(original);
            int writes = port.Writes;
            Assert.That(bridge.Confirm(statePrepared, resetToken).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(bridge.IssueConfirmation(statePrepared), Is.Null);
            Assert.That(port.Writes, Is.EqualTo(writes));

            // 故意违反 IStatePort 原子契约：先改状态再抛出。不声称桥能回滚宿主的部分效果。
            var nonAtomicProvider = Provider(true);
            var nonAtomicTask = bridge.PrepareAsync(Begin(bridge, "non-atomic-port"), nonAtomicProvider);
            nonAtomicProvider.Release("state:partial-host-effect");
            yield return Finished(nonAtomicTask);
            var nonAtomic = Result(nonAtomicTask);
            string nonAtomicToken = bridge.IssueConfirmation(nonAtomic);
            port.AfterWrite = () => throw new InvalidOperationException("non-atomic-test-port");
            Assert.Throws<InvalidOperationException>(() => bridge.Confirm(nonAtomic, nonAtomicToken));
            port.AfterWrite = null;
            Assert.That(port.Writes, Is.EqualTo(writes + 1), "违约端口的实际副作用不能假装已回滚");
            Assert.That(bridge.Confirm(nonAtomic, nonAtomicToken).Code, Is.EqualTo("stale_state"),
                "非原子失败后不能把重试错误标为 duplicate 或再次无条件写入");
            Assert.That(port.Writes, Is.EqualTo(writes + 1));
        }

        [UnityTest]
        public IEnumerator Completed监听抛异常不阻断后续监听_不把已提交异步流程变成失败()
        {
            var bridge = Create(new RecordingHandler(), out var port);
            int throwingThread = 0;
            int throwingCalls = 0;
            var events = new EventLog();
            bridge.Completed += _ =>
            {
                throwingThread = Thread.CurrentThread.ManagedThreadId;
                throwingCalls++;
                throw new InvalidOperationException("observer-must-not-change-commit-result");
            };
            bridge.Completed += events.Record;
            var task = PrepareThenCommit(bridge, Begin(bridge, "observer-error"), Provider(false));
            yield return Finished(task);
            var receipt = Result(task);

            Assert.That(receipt.Code, Is.EqualTo("committed"));
            Assert.That(task.IsFaulted, Is.False);
            Assert.That(task.IsCanceled, Is.False);
            Assert.That(port.Writes, Is.EqualTo(1));
            Assert.That(throwingCalls, Is.EqualTo(1));
            Assert.That(throwingThread, Is.EqualTo(_mainThread));
            Assert.That(events.Codes, Is.EqualTo(new[] { "committed" }));
            Assert.That(events.Threads, Is.EqualTo(new[] { _mainThread }));

            Assert.That(bridge.Transact("external-observer", bridge.CaptureState(), "state:external").Code,
                Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(2));
            Assert.That(events.Count, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator 端口回调重入先拒绝_已入队候选取消后不提交()
        {
            var bridge = Create(new RecordingHandler(), out var port);
            bridge.enabled = false;
            var cancellation = Cancellation();
            var task = bridge.PrepareAsync(Begin(bridge, "queued-cancel"), Provider(false), cancellation.Token);
            yield return Until(() => bridge.PendingCompletionCount == 1, "未得到确定的入队屏障");
            cancellation.Cancel();
            bridge.enabled = true;
            yield return Finished(task);
            Assert.That(bridge.Commit(Result(task)).Code, Is.EqualTo("cancelled"));
            Assert.That(port.Writes, Is.Zero);

            Exception reentry = null;
            port.BeforeWrite = () =>
            {
                try { bridge.Capture(); }
                catch (Exception error) { reentry = error; }
            };
            Assert.That(bridge.Transact("reentry-guard", bridge.CaptureState(), "state:replacement").Code,
                Is.EqualTo("committed"));
            Assert.That(reentry, Is.TypeOf<InvalidOperationException>());
            Assert.That(reentry.Message, Is.EqualTo("bridge_reentry"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        UnityKernelBridge Create(RecordingHandler handler, out RecordingPort port)
        {
            var gameObject = new GameObject("PieGame SDK PlayMode fixture");
            _objects.Add(gameObject);
            var bridge = gameObject.AddComponent<UnityKernelBridge>();
            port = new RecordingPort(_mainThread);
            bridge.Initialize(port, "offline-playmode-rules-1");
            bridge.Register(Kind, handler);
            return bridge;
        }

        static Request Begin(UnityKernelBridge bridge, string id) =>
            bridge.Begin(id, Kind, new FrozenPayload("test-visible-role", Encoding.UTF8.GetBytes("detached-test-payload")));

        ControlledProvider Provider(bool asynchronous)
        {
            var provider = new ControlledProvider(asynchronous);
            _providers.Add(provider);
            return provider;
        }

        CancellationTokenSource Cancellation()
        {
            var cancellation = new CancellationTokenSource();
            _cancellations.Add(cancellation);
            return cancellation;
        }

        static async Task<Receipt> PrepareThenCommit(UnityKernelBridge bridge, Request request, IProvider provider)
        {
            // 有意保留 UnitySynchronizationContext，复现宿主 await 后在主线程 Commit 的用法。
            var preparation = await bridge.PrepareAsync(request, provider);
            return bridge.Commit(preparation);
        }

        static IEnumerator Until(Func<bool> completed, string failure)
        {
            var timeout = Stopwatch.StartNew();
            while (!completed())
            {
                Assert.That(timeout.Elapsed.TotalSeconds, Is.LessThan(TimeoutSeconds), failure);
                yield return null;
            }
        }

        static IEnumerator Finished(Task task) => Until(() => task.IsCompleted, "公开 Task 超时未终结");

        static T Result<T>(Task<T> task)
        {
            Assert.That(task.IsCanceled, Is.False, "Task 不应被取消");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            return task.GetAwaiter().GetResult();
        }

        sealed class RecordingPort : IStatePort
        {
            readonly int _ownerThread;
            readonly List<int> _threads = new List<int>();
            string _state = Initial;
            int _reads;
            int _writes;
            public Action BeforeWrite;
            public Action AfterWrite;
            public int Reads => Volatile.Read(ref _reads);
            public int Writes => Volatile.Read(ref _writes);
            public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }

            public RecordingPort(int ownerThread) => _ownerThread = ownerThread;

            public string Read()
            {
                RecordThread();
                Interlocked.Increment(ref _reads);
                return _state;
            }

            public void Write(string state)
            {
                RecordThread();
                BeforeWrite?.Invoke();
                _state = state;
                Interlocked.Increment(ref _writes);
                AfterWrite?.Invoke();
            }

            void RecordThread()
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                lock (_threads) _threads.Add(thread);
                if (thread != _ownerThread) throw new InvalidOperationException("test_port_wrong_thread");
            }
        }

        sealed class RecordingHandler : ITaskHandler
        {
            readonly bool _confirmation;
            readonly List<int> _threads = new List<int>();
            int _calls;
            string _before;
            public int Calls => Volatile.Read(ref _calls);
            public string Before => Volatile.Read(ref _before);
            public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }

            public RecordingHandler(bool confirmation = false) => _confirmation = confirmation;

            public Candidate Prepare(string before, string raw)
            {
                lock (_threads) _threads.Add(Thread.CurrentThread.ManagedThreadId);
                Volatile.Write(ref _before, before);
                var candidate = new Candidate("test-proposal:" + raw, raw, _confirmation);
                Interlocked.Increment(ref _calls);
                return candidate;
            }
        }

        sealed class ControlledProvider : IProvider
        {
            readonly bool _asynchronous;
            readonly TaskCompletionSource<ProviderReply> _reply =
                new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            int _invocations;
            int _invocationThread;
            public readonly TaskCompletionSource<bool> Entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Returned =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken SeenCancellation { get; private set; }
            public int Invocations => Volatile.Read(ref _invocations);
            public int InvocationThread => Volatile.Read(ref _invocationThread);
            public bool ReplyReleased => _reply.Task.IsCompleted;

            public ControlledProvider(bool asynchronous) => _asynchronous = asynchronous;

            public Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _invocations);
                Volatile.Write(ref _invocationThread, Thread.CurrentThread.ManagedThreadId);
                SeenCancellation = cancellationToken;
                Entered.TrySetResult(true);
                if (_asynchronous) return AwaitReply();
                Returned.TrySetResult(true);
                return Task.FromResult(new ProviderReply("state:synchronous"));
            }

            async Task<ProviderReply> AwaitReply()
            {
                // 故意不注册取消：测试销毁不依赖 provider 合作以及用户取消后的迟到丢弃。
                var reply = await _reply.Task.ConfigureAwait(false);
                Returned.TrySetResult(true);
                return reply;
            }

            public void Release(string raw) => _reply.TrySetResult(new ProviderReply(raw));
        }

        sealed class EventLog
        {
            readonly List<int> _threads = new List<int>();
            readonly List<string> _codes = new List<string>();
            public int Count { get { lock (_threads) return _threads.Count; } }
            public int[] Threads { get { lock (_threads) return _threads.ToArray(); } }
            public string[] Codes { get { lock (_threads) return _codes.ToArray(); } }

            public void Record(Receipt receipt)
            {
                lock (_threads)
                {
                    _threads.Add(Thread.CurrentThread.ManagedThreadId);
                    _codes.Add(receipt.Code);
                }
            }
        }
    }
}
