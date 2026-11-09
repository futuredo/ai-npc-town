using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Town.Core.Dragonsleep;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 《龙眠镇》局内入口：把 Core 的 DragonsleepSession 接到场景（地点、角色、雏龙）和织言镜界面上。
    /// 默认使用离线规则提供方，进入 Play 不会产生任何费用。
    /// </summary>
    public sealed class DragonsleepGame : MonoBehaviour
    {
        public DsPlayerController Player;
        public Transform PlacesRoot;
        public Transform Whelp;
        public LoomLensHud Hud;
        public float PlaceRadius = 45f;
        public float TalkRadius = 4.5f;

        public DragonsleepSession Session { get; private set; }
        public string NearestActorId { get; private set; }
        public bool Busy { get; private set; }
        public DsEndings.Ending Ending { get; private set; }

        /// <summary>可由 Editor 工具替换为真实模型提供方（受预算闸门约束）。</summary>
        public static Func<IAiProvider> ProviderFactory = () => new RuleProvider();

        readonly Dictionary<string, Transform> _places = new Dictionary<string, Transform>();
        readonly Dictionary<string, Transform> _actors = new Dictionary<string, Transform>();
        string _exportPath;
        int _dayStartTrace;
        Animator _whelpAnimator;
        Coroutine _whelpReaction;
        readonly System.Threading.CancellationTokenSource _lifetime = new System.Threading.CancellationTokenSource();

        public IReadOnlyDictionary<string, Transform> ActorTransforms => _actors;

        void Awake()
        {
            Session = new DragonsleepSession(ProviderFactory());
            if (PlacesRoot != null)
                foreach (Transform t in PlacesRoot) _places[t.name.Replace("Place_", "")] = t;
            foreach (var tag in FindObjectsByType<DragonsleepActorTag>(FindObjectsSortMode.None)) _actors[tag.ActorId] = tag.transform;
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "../../.local/dragonsleep/traces"));
            _exportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/dragonsleep/traces",
                "trace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl"));
            Session.TraceCompleted += t => { try { File.AppendAllText(_exportPath, MiniJson.Write(t.ToRecord()) + "\n"); } catch (IOException) { } };
            if (Whelp != null) _whelpAnimator = Whelp.GetComponentInChildren<Animator>(true);
        }

        void Start() => ReflectWorld();

        void OnDestroy()
        {
            _lifetime.Cancel();
            (Session?.Provider as IDisposable)?.Dispose();
            _lifetime.Dispose();
        }

        void Update()
        {
            if (Player == null) return;
            UpdatePlace();
            UpdateNearestActor();
            bool typing = Hud != null && (Hud.Typing || Hud.ReadingPanelOpen);
            Player.InputBlocked = typing || Busy || Ending != null;
            var cam = Player.CameraBasis ? Player.CameraBasis.GetComponent<DsFollowCamera>() : null;
            if (cam) cam.InputBlocked = typing || Busy;
        }

        void UpdatePlace()
        {
            string best = null; float bestD = PlaceRadius;
            foreach (var kv in _places)
            {
                float d = Vector3.Distance(Flat(kv.Value.position), Flat(Player.transform.position));
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            if (best == "castle_gate") best = "castle";
            if (best == "dragon_skull") best = "skull_hill";
            if (best != null && Session.World.Places.ContainsKey(best)) Session.World.PlayerPlace = best;
        }

        void UpdateNearestActor()
        {
            NearestActorId = null; float bestD = TalkRadius;
            foreach (var kv in _actors)
            {
                if (kv.Key == "player") continue;
                float d = Vector3.Distance(kv.Value.position, Player.transform.position);
                if (d < bestD) { bestD = d; NearestActorId = kv.Key; }
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);

        public string PlaceName => Session.World.Places.TryGetValue(Session.World.PlayerPlace, out var n) ? n : Session.World.PlayerPlace;

        public IEnumerable<string> TravelTargets => _places.Keys;

        public void TravelTo(string place)
        {
            if (!_places.TryGetValue(place, out var p) || Player == null) return;
            Player.Teleport(p.position + new Vector3(-3f, 0.2f, -3f));
        }

        // ---------- 玩家三种输入 ----------

        public async void Submit(LoomMode mode, string text)
        {
            if (Busy || string.IsNullOrWhiteSpace(text) || Ending != null) return;
            Busy = true;
            try
            {
                AiTrace trace;
                if (mode == LoomMode.Speak)
                {
                    if (NearestActorId == null) { Hud?.Toast("身边没有可以说话的人。走近一些（4 米内）。"); return; }
                    Face(NearestActorId);
                    SetPose(NearestActorId, "Talk");
                    trace = await Session.SpeakAsync(NearestActorId, text, _lifetime.Token);
                    SetPose(NearestActorId, trace.Succeeded ? "Talk" : "Idle");
                }
                else if (mode == LoomMode.Act) trace = await Session.ActAsync(text, _lifetime.Token);
                else trace = await Session.CraftAsync(text, _lifetime.Token);
                if (this == null) return;
                if (Session.HasPendingCraft)
                { Hud?.Present(trace); Hud?.ShowCraftProposal(); return; }
                ReflectWorld(trace);
                Hud?.Present(trace);
                if (Session.NightPending) await RunNight();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (this != null) { Debug.LogException(e); Hud?.Toast("织言机出错：" + e.GetType().Name); } }
            finally { Busy = false; }
        }

        public async void DecideCraft(bool accept)
        {
            if (Busy) return;
            Busy = true;
            try
            {
                var trace = Session.ConfirmCraft(accept);
                if (trace == null) return;
                ReflectWorld(); Hud?.Present(trace);
                if (Session.NightPending) await RunNight();
            }
            catch (OperationCanceledException) { }
            finally { Busy = false; }
        }

        public async void EndPhase()
        {
            if (Busy || Ending != null || (Hud != null && Hud.ModalOpen)) return;
            Busy = true;
            try { await RunNight(); }
            catch (OperationCanceledException) { }
            finally { Busy = false; }
        }

        async Task RunNight()
        {
            int day = Session.World.Day;
            var traces = await Session.EndPhaseAsync(_lifetime.Token);
            if (this == null) return;
            ReflectWorld();
            if (traces.Count > 0)
            {
                Hud?.PresentNight(traces);
                var dayTraces = Session.Traces.Skip(_dayStartTrace).ToList();
                _dayStartTrace = Session.Traces.Count;
                if (!Session.World.FinalDawn) Hud?.ShowDayRecap(DsObjectives.Recap(Session.World, day, dayTraces));
            }
            else Hud?.Toast("时间来到 " + Session.World.ClockText);
            if (Session.World.FinalDawn)
            {
                Ending = DsEndings.Evaluate(Session.World);
                Hud?.ShowEnding(Ending);
            }
        }

        public void Restart()
        {
            Session.Restart();
            Ending = null;
            _dayStartTrace = 0;
            ReflectWorld();
            Hud?.Toast("新的一局开始了。旧局未返回的请求将被丢弃。");
        }

        /// <summary>把世界状态反映到场景：居民位置、雏龙是否现身。</summary>
        void ReflectWorld(AiTrace trace = null)
        {
            var w = Session.World;
            foreach (var a in w.Actors.Values)
            {
                if (!_actors.TryGetValue(a.Id, out var t) || !_places.TryGetValue(a.Place == "castle" ? "castle_gate" : a.Place, out var p)) continue;
                if (Vector3.Distance(Flat(t.position), Flat(p.position)) > PlaceRadius)
                    t.position = p.position + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0, UnityEngine.Random.Range(-3f, 3f));
            }
            if (Whelp != null)
            {
                bool show = w.Whelp.CapturedBy == null && !w.Whelp.Hidden && !w.Whelp.WithMother;
                Whelp.gameObject.SetActive(show);
                // 演出只读提交后的回执；台词或尚待确认的提案不能触发成功动作。
                if (show && trace?.Receipt?.Applied == true && trace.Receipt.Effects.Any(e => e.Path.StartsWith("whelp.")))
                {
                    if (_whelpReaction != null) StopCoroutine(_whelpReaction);
                    _whelpReaction = StartCoroutine(ReactWhelp(trace.Succeeded));
                }
            }
        }

        System.Collections.IEnumerator ReactWhelp(bool success)
        {
            if (_whelpAnimator == null) yield break;
            if (Player != null)
            {
                var direction = Player.transform.position - Whelp.position; direction.y = 0;
                if (direction.sqrMagnitude > 0.01f) Whelp.rotation = Quaternion.LookRotation(direction);
            }
            string state = success ? "Fly_Idle01" : "Combat_Fly_Dodge";
            if (_whelpAnimator.HasState(0, Animator.StringToHash(state))) _whelpAnimator.CrossFadeInFixedTime(state, 0.18f);
            yield return new WaitForSeconds(1.4f);
            if (_whelpAnimator != null && _whelpAnimator.gameObject.activeInHierarchy)
                _whelpAnimator.CrossFadeInFixedTime("Fly_Idle", 0.22f);
            _whelpReaction = null;
        }

        void Face(string actorId)
        {
            if (!_actors.TryGetValue(actorId, out var t)) return;
            Vector3 d = Player.transform.position - t.position; d.y = 0;
            if (d.sqrMagnitude > 0.01f) t.rotation = Quaternion.LookRotation(d);
        }

        void SetPose(string actorId, string state)
        {
            if (_actors.TryGetValue(actorId, out var t)) t.GetComponentInChildren<AmbientPose>()?.Play(state);
        }

        public string ActorName(string id) => Session.World.Actors.TryGetValue(id ?? "", out var a) ? a.Name : id;
        public string ExportPath => _exportPath;
    }

    public enum LoomMode { Speak, Act, Craft }
}
