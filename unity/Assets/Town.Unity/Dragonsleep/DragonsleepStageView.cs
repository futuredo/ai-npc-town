using System.Collections.Generic;
using System.Collections.ObjectModel;
using Town.Core.Dragonsleep;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    public enum WhelpStageMode { Hidden, Visible, Following, Concealed, Captured, Reunited }

    /// <summary>主线程空闲时复制的表现数据。动画不持有世界引用，不发起规则事务。</summary>
    public sealed class DragonsleepStageFrame
    {
        public readonly string PlayerPlace, WhelpPlace, Captor;
        public readonly WhelpStageMode Mode;
        public readonly bool MotherDescended, Fled, FinalDawn;
        public readonly IReadOnlyDictionary<string, bool> Guards;

        DragonsleepStageFrame(DsWorld world)
        {
            PlayerPlace = world.PlayerPlace; WhelpPlace = world.Whelp.Place; Captor = world.Whelp.CapturedBy;
            var wh = world.Whelp;
            Mode = wh.CapturedBy != null ? WhelpStageMode.Captured : wh.WithMother ? WhelpStageMode.Reunited
                : wh.Following ? wh.Hidden ? WhelpStageMode.Concealed : WhelpStageMode.Following
                : wh.Hidden ? WhelpStageMode.Hidden : WhelpStageMode.Visible;
            MotherDescended = world.Flags.ContainsKey("mother_descended");
            Fled = world.Flags.ContainsKey("whelp_fled") && wh.Place == "docks" && Mode == WhelpStageMode.Hidden;
            FinalDawn = world.FinalDawn;
            var guards = new Dictionary<string, bool>();
            foreach (string place in world.Places.Keys) guards[place] = DsGuards.IsWatched(world, place);
            Guards = new ReadOnlyDictionary<string, bool>(guards);
        }

        public static DragonsleepStageFrame Capture(DsWorld world) => new DragonsleepStageFrame(world);
    }

    /// <summary>纯场景投影：跟随、斗篷、铁笼、岗位与母龙落位。灰盒装置不冒充正式素材。</summary>
    public sealed class DragonsleepStageView : MonoBehaviour
    {
        public DragonsleepStageFrame Frame { get; private set; }
        public Transform Cloak { get; private set; }
        public Transform Cage { get; private set; }
        public Transform Mother { get; private set; }
        public IReadOnlyDictionary<string, Transform> GuardPosts => _guards;
        public bool MotherSettled { get; private set; }
        public bool IsFleeing { get; private set; }

        readonly Dictionary<string, Transform> _places = new Dictionary<string, Transform>();
        readonly Dictionary<string, Transform> _guards = new Dictionary<string, Transform>();
        Transform _player, _whelp;
        DragonCircle _circle;
        Animator _whelpAnimator, _motherAnimator;
        Material _iron, _cloth;
        bool _circleEnabled;
        string _motion;
        float _fleeUntil;
        Vector3 _motherStart;
        Quaternion _motherRotation;
        Camera _endingCamera;
        DsFollowCamera _followCamera;
        bool _followingWasEnabled;

        public void Initialize(Transform player, Transform whelp, Transform places, DragonCircle circle)
        {
            _player = player; _whelp = whelp; _circle = circle;
            if (places != null)
                foreach (Transform place in places) _places[place.name.Replace("Place_", "")] = place;
            if (whelp != null) _whelpAnimator = whelp.GetComponentInChildren<Animator>(true);
            if (circle != null)
            {
                Mother = circle.transform; _circleEnabled = circle.enabled;
                _motherStart = Mother.position; _motherRotation = Mother.rotation;
                _motherAnimator = Mother.GetComponentInChildren<Animator>(true);
            }
            _iron = MaterialFor("Stage Graybox Iron", new Color(0.25f, 0.29f, 0.31f));
            _cloth = MaterialFor("Stage Graybox Cloak", new Color(0.12f, 0.24f, 0.29f));
            Cloak = Part("Graybox_CloakBundle", transform, PrimitiveType.Capsule, Vector3.zero,
                new Vector3(0.7f, 0.45f, 0.9f), _cloth);
            Cage = new GameObject("Graybox_IronCage").transform; Cage.SetParent(transform, false);
            for (int side = -1; side <= 1; side += 2)
                for (int i = 0; i < 5; i++)
                {
                    float offset = (i - 2) * 0.55f;
                    Part("Bar", Cage, PrimitiveType.Cube, new Vector3(offset, 1.3f, side * 1.4f),
                        new Vector3(0.08f, 2.6f, 0.08f), _iron);
                    Part("Bar", Cage, PrimitiveType.Cube, new Vector3(side * 1.4f, 1.3f, offset),
                        new Vector3(0.08f, 2.6f, 0.08f), _iron);
                }
            Part("Roof", Cage, PrimitiveType.Cube, new Vector3(0, 2.65f, 0), new Vector3(3, 0.12f, 3), _iron);
            foreach (var pair in _places)
            {
                string key = pair.Key == "castle_gate" ? "castle" : pair.Key == "dragon_skull" ? "skull_hill" : pair.Key;
                var post = new GameObject("Graybox_Guard_" + key).transform; post.SetParent(transform, false);
                post.position = pair.Value.position + new Vector3(3.5f, 0, 0);
                Part("Body", post, PrimitiveType.Capsule, new Vector3(0, 1, 0), new Vector3(0.65f, 1, 0.65f), _cloth);
                Part("Helmet", post, PrimitiveType.Cube, new Vector3(0, 1.9f, 0), Vector3.one * 0.5f, _iron);
                Part("Spear", post, PrimitiveType.Cylinder, new Vector3(0.55f, 1.45f, 0), new Vector3(0.07f, 1.4f, 0.07f), _iron);
                _guards[key] = post;
            }
            Cloak.gameObject.SetActive(false); Cage.gameObject.SetActive(false);
            foreach (var guard in _guards.Values) guard.gameObject.SetActive(false);
        }

        public void Apply(DragonsleepStageFrame frame)
        {
            if (frame.Fled && Frame != null && Frame.Mode == WhelpStageMode.Following && _whelp != null && _whelp.gameObject.activeSelf)
            { IsFleeing = !DsUiMotion.Reduced; _fleeUntil = Time.time + 1.2f; }
            if (!frame.Fled) IsFleeing = false;
            Frame = frame;
            foreach (var pair in _guards)
                pair.Value.gameObject.SetActive(frame.Guards.TryGetValue(pair.Key, out bool watched) && watched);
            if (!frame.MotherDescended && _circle != null && !_circle.enabled)
            {
                _circle.enabled = _circleEnabled;
                if (!_circleEnabled) { Mother.position = _motherStart; Mother.rotation = _motherRotation; }
                MotherSettled = false;
            }
            if (frame.MotherDescended && _circle != null) _circle.enabled = false;
            bool captured = frame.Mode == WhelpStageMode.Captured && frame.Captor == "osric";
            Cage.gameObject.SetActive(captured);
            if (captured) Cage.position = Anchor(frame.WhelpPlace) + new Vector3(0, 0, 4);
            Cloak.gameObject.SetActive(frame.Mode == WhelpStageMode.Concealed);
            if (_whelp != null)
                _whelp.gameObject.SetActive(IsFleeing || frame.Mode != WhelpStageMode.Hidden && frame.Mode != WhelpStageMode.Concealed
                    && (frame.Mode != WhelpStageMode.Captured || captured));
        }

        void LateUpdate()
        {
            if (Frame == null || _player == null) return;
            bool reduced = DsUiMotion.Reduced;
            if (Frame.MotherDescended && Mother != null)
            {
                Vector3 landing = Anchor("skull_hill") + new Vector3(5, 4, 6);
                Mother.position = reduced ? landing : Vector3.MoveTowards(Mother.position, landing, 85f * Time.deltaTime);
                MotherSettled = Vector3.Distance(Mother.position, landing) < 0.1f;
                Face(Mother, Anchor("skull_hill"));
                Play(_motherAnimator, MotherSettled ? "Idle_Ground" : "Fly_Idle");
            }
            UpdateEndingCamera();
            if (Cloak.gameObject.activeSelf)
            {
                Vector3 bundle = !Frame.FinalDawn && Frame.PlayerPlace == Frame.WhelpPlace
                    ? _player.position - _player.forward * 0.8f + Vector3.up * 0.55f
                    : Anchor(Frame.WhelpPlace) + Vector3.up * 0.55f;
                Move(Cloak, bundle, reduced);
            }
            if (_whelp == null || !_whelp.gameObject.activeSelf) return;
            if (IsFleeing)
            {
                Vector3 escape = Anchor("docks") + Vector3.up * 3;
                _whelp.position = Vector3.MoveTowards(_whelp.position, escape, 18f * Time.deltaTime);
                Face(_whelp, escape); Play(_whelpAnimator, "Run_Fly_Forward");
                if (Time.time >= _fleeUntil) { IsFleeing = false; _whelp.gameObject.SetActive(false); }
                return;
            }
            Vector3 target;
            switch (Frame.Mode)
            {
                case WhelpStageMode.Following:
                    target = !Frame.FinalDawn && Frame.PlayerPlace == Frame.WhelpPlace
                        ? _player.position - _player.forward * 1.8f + _player.right * 0.8f + Vector3.up * 0.9f
                        : Anchor(Frame.WhelpPlace) + new Vector3(0.6f, 0.9f, 0.8f);
                    break;
                case WhelpStageMode.Captured:
                    target = Cage.position + Vector3.up * 0.8f; break;
                case WhelpStageMode.Reunited:
                    target = Mother != null && MotherSettled ? Mother.position - Mother.forward * 2 + Vector3.up
                        : Anchor("skull_hill") + new Vector3(1, 0.9f, 1);
                    break;
                default:
                    target = Anchor(Frame.WhelpPlace) + new Vector3(0.6f, 0.9f, 0.8f); break;
            }
            // 地图快捷移动不是走路，跨地点先落位，避免雏龙穿越整座城追赶。
            bool moving = Vector3.Distance(_whelp.position, target) > 0.3f;
            Move(_whelp, target, reduced);
            Face(_whelp, Frame.Mode == WhelpStageMode.Following ? _player.position : target + Vector3.forward);
            string motion = moving && Frame.Mode != WhelpStageMode.Captured ? "Walk_Fly_Forward" : "Fly_Idle";
            if (_motion != motion) { Play(_whelpAnimator, motion); _motion = motion; }
        }

        public void ResetStage()
        {
            Frame = null; _motion = null; MotherSettled = false; IsFleeing = false;
            if (_followCamera != null) _followCamera.enabled = _followingWasEnabled;
            _endingCamera = null; _followCamera = null;
            if (_circle != null)
            { _circle.enabled = _circleEnabled; Mother.position = _motherStart; Mother.rotation = _motherRotation; }
        }

        public void ShowEndingView()
        {
            if (_endingCamera != null) return;
            _endingCamera = Camera.main;
            if (_endingCamera == null) return;
            _followCamera = _endingCamera.GetComponent<DsFollowCamera>();
            if (_followCamera != null) { _followingWasEnabled = _followCamera.enabled; _followCamera.enabled = false; }
        }

        void UpdateEndingCamera()
        {
            if (_endingCamera == null) return;
            bool cage = Cage.gameObject.activeSelf;
            Vector3 pivot = cage ? Cage.position + Vector3.up : Anchor("skull_hill") + Vector3.up * 4;
            float distance = cage ? 10 : 35;
            if (!cage && Mother != null)
            {
                var bounds = new Bounds(Mother.position, Vector3.one * 8);
                foreach (var renderer in Mother.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
                pivot = bounds.center;
                distance = Mathf.Max(distance, bounds.extents.magnitude / Mathf.Tan(_endingCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f);
            }
            _endingCamera.transform.position = pivot + new Vector3(0.6f, 0.35f, -1).normalized * distance;
            _endingCamera.transform.LookAt(pivot);
        }

        Vector3 Anchor(string place)
        {
            string key = place == "castle" ? "castle_gate" : place == "skull_hill" ? "dragon_skull" : place;
            return _places.TryGetValue(key ?? "", out var anchor) ? anchor.position : _player.position;
        }

        static void Move(Transform target, Vector3 position, bool reduced)
        {
            target.position = reduced || Vector3.Distance(target.position, position) > 35f ? position
                : Vector3.MoveTowards(target.position, position, 7.5f * Time.deltaTime);
        }

        static void Face(Transform target, Vector3 position)
        {
            Vector3 direction = position - target.position; direction.y = 0;
            if (direction.sqrMagnitude > 0.01f) target.rotation = Quaternion.LookRotation(direction);
        }

        static void Play(Animator animator, string state)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;
            int hash = Animator.StringToHash(state);
            if (!animator.HasState(0, hash)) return;
            if (animator.GetCurrentAnimatorStateInfo(0).shortNameHash != hash && !animator.IsInTransition(0))
                animator.CrossFadeInFixedTime(hash, 0.2f);
        }

        static Material MaterialFor(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            return new Material(shader) { name = name, color = color };
        }

        static Transform Part(string name, Transform parent, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type); part.name = name; part.transform.SetParent(parent, false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
            var collider = part.GetComponent<Collider>(); if (collider != null) Destroy(collider);
            return part.transform;
        }

        void OnDestroy() { if (_iron != null) Destroy(_iron); if (_cloth != null) Destroy(_cloth); }
    }
}
