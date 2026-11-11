using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
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
    public sealed class DragonsleepCraftConfirmationPresentationTests
    {
        const string Goal = "用蓝晶石织一盏灯";
        DragonsleepGame _game;
        VisualElement _root;
        (Func<IAiProvider> Factory, bool Motion) _previous;
        readonly List<(AiTrace Trace, int Thread)> _notices = new List<(AiTrace, int)>();
        DragonsleepSession S => _game.Session;
        [UnitySetUp]
        public IEnumerator Load()
        {
            _previous = (DragonsleepGame.ProviderFactory, DsUiMotion.Reduced);
            DragonsleepGame.ProviderFactory = () => new RuleProvider();
            DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            _notices.Clear();
            S.TraceCompleted += t => _notices.Add((t, Thread.CurrentThread.ManagedThreadId));
            yield return Until(() => _root.Q<Button>("beginJourney")?.worldBound.width > 0);
            Click("beginJourney");
            _game.TravelTo("workshop");
            yield return Until(() => S.World.PlayerPlace == "workshop" && !_game.Hud.ModalOpen);
            Assert.IsInstanceOf<RuleProvider>(S.Provider);
            Click("mode_Craft");
        }
        [UnityTearDown]
        public IEnumerator Restore()
        {
            try { if (_game != null) yield return Idle(); }
            finally
            {
                DragonsleepGame.ProviderFactory = _previous.Factory;
                DsUiMotion.Reduced = _previous.Motion;
            }
        }
        IEnumerator Until(Func<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (!done() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(done(), "离线任务和 Busy 必须有界收尾");
            yield return null;
        }
        IEnumerator Idle() => Until(() => !_game.Busy && !S.Busy);
        void Click(string name) => Submit(_root.Q<Button>(name));
        static void Submit(Button button)
        {
            Assert.IsNotNull(button);
            button.Focus();
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }
        IEnumerator Propose()
        {
            _root.Q<TextField>("playerInput").value = Goal;
            Click("sendButton");
            yield return Idle();
        }
        void Completed(AiTrace trace) => CollectionAssert.AreEqual(Enumerable.Repeat(Thread.CurrentThread.ManagedThreadId, 2),
            _notices.Where(n => ReferenceEquals(n.Trace, trace)).Select(n => n.Thread), "同一 trace 两次 owner 通知");
        [UnityTest]
        public IEnumerator 旧按钮和句柄不决定或关闭新卡_新确认只耗一次() => Verify("declined");
        [UnityTest]
        public IEnumerator 严格会话拒无句柄_两种造物入口不覆盖原案_展示冻结描述() => Verify("strict");
        [UnityTest]
        public IEnumerator 原请求准备后取消_真实确认零提交_新案不受旧句柄影响() => Verify("cancelled");
        IEnumerator Verify(string decision)
        {
            string before = S.CaptureState().Hash;
            using var cancellation = new CancellationTokenSource();
            if (decision == "cancelled")
            {
                var preparation = S.CraftAsync(Goal, cancellation.Token);
                yield return Until(() => preparation.IsCompleted && !S.Busy);
                _game.Hud.ShowCraftProposal(S.GetCraftProposal(preparation.GetAwaiter().GetResult()));
                cancellation.Cancel();
                yield return Until(() => _root.Q<Button>("confirmCraft")?.worldBound.width > 0);
            }
            else yield return Propose();
            var trace = S.Traces.Last();
            var a = S.GetCraftProposal(trace);
            Assert.IsNotNull(a);
            if (decision == "strict")
            {
                string description = a.Description;
                Assert.IsNull(S.ConfirmCraft(true));
                Assert.IsNull(S.ConfirmCraft(false));
                foreach (bool direct in new[] { false, true })
                {
                    var task = direct ? S.RunAsync(AiTaskKind.Craft, "player", Goal) : S.CraftAsync(Goal);
                    yield return Until(() => task.IsCompleted && !S.Busy);
                    var rejected = task.GetAwaiter().GetResult();
                    Assert.AreEqual("precondition", rejected.FailureCode);
                    Assert.IsNull(S.GetCraftProposal(rejected));
                }
                Assert.AreSame(a, S.GetCraftProposal(trace));
                Assert.AreEqual("pending", trace.CraftDecision);
                trace.Proposal["name"] = "被改写的公开记录";
                _game.Hud.ShowCraftProposal(a);
                CollectionAssert.Contains(_root.Q("craftProposal").Query<Label>().ToList().Select(l => l.text), description);
            }
            else
            {
                Button oldConfirm = _root.Q<Button>("confirmCraft"), oldDecline = _root.Q<Button>("declineCraft");
                Submit(decision == "cancelled" ? oldConfirm : oldDecline);
                yield return Idle();
                Assert.AreEqual(decision, trace.Receipt.Status);
                Assert.IsFalse(trace.Receipt.Applied);
                Assert.AreEqual(0, trace.Receipt.TimeCost);
                Assert.IsEmpty(trace.Receipt.Effects);
                Assert.AreEqual(before, S.CaptureState().Hash);
                yield return Propose();
                var b = _root.Q<Button>("confirmCraft");
                // 脱离 panel 的导航事件不保证触发 Clickable；句柄拒绝另验，不冒充旧闭包覆盖。
                Submit(oldConfirm);
                Submit(oldDecline);
                Assert.IsFalse(_game.DecideCraft(a, true));
                Assert.IsFalse(_game.DecideCraft(a, false));
                Assert.AreSame(b, _root.Q<Button>("confirmCraft"));
                Assert.AreSame(_root.panel, b.panel);
                Completed(trace);
            }
            Assert.AreEqual(before, S.CaptureState().Hash);
            Assert.IsTrue(_game.Hud.ModalOpen && S.HasPendingCraft);
            var finalTrace = decision == "strict" ? trace : S.Traces.Last();
            Click("confirmCraft");
            yield return Idle();
            Assert.IsFalse(S.HasPendingCraft);
            Assert.IsTrue(finalTrace.Receipt.Applied);
            Assert.AreEqual("consumed", S.World.Items["crystal_blue"].Holder);
            Assert.AreEqual(2, S.TimeLeftInPhase);
            Assert.AreEqual(1, S.World.Items.Values.Count(i => i.Crafted));
            Completed(finalTrace);
            Assert.AreEqual(decision == "strict" ? 1 : 2, S.Traces.Count(t => t.Provider != null));
        }
    }
}
