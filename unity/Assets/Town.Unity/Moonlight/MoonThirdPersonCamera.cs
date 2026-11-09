using UnityEngine;

namespace Town.Unity.Moonlight
{
    /// <summary>近景透视跟随。收近即时、退远缓动；不让平滑把镜头插入遮挡物。</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class MoonThirdPersonCamera : MonoBehaviour
    {
        public Transform target;
        public MoonRescueHud hud;
        public LayerMask obstacleMask = ~(1 << 28);
        public float Distance { get; private set; } = 5.5f;
        public float ActualDistance { get; private set; }
        public float Pitch { get; private set; } = 22;
        public float Yaw { get; private set; }
        public string Obstruction { get; private set; }
        public bool readHardwareInput = true;
        Vector3 _focus;
        bool _initialized, _captured;
        CursorLockMode _previousLock;
        bool _previousVisible;
        Transform _lastTarget;
        Renderer[] _body;
        bool _bodyHidden;

        void Awake()
        {
            var camera = GetComponent<Camera>();
            camera.orthographic = false; camera.fieldOfView = 55;
            camera.nearClipPlane = .1f; camera.rect = new Rect(0, 0, 1, 1);
        }

        public void Orbit(float yaw, float pitch)
        { Yaw = Mathf.Repeat(Yaw + yaw, 360); Pitch = Mathf.Clamp(Pitch + pitch, -8, 65); }
        public void Zoom(float delta) => Distance = Mathf.Clamp(Distance + delta, 2.4f, 8);
        public void ResetView()
        {
            Distance = 5.5f; Pitch = 22; Yaw = target ? target.eulerAngles.y : 0;
            _initialized = false;
        }

        void LateUpdate()
        {
            if (!target) return;
            if (readHardwareInput && !(hud && hud.IsModalOpen))
            {
                if (Input.GetMouseButtonDown(1)) CaptureCursor();
                if (_captured && Input.GetMouseButton(1)) Orbit(Input.GetAxis("Mouse X") * 2.5f, -Input.GetAxis("Mouse Y") * 2);
                if (Input.GetMouseButtonUp(1) || Input.GetKeyDown(KeyCode.Escape)) ReleaseCursor();
                Zoom(-Input.mouseScrollDelta.y * .5f);
                if (Input.GetKeyDown(KeyCode.Home)) ResetView();
            }
            else ReleaseCursor();
            UpdatePose(Time.deltaTime);
        }

        public void UpdatePose(float seconds)
        {
            if (!target) return;
            var desiredFocus = target.position + Vector3.up * 1.25f;
            bool snap = !_initialized || target != _lastTarget || Vector3.Distance(_focus, desiredFocus) > 5;
            if (target != _lastTarget)
            {
                SetBodyHidden(false);
                _body = target.GetComponentsInChildren<Renderer>();
            }
            _focus = snap ? desiredFocus : Vector3.Lerp(_focus, desiredFocus, 1 - Mathf.Exp(-16 * seconds));
            var direction = Quaternion.Euler(Pitch, Yaw, 0) * Vector3.back;
            float clear = Distance;
            Obstruction = null;
            if (Physics.SphereCast(_focus, .22f, direction, out var hit, Distance, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                clear = Mathf.Max(.15f, hit.distance - .08f);
                Obstruction = hit.collider.name;
            }
            ActualDistance = snap || clear < ActualDistance ? clear : Mathf.Lerp(ActualDistance, clear, 1 - Mathf.Exp(-5 * seconds));
            transform.SetPositionAndRotation(_focus + direction * ActualDistance, Quaternion.LookRotation(-direction));
            // 被墙挤到近身时不让头部网格占满画面；只影响渲染，不停动画或碰撞。
            SetBodyHidden(ActualDistance < .85f);
            _initialized = true; _lastTarget = target;
        }

        void SetBodyHidden(bool hidden)
        {
            if (_bodyHidden == hidden) return;
            if (_body != null) foreach (var renderer in _body) if (renderer) renderer.forceRenderingOff = hidden;
            _bodyHidden = hidden;
        }

        void CaptureCursor()
        {
            if (_captured) return;
            _previousLock = Cursor.lockState; _previousVisible = Cursor.visible; _captured = true;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
        void ReleaseCursor()
        {
            if (!_captured) return;
            Cursor.lockState = _previousLock; Cursor.visible = _previousVisible; _captured = false;
        }
        void OnApplicationFocus(bool focused) { if (!focused) ReleaseCursor(); }
        void OnDisable() { ReleaseCursor(); SetBodyHidden(false); }
    }
}
