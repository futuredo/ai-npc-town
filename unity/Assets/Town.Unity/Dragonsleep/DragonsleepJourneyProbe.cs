using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    /// <summary>显式离线验证入口；通过实际 UI 按钮行走短局，不代表真人操作、性能或视觉验收。</summary>
    public sealed partial class DragonsleepJourneyProbe : MonoBehaviour
    {
        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string scope = "固定种子、默认离线提供方、真实场景按钮短局；不是真人试玩或像素命中验收。";
            public bool passed;
            public string failure, finishedUtc, unityVersion, provider, ending, finalHash;
            public int seed, buttons, planCalls, replans, successfulSteps, failedSteps, voidSteps, appliedReceipts;
            public int observedFrames, runtimeErrors;
            public bool withMother, finalDawn, restarted, secondProposal, stageReunited, realExecutionFailure;
            public int choiceButtons;
            public bool privatePlan;
            public int confideCalls;
            public string rulesVersion = DsExecutor.RulesVersion;
            public bool socialFixture, custodyDelivered, allyMaren, actualCooperationRejection, deliveryCostVisible;
            public bool sceneOnly;
            public int sceneCommits;
            public bool dayOnly, objectiveAchieved;
            public int dayCommits, pactGiveCalls;
            public string cooperationMode;
            public bool lateRescue;
            public bool craftOnly, boundCraftConfirmed, oldCraftRejected;
            public bool actionOnly;
            public int actionCommits, actionRetainedRequests;
            public long actionRetainedBeforeUtf8Bytes;
            public int sessionOwnerCallbacks, sessionWrongThreadCallbacks;
            public double elapsedSeconds, longestFrameSeconds;
            public string[] checkpoints;
        }

        public Report Result { get; private set; }
        public bool Finished { get; private set; }
        readonly List<string> _checkpoints = new List<string>();
        DragonsleepGame _game;
        VisualElement _root;
        string _output, _before, _pending;
        int _phase, _afterFrame, _generation, _operations, _previousResults;
        double _started, _lastFrame;
        bool _exit;
        readonly Dictionary<string, string> _selection = new Dictionary<string, string>();
        readonly Queue<string> _choiceClicks = new Queue<string>();
        string _chosenRequest, _previousRequest;
        int _stepIndex, _stepResults, _stepSuccesses;
        string _settledHash;
        int _settledResults;
        DragonsleepSession _observedSession;
        CallbackThreads _callbackThreads;

        sealed class CallbackThreads
        {
            readonly int _owner;
            int _callbacks, _wrongThread;
            internal CallbackThreads(int owner) { _owner = owner; }
            internal int Callbacks => Volatile.Read(ref _callbacks);
            internal int WrongThread => Volatile.Read(ref _wrongThread);
            void Record()
            {
                Interlocked.Increment(ref _callbacks);
                if (Thread.CurrentThread.ManagedThreadId != _owner) Interlocked.Increment(ref _wrongThread);
            }
            internal void Stage(AiTrace trace, AiStage stage) => Record();
            internal void Completed(AiTrace trace) => Record();
        }

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--dragonsleep-journey")) return;
            try
            {
                int index = Array.IndexOf(args, "--dragonsleep-journey-output");
                if (args.Contains("--dragonsleep-smoke") || args.Count(a => a == "--dragonsleep-journey") != 1 ||
                    args.Count(a => a == "--dragonsleep-journey-output") != 1 || index + 1 >= args.Length)
                    throw new ArgumentException("短局验证需要唯一输出参数，且不可与启动冒烟同时运行。");
                string output = ValidateOutput(args[index + 1]);
                var probe = new GameObject("__DragonsleepJourneyProbe").AddComponent<DragonsleepJourneyProbe>();
                DontDestroyOnLoad(probe.gameObject);
                bool social = args.Contains("--dragonsleep-journey-social");
                bool scene = args.Contains("--dragonsleep-journey-scene");
                bool privatePlan = args.Contains("--dragonsleep-journey-private");
                bool day = args.Contains("--dragonsleep-journey-day");
                bool late = args.Contains("--dragonsleep-journey-late-rescue");
                bool craft = args.Contains("--dragonsleep-journey-craft");
                bool action = args.Contains("--dragonsleep-journey-action");
                string cooperation = args.FirstOrDefault(a => a.StartsWith("--dragonsleep-journey-cooperation=", StringComparison.Ordinal));
                cooperation = cooperation?.Substring("--dragonsleep-journey-cooperation=".Length);
                Require((social ? 1 : 0) + (scene ? 1 : 0) + (privatePlan ? 1 : 0) + (day ? 1 : 0) +
                    (cooperation != null ? 1 : 0) + (late ? 1 : 0) + (craft ? 1 : 0) + (action ? 1 : 0) <= 1, "验证模式不可混用。");
                probe.Begin(social || late ? 109 : cooperation != null ? 253421 : 7932,
                    output, true, privatePlan, social, scene, cooperation, day, late, craft, action);
                Application.runInBackground = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[龙眠镇短局] 参数无效：" + e.Message); Application.Quit(2);
            }
        }
#endif

        /// <summary>测试可显式创建同一验证器；不替换提供方、不调用 Game 的行动方法，不绕过确认按钮。</summary>
        public void Begin(int seed = 7932, string output = null, bool exitWhenFinished = false, bool privatePlan = false,
            bool socialFixture = false, bool sceneOnly = false, string cooperationMode = null, bool dayOnly = false,
            bool lateRescue = false, bool craftOnly = false, bool actionOnly = false)
        {
            if (Result != null) throw new InvalidOperationException("验证器不能重复启动。");
            Require(cooperationMode == null || new[] { "solo", "maren", "pip" }.Contains(cooperationMode), "合作验证模式无效。");
            Require((privatePlan ? 1 : 0) + (socialFixture ? 1 : 0) + (sceneOnly ? 1 : 0) + (dayOnly ? 1 : 0) +
                (cooperationMode != null ? 1 : 0) + (lateRescue ? 1 : 0) + (craftOnly ? 1 : 0) +
                (actionOnly ? 1 : 0) <= 1, "验证模式不可混用。");
            if (output != null) output = ValidateOutput(output);
            _output = output; _exit = exitWhenFinished;
            _started = _lastFrame = Time.realtimeSinceStartupAsDouble;
            Result = new Report { seed = seed, unityVersion = Application.unityVersion, privatePlan = privatePlan, socialFixture = socialFixture };
            if (socialFixture) Result.scope = "显式低信任夹具、默认离线提供方、实际按钮补救前缀；不是从新局获取原件或完整归巢验收。";
            Result.sceneOnly = sceneOnly;
            if (sceneOnly) Result.scope = "默认离线新局地图按钮的两次 SDK 场景移动；不是计划、夜间或模型提交迁移验收。";
            Result.cooperationMode = lateRescue ? "pip" : cooperationMode; Result.dayOnly = dayOnly; Result.lateRescue = lateRescue;
            if (cooperationMode != null) Result.scope = "显式 pact_escort 等价夹具与 v2 合作条件的按钮完整局；非全新局或真实模型验收。";
            if (dayOnly) Result.scope = "真实观察耗时后，地图按钮日间 SDK 两次提交；非夜间或模型提交迁移验收。";
            if (lateRescue) Result.scope = "显式第三天黄昏被捕夹具、默认离线提供方与实际按钮救援归巢；不是自然走出的新局或必胜证明。";
            Result.craftOnly = craftOnly;
            Result.actionOnly = actionOnly;
            if (actionOnly) Result.scope = "默认离线新局的十二次真实自由行动按钮与内核保留量；不是付费模型或完整归巢验收。";
            if (craftOnly) Result.scope = "真实造物按钮准备、放弃和绑定确认；旧句柄负例直接调用授权入口，不冒充 OS 点击或完整短局。";
            Application.logMessageReceived += OnLog;
        }

        void Update()
        {
            if (Result == null || Finished) return;
            double now = Time.realtimeSinceStartupAsDouble;
            Result.observedFrames++; Result.elapsedSeconds = now - _started;
            Result.longestFrameSeconds = Math.Max(Result.longestFrameSeconds, now - _lastFrame); _lastFrame = now;
            try
            {
                Require(Result.runtimeErrors == 0, "运行期间出现 Unity Error/Exception/Assert。");
                Require(Result.elapsedSeconds < 180, "短局验证超过 180 秒。");
                if (Time.frameCount < _afterFrame) return;
                if (_phase == 0) { Startup(); return; }
                Require(_game != null, "实际游戏对象丢失。");
                if (_game.Busy || _game.Session.Busy) return; // 空闲前不枚举世界、计划或回执。
                if (Result.sceneOnly) { DriveScene(); return; }
                if (Result.dayOnly) { DriveDay(); return; }
                if (Result.craftOnly) { DriveCraft(); return; }
                if (Result.actionOnly) { DriveAction(); return; }
                if (Result.cooperationMode != null) CheckCooperationActions();
                if (Result.privatePlan)
                {
                    Result.confideCalls = _game.Session.Traces.Count(t => t.Kind == AiTaskKind.Adjudicate && t.Proposal?.Str("verb") == "confide");
                    Require(Result.confideCalls == 0, "明确不托付条件下执行了 confide。");
                    Require(_game.ActivePlan == null || _game.ActivePlan.Constraints.HasRestrictions, "重规划丢失明确条件。");
                }
                if (_pending != null) { CheckPending(); return; }
                switch (_phase)
                {
                    case 1: Click("openPlan"); _phase = 2; break;
                    case 2: Propose(); _phase = 3; break;
                    case 3: DrivePlan(); break;
                    case 4:
                        Require(_root.Query<Label>().ToList().Any(l => l.text == _game.Ending.Title), "结局标题未呈现。");
                        Click("viewEndingScene"); Checkpoint("结局卡与查看现场"); _phase = 5; break;
                    case 5:
                        Require(!_game.Hud.ModalOpen, "查看现场没有收起结局卡。");
                        Require(_game.Stage.Frame.Mode == WhelpStageMode.Reunited && _game.Whelp.gameObject.activeSelf,
                            "实际团聚后实体现场未对应。");
                        Result.stageReunited = true; Click("reopenEnding"); _phase = 6; break;
                    case 6:
                        _generation = _game.Session.World.SessionGen; Click("restartJourney"); _phase = 7; break;
                    case 7: CheckRestart(); Click("openPlan"); _phase = 8; break;
                    case 8: Propose(); _phase = 9; break;
                    case 9:
                        Require(_game.ActivePlan.Status == "awaiting_confirmation", "重开后不能生成新的待确认计划。");
                        Require(_game.ActivePlan.Results.Count == 0 && _game.ActivePlan.Replans.Count == 0, "重开后继承旧步骤或重规划。");
                        Require(!_root.Q<Button>("executePlanStep").enabledInHierarchy, "新草稿未确认却能执行。");
                        Result.secondProposal = true; Checkpoint("重开后的第二次提案仍需确认"); Finish(null); break;
                }
            }
            catch (Exception e) { Finish(e.GetType().Name + ": " + e.Message); }
        }

        void Startup()
        {
            if (Result.observedFrames < 4) return;
            _game = FindFirstObjectByType<DragonsleepGame>();
            if (_game == null || _game.Session == null || _game.Hud == null || _game.Stage?.Frame == null) return;
            Require(SceneManager.GetActiveScene().path == "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity", "不是实际主场景。");
            Result.provider = _game.Session.Provider.GetType().FullName;
            Require(_game.Session.Provider.GetType() == typeof(RuleProvider) && _game.Session.Provider.IsSimulated, "必须使用默认离线提供方。");
            Require(!_game.Busy && !_game.Session.Busy && _game.Session.Traces.Count == 0, "启动存在非预期自动调用。");
            _root = _game.GetComponent<UIDocument>().rootVisualElement;
            if (_root.panel == null || _root.worldBound.width <= 0 || _root.Q<Button>("beginJourney") == null) return;
            Require(_game.Player.InputBlocked && _game.Hud.ModalOpen, "启程前应阻断移动。");
            _observedSession = _game.Session;
            _callbackThreads = new CallbackThreads(Thread.CurrentThread.ManagedThreadId);
            _observedSession.StageChanged += _callbackThreads.Stage;
            _observedSession.TraceCompleted += _callbackThreads.Completed;
            // 唯一的初态参数：固定随机种子。不补物品、信任、时间、知识或雏龙状态。
            _game.Session.World.Seed = Result.seed;
            if (Result.socialFixture) SetupSocialFixture();
            if (Result.lateRescue) SetupLateRescueFixture();
            else if (Result.cooperationMode != null) SetupCooperationFixture();
            Click("beginJourney"); Checkpoint("默认离线新局启程"); _phase = 1;
        }

        string State() => _game.Session.CaptureState().Hash;

        void Propose()
        {
            if (Result.privatePlan)
            {
                var disclosure = _root.Q<DropdownField>("planDisclosure");
                Require(disclosure != null && disclosure.enabledInHierarchy, "托付条件控件不可用。");
                disclosure.index = 1; // 通过真实控件变更事件，不调用 Game/Core 的条件设置捷径。
            }
            if (Result.socialFixture)
            {
                _root.Q<TextField>("planGoal").value = "请梅伦修女同行护送小烬";
                _root.Q<DropdownField>("planDisclosure").index = 2;
            }
            if (Result.cooperationMode != null)
            {
                string mode = Result.cooperationMode;
                _root.Q<TextField>("planGoal").value = mode == "solo" ? "独行送小烬归巢" : "必须请" +
                    (mode == "maren" ? "梅伦" : "皮普") + "帮助小烬归巢";
                _root.Q<DropdownField>("planDisclosure").index = mode == "solo" ? 4 : mode == "maren" ? 5 : 6;
            }
            _before = State(); _previousResults = 0; Click("proposePlan"); _pending = "proposal";
        }

        void CheckPending()
        {
            string kind = _pending; _pending = null;
            if (kind == "proposal" || kind == "confirm" || kind == "replan")
                Require(State() == _before, kind + " 不得提交世界变化。");
            if (kind == "proposal" || kind == "replan")
            {
                Require(_game.ActivePlan.Status == "awaiting_confirmation", kind + " 没有生成可归巢的待确认草稿。");
                Require(_game.ActivePlan.Results.Count == _previousResults, "规划期间自动执行了步骤。");
                if (kind == "replan")
                {
                    Require(_game.ActivePlan.LatestPlanTrace.RequestId != _previousRequest, "重规划沿用了旧请求身份。");
                    Require(_game.ActivePlan.Replans.Last().Decision == "pending", "新提案在确认前已被批准。");
                    Require(!_root.Q<Button>("executePlanStep").enabledInHierarchy, "新提案未经确认即可执行。");
                }
                Checkpoint(kind == "replan" ? "失败后的新草稿未自动确认或执行" : "提出目标未改变世界");
                if (Result.socialFixture) CheckSocialDraft(kind);
            }
            if (kind == "confirm")
            {
                Require(_game.ActivePlan.Status == "running" && _game.ActivePlan.NextIndex == 0, "确认未进入首步等待状态。");
                Require(_selection.All(p => _game.ActivePlan.Selection.TryGetValue(p.Key, out var value) && value == p.Value),
                    "实际执行分支不对应点击的取舍。");
                if (_game.ActivePlan.Replans.Count > 0)
                    Require(new[] { "accepted", "modified" }.Contains(_game.ActivePlan.Replans.Last().Decision), "重规划确认未登记决定。");
                Checkpoint("确认取舍不执行步骤");
            }
            if (kind == "step")
            {
                var run = _game.ActivePlan;
                if (Result.socialFixture) CheckSocialStep();
                int executed = run.Results.Skip(_stepResults).Count(r => r.Status != "void");
                Require(executed <= 1, "一次按钮执行了多个实际步骤。");
                if (run.Results.Count(r => r.Status == "success") > _stepSuccesses)
                    Require(run.NextIndex == _stepIndex + 1 && run.Results.Count(r => r.Status == "success") == _stepSuccesses + 1,
                        "一次成功执行没有恰好推进一个步骤。");
                _settledHash = State(); _settledResults = run.Results.Count;
                _pending = "settled"; _afterFrame = Time.frameCount + 5;
            }
            if (kind == "settled")
                Require(State() == _settledHash && _game.ActivePlan.Results.Count == _settledResults, "未点击时后续步骤自动执行。");
        }

        void DrivePlan()
        {
            Require(++_operations <= 120, "计划按钮操作超过有界上限。");
            if (Result.socialFixture && FinishSocialFixture()) return;
            if (_game.Ending != null)
            {
                var w = _game.Session.World; var run = _game.ActivePlan;
                Require(w.FinalDawn && w.Whelp.WithMother && run.Status == "completed", "未达到实际团聚结局。");
                Require(_game.Stage.Frame.Mode == WhelpStageMode.Reunited, "结局实体映射不一致。");
                Result.ending = _game.Ending.Id; Result.finalHash = State(); Result.finalDawn = w.FinalDawn; Result.withMother = w.Whelp.WithMother;
                Result.planCalls = _game.Session.Traces.Count(t => t.Kind == AiTaskKind.Plan);
                Result.replans = run.Replans.Count;
                Result.successfulSteps = run.Results.Count(r => r.Status == "success");
                Result.failedSteps = run.Results.Count(r => r.Status == "failed");
                Result.voidSteps = run.Results.Count(r => r.Status == "void");
                Result.realExecutionFailure = run.Results.Any(r => r.Failure != DsPlanFailure.None && r.Status != "void");
                if (Result.cooperationMode == null)
                    Require(Result.realExecutionFailure && Result.replans > 0 && Result.voidSteps > 0, "本局没有覆盖真实执行失败与作废补救。");
                Result.appliedReceipts = _game.Session.Traces.Count(t => t.Receipt?.Applied == true) +
                    _game.Session.RuleEvents.Count(t => t.Receipt?.Applied == true);
                if (Result.cooperationMode != null)
                {
                    Result.objectiveAchieved = run.Constraints.CooperationAchieved(w);
                    Require(Result.objectiveAchieved, "团聚不能替代指定居民的真实合作。");
                    Checkpoint("实际归巢与指定合作均已达成；不是只靠草稿或预测结盟"); Finish(null); return;
                }
                Checkpoint("实际规则团聚与结局"); _phase = 4; return;
            }
            switch (_game.ActivePlan.Status)
            {
                case "awaiting_confirmation":
                    if (_chosenRequest != _game.ActivePlan.LatestPlanTrace.RequestId)
                    {
                        _chosenRequest = _game.ActivePlan.LatestPlanTrace.RequestId; _selection.Clear();
                        foreach (var choice in _game.ActivePlan.Plan.Choices)
                        {
                            // 显式触发非默认按钮再选择独行分支，验证控件输入，而不是假定默认 Selection 已生效。
                            string helper = Result.socialFixture ? "maren" : "none";
                            var desired = choice.Options.FirstOrDefault(o => o.Id == helper) ?? choice.Options[0];
                            var other = Result.socialFixture ? null : choice.Options.FirstOrDefault(o => o.Id != desired.Id);
                            if (other != null) _choiceClicks.Enqueue("planChoice_" + choice.Id + "_" + other.Id);
                            _choiceClicks.Enqueue("planChoice_" + choice.Id + "_" + desired.Id);
                            _selection.Add(choice.Id, desired.Id);
                        }
                        return;
                    }
                    // 每次选择都会重建控件树，下一按钮须等真实布局完成，不能同帧点到尚无几何的新按钮。
                    if (_choiceClicks.Count > 0) { Click(_choiceClicks.Dequeue()); Result.choiceButtons++; return; }
                    _before = State(); Click("confirmPlan"); _pending = "confirm"; break;
                case "running":
                    _stepIndex = _game.ActivePlan.NextIndex; _stepResults = _game.ActivePlan.Results.Count;
                    _stepSuccesses = _game.ActivePlan.Results.Count(r => r.Status == "success");
                    Click("executePlanStep"); _pending = "step"; break;
                case "planning":
                case "needs_replan":
                    Require(_game.Session.Traces.Count(t => t.Kind == AiTaskKind.Plan) < 10, "重规划超过 10 次。");
                    Require(_game.ActivePlan.Results.Any(r => r.Status == "void") || _game.ActivePlan.Status == "planning",
                        "失败后未保留作废步骤。");
                    _before = State(); _previousResults = _game.ActivePlan.Results.Count;
                    _previousRequest = _game.ActivePlan.LatestPlanTrace.RequestId;
                    Click("replanPlan"); _pending = "replan"; break;
                default: throw new InvalidOperationException("意外计划状态：" + _game.ActivePlan.Status);
            }
        }

        void CheckRestart()
        {
            var w = _game.Session.World;
            Require(w.SessionGen == _generation + 1 && w.Day == 1 && w.Phase == DayPhase.Dawn && !w.FinalDawn, "局代际或时间未复位。");
            Require(_game.Ending == null && _game.ActivePlan == null && _game.Session.Traces.Count == 0 && _game.Session.RuleEvents.Count == 0,
                "旧局计划、结局或回执残留。");
            Require(w.PlayerPlace == "market" && !w.Whelp.Following && !w.Whelp.WithMother && w.Whelp.CapturedBy == null, "新局世界未复位。");
            Require(_game.Stage.Frame.Mode == WhelpStageMode.Hidden && !_game.Stage.Frame.MotherDescended &&
                !_game.Stage.Cage.gameObject.activeSelf && !_game.Stage.Cloak.gameObject.activeSelf, "实体现场未复位。");
            Require(Camera.main.GetComponent<DsFollowCamera>().enabled && !_game.Hud.ModalOpen, "镜头或结局阅读层未复位。");
            Require(_root.Q<Button>("reopenEnding").resolvedStyle.display == DisplayStyle.None, "重开仍显示旧结局入口。");
            Require(_root.Q("lensDetail").childCount == 0 && _root.Q("lensStages").childCount == 0, "重开后残留旧织言镜详情。");
            Require(_root.Query<Label>().ToList().Any(l => l.text == "新的一局，尚无调用。"), "重开后常驻织言条残留旧局通知。");
            Result.restarted = true; Checkpoint("重开清除旧局世界、计划、回执和镜头状态");
        }

        void Click(string name)
        {
            var button = _root.Q<Button>(name);
            Require(button != null && button.panel != null && button.enabledInHierarchy && button.visible &&
                button.resolvedStyle.display != DisplayStyle.None && button.worldBound.width > 0 && button.worldBound.height > 0,
                "按钮缺失、不可见或不可用：" + name);
            button.Focus();
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
            Result.buttons++; _afterFrame = Time.frameCount + 3;
        }

        void Checkpoint(string text) => _checkpoints.Add(text);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Result.runtimeErrors++;
        }

        void Finish(string failure)
        {
            if (Finished) return;
            Finished = true; Application.logMessageReceived -= OnLog;
            StopObserving();
            Result.sessionOwnerCallbacks = _callbackThreads?.Callbacks ?? 0;
            Result.sessionWrongThreadCallbacks = _callbackThreads?.WrongThread ?? 0;
            if (failure == null && Result.sessionWrongThreadCallbacks != 0)
                failure = "Session 阶段或完成回调不在 Unity 所有者线程。";
            Result.failure = failure; Result.passed = failure == null;
            Result.finishedUtc = DateTime.UtcNow.ToString("O"); Result.checkpoints = _checkpoints.ToArray();
            int exitCode = Result.passed ? 0 : 1;
            if (_output != null)
            {
                try
                {
                    ValidateOutput(_output);
                    using (var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(Result, true));
                }
                catch (Exception) { exitCode = 2; }
            }
            if (_exit) Application.Quit(exitCode);
        }

        static string ValidateOutput(string path)
        {
            Require(!string.IsNullOrWhiteSpace(path) && path.StartsWith("/", StringComparison.Ordinal) &&
                !path.StartsWith("//", StringComparison.Ordinal) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase),
                "输出须为绝对本地 JSON 路径。");
            path = Path.GetFullPath(path);
            Require(!File.Exists(path) && !Directory.Exists(path), "拒绝覆盖既有输出。");
            var parent = new DirectoryInfo(Path.GetDirectoryName(path)); Require(parent.Exists, "输出父目录不存在。");
            for (var p = parent; p != null; p = p.Parent)
                Require((p.Attributes & FileAttributes.ReparsePoint) == 0, "输出父目录不可为符号链接。");
            return path;
        }

        void StopObserving()
        {
            if (_observedSession == null) return;
            _observedSession.StageChanged -= _callbackThreads.Stage;
            _observedSession.TraceCompleted -= _callbackThreads.Completed;
            _observedSession = null;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            StopObserving();
        }
    }
}
