using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Kernel;
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
    /// <summary>真实主场景的行动接缝；仅规则/TCS 离线回包，不使用取消模拟旧局晚回。</summary>
    public sealed class DragonsleepActionKernelPresentationTests
    {
        DragonsleepGame _game; VisualElement _root;
        Func<IAiProvider> _factory; bool _motion, _playerEnabled;
        Task _pending; GateRules _gate;
        bool Idle => !_game.Busy && !_game.Session.Busy && _root.Q<Button>("sendButton").enabledInHierarchy;
        sealed class GateRules : IAiProvider
        {
            public string Name => "offline-action-kernel-tcs"; public bool IsSimulated => true;
            public int Calls, Worker; public Task WorkerTask;
            ProviderResult _value; int _released;
            readonly TaskCompletionSource<ProviderResult> _reply = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                var value = _value = new RuleProvider().CompleteAsync(packet, ct).GetAwaiter().GetResult();
                if (Interlocked.Increment(ref Calls) != 1 || Volatile.Read(ref _released) != 0) return Task.FromResult(value);
                return _reply.Task;
            }
            public void Release()
            {
                Interlocked.Exchange(ref _released, 1);
                WorkerTask ??= Task.Run(() => { Worker = Thread.CurrentThread.ManagedThreadId; _reply.TrySetResult(_value); });
            }
        }
        static IEnumerator Until(Func<bool> done)
        {
            float end = Time.realtimeSinceStartup + 12;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsTrue(done(), "真实离线条件须在 12 秒内完成，不能用固定帧数或 Sleep 代替证据");
        }
        IEnumerator Finish(Task task) { _pending = task; yield return Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void Click(string name)
        {
            var b = _root.Q<Button>(name);
            Assert.IsTrue(b != null && b.enabledInHierarchy && b.worldBound.width > 0, name);
            b.Focus(); using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }
        void SendAction() { Click("mode_Act"); _root.Q<TextField>("playerInput").value = "观察四周"; Click("sendButton"); }
        [UnitySetUp] public IEnumerator Load()
        {
            _pending = null; _gate = null; _game = null;
            _factory = DragonsleepGame.ProviderFactory; _motion = DsUiMotion.Reduced;
            DragonsleepGame.ProviderFactory = () => new RuleProvider(); DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>(); Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            _playerEnabled = _game.Player.enabled; _game.Player.enabled = false;
            yield return Until(() => _root.Q<Button>("beginJourney")?.worldBound.width > 0);
            Click("beginJourney"); yield return Until(() => !_game.Hud.ModalOpen && _root.Q<Button>("sendButton")?.worldBound.width > 0);
            Assert.IsInstanceOf<RuleProvider>(_game.Session.Provider);
        }
        [UnityTest] public IEnumerator 真实行动按钮仅提交一次且时间与完整旧回执一致()
        {
            var s = _game.Session; var before = s.CaptureState(); int commits = _game.ActionCommits;
            var legacy = new DragonsleepSession(new RuleProvider()); legacy.RestoreState(before);
            var expected = legacy.ActAsync("观察四周"); yield return Finish(expected);
            SendAction(); yield return Until(() => Idle && s.Traces.Count == 1);
            var actual = s.Traces.Single(); var receipt = _game.SceneTransaction.ActionKernel.LastReceipt;
            Assert.AreEqual("committed", actual.KernelCode); Assert.IsTrue(actual.Receipt.Applied);
            Assert.AreEqual(commits + 1, _game.ActionCommits); Assert.AreEqual("committed", receipt.Code);
            Assert.AreEqual(before.Hash, receipt.BeforeDigest); Assert.AreEqual(s.CaptureState().Hash, receipt.AfterDigest);
            Assert.AreEqual(s.CaptureState().Hash, _game.ActionKernelDigest);
            Assert.AreEqual(expected.Result.Receipt.TimeCost, actual.Receipt.TimeCost);
            Assert.AreEqual(MiniJson.Write(expected.Result.Receipt.ToRecord()), MiniJson.Write(actual.Receipt.ToRecord()));
            Assert.AreEqual(legacy.CaptureState().Json, s.CaptureState().Json);
        }
        [UnityTearDown] public IEnumerator Restore()
        {
            _gate?.Release();
            try { yield return Until(() => (_pending == null || _pending.IsCompleted)
                    && (_gate?.WorkerTask == null || _gate.WorkerTask.IsCompleted)
                    && (_game == null || (!_game.Busy && !_game.Session.Busy)));
            } finally {
                DragonsleepGame.ProviderFactory = _factory; DsUiMotion.Reduced = _motion;
                if (_pending?.IsFaulted == true) _ = _pending.Exception;
                if (_game != null) _game.Player.enabled = _playerEnabled;
            }
        }
        [UnityTest] public IEnumerator 离线worker有效晚回由真实RestartAPI隔离且不呈现旧局()
        {
            foreach (var phase in new[] { DayPhase.Dawn, DayPhase.Dusk })
            {
                var s = _game.Session; _gate = new GateRules(); s.Provider = _gate;
                s.World.Phase = phase; s.RestoreState(DsWorldSnapshot.Capture(s.World, 2, s.PhaseBudget));
                var world = s.World; var before = s.CaptureState(); var position = _game.Player.transform.position;
                int main = Thread.CurrentThread.ManagedThreadId;
                SendAction(); yield return Until(() => _gate.Calls == 1 && s.Busy && s.KernelActionInFlight);
                var trace = s.Traces.Single(); Assert.IsTrue(_game.Busy);
                Assert.IsNull(s.EnterPlace("workshop")); Assert.IsNull(s.EndDayPhase(_game.SceneTransaction));
                _game.TravelTo("workshop"); Assert.AreEqual(position, _game.Player.transform.position);
                Assert.AreEqual(before.Json, s.CaptureState().Json);
                yield return Until(() => _root.Q<Button>("restartPendingAction").worldBound.width > 0);
                Click("restartPendingAction");
                Assert.AreNotSame(world, s.World); Assert.AreEqual(0, _game.ActionCommits);
                Assert.IsNull(_game.SceneTransaction.ActionKernel.LastReceipt); Assert.IsTrue(_game.Busy && s.Busy);
                var fresh = s.CaptureState(); string line = _root.Q<Label>("dialogueLine").text;
                _game.Submit(LoomMode.Act, "观察四周"); Assert.AreEqual(1, _gate.Calls); Assert.IsEmpty(s.Traces);
                _gate.Release(); yield return Until(() => Idle && _gate.WorkerTask.IsCompleted);
                Assert.AreNotEqual(main, _gate.Worker); Assert.AreEqual("stale_session", trace.KernelCode);
                Assert.IsTrue(trace.Provider.Simulated); Assert.IsNull(trace.Provider.Error);
                Assert.AreEqual(fresh.Json, s.CaptureState().Json); Assert.AreEqual(fresh.Hash, _game.ActionKernelDigest);
                Assert.AreEqual(line, _root.Q<Label>("dialogueLine").text); Assert.IsEmpty(s.Traces);
                Assert.AreEqual(1, _gate.Calls, "晚回不能续跑 RunNight"); Assert.AreEqual(0, _game.ActionCommits);
            }
        }
        [UnityTest] public IEnumerator worker直接调用adapter在端口和参数校验前拒绝()
        {
            var adapter = _game.SceneTransaction.ActionKernel; string before = _game.Session.CaptureState().Json;
            var worker = Task.Run(() => new[] {
                Assert.Throws<InvalidOperationException>(() => adapter.Begin("worker", "unused", new FrozenPayload("offline", new byte[] { 1 }))),
                Assert.Throws<InvalidOperationException>(() => adapter.Commit(null, null)) });
            yield return Finish(worker);
            Assert.AreEqual("bridge_wrong_thread", worker.Result[0].Message); Assert.AreEqual("kernel_port_wrong_thread", worker.Result[1].Message);
            Assert.AreEqual(before, _game.Session.CaptureState().Json); Assert.AreEqual(0, _game.ActionCommits); Assert.IsNull(adapter.LastReceipt);
        }
        [UnityTest] public IEnumerator 非行动入口和headless不注入行动内核()
        {
            var s = _game.Session; int commits = _game.ActionCommits;
            yield return Finish(s.PlanAsync("把小烬送回母亲身边"));
            yield return Finish(s.RunWithProviderAsync(AiTaskKind.Adjudicate, "player", "观察四周", new RuleProvider()));
            yield return Finish(s.RunStepAsync(new DsPlanStep { Op = "act", Verb = "observe" }.ToProposal(s.World.PlayerPlace), "计划观察"));
            Assert.IsTrue(s.Traces.All(t => t.KernelCode == null && !t.ToRecord().ContainsKey("kernel_code")));
            Assert.AreEqual(commits, _game.ActionCommits);
            var headless = new DragonsleepSession(new RuleProvider());
            yield return Finish(headless.ActAsync("观察四周"));
            Assert.IsTrue(headless.Traces.Single().Receipt.Applied); Assert.IsFalse(headless.Traces.Single().ToRecord().ContainsKey("kernel_code"));
        }
        [UnityTest] public IEnumerator 空闲Restore同JSON重置内核后新行动有效()
        {
            var s = _game.Session; SendAction(); yield return Until(() => Idle && _game.ActionCommits == 1);
            var id = s.Traces.Single().RequestId; var same = s.CaptureState(); var world = s.World;
            s.RestoreState(same); Assert.AreNotSame(world, s.World); Assert.AreEqual(same.Json, s.CaptureState().Json);
            Assert.AreEqual(0, _game.ActionCommits); Assert.IsNull(_game.SceneTransaction.ActionKernel.LastReceipt);
            SendAction(); yield return Until(() => Idle && s.Traces.Count == 1);
            Assert.AreEqual(id, s.Traces.Single().RequestId); Assert.AreEqual("committed", s.Traces.Single().KernelCode);
            Assert.AreEqual(1, _game.ActionCommits); Assert.AreEqual(s.CaptureState().Hash, _game.ActionKernelDigest);
        }
    }
}
