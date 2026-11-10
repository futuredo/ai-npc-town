using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Unity;
using Town.Core.Dragonsleep;
using Town.Unity.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Town.Tests.PlayMode
{
    /// <summary>真实主场景、真实 Session 和受控离线回包；不使用 SDK 替身，不产生联网费用。</summary>
    public sealed class DragonsleepOwnerThreadPresentationTests
    {
        DragonsleepGame _game;
        DragonsleepSession _session;
        VisualElement _root;
        UnityOwnerThreadDispatcher _host;
        OwnerThreadDispatchQueue _queue;
        Delayed _provider;
        Func<IAiProvider> _factory;
        bool _reduced;
        bool _playerEnabled;
        int _main;
        Task<AiTrace> _run;
        readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<int> _threads = new ConcurrentQueue<int>();

        static TaskCompletionSource<T> Signal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        sealed class Call
        {
            public PromptPacket Packet;
            public ProviderResult Frozen;
            public int Worker;
            public readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();
            public readonly TaskCompletionSource<bool> Exited = Signal<bool>();
        }

        sealed class Delayed : IAiProvider
        {
            public string Name => "offline-owner-presentation-tcs";
            public bool IsSimulated => true;
            public readonly ConcurrentQueue<Call> Calls = new ConcurrentQueue<Call>();
            readonly ConcurrentQueue<Task> _workers = new ConcurrentQueue<Task>();
            int _stopped;

            public bool Drained => Calls.All(c => c.Exited.Task.IsCompleted) && _workers.All(t => t.IsCompleted);

            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                var call = new Call
                {
                    Packet = packet,
                    Frozen = await new RuleProvider().CompleteAsync(packet, ct).ConfigureAwait(false)
                };
                Calls.Enqueue(call); // 有效回复由本次冻结 packet 生成，但必须等测试显式放行。
                if (Volatile.Read(ref _stopped) != 0)
                    call.Reply.TrySetCanceled();
                try
                {
                    return await call.Reply.Task.ConfigureAwait(false);
                }
                finally
                {
                    call.Exited.TrySetResult(true);
                }
            }

            public void Release(Call call) => _workers.Enqueue(Task.Run(() =>
            {
                call.Worker = Thread.CurrentThread.ManagedThreadId;
                call.Reply.TrySetResult(call.Frozen);
            }));

            public void Cancel()
            {
                Interlocked.Exchange(ref _stopped, 1);
                foreach (var call in Calls)
                    call.Reply.TrySetCanceled();
            }
        }

        static void Watch(DragonsleepSession s, Action<string> note)
        {
            s.StageChanged += (t, stage) => note(t.RequestId + ":" + stage + ":" + t.Stage(stage).Status);
            s.TraceCompleted += t =>
            {
                note("done:" + t.RequestId + ":" + t.Receipt?.Status);
                if (t.CraftDecision == "pending")
                    note("pending:" + s.HasPendingCraft);
            };
        }

        static string Receipt(AiTrace t) => MiniJson.Write(t.Receipt.ToRecord());

        IEnumerator Until(Func<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (!done() && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(done(), "受控离线条件必须在 12 秒内满足，不能凭跨一帧认定完成");
        }

        bool Ready(string name) => _root.Q<Button>(name) is Button b && b.enabledInHierarchy
            && b.worldBound.width > 0 && b.worldBound.height > 0;

        void Click(string name)
        {
            Assert.IsTrue(Ready(name), name);
            var b = _root.Q<Button>(name);
            b.Focus();
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        DragonsleepSession Baseline()
        {
            var s = new DragonsleepSession(new RuleProvider());
            s.RestoreState(_session.CaptureState());
            return s;
        }

        void Same(DragonsleepSession old, List<string> events)
        {
            Assert.AreEqual(old.CaptureState().Json, _session.CaptureState().Json);
            CollectionAssert.AreEqual(events, _events.ToArray());
            Assert.IsNotEmpty(_threads);
            Assert.IsTrue(_threads.All(id => id == _main), "所有阶段及完成通知必须属于 Unity 主线程");
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            while (_events.TryDequeue(out _)) { }
            while (_threads.TryDequeue(out _)) { }
            _run = null;
            _game = null;
            _session = null;
            _host = null;
            _factory = DragonsleepGame.ProviderFactory;
            _reduced = DsUiMotion.Reduced;
            _main = Thread.CurrentThread.ManagedThreadId;
            _provider = new Delayed();
            DragonsleepGame.ProviderFactory = () => _provider;
            DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            Assert.IsNotNull(_game);
            _session = _game.Session;
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            _host = _game.GetComponent<UnityOwnerThreadDispatcher>();
            Assert.IsNotNull(_host);
            _queue = (OwnerThreadDispatchQueue)_host.Dispatcher;
            Assert.AreSame(_provider, _session.Provider);
            _playerEnabled = _game.Player.enabled;
            _game.Player.enabled = false;
            Watch(_session, e =>
            {
                _threads.Enqueue(Thread.CurrentThread.ManagedThreadId);
                _events.Enqueue(e);
            });
            yield return Until(() => Ready("beginJourney"));
            Click("beginJourney");
            yield return Until(() => !_game.Hud.ModalOpen && Ready("sendButton") && _session.World.PlayerPlace == "market");
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            DragonsleepGame.ProviderFactory = _factory;
            DsUiMotion.Reduced = _reduced;
            if (_host != null)
                _host.enabled = true;
            _provider?.Cancel();
            try
            {
                yield return Until(() => (_provider == null || _provider.Drained) && (_session == null || !_session.Busy)
                    && (_game == null || !_game.Busy) && (_run == null || _run.IsCompleted));
            }
            finally
            {
                if (_run != null && _run.IsFaulted)
                    _ = _run.Exception;
                if (_game != null)
                    _game.Player.enabled = _playerEnabled;
            }
        }

        [UnityTest]
        public IEnumerator 行动输入挂起期间拒绝移动_worker回包后主线程全阶段回执等同旧顺序()
        {
            var old = Baseline();
            var expectedEvents = new List<string>();
            Watch(old, expectedEvents.Add);
            for (int i = 0; i < 2; i++)
            {
                string before = _session.CaptureState().Json;
                Click("mode_Act");
                _root.Q<TextField>("playerInput").value = "观察四周";
                Click("sendButton");
                yield return Until(() => _provider.Calls.Count == i + 1 && _game.Player.InputBlocked && !Ready("sendButton"));
                var call = _provider.Calls.Last();
                Assert.AreEqual(AiTaskKind.Adjudicate, call.Packet.Kind);
                Assert.IsFalse(call.Reply.Task.IsCompleted);
                Assert.IsTrue(_game.Busy && _session.Busy);
                Assert.IsNull(_session.EnterPlace("workshop"));
                Assert.AreEqual(before, _session.CaptureState().Json);
                _provider.Release(call);
                yield return Until(() => _provider.Drained && !_game.Busy && !_session.Busy && Ready("sendButton"));
                Assert.AreNotEqual(_main, call.Worker);
                var actual = _session.Traces.Last();
                var expected = old.ActAsync("观察四周").GetAwaiter().GetResult();
                Assert.IsTrue(actual.Receipt.Applied);
                Assert.AreEqual(expected.Receipt.TimeCost, actual.Receipt.TimeCost);
                Assert.AreEqual(expected.WorldVersionAfter, actual.WorldVersionAfter);
                Assert.AreEqual(old.TimeLeftInPhase, _session.TimeLeftInPhase);
                Assert.AreEqual(Receipt(expected), Receipt(actual));
                Same(old, expectedEvents);
            }
        }

        [UnityTest]
        public IEnumerator 图样延迟返回通知归owner_实际放弃及确认不额外调用且材料时间等同旧入口()
        {
            _game.TravelTo("workshop");
            yield return Until(() => _session.World.PlayerPlace == "workshop");
            var old = Baseline();
            var expectedEvents = new List<string>();
            Watch(old, expectedEvents.Add);
            for (int i = 0; i < 2; i++)
            {
                string before = _session.CaptureState().Json;
                int time = _session.TimeLeftInPhase;
                Click("mode_Craft");
                _root.Q<TextField>("playerInput").value = "用蓝晶石织一盏灯";
                Click("sendButton");
                yield return Until(() => _provider.Calls.Count == i + 1);
                var call = _provider.Calls.Last();
                Assert.AreEqual(AiTaskKind.Craft, call.Packet.Kind);
                Assert.IsTrue(_session.Busy);
                Assert.IsFalse(call.Reply.Task.IsCompleted);
                Assert.IsFalse(_session.HasPendingCraft);
                Assert.AreEqual(before, _session.CaptureState().Json);
                _provider.Release(call);
                yield return Until(() => _provider.Drained && !_game.Busy && !_session.Busy && Ready("confirmCraft"));
                var actual = _session.Traces.Last();
                var expected = old.CraftAsync("用蓝晶石织一盏灯").GetAwaiter().GetResult();
                Assert.AreNotEqual(_main, call.Worker);
                Assert.IsTrue(_session.HasPendingCraft);
                Assert.AreEqual("pending", actual.CraftDecision);
                Assert.AreEqual(i + 1, _events.Count(e => e == "pending:True"));
                Assert.IsFalse(actual.Receipt.Applied);
                Assert.AreEqual(before, _session.CaptureState().Json);
                Assert.AreEqual(Receipt(expected), Receipt(actual));
                Same(old, expectedEvents);
                Click(i == 0 ? "declineCraft" : "confirmCraft");
                expected = old.ConfirmCraft(i != 0);
                yield return Until(() => !_session.HasPendingCraft && !_game.Hud.ModalOpen && !_game.Busy && Ready("sendButton"));
                Assert.AreEqual(i != 0, actual.Receipt.Applied);
                Assert.AreEqual(i == 0 ? "declined" : "consumed",
                    i == 0 ? actual.Receipt.Status : _session.World.Items["crystal_blue"].Holder);
                Assert.AreEqual(i == 0 ? time : time - 2, _session.TimeLeftInPhase);
                Assert.AreEqual(i, _session.World.Items.Values.Count(item => item.Crafted));
                Assert.AreEqual(i + 1, _provider.Calls.Count);
                Assert.AreEqual(Receipt(expected), Receipt(actual));
                Same(old, expectedEvents);
            }
        }

        [UnityTest]
        public IEnumerator 场景卸载取消确已排队的真实Session回包_不后台提交不向已销毁对象补发通知()
        {
            string before = _session.CaptureState().Json;
            _run = _session.ActAsync("观察四周");
            yield return Until(() => _provider.Calls.Count == 1);
            var call = _provider.Calls.Single();
            _host.enabled = false;
            int notices = _events.Count;
            _provider.Release(call);
            yield return Until(() => _provider.Drained && _queue.PendingCount > 0);
            Assert.Greater(_queue.PendingCount, 0);
            Assert.IsFalse(_run.IsCompleted);
            Assert.IsTrue(_session.Busy);
            Assert.AreEqual(before, _session.CaptureState().Json);
            var trace = _session.Traces.Single();
            Assert.IsNull(trace.Provider);
            Assert.IsNull(trace.Receipt);
            Assert.AreEqual(notices, _events.Count);
            var scene = _game.gameObject.scene;
            SceneManager.SetActiveScene(SceneManager.CreateScene("OwnerDispatchUnload_" + Guid.NewGuid().ToString("N")));
            yield return SceneManager.UnloadSceneAsync(scene);
            yield return Until(() => _run.IsCompleted && !_session.Busy && _provider.Drained);
            Assert.IsTrue(_game == null);
            // 后半段取消后，finally 再投递已关闭队列也可能以 ObjectDisposedException 结束。
            bool closed = _run.IsCanceled || (_run.IsFaulted && _run.Exception.Flatten().InnerExceptions.All(
                error => error is ObjectDisposedException || error is OperationCanceledException));
            Assert.IsTrue(closed, "关闭队列只允许取消或明确的关闭异常，不得成功提交或掩盖其他异常");
            Assert.IsFalse(_session.Busy, "所有结束路径都必须释放会话 gate");
            Assert.AreEqual(0, _queue.PendingCount);
            Assert.AreEqual(before, _session.CaptureState().Json);
            Assert.IsNull(trace.Receipt);
            Assert.AreEqual(notices, _events.Count);
            Assert.IsTrue(_threads.All(id => id == _main));
            Assert.AreNotEqual(_main, call.Worker);
        }
    }
}
