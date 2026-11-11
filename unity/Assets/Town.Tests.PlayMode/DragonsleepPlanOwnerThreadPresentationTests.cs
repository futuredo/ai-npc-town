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
    /// <summary>真实主场景及 owner；直接 worker Step 禁用 Game.Update，验证 Session/Run 线程而非 UI 移动可达。</summary>
    public sealed class DragonsleepPlanOwnerThreadPresentationTests
    {
        DragonsleepGame _game;
        DragonsleepSession _session;
        VisualElement _root;
        UnityOwnerThreadDispatcher _host;
        OwnerThreadDispatchQueue _queue;
        Delayed _provider;
        Task _task;
        Func<IAiProvider> _factory;
        bool _reduced;
        bool _playerEnabled;
        int _main;

        readonly ConcurrentQueue<int> _threads = new ConcurrentQueue<int>();
        readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();

        static TaskCompletionSource<T> Signal<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        sealed class Call
        {
            public PromptPacket Packet;
            public ProviderResult Value;
            public int Worker;
            public int Exited;

            public readonly TaskCompletionSource<ProviderResult> Reply = Signal<ProviderResult>();
        }

        sealed class Delayed : IAiProvider
        {
            public string Name => "offline-plan-owner-tcs";
            public bool IsSimulated => true;
            public bool Immediate;

            public readonly ConcurrentQueue<Call> Calls = new ConcurrentQueue<Call>();

            readonly ConcurrentQueue<Task> _workers = new ConcurrentQueue<Task>();
            int _stopped;

            public bool Drained => Calls.All(c => Volatile.Read(ref c.Exited) != 0) && _workers.All(t => t.IsCompleted);

            public async Task<ProviderResult> CompleteAsync(PromptPacket p, CancellationToken ct)
            {
                var value = p.Kind == AiTaskKind.Plan
                    ? new ProviderResult
                    {
                        ProviderName = Name,
                        Simulated = true,
                        RawText = Plan()
                    }
                    : await new RuleProvider().CompleteAsync(p, ct).ConfigureAwait(false);

                if (Immediate)
                {
                    return value;
                }

                var call = new Call
                {
                    Packet = p,
                    Value = value
                };
                Calls.Enqueue(call);

                if (Volatile.Read(ref _stopped) != 0)
                {
                    call.Reply.TrySetCanceled();
                }

                try
                {
                    return await call.Reply.Task.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref call.Exited, 1);
                }
            }

            public void Release(Call c)
            {
                _workers.Enqueue(Task.Run(() =>
                {
                    c.Worker = Thread.CurrentThread.ManagedThreadId;
                    c.Reply.TrySetResult(c.Value);
                }));
            }

            public void Stop()
            {
                Interlocked.Exchange(ref _stopped, 1);
                foreach (var c in Calls)
                {
                    c.Reply.TrySetCanceled();
                }
            }
        }

        static string Plan()
        {
            var p = new DsPlan
            {
                Goal = "归巢",
                Summary = "工坊观察后在龙骨岗等到母龙到来。"
            };
            p.Steps.Add(new DsPlanStep
            {
                Id = "go",
                Op = "go",
                Place = "workshop"
            });
            // 规则固定掷骰 1，不依赖随机种子。
            p.Steps.Add(new DsPlanStep
            {
                Id = "look",
                Op = "act",
                Verb = "observe"
            });
            p.Steps.Add(new DsPlanStep
            {
                Id = "home",
                Op = "wait",
                Place = "skull_hill",
                Until = "第4天·清晨"
            });
            return MiniJson.Write(p.ToRecord());
        }

        void Watch(DragonsleepSession s)
        {
            void Note(string e)
            {
                _threads.Enqueue(Thread.CurrentThread.ManagedThreadId);
                _events.Enqueue(e);
            }

            s.StageChanged += (t, stage) => Note("stage");
            s.TraceCompleted += t => Note("trace");
            s.RuleEventCompleted += e => Note("rule:" + e.Label);
        }

        void MainOnly()
        {
            Assert.IsNotEmpty(_threads);
            Assert.IsTrue(_threads.All(t => t == _main), "规则、阶段与完成通知均须为 Unity main");
        }

        IEnumerator Until(Func<bool> done, bool pump = false)
        {
            float end = Time.realtimeSinceStartup + 12;
            while (!done() && Time.realtimeSinceStartup < end)
            {
                if (pump && _queue.PendingCount > 0)
                {
                    _queue.Drain(1);
                }
                yield return null;
            }
            Assert.IsTrue(done(), "受控条件未完成；不能以跨一帧或 Sleep 代替队列/任务证据");
        }

        void Click(string name)
        {
            var b = _root.Q<Button>(name);
            Assert.IsNotNull(b);
            Assert.IsTrue(b.enabledInHierarchy, name);
            b.Focus();
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        IEnumerator HoldAdoption(DsPlanRun run)
        {
            yield return Until(() => _provider.Calls.Count == 1);
            _host.enabled = false;
            var c = _provider.Calls.Single();
            Assert.AreEqual(AiTaskKind.Plan, c.Packet.Kind);
            _provider.Release(c);
            yield return Until(() => Volatile.Read(ref c.Exited) != 0 && _queue.PendingCount > 0);
            _queue.Drain(1);
            yield return Until(() => !_session.Busy && _queue.PendingCount > 0);
            Assert.AreEqual("plan_only", _session.Traces.Single().Receipt.Status);
            Assert.IsNull(run.LatestPlanTrace);
            Assert.IsEmpty(run.PlanTraces);
            Assert.AreEqual("planning", run.Status);
            Assert.AreNotEqual(_main, c.Worker);
        }

        static object Strip(object value)
        {
            if (value is Dictionary<string, object> d)
            {
                return d.Where(x => x.Key != "ms" && x.Key != "total_ms").ToDictionary(x => x.Key, x => Strip(x.Value));
            }
            return value is List<object> l ? l.Select(Strip).ToList() : value;
        }

        static string Record(DragonsleepSession s, DsPlanRun r)
        {
            return MiniJson.Write(Strip(new Dictionary<string, object>
            {
                ["state"] = s.CaptureState().Json,
                ["run"] = r.ToRecord(),
                ["traces"] = s.Traces.Select(t => (object)t.ToRecord()).ToList(),
                ["rules"] = s.RuleEvents.Select(e => (object)e.ToRecord()).ToList()
            }));
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            _task = null;
            _game = null;
            _session = null;
            _host = null;
            while (_threads.TryDequeue(out _))
            {
            }
            while (_events.TryDequeue(out _))
            {
            }
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
            _queue = (OwnerThreadDispatchQueue)_host.Dispatcher;
            _playerEnabled = _game.Player.enabled;
            _game.Player.enabled = false;
            Watch(_session);
            yield return Until(() => _root.Q<Button>("beginJourney")?.worldBound.width > 0);
            Click("beginJourney");
            yield return Until(() => !_game.Hud.ModalOpen && _root.Q<Button>("openPlan")?.worldBound.width > 0);
            var w = _session.World;
            w.Day = 3;
            w.Phase = DayPhase.Dusk;
            w.Whelp.Place = w.PlayerPlace;
            w.Flags["guard:skull_hill"] = "osric"; // 夜间换岗必产生真实规则回执。
            w.Whelp.Following = true;
            w.Whelp.Hidden = true;
            w.Whelp.TrustPlayer = 0.6f;
            w.Whelp.Fear = 0.4f;
            w.Flags["whelp_hidden_by_player"] = w.ClockText;
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            DragonsleepGame.ProviderFactory = _factory;
            DsUiMotion.Reduced = _reduced;
            if (_host != null)
            {
                _host.enabled = true;
            }
            _provider?.Stop();
            try
            {
                yield return Until(() => (_provider == null || _provider.Drained)
                    && (_task == null || _task.IsCompleted)
                    && (_session == null || !_session.Busy)
                    && (_game == null || !_game.Busy)
                    && (_host == null || _queue.PendingCount == 0));
            }
            finally
            {
                if (_task?.IsFaulted == true)
                {
                    _ = _task.Exception;
                }
                if (_game != null)
                {
                    _game.enabled = true;
                    _game.Player.enabled = _playerEnabled;
                }
            }
        }

        [UnityTest]
        public IEnumerator 真实提案确认后_worker逐步执行_采用及结果必须等待owner泵()
        {
            string before = _session.CaptureState().Json;
            int time = _session.TimeLeftInPhase;
            Click("openPlan");
            _root.Q<TextField>("planGoal").value = "把小烬送回母亲身边";
            Click("proposePlan");
            var run = _game.ActivePlan;
            yield return HoldAdoption(run);
            Assert.IsTrue(_game.Busy);
            Assert.AreEqual(before, _session.CaptureState().Json);
            _host.enabled = true;
            yield return Until(() => !_game.Busy && run.Status == "awaiting_confirmation");
            Click("confirmPlan");
            Assert.AreEqual(before, _session.CaptureState().Json);
            Assert.AreEqual(time, _session.TimeLeftInPhase);
            Assert.AreEqual("running", run.Status);

            _game.enabled = false;
            _host.enabled = false;
            _task = Task.Run(() => run.StepAsync());
            yield return Until(() => _queue.PendingCount > 0);
            Assert.IsEmpty(run.Results);
            Assert.AreEqual(before, _session.CaptureState().Json);
            _queue.Drain(1);
            yield return Until(() => _task.IsCompleted);
            Assert.IsFalse(_task.IsFaulted);
            Assert.AreEqual(1, run.Results.Count);
            Assert.AreEqual(1, run.NextIndex);
            Assert.AreEqual("workshop", _session.World.PlayerPlace);

            _task = Task.Run(() => run.StepAsync());
            yield return Until(() => _session.Traces.Count == 2, true);
            yield return Until(() => _queue.PendingCount > 0);
            Assert.AreEqual(1, run.Results.Count);
            _queue.Drain(1);
            yield return Until(() => !_session.Busy && _queue.PendingCount > 0);
            Assert.IsFalse(_task.IsCompleted);
            Assert.AreEqual(1, run.Results.Count);
            _host.enabled = true;
            yield return Until(() => _task.IsCompleted);
            Assert.IsFalse(_task.IsFaulted);
            Assert.AreEqual(2, run.NextIndex);
            Assert.AreEqual(2, run.Results.Count);
            Assert.AreEqual("running", run.Status);
            Assert.AreEqual(time - 2, _session.TimeLeftInPhase);
            CollectionAssert.Contains(_events, "rule:enter:workshop");
            MainOnly();
        }

        [UnityTest]
        public IEnumerator 固定成功骰同快照跨夜逐个worker回包_与null_owner完整记录一致且不自动下一步()
        {
            _game.enabled = false;
            _host.enabled = false;
            var snapshot = _session.CaptureState();
            var old = new DragonsleepSession(new Delayed
            {
                Immediate = true
            }, rolls: new DsFixedRoll(0.999));
            old.RestoreState(snapshot);
            _session = new DragonsleepSession(_provider, rolls: new DsFixedRoll(0.999),
                ownerDispatcher: new DsOwnerThreadDispatcher(_queue));
            _session.RestoreState(snapshot);
            Watch(_session);
            var expected = new DsPlanRun(old, "归巢", true);
            var run = new DsPlanRun(_session, "归巢", true);
            expected.ProposeAsync().GetAwaiter().GetResult();
            _task = Task.Run(() => run.ProposeAsync());
            yield return Until(() => _provider.Calls.Count == 1, true);
            _provider.Release(_provider.Calls.Single());
            yield return Until(() => _task.IsCompleted, true);
            Assert.IsFalse(_task.IsFaulted);
            expected.Accept();
            run.Accept();

            for (int i = 0; i < 3; i++)
            {
                expected.StepAsync().GetAwaiter().GetResult();
                _task = Task.Run(() => run.StepAsync());
                int replies = 0;
                while (!_task.IsCompleted)
                {
                    yield return Until(() => _task.IsCompleted || _provider.Calls.Any(c => !c.Reply.Task.IsCompleted), true);
                    if (_task.IsCompleted)
                    {
                        break;
                    }

                    var c = _provider.Calls.Single(x => !x.Reply.Task.IsCompleted);
                    Assert.Less(++replies, 9);
                    Assert.IsTrue(c.Packet.Kind == AiTaskKind.AgentPlan || c.Packet.Kind == AiTaskKind.Direct);
                    string before = _session.CaptureState().Json;
                    _provider.Release(c);
                    yield return Until(() => Volatile.Read(ref c.Exited) != 0 && _queue.PendingCount > 0);
                    Assert.AreEqual(before, _session.CaptureState().Json);
                    Assert.AreEqual(i, run.Results.Count);
                    Assert.AreNotEqual(_main, c.Worker);
                }
                Assert.IsFalse(_task.IsFaulted);
                Assert.AreEqual(i + 1, run.NextIndex);
                Assert.AreEqual(Record(old, expected), Record(_session, run));
            }

            Assert.IsTrue(_session.World.Whelp.WithMother && _session.World.FinalDawn);
            Assert.AreEqual("completed", run.Status);
            Assert.AreEqual(8, _provider.Calls.Count);
            Assert.AreEqual(0, _queue.PendingCount);
            CollectionAssert.Contains(_events, "rule:phase_start");
            MainOnly();
        }

        [UnityTest]
        public IEnumerator Session规划已结束但Adopt确已排队时卸载_关闭任务不采用不补写()
        {
            _game.enabled = false;
            var run = new DsPlanRun(_session, "归巢", true);
            string before = _session.CaptureState().Json;
            _task = Task.Run(() => run.ProposeAsync());
            yield return HoldAdoption(run);
            Assert.IsFalse(_task.IsCompleted);
            int notices = _events.Count;
            var source = _game.gameObject.scene;
            SceneManager.SetActiveScene(SceneManager.CreateScene("PlanOwnerUnload_" + Guid.NewGuid().ToString("N")));
            yield return SceneManager.UnloadSceneAsync(source);
            yield return Until(() => _task.IsCompleted && !_session.Busy && _provider.Drained);
            Assert.IsTrue(_task.IsCanceled || (_task.IsFaulted && _task.Exception.Flatten().InnerExceptions.All(
                e => e is ObjectDisposedException || e is OperationCanceledException)));
            Assert.IsTrue(_game == null);
            Assert.IsNull(run.LatestPlanTrace);
            Assert.IsEmpty(run.Results);
            Assert.IsEmpty(run.PlanTraces);
            Assert.AreEqual(before, _session.CaptureState().Json);
            Assert.AreEqual(notices, _events.Count);
            Assert.AreEqual(0, _queue.PendingCount);
            MainOnly();
        }
    }
}
