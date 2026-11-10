#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    /// <summary>仅 player 的显式启动冒烟；未传 --dragonsleep-smoke 时不创建对象、不运行验证、不自动退出。</summary>
    public sealed class DragonsleepPlayerSmoke : MonoBehaviour
    {
        // 与 Editor 的 DragonsleepSceneBuilder.ScenePath 一致；player 不引用 Editor 程序集。
        const string ScenePath = "Assets/LocalThirdParty/Dragonsleep/Dragonsleep_Town.unity";
        const double TimeoutSeconds = 60;

        [Serializable]
        sealed class SmokeSummary
        {
            public int schemaVersion = 1;
            public string scope = "player 启动冒烟，不是完整玩法、实体演出验收或素材发布许可。";
            public string finishedUtc;
            public bool passed;
            public int exitCode;
            public string failure;
            public string unityVersion;
            public string scene;
            public string provider;
            public bool simulated;
            public int modelCalls;
            public int observedFrames;
            public double startupSeconds;
            public int runtimeErrors;
            public string firstRuntimeError;
            public string[] checks;
        }

        string _output;
        double _started;
        int _firstReadyFrame = -1;
        bool _finished;
        readonly SmokeSummary _summary = new SmokeSummary();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            string[] args = Environment.GetCommandLineArgs(); // 只读启动参数，不读取环境变量或本地凭据。
            if (!args.Contains("--dragonsleep-smoke")) return;
            try
            {
                int outputIndex = Array.IndexOf(args, "--dragonsleep-smoke-output");
                if (args.Count(arg => arg == "--dragonsleep-smoke") != 1 ||
                    args.Count(arg => arg == "--dragonsleep-smoke-output") != 1 || outputIndex + 1 >= args.Length)
                    throw new ArgumentException("冒烟必须传入一次 --dragonsleep-smoke-output <绝对本地 JSON 路径>。");
                string output = ValidateOutput(args[outputIndex + 1]);
                var runner = new GameObject("__DragonsleepPlayerSmoke").AddComponent<DragonsleepPlayerSmoke>();
                DontDestroyOnLoad(runner.gameObject);
                runner._output = output;
                runner._started = Time.realtimeSinceStartupAsDouble;
                runner._summary.unityVersion = Application.unityVersion;
                Application.runInBackground = true;
                Application.logMessageReceived += runner.OnLog;
            }
            catch (Exception exception)
            {
                Debug.LogError("[龙眠镇冒烟] 参数或输出路径无效：" + exception.Message);
                Application.Quit(2);
            }
        }

        void Update()
        {
            if (_finished) return;
            _summary.observedFrames++;
            _summary.startupSeconds = Time.realtimeSinceStartupAsDouble - _started;
            try
            {
                if (_summary.runtimeErrors > 0) { Finish(false, "启动期间出现 Unity Error/Exception/Assert。"); return; }
                if (_summary.observedFrames < 3) return; // 让真实 Game.Awake、Game/Hud.Start 与 UI 布局先运行。
                string problem = StartupProblem();
                if (problem == null)
                {
                    if (_firstReadyFrame < 0) _firstReadyFrame = Time.frameCount;
                    if (Time.frameCount >= _firstReadyFrame + 2 && _summary.startupSeconds >= 0.5)
                        Finish(true, null);
                }
                else
                {
                    _firstReadyFrame = -1;
                    if (_summary.startupSeconds >= TimeoutSeconds) Finish(false, "等待启动超时：" + problem);
                }
            }
            catch (Exception exception) { Finish(false, exception.GetType().Name + ": " + exception.Message); }
        }

        string StartupProblem()
        {
            var scene = SceneManager.GetActiveScene();
            _summary.scene = scene.path;
            if (!scene.IsValid() || !scene.isLoaded) return "主场景尚未载入。";
            if (scene.path != ScenePath || scene.buildIndex != 0)
                throw new InvalidOperationException("启动场景不是构建列表首位的龙眠镇主场景：" + scene.path);
            var games = FindObjectsByType<DragonsleepGame>(FindObjectsSortMode.None);
            if (games.Length == 0) return "未找到实际 DragonsleepGame。";
            if (games.Length != 1 || games[0].gameObject.scene != scene)
                throw new InvalidOperationException("必须只有一个属于主场景的 DragonsleepGame。");
            var game = games[0];
            if (game.Session == null || game.Session.Provider == null) return "实际游戏会话尚未初始化。";
            var provider = game.Session.Provider;
            _summary.provider = provider.GetType().FullName;
            _summary.simulated = provider.IsSimulated;
            _summary.modelCalls = game.Session.Traces.Count;
            // 检查实际默认提供方，不设置 ProviderFactory，不用测试替身掩盖错误的在线配置。
            if (provider.GetType() != typeof(RuleProvider) || !provider.IsSimulated)
                throw new InvalidOperationException("默认提供方不是离线 RuleProvider 模拟。");
            if (game.Busy || game.Session.Busy || game.Session.Traces.Count != 0)
                throw new InvalidOperationException("启动时出现非预期自动调用或玩法操作。");
            if (FindObjectsByType<DragonsleepJourneyProbe>(FindObjectsSortMode.None).Length != 0)
                throw new InvalidOperationException("未传短局参数时不应创建自动玩法验证器。");
            if (game.Player == null || !game.Player.isActiveAndEnabled || game.PlacesRoot == null || game.Whelp == null)
                return "主场景缺少玩家、地点或雏龙引用。";
            var stage = game.Stage;
            if (stage == null || stage.Frame == null || stage.Mother == null || stage.GuardPosts.Count == 0)
                return "实体表现帧、母龙或灰盒岗位尚未就绪。";
            if (stage.Frame.Mode != WhelpStageMode.Hidden || stage.Frame.MotherDescended ||
                stage.Cage.gameObject.activeSelf || stage.Cloak.gameObject.activeSelf)
                throw new InvalidOperationException("启动时实体表现不是新局隐藏状态。");
            foreach (string actor in new[] { "player", "osric", "marta", "maren", "vesna", "pip" })
                if (!game.ActorTransforms.TryGetValue(actor, out var transform) || transform == null)
                    return "主场景缺少角色：" + actor;
            var camera = Camera.main;
            if (camera == null || !camera.isActiveAndEnabled || game.Player.CameraBasis != camera.transform)
                return "玩家主镜头尚未就绪。";
            if (game.Hud == null || !game.Hud.isActiveAndEnabled || game.Hud.Game != game)
                return "真实 LoomLensHud 未接到实际游戏。";
            var document = game.Hud.GetComponent<UIDocument>();
            if (document == null || !document.isActiveAndEnabled || document.panelSettings == null)
                return "UIDocument 或 PanelSettings 缺失。";
            var root = document.rootVisualElement;
            if (root == null || root.name != "dragonsleepHud" || root.panel == null || !HasLayout(root))
                return "UIDocument 尚未连接运行时面板或完成初始布局。";
            foreach (string element in new[] { "clockPlate", "questCard", "conversationDock", "inputRow", "shortcuts", "introCard" })
                if (root.Q(element) == null) return "必要 UI 元素缺失：" + element;
            var begin = root.Q<Button>("beginJourney");
            if (begin == null || !begin.enabledInHierarchy || !HasLayout(begin) || begin.resolvedStyle.opacity < 0.99f)
                return "开场启程按钮尚未就绪。";
            if (root.Q<Button>("sendButton") == null || root.Q("inputRow").Q<TextField>() == null)
                return "织言输入框或发送按钮缺失。";
            if (!game.Hud.ModalOpen || !game.Player.InputBlocked) return "开场阅读层未阻断玩家移动。";
            if (!root.Q("clockPlate").Query<Label>().ToList().Any(label => label.text == game.Session.World.ClockText))
                return "时间牌未呈现实际会话时钟。";
            if (Resources.Load<Texture2D>("Dragonsleep/TitleArt") == null) return "开场主视觉资源缺失。";
            return null;
        }

        static bool HasLayout(VisualElement element) =>
            element.resolvedStyle.display != DisplayStyle.None && element.visible &&
            element.resolvedStyle.width > 0 && element.resolvedStyle.height > 0;

        void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            _summary.runtimeErrors++;
            if (_summary.firstRuntimeError == null)
                _summary.firstRuntimeError = message.Length <= 512 ? message : message.Substring(0, 512);
        }

        void Finish(bool passed, string failure)
        {
            if (_finished) return;
            _finished = true;
            Application.logMessageReceived -= OnLog;
            _summary.passed = passed;
            _summary.exitCode = passed ? 0 : 1;
            _summary.failure = failure;
            _summary.finishedUtc = DateTime.UtcNow.ToString("O");
            if (passed)
                _summary.checks = new[] { "主场景与实际游戏会话", "默认离线 RuleProvider 模拟且无自动调用", "玩家/角色/雏龙/镜头引用", "实体表现新局状态与灰盒岗位",
                    "实际 UIDocument 与必要 UI 初始布局", "开场阅读阻断移动", "启动期间无 Unity 错误" };
            try
            {
                ValidateOutput(_output);
                using (var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(JsonUtility.ToJson(_summary, true));
                Debug.Log("[龙眠镇冒烟] " + (passed ? "通过" : "失败：" + failure) + "；摘要：" + _output);
            }
            catch (Exception exception)
            {
                _summary.exitCode = 2;
                Debug.LogError("[龙眠镇冒烟] 无法写入显式本地摘要：" + exception.Message);
            }
            Application.Quit(_summary.exitCode);
        }

        static string ValidateOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output) || !output.StartsWith("/", StringComparison.Ordinal) ||
                output.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathRooted(output) ||
                !string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出必须是显式绝对本地 .json 路径，不接受 URL、UNC 或相对路径。");
            string fullPath = Path.GetFullPath(output);
            if (File.Exists(fullPath) || Directory.Exists(fullPath)) throw new IOException("拒绝覆盖已存在的冒烟输出。");
            var parent = new DirectoryInfo(Path.GetDirectoryName(fullPath));
            if (!parent.Exists) throw new DirectoryNotFoundException("冒烟输出的父目录必须已存在。");
            for (var directory = parent; directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("冒烟输出的父目录不能是符号链接。");
            return fullPath;
        }

        void OnDestroy() => Application.logMessageReceived -= OnLog;
    }
}
#endif
