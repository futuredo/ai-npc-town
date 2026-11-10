using System;
using System.Collections;
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
using PieGameReceipt = PieGame.AI.Kernel.Receipt;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Town.Tests.PlayMode
{
    /// <summary>真实主场景的首条 SDK 接缝；默认离线，不代表其它 Core 事务已迁移。</summary>
    public sealed class DragonsleepKernelSceneTests
    {
        DragonsleepGame _game;
        Func<IAiProvider> _factory;
        bool _reduced;
        GateRules _gate;
        readonly List<GameObject> _extras = new List<GameObject>();

        PieGameReceipt Receipt => _game.SceneTransaction.LastReceipt;

        sealed class Intercept : IDsSceneRuleTransaction
        {
            public Func<string, DsWorldSnapshot, DsWorldSnapshot, DsSceneRuleWrite, string> Next;
            public int Calls;
            public int Writes;

            public string Commit(string id, DsWorldSnapshot before, DsWorldSnapshot after, DsSceneRuleWrite write)
            {
                Calls++;
                try
                {
                    return Next(id, before, after, write);
                }
                finally
                {
                    if (write.Applied) Writes++;
                }
            }
        }

        sealed class GateRules : IAiProvider
        {
            public string Name => "离线规则等待夹具";
            public bool IsSimulated => true;
            public readonly TaskCompletionSource<ProviderResult> Pending =
                new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public ProviderResult Reply; // 仅本地规则或测试提案；不访问任何真实模型。

            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                Reply = new RuleProvider().CompleteAsync(packet, ct).GetAwaiter().GetResult();
                return Pending.Task;
            }

            public void Release() => Pending.TrySetResult(Reply);
        }

        [UnitySetUp]
        public IEnumerator SetupLoad()
        {
            _factory = DragonsleepGame.ProviderFactory;
            _reduced = DsUiMotion.Reduced;
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

            var button = _game.GetComponent<UIDocument>().rootVisualElement.Q<Button>("beginJourney");
            Assert.IsNotNull(button, "必须经过开场入口");
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }

            yield return null;
            Assert.IsInstanceOf<RuleProvider>(_game.Session.Provider, "默认只能使用离线规则");
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            try
            {
                _gate?.Release();
                if (_game != null) yield return Until(() => !_game.Busy && !_game.Session.Busy);
            }
            finally
            {
                foreach (var go in _extras)
                {
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                }

                _extras.Clear();
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
                Assert.Less(Time.realtimeSinceStartup, end, "操作必须在有界时间内收尾");
                yield return null;
            }
        }

        DsSceneRuleTransaction Backend(DragonsleepSession session)
        {
            var go = new GameObject("离线独立场景事务");
            _extras.Add(go);
            var bridge = go.AddComponent<UnityKernelBridge>();
            return new DsSceneRuleTransaction(session, bridge);
        }

        [UnityTest]
        public IEnumerator 真实旅行经SDK只提交一次且保留世界身份和时间()
        {
            var world = _game.Session.World;
            int version = world.Version;
            int commits = _game.SceneTransaction.Commits;
            var before = _game.Session.CaptureState();

            _game.TravelTo("workshop");
            yield return null;
            yield return null;

            Assert.AreEqual("workshop", world.PlayerPlace, "真实旅行必须由 Update 提交");
            Assert.Greater(_game.SceneTransaction.Commits, 0, "真实路径必须调用 SDK");
            Assert.AreEqual(commits + 1, _game.SceneTransaction.Commits, "场景只提交一次，不能双写");
            Assert.AreEqual("committed", Receipt.Code, "必须得到 SDK 已提交回执");
            Assert.AreEqual(_game.Session.CaptureState().Hash, Receipt.AfterDigest, "回执摘要必须等于真实会话");
            Assert.AreSame(world, _game.Session.World, "不能替换权威世界引用");
            Assert.AreEqual(version + 1, world.Version, "仅增长一个世界版本");
            Assert.AreEqual(before.Spent, _game.Session.CaptureState().Spent, "旅行不耗时");
        }

        [UnityTest]
        public IEnumerator 同快照旧路径与独立SDK路径逐字一致_覆盖抓捕惊逃换岗和普通移动()
        {
            var initial = _game.Session.CaptureState();
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var legacy = new DragonsleepSession(new RuleProvider());
                legacy.RestoreState(initial);
                var w = legacy.World;
                w.Seed = 7;
                w.RollCounter = 0;
                w.Whelp.Place = w.PlayerPlace;
                w.Whelp.Following = mode != 3;
                w.Whelp.Hidden = false;
                w.Whelp.Fear = mode == 1 ? 0.8f : 0.1f;
                w.Phase = mode == 2 ? DayPhase.Dusk : DayPhase.Noon;
                w.Actors["osric"].Place = mode == 1 ? "castle" : "catacombs";

                var migrated = new DragonsleepSession(new RuleProvider());
                migrated.RestoreState(legacy.CaptureState());
                var backend = Backend(migrated);
                string target = mode == 3 ? "workshop" : "castle";
                var oldEvent = legacy.EnterPlace(target);
                var newEvent = migrated.EnterPlace(target, backend);

                Assert.AreEqual(legacy.CaptureState().Json, migrated.CaptureState().Json, "新旧路径完整快照必须逐字一致：" + mode);
                Assert.AreEqual(MiniJson.Write(oldEvent.ToRecord()), MiniJson.Write(newEvent.ToRecord()), "规则事件必须逐字一致：" + mode);
                Assert.AreEqual(1, backend.Commits, "含局部结果和抓捕也只提交一次");
                if (mode == 0) Assert.IsTrue(newEvent.WhelpCaptured, "固定种子必须实际触发巡逻抓捕");
                if (mode == 1) Assert.AreEqual(VerdictKind.Partial, newEvent.Verdict.Kind, "恐惧必须实际触发局部结果");
                if (mode == 2)
                {
                    Assert.AreEqual(0, migrated.World.RollCounter, "黄昏岗哨关闭时不能掷骰");
                    Assert.IsNull(migrated.World.Whelp.CapturedBy, "关闭岗哨后不能抓捕");
                }

                if (mode == 3)
                {
                    Assert.AreEqual(initial.Restore().PlayerPlace, migrated.World.Whelp.Place, "普通独行不移动雏龙");
                }
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator 旧执行器前置拒绝与无变化不调用SDK_同地点直接返回空()
        {
            var s = _game.Session;
            string before = s.CaptureState().Json;
            var receipt = Receipt;
            var spy = new Intercept { Next = _game.SceneTransaction.Commit };

            Assert.AreEqual("unknown_entity", s.EnterPlace("missing-place", spy).Receipt.Status, "未知地点须先由旧执行器拒绝");
            Assert.IsNull(s.EnterPlace(s.World.PlayerPlace, spy), "原地移动应返回空");

            var executor = new DsExecutor();
            int spent = 0;
            var proposal = new DsPlanStep { Verb = "rescue", Target = "whelp" }.ToProposal(s.World.PlayerPlace);
            var rejected = executor.Prepare(s.World, 0, s.PhaseBudget, AiTaskKind.Adjudicate, "player", "前置拒绝", proposal);
            Assert.AreEqual("no_change", executor.Commit(rejected, s.World, ref spent, s.PhaseBudget, spy).Status,
                "没有被关押的雏龙不能救出，也不能写 SDK");

            var plan = executor.PrepareRule(s.World, 0, s.PhaseBudget, "不变", "不变", (w, t) => { });
            Assert.AreEqual("no_change", executor.Commit(plan, s.World, ref spent, s.PhaseBudget, spy).Status, "无变化不能触达 SDK");
            Assert.AreEqual("duplicate", executor.Commit(plan, s.World, ref spent, s.PhaseBudget, spy).Status, "旧执行器仍负责去重");

            var foreign = executor.PrepareRule(s.World, 0, s.PhaseBudget, "非移动", "phase_start", (w, t) => w.Coins++);
            Assert.Throws<InvalidOperationException>(
                () => executor.Commit(foreign, s.World, ref spent, s.PhaseBudget, spy), "非 enter 事务不能借用场景适配器");
            Assert.AreEqual(before, s.CaptureState().Json, "拒绝与不变均不得写世界");
            Assert.AreEqual(0, spy.Calls, "旧执行器须在调用 SDK 之前拦截");
            Assert.AreSame(receipt, Receipt, "拒绝不得产生 SDK 回执");
            yield return null;
        }

        [UnityTest]
        public IEnumerator 真实会话事务的错误线程与锁内过期快照均不执行宿主写入()
        {
            var s = _game.Session;
            var backend = _game.SceneTransaction;
            var before = s.CaptureState();
            int commits = backend.Commits;
            var spy = new Intercept { Next = backend.Commit };

            var worker = Task.Run(() =>
            {
                try
                {
                    s.EnterPlace("workshop", spy);
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            });
            yield return Until(() => worker.IsCompleted);
            var error = worker.GetAwaiter().GetResult();
            Assert.IsInstanceOf<InvalidOperationException>(error, "必须同步拒绝工作线程");
            Assert.AreEqual("scene_transaction_wrong_thread", error.Message, "线程检查应先于实况访问");
            Assert.AreEqual(before.Json, s.CaptureState().Json, "错误线程不能改实况");

            string current = null;
            spy.Next = (id, b, a, write) =>
            {
                Assert.IsTrue(s.Busy, "拦截时必须仍持有会话锁");
                s.World.Coins++;
                current = s.CaptureState().Json;
                return backend.Commit(id, b, a, write);
            };
            Assert.AreEqual("stale_state", s.EnterPlace("workshop", spy).Receipt.Status, "候选产生后实况改变必须拒绝");
            Assert.AreEqual(2, spy.Calls, "两次都必须来自真实执行器事务");
            Assert.AreEqual(0, spy.Writes, "拒绝不得执行宿主写入能力");
            Assert.AreEqual(current, s.CaptureState().Json, "过期拒绝不得覆盖新状态");
            Assert.AreEqual(commits, backend.Commits, "两次失败均不得记为提交");
        }

        [UnityTest]
        public IEnumerator 重启清空SDK记录并经新局事务复用旧ID_旧世界不再写入()
        {
            _game.TravelTo("workshop");
            yield return null;
            yield return null;
            string id = Receipt.RequestId;
            var old = _game.Session.World;
            var oldState = _game.Session.CaptureState();

            _game.Restart();
            Assert.AreEqual(0, _game.SceneTransaction.Commits, "重启清空提交计数");
            Assert.IsNull(Receipt, "重启清空旧回执");
            Assert.AreNotSame(old, _game.Session.World, "重启建立新世界");
            string fresh = _game.Session.CaptureState().Json;
            yield return null;
            yield return null;
            Assert.AreEqual(fresh, _game.Session.CaptureState().Json, "场景更新不得恢复旧局位置");
            Assert.AreEqual("market", _game.Session.World.PlayerPlace, "场景位置也须回到新局集市");

            var reuse = new Intercept { Next = (request, b, a, write) => _game.SceneTransaction.Commit(id, b, a, write) };
            Assert.AreEqual("committed", _game.Session.EnterPlace("workshop", reuse).Receipt.Status, "新局真实事务允许复用旧 SDK 请求 ID");
            Assert.AreEqual(1, reuse.Writes, "复用旧 ID 也只能写一次");
            Assert.AreEqual(id, Receipt.RequestId, "SDK 必须实际接收复用标识");
            _game.TravelTo("workshop");
            yield return null;

            _game.TravelTo("docks");
            yield return null;
            yield return null;
            Assert.AreEqual("docks", _game.Session.World.PlayerPlace, "新局真实旅行仍工作");
            Assert.AreEqual(oldState.Json, DsWorldSnapshot.Capture(old, oldState.Spent, oldState.PhaseBudget).Json,
                "旧世界不再接收任何写入");
            Assert.AreEqual(_game.Session.CaptureState().Hash, Receipt.AfterDigest, "新回执必须绑定新局实况");
        }

        [UnityTest]
        public IEnumerator 计划Core移动尚未迁移_其后普通玩家旅行仍走SDK()
        {
            var w = _game.Session.World;
            w.Whelp.Place = w.PlayerPlace;
            w.Whelp.Following = true;
            w.Whelp.Hidden = true;
            w.Whelp.Fear = 0.4f;
            w.Whelp.TrustPlayer = 0.6f;
            w.Flags["whelp_hidden_by_player"] = w.ClockText;

            _gate = new GateRules();
            _game.Session.Provider = _gate;
            var propose = _game.ProposePlanAsync("把小烬送回母亲身边");
            var plan = new DsPlan { Goal = "归巢", Summary = "离线单步移动边界" };
            plan.Steps.Add(new DsPlanStep
            {
                Id = "core",
                Op = "go",
                Place = "workshop",
                Why = "仅验证迁移边界"
            });
            plan.Steps.Add(new DsPlanStep
            {
                Id = "home",
                Op = "wait",
                Place = "skull_hill",
                Until = "第4天·清晨",
                Why = "只验证首步，不执行等待"
            });
            _gate.Pending.TrySetResult(new ProviderResult
            {
                ProviderName = _gate.Name,
                Simulated = true,
                RawText = MiniJson.Write(plan.ToRecord())
            });
            yield return Until(() => propose.IsCompleted);
            propose.GetAwaiter().GetResult();
            Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status, "离线提案必须经过正式校验");

            _game.AcceptPlan(new Dictionary<string, string>());
            int commits = _game.SceneTransaction.Commits;
            var step = _game.ExecutePlanStepAsync();
            yield return Until(() => step.IsCompleted);
            step.GetAwaiter().GetResult();
            Assert.AreEqual("success", _game.ActivePlan.Results.Single().Status, "计划 Core 步骤应实际执行");
            Assert.AreEqual("workshop", _game.Session.World.PlayerPlace, "Core 已移动世界");
            Assert.AreEqual(commits, _game.SceneTransaction.Commits, "不能声称计划内部移动已迁移 SDK");

            _game.TravelTo("docks");
            yield return null;
            yield return null;
            Assert.AreEqual("docks", _game.Session.World.PlayerPlace, "Core 步骤后玩家旅行仍正常");
            Assert.AreEqual(commits + 1, _game.SceneTransaction.Commits, "玩家旅行仍经 SDK");
        }

        [UnityTest]
        public IEnumerator 仅会话忙于离线TCS时旅行不写SDK_释放后可继续()
        {
            _gate = new GateRules();
            _game.Session.Provider = _gate;
            var task = _game.Session.PlanAsync("归巢");
            Assert.IsTrue(_game.Session.Busy, "TCS 未释放时会话必须持锁");
            Assert.IsFalse(_game.Busy, "须隔离仅 Session 忙的边界");

            string before = _game.Session.CaptureState().Json;
            int commits = _game.SceneTransaction.Commits;
            var playerPosition = _game.Player.transform.position;
            _game.Restart();
            _game.TravelTo("workshop");
            yield return null;
            yield return null;
            Assert.AreEqual(before, _game.Session.CaptureState().Json, "等待期间不能重启或写世界");
            Assert.AreEqual(playerPosition, _game.Player.transform.position, "会话忙时也不能先移动实体，稍后偷偷补交移动");
            Assert.AreEqual(commits, _game.SceneTransaction.Commits, "等待期间不能写 SDK");
            Assert.IsNull(_game.Session.EnterPlace("docks", _game.SceneTransaction), "会话锁也必须直接拒绝插队");

            _gate.Release();
            yield return Until(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            yield return null;
            yield return null;
            Assert.IsFalse(_game.Session.Busy, "释放后必须解锁");

            _game.TravelTo("docks");
            yield return null;
            yield return null;
            Assert.AreEqual("docks", _game.Session.World.PlayerPlace, "释放后旅行仍工作");
            Assert.Greater(_game.SceneTransaction.Commits, commits, "释放后应能提交 SDK");
        }
    }
}
