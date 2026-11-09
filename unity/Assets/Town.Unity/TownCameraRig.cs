using UnityEngine;

namespace Town.Unity
{
    /// <summary>不依赖素材的 3C 镜头：平滑跟随、右键环绕、滚轮缩放、Home 回正、建筑遮挡收近。</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class TownCameraRig : MonoBehaviour
    {
        public Transform Target { get; private set; }
        public float Distance { get; private set; } = 23f;
        public float Pitch { get; private set; } = 52f;
        public float Yaw { get; private set; }
        private Vector3 _focus;
        public void Bind(Transform target) { Target = target; ResetView(); }
        public void ResetView()
        {
            Distance = 23f; Pitch = 52f; Yaw = 0;
            if (Target != null) { _focus = Target.position; Apply(1f); }
        }
        public void Orbit(float yaw, float pitch) { Yaw = Mathf.Repeat(Yaw + yaw, 360); Pitch = Mathf.Clamp(Pitch + pitch, 30, 75); }
        public void Zoom(float delta) => Distance = Mathf.Clamp(Distance + delta, 8f, 35f);

        private void LateUpdate()
        {
            if (Target == null) return;
            var game = TownGame.Instance;
            bool blocked = game != null && (game.InputBlocked || game.Paused || game.Runtime.IsDialoguePending || game.IsPointerOverPanel(Input.mousePosition));
            if (!blocked)
            {
                if (Input.GetMouseButton(1)) Orbit(Input.GetAxis("Mouse X") * 3f, -Input.GetAxis("Mouse Y") * 2f);
                Zoom(-Input.mouseScrollDelta.y * 2f);
                if (Input.GetKeyDown(KeyCode.Home)) ResetView();
            }
            Apply(1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        private void Apply(float blend)
        {
            _focus = Vector3.Lerp(_focus, Target.position + Vector3.up, blend);
            var rotation = Quaternion.Euler(Pitch, Yaw, 0);
            Vector3 direction = rotation * Vector3.back;
            float distance = Distance;
            if (Physics.SphereCast(_focus, 0.3f, direction, out var hit, distance, ~0, QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(2f, hit.distance - 0.3f);
            transform.position = _focus + direction * distance;
            transform.LookAt(_focus);
        }
    }
}
