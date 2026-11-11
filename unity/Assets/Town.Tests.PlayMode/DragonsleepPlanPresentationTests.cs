using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>M4：真实主场景按钮链路，只用离线脚本；不读取密钥，不请求外网。</summary>
    public sealed class DragonsleepPlanPresentationTests
    {
        DragonsleepGame _game;
        VisualElement _root;
        Func<IAiProvider> _factory;
        bool _reduced;
        Scripted _provider;

        sealed class Scripted : IAiProvider
        {
            public string Name => "offline-m4-fixture";
            public bool IsSimulated => true;
            public readonly Queue<string> Plans = new Queue<string>();
            public readonly List<PromptPacket> Packets = new List<PromptPacket>();
            public bool DelayPlan;
            public CancellationToken LastCancellation;
            public TaskCompletionSource<ProviderResult> Pending;
            public ProviderResult LateReply;
            public bool ThrowOnSecondAgent;
            public string PlanError;
            int _agents;
            readonly RuleProvider _rules = new RuleProvider();
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
            {
                Packets.Add(packet);
                if (packet.Kind == AiTaskKind.AgentPlan && ThrowOnSecondAgent && ++_agents == 2)
                    throw new InvalidOperationException("offline-m4-night-interruption");
                if (packet.Kind != AiTaskKind.Plan) return _rules.CompleteAsync(packet, cancellationToken);
                if (PlanError != null)
                {
                    var error = PlanError; PlanError = null;
                    return Task.FromResult(new ProviderResult { ProviderName = Name, Error = error, Simulated = true });
                }
                var reply = Plans.Count > 0 ? new ProviderResult { ProviderName = Name, RawText = Plans.Dequeue(), Simulated = true }
                    : _rules.CompleteAsync(packet, cancellationToken).GetAwaiter().GetResult();
                if (!DelayPlan) return Task.FromResult(reply);
                DelayPlan = false; LastCancellation = cancellationToken; LateReply = reply;
                Pending = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                return Pending.Task; // 故意忽略取消，释放时仍返回一份有效提案。
            }
            public void Release() { Pending?.TrySetResult(LateReply); }
        }

        [UnitySetUp]
        public IEnumerator Load()
        {
            _factory = DragonsleepGame.ProviderFactory; _reduced = DsUiMotion.Reduced;
            _provider = new Scripted(); DragonsleepGame.ProviderFactory = () => _provider; DsUiMotion.Reduced = true;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Dragonsleep_Town");
#endif
            yield return null; yield return null;
            _game = UnityEngine.Object.FindFirstObjectByType<DragonsleepGame>(); Assert.IsNotNull(_game);
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            Click("beginJourney"); yield return null;
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            _provider.Release();
            if (_game != null) yield return Idle();
            DragonsleepGame.ProviderFactory = _factory; DsUiMotion.Reduced = _reduced;
        }

        static void Activate(Button button)
        {
            Assert.IsNotNull(button); Assert.IsTrue(button.enabledInHierarchy, button.name + " 应可用");
            button.Focus(); using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }
        void Click(string name) => Activate(_root.Q<Button>(name));
        IEnumerator Idle()
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (_game != null && _game.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(_game == null || !_game.Busy, "操作必须收尾，不能悬挂 Busy"); yield return null;
        }
        IEnumerator ProviderPending()
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (_provider.Pending == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(_provider.Pending, "先建立提供方真实等待屏障，再验证忽略取消后的迟到回复");
        }
        IEnumerator Open() { Click("openPlan"); yield return null; Assert.IsNotNull(_root.Q("planPanel")); }
        string State() => _game.Session.CaptureState().Hash;
        string Text() => string.Join("\n", _root.Q("planPanel").Query<Label>().ToList().Select(l => l.text));

        static DsPlanStep Step(string id, string op, string place = "", string until = "", string when = "", string verb = "", string target = "") =>
            new DsPlanStep { Id = id, Op = op, Place = place, Until = until, When = when, Verb = verb, Target = target, Why = "离线按钮链路验收" };
        static string Plan(params DsPlanStep[] steps)
        {
            var plan = new DsPlan { Goal = "把小烬送回母亲身边", Summary = "先前往工坊，最后在龙骨岗与母亲团聚。" };
            plan.Steps.AddRange(steps); return MiniJson.Write(plan.ToRecord());
        }
        static string ChoicePlan()
        {
            var plan = new DsPlan { Goal = "归巢", Summary = "先到工坊，再选择是否按时带小烬回龙骨岗。" };
            var choice = new DsPlanChoice { Id = "route", Question = "最后在哪里等待？" };
            choice.Options.Add(new DsPlanOption { Id = "home", Label = "去龙骨岗团聚" });
            choice.Options.Add(new DsPlanOption { Id = "stay", Label = "留在工坊" }); plan.Choices.Add(choice);
            plan.Steps.Add(Step("first", "go", "workshop"));
            plan.Steps.Add(Step("home", "wait", "skull_hill", "第4天·清晨", "route=home"));
            plan.Steps.Add(Step("stay", "wait", "workshop", "第4天·清晨", "route=stay"));
            return MiniJson.Write(plan.ToRecord());
        }
        void Following()
        {
            var w = _game.Session.World;
            w.Whelp.Place = w.PlayerPlace; w.Whelp.Following = true; w.Whelp.Hidden = true;
            w.Whelp.TrustPlayer = 0.6f; w.Whelp.Fear = 0.4f; w.Flags["whelp_hidden_by_player"] = w.ClockText;
        }

        [UnityTest]
        public IEnumerator 提案取舍确认不改世界_一次按钮只执行一步且角色不会被原位置改回()
        {
            Following(); _provider.Plans.Enqueue(ChoicePlan()); yield return Open();
            string before = State(); int time = _game.Session.TimeLeftInPhase;
            Click("proposePlan"); yield return Idle();
            Assert.AreEqual(before, State()); Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status);
            Click("planChoice_route_stay"); yield return null;
            Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf);
            StringAssert.Contains("无法完成归巢", Text());
            Click("planChoice_route_home"); yield return null;
            Click("confirmPlan"); yield return null;
            Assert.AreEqual(before, State()); Assert.AreEqual(time, _game.Session.TimeLeftInPhase);
            Assert.AreEqual("running", _game.ActivePlan.Status); Assert.AreEqual(0, _game.ActivePlan.NextIndex);
            Click("executePlanStep"); yield return Idle();
            Assert.AreEqual(1, _game.ActivePlan.NextIndex); Assert.AreEqual(1, _game.ActivePlan.Results.Count);
            Assert.AreEqual("workshop", _game.Session.World.PlayerPlace);
            for (int i = 0; i < 4; i++) yield return null;
            Assert.AreEqual("workshop", _game.Session.World.PlayerPlace, "场景不能把计划移动改回旧位置");
            Assert.IsTrue(_game.ActivePlan.Results.Single().Receipts.Any(r => r.Applied));
            StringAssert.Contains("已提交", Text()); StringAssert.Contains("尚未执行", Text());
            Assert.IsTrue(_root.Query<Label>().ToList().Any(l => l.text == "呈现给玩家"), "织言镜无需再次点轨迹就应展开完整七阶段");
            yield return CapturePlanPanel(1600, 1000, "plan-panel.png");
            yield return CapturePlanPanel(1024, 768, "plan-panel-compact.png");
        }

        IEnumerator CapturePlanPanel(int width, int height, string fileName)
        {
            // batchmode 的 ScreenCapture 不保证产生文件；将运行时 UI 面板渲染到离屏纹理，不改场景或主题资源。
            var panel = _game.GetComponent<UIDocument>().panelSettings;
            var previous = panel.targetTexture;
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var active = RenderTexture.active;
            target.Create(); panel.targetTexture = target;
            try
            {
                for (int i = 0; i < 4; i++) yield return null;
                var card = _root.Q("planPanel");
                foreach (string name in new[] { "planPanel", "planGoal", "executePlanStep", "closePlan" })
                {
                    var rect = _root.Q(name).worldBound; var outer = name == "planPanel" ? _root.worldBound : card.worldBound;
                    Assert.Greater(rect.width, 0, name); Assert.Greater(rect.height, 0, name);
                    Assert.GreaterOrEqual(rect.xMin, outer.xMin - 1, name); Assert.LessOrEqual(rect.xMax, outer.xMax + 1, name);
                    Assert.GreaterOrEqual(rect.yMin, outer.yMin - 1, name); Assert.LessOrEqual(rect.yMax, outer.yMax + 1, name);
                }
                RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/m4-plan")); Directory.CreateDirectory(folder);
                var png = image.EncodeToPNG(); Assert.Greater(png.Length, 10000, "离屏图片不能只有空白背景");
                File.WriteAllBytes(Path.Combine(folder, fileName), png);
            }
            finally
            {
                panel.targetTexture = previous; RenderTexture.active = active;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(image);
            }
        }

        [UnityTest]
        public IEnumerator 首案只覆盖局部步骤必须修正_新草稿仍需重新确认()
        {
            Following();
            _provider.Plans.Enqueue(Plan(Step("local", "act", verb: "observe")));
            _provider.Plans.Enqueue(ChoicePlan()); yield return Open(); string before = State();
            Click("proposePlan"); yield return Idle();
            Assert.AreEqual("planning", _game.ActivePlan.Status); Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf);
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            Click("replanPlan"); yield return Idle();
            Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status); Assert.AreEqual(before, State());
            StringAssert.Contains("goal_not_reached", _game.ActivePlan.LatestPlanTrace.PlanContext);
            Assert.IsEmpty(_game.ActivePlan.AcceptedRequestIds); Assert.IsEmpty(_game.ActivePlan.Results);
        }

        [UnityTest]
        public IEnumerator 确认前世界失效_不显示已开始且剩余步骤作废()
        {
            Following(); _provider.Plans.Enqueue(ChoicePlan()); yield return Open();
            Click("proposePlan"); yield return Idle();
            _game.Session.World.Whelp.Following = false; _game.Session.World.Whelp.Place = "docks";
            string before = State(); Click("confirmPlan"); yield return null;
            Assert.AreEqual(before, State()); Assert.AreEqual("needs_replan", _game.ActivePlan.Status);
            Assert.IsTrue(_game.ActivePlan.Results.All(r => r.Status == "void"));
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf); StringAssert.Contains("情况有变", Text());
        }

        IEnumerator CancelLate(bool replan)
        {
            Following(); yield return Open();
            if (replan)
            {
                _provider.Plans.Enqueue(Plan(Step("local", "act", verb: "observe")));
                Click("proposePlan"); yield return Idle();
            }
            _provider.Plans.Enqueue(ChoicePlan()); _provider.DelayPlan = true;
            string before = State(); int generation = _game.Session.World.SessionGen;
            Click(replan ? "replanPlan" : "proposePlan"); yield return ProviderPending();
            Assert.IsTrue(_game.Busy); Assert.IsTrue(_game.Player.InputBlocked);
            Click("cancelPlanRequest"); yield return null;
            Assert.IsTrue(_provider.LastCancellation.IsCancellationRequested);
            Assert.IsTrue(_game.Busy); Assert.IsTrue(_game.Session.Busy);
            int calls = _provider.Packets.Count;
            _game.Restart(); _ = _game.ProposePlanAsync("不应插队"); _ = _game.ExecutePlanStepAsync();
            _game.Submit(LoomMode.Act, "观察"); _game.TravelTo("workshop");
            Assert.AreEqual(calls, _provider.Packets.Count);
            Assert.IsFalse(_root.Q<Button>("proposePlan").enabledSelf);
            _provider.Release(); yield return Idle();
            Assert.AreEqual(before, State()); Assert.AreEqual(generation, _game.Session.World.SessionGen);
            Assert.AreEqual("abandoned", _game.ActivePlan.Status); Assert.IsEmpty(_game.ActivePlan.Results);
            Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf); Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            StringAssert.Contains("规划已取消", Text()); Assert.IsNotEmpty(_root.Q<TextField>("planGoal").value);
            Assert.AreEqual(calls, _provider.Packets.Count);
        }
        [UnityTest] public IEnumerator 取消首案后迟到成功不会生效且忙时不能重启插队() => CancelLate(false);
        [UnityTest] public IEnumerator 取消重规划后迟到成功不会恢复旧计划() => CancelLate(true);

        [UnityTest]
        public IEnumerator 前置失败显示真实回执与作废_重规划不能自动执行()
        {
            var w = _game.Session.World; w.Day = 2; w.Phase = DayPhase.Noon;
            w.Whelp.CapturedBy = "osric"; w.Whelp.Place = "castle"; w.Whelp.Hidden = false; w.Whelp.TrustPlayer = 0.6f;
            w.Flags["seen_by_guards"] = w.ClockText;
            w.PlayerKnows.Add("b_pip_whelp_place"); w.PlayerKnows.Add("law:law_guards_dusk");
            _provider.Plans.Enqueue(Plan(Step("rescue", "act", "castle", verb: "rescue", target: "whelp"),
                Step("home", "wait", "skull_hill", "第4天·清晨")));
            yield return Open(); Click("proposePlan"); yield return Idle();
            Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status, Text());
            Click("confirmPlan"); yield return null; Click("executePlanStep"); yield return Idle();
            Assert.AreEqual("needs_replan", _game.ActivePlan.Status);
            Assert.AreEqual(DsPlanFailure.Precondition, _game.ActivePlan.LastFailure);
            Assert.AreEqual(4, _game.Session.TimeLeftInPhase); Assert.AreEqual("void", _game.ActivePlan.Results.Last().Status);
            StringAssert.Contains("前置条件不满足", Text()); StringAssert.Contains("已作废，未执行", Text());
            int receipts = _game.ActivePlan.Results.Count;
            Click("replanPlan"); yield return Idle();
            Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status, Text());
            Assert.AreEqual(receipts, _game.ActivePlan.Results.Count);
            Assert.AreEqual("pending", _game.ActivePlan.Replans.Last().Decision);
        }

        [UnityTest]
        public IEnumerator 两步之间公开搜城作废后续_必须重规划再确认()
        {
            Following();
            _provider.Plans.Enqueue(Plan(Step("look1", "act", verb: "observe"), Step("look2", "act", verb: "observe"),
                Step("home", "wait", "skull_hill", "第4天·清晨")));
            yield return Open(); Click("proposePlan"); yield return Idle();
            Click("confirmPlan"); yield return null; Click("executePlanStep"); yield return Idle();
            _game.Session.World.PublicNews.Add("卫兵正在搜城（离线外部事件夹具）"); string before = State();
            Click("executePlanStep"); yield return Idle();
            Assert.AreEqual(before, State()); Assert.AreEqual("needs_replan", _game.ActivePlan.Status);
            Assert.AreEqual(DsPlanFailure.Stale, _game.ActivePlan.LastFailure);
            Assert.AreEqual("void", _game.ActivePlan.Results.Last().Status);
            StringAssert.Contains("世界已变", Text());
        }

        IEnumerator FailedReplanKeepsHistory(bool parseFailure)
        {
            Following();
            _provider.Plans.Enqueue(Plan(Step("look1", "act", verb: "observe"), Step("look2", "act", verb: "observe"),
                Step("home", "wait", "skull_hill", "第4天·清晨")));
            yield return Open(); Click("proposePlan"); yield return Idle();
            Click("confirmPlan"); yield return null; Click("executePlanStep"); yield return Idle();
            _game.Session.World.PublicNews.Add("卫兵正在搜城（离线外部事件夹具）");
            Click("executePlanStep"); yield return Idle();
            int results = _game.ActivePlan.Results.Count; string before = State();
            if (parseFailure) _provider.Plans.Enqueue("{broken-json"); else _provider.PlanError = "timeout";
            Click("replanPlan"); yield return Idle();
            Assert.AreEqual(before, State()); Assert.AreEqual(results, _game.ActivePlan.Results.Count);
            Assert.AreEqual("needs_replan", _game.ActivePlan.Status);
            StringAssert.Contains("历史步骤：look1", Text()); StringAssert.Contains("历史步骤：look2", Text());
            StringAssert.Contains("实际回执", Text()); StringAssert.Contains("世界已变", Text());
            Assert.IsFalse(_root.Q<Button>("confirmPlan").enabledSelf); Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            if (!parseFailure) Assert.IsFalse(_root.Q<Button>("replanPlan").enabledSelf, "传输失败不能伪装成语义修正重试");
        }
        [UnityTest] public IEnumerator 重规划超时仍保留已执行历史与真实回执() => FailedReplanKeepsHistory(false);
        [UnityTest] public IEnumerator 重规划坏JSON仍保留已执行历史与真实回执() => FailedReplanKeepsHistory(true);

        [UnityTest]
        public IEnumerator 跨夜步骤部分提交后异常_剩余计划终止且场景同步真实位置()
        {
            Following(); var w = _game.Session.World; w.Day = 3; w.Phase = DayPhase.Dusk;
            _provider.Plans.Enqueue(Plan(Step("home", "wait", "skull_hill", "第4天·清晨")));
            yield return Open(); Click("proposePlan"); yield return Idle();
            Assert.AreEqual("awaiting_confirmation", _game.ActivePlan.Status, Text());
            Click("confirmPlan"); yield return null; string before = State();
            _provider.ThrowOnSecondAgent = true;
            Click("executePlanStep"); yield return Idle();
            Assert.AreNotEqual(before, State(), "异常前已移动及推进时间，不能声称整步零变化");
            Assert.AreEqual("abandoned", _game.ActivePlan.Status); Assert.AreEqual(DayPhase.Night, w.Phase);
            Assert.AreEqual("skull_hill", w.PlayerPlace);
            Assert.IsTrue(_game.Session.Traces.Any(t => t.Kind == AiTaskKind.AgentPlan && t.Receipt?.Applied == true));
            Assert.IsFalse(_root.Q<Button>("executePlanStep").enabledSelf);
            StringAssert.Contains("后果保留", Text()); StringAssert.Contains("最近实际调用", Text());
            int calls = _provider.Packets.Count; _ = _game.ExecutePlanStepAsync(); yield return null;
            Assert.AreEqual(calls, _provider.Packets.Count, "异常步骤不能重复执行已提交的居民行动");
            for (int i = 0; i < 4; i++) yield return null;
            Assert.AreEqual("skull_hill", w.PlayerPlace, "异常收尾也必须同步场景玩家位置");
        }

        [UnityTest]
        public IEnumerator 收起面板后取消重规划_常驻提示不会停在等待文字()
        {
            Following(); _provider.Plans.Enqueue(Plan(Step("local", "act", verb: "observe")));
            yield return Open(); Click("proposePlan"); yield return Idle();
            _provider.Plans.Enqueue(ChoicePlan()); _provider.DelayPlan = true;
            Click("replanPlan"); yield return ProviderPending(); Click("closePlan"); yield return null;
            _game.CancelPlanRequest(); _provider.Release(); yield return Idle();
            Assert.IsFalse(_game.Hud.ModalOpen); Assert.AreEqual("abandoned", _game.ActivePlan.Status);
            var labels = _root.Query<Label>().ToList().Select(l => l.text ?? "").ToList();
            Assert.IsTrue(labels.Any(t => t.Contains("规划已取消")));
            Assert.IsFalse(labels.Any(t => t.StartsWith("丝线正在回应")), "收起面板也必须有明确的最终状态");
        }

        [UnityTest]
        public IEnumerator 从全新离线局逐次点击到团聚结局_中途补救也不自动确认()
        {
            _game.Session.World.Seed = 7932;
            yield return Open(); Click("proposePlan"); yield return Idle();
            int calls = 0;
            for (int i = 0; i < 120 && _game.Ending == null; i++)
            {
                switch (_game.ActivePlan.Status)
                {
                    case "awaiting_confirmation": Click("confirmPlan"); break;
                    case "running": Click("executePlanStep"); break;
                    case "planning":
                    case "needs_replan": Assert.Less(++calls, 11, Text()); Click("replanPlan"); break;
                    default: Assert.Fail("计划意外终止：" + _game.ActivePlan.Status + " " + Text()); break;
                }
                yield return Idle();
            }
            Assert.IsNotNull(_game.Ending, "有界点击必须到结局"); Assert.IsTrue(_game.Session.World.Whelp.WithMother);
            Assert.IsTrue(_game.Session.World.FinalDawn); Assert.AreEqual("completed", _game.ActivePlan.Status);
            Assert.IsNotNull(_root.Query<Label>().ToList().FirstOrDefault(l => l.text == _game.Ending.Title));
        }

        [UnityTest]
        public IEnumerator 场景卸载后迟到规划不触碰已销毁界面或写世界()
        {
            Following(); _provider.Plans.Enqueue(ChoicePlan()); _provider.DelayPlan = true;
            yield return Open(); string before = State(); Click("proposePlan"); yield return null;
            var session = _game.Session; var run = _game.ActivePlan;
            var source = _game.gameObject.scene;
            var empty = SceneManager.CreateScene("M4LateReplyEmpty"); SceneManager.SetActiveScene(empty);
            yield return SceneManager.UnloadSceneAsync(source);
            _provider.Release();
            float deadline = Time.realtimeSinceStartup + 5;
            while (session.Busy && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null; yield return null;
            Assert.IsFalse(session.Busy); Assert.AreEqual(before, session.CaptureState().Hash);
            Assert.AreEqual("abandoned", run.Status); Assert.IsTrue(_game == null);
        }
    }
}
