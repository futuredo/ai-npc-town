using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>真实场景的第二条 SDK 接缝：只覆盖日间推进，跨夜仍由原会话执行。</summary>
    public sealed class DragonsleepKernelDayPhaseTests
    {
        DragonsleepGame _game;
        VisualElement _root;
        Func<IAiProvider> _factory;
        bool _reduced;
        GateRules _gate;
        Task _pending;

        sealed class Intercept : IDsDayPhaseTransaction
        {
            public Func<string, DsWorldSnapshot, DsWorldSnapshot, DsDayPhaseWrite, string> Next;
            public int Calls;
            public int Writes;
            public DsDayPhaseWrite LastWrite;

            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsDayPhaseWrite write)
            {
                Calls++;
                LastWrite = write;
                try { return Next(id, before, after, write); }
                finally { if (write.Applied) Writes++; }
            }
        }

        sealed class GateRules : IAiProvider
        {
            public string Name => "离线日间边界等待夹具";
            public bool IsSimulated => true;
            public int Calls;
            public readonly TaskCompletionSource<ProviderResult> Pending =
                new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ProviderResult _reply;
            volatile bool _released;

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Calls++;
                var reply = new RuleProvider().CompleteAsync(packet, ct).GetAwaiter().GetResult();
                if (_released) return Task.FromResult(reply);
                _reply = reply;
                return Pending.Task;
            }

            public void Release()
            {
                _released = true;
                Pending.TrySetResult(_reply);
            }
        }

        [UnitySetUp]
        public IEnumerator SetupLoad()
        {
            _factory = DragonsleepGame.ProviderFactory;
            _reduced = DsUiMotion.Reduced;
            _pending = null;
            _gate = null;
            DragonsleepGame.ProviderFactory = () => new RuleProvider();
            DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null;
            yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>();
            Assert.IsNotNull(_game, "必须加载真实主场景");
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Click("beginJourney");
            yield return null;
            Assert.IsFalse(_game.Hud.ModalOpen, "必须经过开场启程按钮收起阅读层");
            Assert.IsInstanceOf<RuleProvider>(_game.Session.Provider, "测试不得访问真实模型");
            Assert.AreEqual(DayPhase.Dawn, _game.Session.World.Phase);
            Assert.AreEqual(0, _game.SceneTransaction.DayCommits);
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            try
            {
                _gate?.Release();
                if (_game != null)
                    yield return Until(() => !_game.Busy && !_game.Session.Busy && (_pending == null || _pending.IsCompleted));
                _pending?.GetAwaiter().GetResult();
            }
            finally
            {
                _pending = null;
                _gate = null;
                DragonsleepGame.ProviderFactory = _factory;
                DsUiMotion.Reduced = _reduced;
            }
        }

        static IEnumerator Until(Func<bool> done)
        {
            float end = Time.realtimeSinceStartup + 12;
            while (!done())
            {
                Assert.Less(Time.realtimeSinceStartup, end, "离线操作必须在有界时间内收尾");
                yield return null;
            }
        }

        void Click(string name)
        {
            var button = _root.Q<Button>(name);
            Assert.IsNotNull(button, "必须使用真实按钮：" + name);
            Assert.IsTrue(button.enabledInHierarchy, "按钮必须可用：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        IEnumerator EndFromMap()
        {
            Click("openMap");
            yield return null;
            Assert.IsTrue(_game.Hud.ModalOpen, "地图必须实际打开");
            Click("endPhase");
            Assert.IsFalse(_game.Hud.ModalOpen, "地图回调须先 CloseModal，再调用 Game.EndPhase");
            yield return null;
        }

        IEnumerator Observe()
        {
            _game.Submit(LoomMode.Act, "观察四周");
            yield return Until(() => !_game.Busy && !_game.Session.Busy);
            Assert.AreEqual(2, _game.Session.CaptureState().Spent, "必须通过真实行动先消耗两点时间");
            Assert.AreEqual(DayPhase.Dawn, _game.Session.World.Phase, "行动不能提前进入正午");
            Assert.AreEqual("committed", _game.Session.Traces.Last().Receipt.Status);
        }

        [UnityTest]
        public IEnumerator 地图真实日间按钮与旧路径完整一致_时间原子归零且不双巡逻_重开清零()
        {
            yield return Observe();
            var s = _game.Session;
            var world = s.World;
            // 保证正午第一次巡逻未抓捕，若误跑第二次仍能从随机序号和完整快照发现。
            world.Seed = Enumerable.Range(1, 100).First(seed =>
                DsSeededRoll.Instance.Next(seed, world.RollCounter + 1) >= 0.6);
            world.Whelp.Place = world.PlayerPlace;
            world.Whelp.Following = true;
            world.Whelp.Hidden = false;
            world.Whelp.CapturedBy = null;
            int initialRoll = world.RollCounter;
            int initialVersion = world.Version;
            int commits = _game.SceneTransaction.Commits;
            var legacy = new DragonsleepSession(new RuleProvider());
            legacy.RestoreState(s.CaptureState());

            for (int i = 1; i <= 2; i++)
            {
                var before = s.CaptureState();
                int events = s.RuleEvents.Count;
                var oldAdvance = legacy.EndPhaseAsync();
                yield return Until(() => oldAdvance.IsCompleted);
                Assert.IsEmpty(oldAdvance.GetAwaiter().GetResult(), "旧日间路径不能调用夜间模型");

                yield return EndFromMap();
                var actual = s.CaptureState();
                Assert.AreEqual(i == 1 ? DayPhase.Noon : DayPhase.Dusk, world.Phase);
                Assert.AreEqual(4, s.TimeLeftInPhase, "世界换段与 spent 归零必须一并提交");
                Assert.AreEqual(0, actual.Spent);
                Assert.AreEqual(legacy.CaptureState().Json, actual.Json, "新旧完整世界与时间快照须逐字一致");
                Assert.AreEqual(legacy.World.Version, world.Version, "版本号必须保留旧路径语义");
                Assert.AreEqual(initialVersion + i + 1, world.Version, "正午巡逻增长一版，黄昏只推进一版");
                Assert.AreEqual(legacy.World.RollCounter, world.RollCounter);
                Assert.AreEqual(initialRoll + 1, world.RollCounter, "正午只巡逻一次，黄昏岗哨关闭不得再掷骰");
                Assert.IsNull(world.Whelp.CapturedBy, "固定夹具的首次巡逻必须未抓捕");
                Assert.AreSame(world, s.World, "提交不能替换权威世界本体");
                Assert.AreEqual(i, _game.SceneTransaction.DayCommits);
                Assert.AreEqual(commits + i, _game.SceneTransaction.Commits, "日间也计入总提交数且只能提交一次");
                Assert.AreEqual("committed", _game.SceneTransaction.LastReceipt.Code);
                Assert.AreEqual(actual.Hash, _game.SceneTransaction.LastReceipt.AfterDigest);
                Assert.AreEqual(events + 1, s.RuleEvents.Count, "每次只产生一个换岗事件");
                Assert.AreEqual(before.Hash, s.RuleEvents.Last().Receipt.BeforeHash);
                Assert.AreEqual(actual.Hash, s.RuleEvents.Last().Receipt.AfterHash);
                yield return null;
                yield return null;
                Assert.AreEqual(actual.Json, s.CaptureState().Json, "后续 Update 不能再推进或补跑巡逻");
                Assert.AreEqual(i, _game.SceneTransaction.DayCommits);
            }

            var oldState = s.CaptureState();
            _game.Restart();
            Assert.AreNotSame(world, s.World, "重开必须建立新世界");
            Assert.AreEqual(0, _game.SceneTransaction.DayCommits, "重开清空日间计数");
            Assert.AreEqual(0, _game.SceneTransaction.Commits, "重开也清空总计数");
            Assert.IsNull(_game.SceneTransaction.LastReceipt, "不能保留上一局回执");
            Assert.AreEqual(DayPhase.Dawn, s.World.Phase);
            Assert.AreEqual(4, s.TimeLeftInPhase);
            var fresh = s.CaptureState();
            yield return null;
            yield return null;
            Assert.AreEqual(fresh.Json, s.CaptureState().Json, "重开后场景更新不能补交旧局推进");
            yield return EndFromMap();
            Assert.AreEqual(DayPhase.Noon, s.World.Phase, "新局真实按钮仍可提交");
            Assert.AreEqual(1, _game.SceneTransaction.DayCommits);
            Assert.AreEqual(1, _game.SceneTransaction.Commits);
            Assert.AreEqual(oldState.Json, DsWorldSnapshot.Capture(world, oldState.Spent, oldState.PhaseBudget).Json,
                "新局提交不能写回旧世界");
        }

        [UnityTest]
        public IEnumerator 会话日间事务错误线程与锁内过期均不执行写入_剩余时间保持不变()
        {
            yield return Observe();
            var s = _game.Session;
            var backend = _game.SceneTransaction;
            var world = s.World;
            var before = s.CaptureState();
            int commits = backend.Commits;
            var spy = new Intercept { Next = backend.Commit };
            var worker = Task.Run(() =>
            {
                try { s.EndDayPhase(spy); return null; }
                catch (Exception e) { return e; }
            });
            _pending = worker;
            yield return Until(() => worker.IsCompleted);
            var error = worker.GetAwaiter().GetResult();
            Assert.IsInstanceOf<InvalidOperationException>(error);
            Assert.AreEqual("scene_transaction_wrong_thread", error.Message);
            Assert.AreEqual(before.Json, s.CaptureState().Json, "错误线程不得写世界或清空 spent");
            Assert.AreEqual(2, s.TimeLeftInPhase);
            Assert.Throws<InvalidOperationException>(() => spy.LastWrite.Apply(), "返回后写入能力必须失效");

            string current = null;
            spy.Next = (id, b, a, write) =>
            {
                Assert.IsTrue(s.Busy, "候选与后端调用仍在同一 Session 锁内");
                Assert.AreEqual(2, b.Spent);
                Assert.AreEqual(0, a.Spent);
                Assert.AreEqual(2, s.CaptureState().Spent, "候选计算不能预先清空真实时间");
                s.World.Coins++;
                current = s.CaptureState().Json;
                return backend.Commit(id, b, a, write);
            };
            var rejected = s.EndDayPhase(spy);
            Assert.IsNotNull(rejected);
            Assert.AreEqual("stale_state", rejected.Receipt.Status);
            Assert.IsFalse(rejected.Receipt.Applied);
            Assert.AreEqual(2, spy.Calls, "两次调用均须来自真实 Session 日间入口");
            Assert.AreEqual(0, spy.Writes, "两类失败均不能执行 DsDayPhaseWrite.Apply");
            Assert.AreEqual(current, s.CaptureState().Json, "过期候选不得覆盖并发的新状态");
            Assert.AreEqual(DayPhase.Dawn, world.Phase);
            Assert.AreEqual(before.Restore().Version, world.Version);
            Assert.AreEqual(before.Restore().RollCounter, world.RollCounter);
            Assert.AreEqual(2, s.TimeLeftInPhase, "拒绝不能损失原时段剩余时间");
            Assert.AreSame(world, s.World);
            Assert.AreEqual(commits, backend.Commits);
            Assert.AreEqual(0, backend.DayCommits);
            Assert.IsEmpty(s.RuleEvents, "未提交的日间候选不能成为已执行规则事件");
            Assert.IsFalse(s.Busy, "失败必须释放会话锁");
            Assert.Throws<InvalidOperationException>(() => spy.LastWrite.Apply());
        }

        [UnityTest]
        public IEnumerator 仅会话忙与待确认图样及终局均前置拒绝_不调用日间SDK也不改时间()
        {
            yield return Observe();
            var s = _game.Session;
            var spy = new Intercept { Next = _game.SceneTransaction.Commit };
            _gate = new GateRules();
            s.Provider = _gate;
            _pending = s.PlanAsync("归巢");
            Assert.IsTrue(s.Busy);
            Assert.IsFalse(_game.Busy, "必须覆盖仅 Session 持锁、Game 未忙的窗口");
            var before = s.CaptureState();
            yield return EndFromMap();
            _game.Restart();
            Assert.IsNull(s.EndDayPhase(spy));
            Assert.AreEqual(before.Json, s.CaptureState().Json);
            Assert.AreEqual(0, _game.SceneTransaction.Commits);
            Assert.AreEqual(0, _game.SceneTransaction.DayCommits);
            Assert.AreEqual(1, _gate.Calls, "拒绝不得发起额外离线提供方调用");

            _gate.Release();
            yield return Until(() => _pending.IsCompleted && !s.Busy);
            _pending.GetAwaiter().GetResult();
            s.Provider = new RuleProvider();
            _game.TravelTo("workshop");
            yield return null;
            yield return null;
            Assert.AreEqual("workshop", s.World.PlayerPlace);
            _pending = s.CraftAsync("用蓝晶石织一盏灯");
            yield return Until(() => _pending.IsCompleted && !s.Busy);
            _pending.GetAwaiter().GetResult();
            Assert.IsTrue(s.HasPendingCraft, "必须创建真实、尚未确认的离线图样");
            Assert.IsFalse(_game.Hud.ModalOpen, "直接会话图样夹具用于隔离 pendingCraft 前置条件");
            before = s.CaptureState();
            int commits = _game.SceneTransaction.Commits;
            yield return EndFromMap();
            Assert.IsNull(s.EndDayPhase(spy));
            Assert.IsTrue(s.HasPendingCraft, "推进拒绝不得擅自确认或取消图样");
            Assert.AreEqual(before.Json, s.CaptureState().Json);
            Assert.AreEqual(2, s.TimeLeftInPhase);
            Assert.AreEqual(commits, _game.SceneTransaction.Commits);
            Assert.AreEqual(0, _game.SceneTransaction.DayCommits);

            s.ConfirmCraft(false);
            s.World.Day = 4;
            before = s.CaptureState();
            _game.EndPhase();
            Assert.IsNull(s.EndDayPhase(spy));
            Assert.AreEqual(before.Json, s.CaptureState().Json, "终局清晨不可再次推进");
            Assert.AreEqual(0, spy.Calls, "忙、图样、终局都须在适配器之前拒绝");
            Assert.AreEqual(0, spy.Writes);
            Assert.AreEqual(commits, _game.SceneTransaction.Commits);
            Assert.AreEqual(0, _game.SceneTransaction.DayCommits);
        }

        [UnityTest]
        public IEnumerator 黄昏真实按钮仍走原跨夜_离线TCS等待期间重开与推进不能插队()
        {
            yield return EndFromMap();
            yield return EndFromMap();
            var s = _game.Session;
            var world = s.World;
            Assert.AreEqual(DayPhase.Dusk, world.Phase);
            Assert.AreEqual(2, _game.SceneTransaction.DayCommits);
            int commits = _game.SceneTransaction.Commits;
            var receipt = _game.SceneTransaction.LastReceipt;
            int day = world.Day;
            _gate = new GateRules();
            s.Provider = _gate;
            yield return EndFromMap();
            Assert.IsTrue(_game.Busy, "真实按钮必须等待旧 RunNight 路径");
            Assert.IsTrue(s.Busy, "居民模型等待期间会话锁必须保持");
            Assert.AreEqual(DayPhase.Night, world.Phase);
            Assert.AreEqual(1, _gate.Calls);
            Assert.AreEqual(AiTaskKind.AgentPlan, s.Traces.Single().Kind, "必须实际进入首名居民的夜间调用");
            var paused = s.CaptureState();
            int events = s.RuleEvents.Count;
            var spy = new Intercept { Next = _game.SceneTransaction.Commit };
            _game.Restart();
            _game.EndPhase();
            Assert.IsNull(s.EndDayPhase(spy));
            yield return null;
            yield return null;
            Assert.AreSame(world, s.World, "跨夜等待时不能插入重开");
            Assert.AreEqual(paused.Json, s.CaptureState().Json, "等待时不能重复推进或清空时间");
            Assert.AreEqual(events, s.RuleEvents.Count, "不得排入第二次跨夜换岗");
            Assert.AreEqual(1, _gate.Calls, "不得排入第二轮居民请求");
            Assert.AreEqual(0, spy.Calls);
            Assert.AreEqual(commits, _game.SceneTransaction.Commits, "夜间不得借用 SDK 日间或移动提交");
            Assert.AreEqual(2, _game.SceneTransaction.DayCommits);
            Assert.AreSame(receipt, _game.SceneTransaction.LastReceipt);

            _gate.Release();
            yield return Until(() => !_game.Busy && !s.Busy);
            Assert.AreSame(world, s.World);
            Assert.AreEqual(day + 1, world.Day, "原跨夜必须完整收尾到次日");
            Assert.AreEqual(DayPhase.Dawn, world.Phase);
            Assert.AreEqual(s.World.Actors.Count + 1, _gate.Calls, "仍由原路径逐名居民加一次 GM");
            Assert.AreEqual(s.World.Actors.Count + 1, s.Traces.Count);
            Assert.IsTrue(s.Traces.All(t => t.Provider != null && t.Provider.Simulated), "全部跨夜调用仅使用离线规则");
            Assert.AreEqual(commits, _game.SceneTransaction.Commits, "释放后跨夜仍不计入 SDK");
            Assert.AreEqual(2, _game.SceneTransaction.DayCommits);
            Assert.AreSame(receipt, _game.SceneTransaction.LastReceipt);
        }
    }
}
