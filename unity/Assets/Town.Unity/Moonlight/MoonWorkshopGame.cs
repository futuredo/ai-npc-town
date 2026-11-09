using System;
using System.Linq;
using Town.Core.Moonlight;
using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity.Moonlight
{
    /// <summary>单角色离线引擎实验。不引用 OpenAI；文字输入与人工样本选择显式分开。</summary>
    public sealed class MoonWorkshopGame : MonoBehaviour
    {
        public NavMeshAgent finn;
        public Transform beacon;
        public Transform prism;
        public Transform runeA;
        public Transform runeB;
        public GameObject beamBlocker;
        public LineRenderer beamA;
        public LineRenderer beamB;
        public Renderer[] runeRenderers;
        public bool showDeveloperPanel = true;
        // 规则使用局部米制坐标；现成场景可在任意世界位置组装，不改旧录制协议。
        public Vector3 worldOrigin;
        public MoonWorkshop Workshop { get; private set; }
        public string Feedback { get; private set; } = "先选择人工录制样本，检查约定，再确认执行。此模式不理解任意输入、不调用模型。";
        public bool Paused { get; set; }
        public bool LightVerified { get; private set; }
        public int SuccessfulArrivalReceipts => Workshop.History.Count(r => r.Code == "completed");
        string _input = "让小月亮沿下路引路，你跟着它走；失去光就停。";
        string _fixture = "follow";
        float _sampleTime;
        Vector3 _origin;
        bool _hasOrigin;
        GUIStyle _title, _body, _caption;
        Vector2 _scroll;
        byte[] _bookmark;
        Vector3 _bookmarkPosition;
        float _bookmarkSampleTime;

        void Awake() => ResetExperiment();
        public void ResetExperiment()
        {
            if (!finn) return;
            if (!_hasOrigin) { _origin = finn.transform.position; _hasOrigin = true; }
            Workshop = new MoonWorkshop((Workshop?.Generation ?? 0) + 1);
            if (finn.isOnNavMesh) { finn.ResetPath(); finn.Warp(_origin); finn.isStopped = true; }
            _sampleTime = 0; Paused = false; LightVerified = false;
            if (beamBlocker) beamBlocker.SetActive(false);
            Feedback = "新试验：尚无约定。供光、角色同意和实际到达是三件不同的事。";
        }

        public MoonResult SubmitFixture(string fixtureId, string playerInput)
        {
            try
            {
                var source = RecordedMoonFixture.Example(fixtureId);
                var request = Workshop.Begin(playerInput);
                var result = Workshop.Receive(request, source.Read(request));
                Feedback = "[" + source.Mode + "] " + result.Message;
                return result;
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            { Feedback = e.Message; return default; }
        }

        public void ConfirmAgreement() => Feedback = Workshop.Confirm().Message;
        public void ToggleLight()
        {
            // 先结清旧供光区间里已经发生的移动，再切换条件。
            // 不能把采样前半段合法的位移追溯判成“灭灯后移动”。
            FlushObservedPose(LightVerified);
            Workshop.SetLight(!Workshop.LightOn);
            if (!Workshop.LightOn) StopAgentImmediately();
        }
        public void ToggleOccluder()
        {
            if (!beamBlocker) return;
            FlushObservedPose(LightVerified);
            beamBlocker.SetActive(!beamBlocker.activeSelf);
            StopAgentImmediately();
            // 显式同步移动的碰撞体，确保本帧试亮不是上一帧的几何。
            Physics.SyncTransforms();
        }

        public bool RecordBookmark()
        {
            try
            {
                _bookmark = MoonReplay.Encode(Workshop); _bookmarkPosition = finn.transform.position;
                _bookmarkSampleTime = _sampleTime;
                Feedback = "已录制候选与执行采样；此按钮尚非磁盘存档。";
                return true;
            }
            catch (InvalidOperationException e) { Feedback = e.Message; return false; }
        }

        public bool RestoreBookmark()
        {
            if (_bookmark == null) { Feedback = "尚未录制书签。"; return false; }
            var restored = MoonReplay.Restore(_bookmark, Workshop.Generation + 1);
            finn.ResetPath();
            if (!finn.Warp(_bookmarkPosition)) { Feedback = "书签位置无法投影到导航面，未替换执行状态。"; return false; }
            // 角色表现可能已走过不足一次采样的距离；保留这段时间，不能在恢复后误判为超速。
            Workshop = restored; finn.isStopped = true; Paused = true; _sampleTime = _bookmarkSampleTime;
            Feedback = "离线回放完毕，保持暂停；继续时重新检查现场光路，没有重新询问模型。";
            return true;
        }

        void Update()
        {
            if (Workshop == null || !finn || !finn.isOnNavMesh) return;
            bool split = Workshop.Agreement?.Method == LightMethod.SplitBeam;
            var target = NextWaypoint();
            Vector3 root = finn.transform.position + Vector3.up * 1.2f;
            if (beacon)
            {
                var toward = target - finn.transform.position; toward.y = 0;
                beacon.position = root + (toward.sqrMagnitude > 0.01f ? toward.normalized * 1.4f : Vector3.right);
                beacon.gameObject.SetActive(!split && Workshop.LightOn);
            }
            bool observedLight = false;
            if (split && prism && runeA && runeB)
            {
                bool a = Trace(prism.position, runeA.position, beamA, Workshop.LightOn);
                bool b = Trace(prism.position, runeB.position, beamB, Workshop.LightOn);
                observedLight = a && b;
            }
            else if (beacon)
            {
                observedLight = Vector3.Distance(beacon.position, root) <= 2.5f && Trace(beacon.position, root, beamA, Workshop.LightOn);
                beamB.enabled = false;
            }
            if (observedLight != LightVerified) FlushObservedPose(LightVerified);
            LightVerified = observedLight;
            foreach (var renderer in runeRenderers)
            {
                // 只改本实例色，不改共享资产；退出时释放运行时克隆。
                renderer.material.color = observedLight ? new Color(0.55f, 0.9f, 0.8f) : new Color(0.15f, 0.23f, 0.3f);
            }
            bool executing = Workshop.State == MoonTaskState.Travelling;
            finn.isStopped = Paused || !executing || !Workshop.CanMove || !observedLight;
            if (!finn.isStopped)
            {
                if (!finn.hasPath) finn.SetDestination(target);
                else if (Vector3.Distance(finn.destination, target) > 0.1f) finn.SetDestination(target);
                if (!finn.pathPending && finn.pathStatus != NavMeshPathStatus.PathComplete)
                { Feedback = Workshop.Stop(true).Message; finn.ResetPath(); return; }
            }
            if (Paused || !executing) return;
            _sampleTime += Time.deltaTime;
            if (_sampleTime < 0.1f) return;
            FlushObservedPose(observedLight);
        }

        void FlushObservedPose(bool observedLight)
        {
            if (Workshop == null || Workshop.State != MoonTaskState.Travelling || !finn || _sampleTime <= 0) return;
            float interval = Mathf.Min(_sampleTime, 0.5f); _sampleTime = 0;
            var position = finn.transform.position;
            var local = position - worldOrigin;
            var result = Workshop.Observe(local.x, local.z, interval, observedLight);
            if (!result.Success)
            {
                Feedback = result.Message;
                Workshop.Stop(true); finn.isStopped = true; finn.ResetPath();
            }
            else if (result.Code == "completed") { Feedback = result.Message; finn.isStopped = true; finn.ResetPath(); }
        }

        void StopAgentImmediately()
        {
            if (!finn || !finn.isOnNavMesh) return;
            finn.isStopped = true; finn.velocity = Vector3.zero;
        }

        public Vector3 RoutePosition(int index)
        {
            var route = Workshop.Route;
            var point = route[Mathf.Min(index, route.Length - 1)];
            var position = worldOrigin + new Vector3((float)point[0], 0, (float)point[1]);
            position.y = _origin.y;
            return NavMesh.SamplePosition(position, out var hit, 2, finn.areaMask) ? hit.position : position;
        }

        Vector3 NextWaypoint() => RoutePosition(Workshop.Waypoint);

        static bool Trace(Vector3 start, Vector3 end, LineRenderer line, bool on)
        {
            line.enabled = on;
            if (!on) return false;
            var delta = end - start;
            bool blocked = Physics.Raycast(start, delta.normalized, out var hit, delta.magnitude, 1 << 29, QueryTriggerInteraction.Ignore);
            line.SetPosition(0, start); line.SetPosition(1, blocked ? hit.point : end);
            return !blocked && delta.magnitude <= 10f;
        }

        void OnDestroy()
        {
            foreach (var renderer in runeRenderers) if (renderer) Destroy(renderer.material);
        }

        void OnGUI()
        {
            if (Workshop == null || !showDeveloperPanel) return;
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 23, fontStyle = FontStyle.Bold, wordWrap = true };
                _body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
                _caption = new GUIStyle(_body) { fontSize = 12, normal = { textColor = new Color(0.65f, 0.82f, 0.85f) } };
            }
            float width = Mathf.Min(370, Screen.width * 0.4f);
            GUI.Box(new Rect(Screen.width - width - 12, 12, width, Screen.height - 24), GUIContent.none);
            GUILayout.BeginArea(new Rect(Screen.width - width, 24, width - 24, Screen.height - 48));
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("借一轮小月亮", _title);
            GUILayout.Label("月光工坊 · 首个引擎执行切片", _body);
            GUILayout.Space(8);
            GUILayout.Label("离线人工候选 / 0 网络请求\n这不是已接通的 AI，也不是六机制已完成。", _caption);
            GUILayout.Space(12);
            GUILayout.Label("01  写下你想试的方法", _body);
            _input = GUILayout.TextArea(_input, 500, GUILayout.Height(66));
            GUILayout.Label("02  显式选择测试候选（不解析上面的文字）", _caption);
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_fixture == "follow", "跟随引路")) _fixture = "follow";
            if (GUILayout.Toggle(_fixture == "split", "双束符文")) _fixture = "split";
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("澄清样本")) _fixture = "clarify";
            if (GUILayout.Button("拒绝样本")) _fixture = "decline";
            GUILayout.EndHorizontal();
            GUI.enabled = Workshop.State == MoonTaskState.None;
            if (GUILayout.Button("送入候选校验", GUILayout.Height(32))) SubmitFixture(_fixture, _input);
            GUI.enabled = true;
            if (Workshop.Agreement != null)
            {
                GUILayout.Space(10); GUILayout.Label("芬恩的条件", _body);
                GUILayout.Label(Workshop.Agreement.Summary, _caption);
            }
            GUI.enabled = Workshop.State == MoonTaskState.AwaitingConfirmation;
            if (GUILayout.Button("确认：预留两份月焰并开始", GUILayout.Height(32))) ConfirmAgreement();
            GUI.enabled = true;
            GUILayout.Space(10);
            GUILayout.Label("03  看实际发生了什么", _body);
            GUILayout.Label($"状态：{Workshop.State}\n可用月焰：{Workshop.FreeEnergy}/2  ·  路点：{Workshop.Waypoint}/3\n供光几何：{(LightVerified ? "通过" : "中断/遮挡")}  ·  版本：{Workshop.Version}", _caption);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Workshop.LightOn ? "切断月光" : "接通月光")) ToggleLight();
            if (GUILayout.Button("切换遮光屏")) ToggleOccluder();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Paused ? "继续" : "暂停")) Paused = !Paused;
            if (GUILayout.Button("撤回约定")) Feedback = Workshop.Stop().Message;
            GUILayout.EndHorizontal();
            GUILayout.Label(Feedback, _body);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("录制书签（内存）")) RecordBookmark();
            GUI.enabled = _bookmark != null;
            if (GUILayout.Button("回放书签")) RestoreBookmark();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("重开试验")) ResetExperiment();
            GUILayout.Space(10);
            GUILayout.Label("最近回执", _caption);
            foreach (var record in Workshop.History.Where(r => r.Kind == "receipt" || r.Kind == "rejected").Reverse().Take(4))
                GUILayout.Label(record.Code + " · " + record.Text, _caption);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
    }
}
