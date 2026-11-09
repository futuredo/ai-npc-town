using UnityEngine;

namespace Town.Unity.Moonlight
{
    /// <summary>局内观察镜头：左侧视区拖动、缩放与复位，不穿透右侧交互面板。</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class MoonSceneCamera : MonoBehaviour
    {
        public Vector3 focus;
        Camera _camera;
        Vector3 _start;
        Quaternion _rotation;
        float _zoom;
        void Awake() { _camera = GetComponent<Camera>(); _start = transform.position; _rotation = transform.rotation; _zoom = _camera.orthographicSize; }
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Home)) { transform.SetPositionAndRotation(_start, _rotation); _camera.orthographicSize = _zoom; }
            if (Input.mousePosition.x > Screen.width * .66f) return;
            _camera.orthographicSize = Mathf.Clamp(_camera.orthographicSize - Input.mouseScrollDelta.y * .6f, 5, 17);
            if (!Input.GetMouseButton(1)) return;
            var right = transform.right; right.y = 0;
            var forward = Vector3.Cross(right, Vector3.up).normalized;
            var delta = -right * Input.GetAxis("Mouse X") * .35f - forward * Input.GetAxis("Mouse Y") * .35f;
            var proposed = transform.position + delta;
            if (Vector3.Distance(proposed, _start) < 18) transform.position = proposed;
        }
    }
}
