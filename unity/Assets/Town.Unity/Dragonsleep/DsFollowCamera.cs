using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>越肩跟随镜头：右键拖动旋转、滚轮缩放、遇到遮挡向前收。</summary>
    public sealed class DsFollowCamera : MonoBehaviour
    {
        public Transform Target;
        public float Distance = 6.5f;
        public float Height = 1.6f;
        public float Yaw = 30f;
        public float Pitch = 16f;
        public Vector3 ShoulderOffset = new Vector3(0.55f, 0f, 0f);
        public bool InputBlocked;

        float _actual;

        void LateUpdate()
        {
            if (Target == null) return;
            if (!InputBlocked)
            {
                if (Input.GetMouseButton(1))
                {
                    Yaw += Input.GetAxis("Mouse X") * 4f;
                    Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * 3f, -5f, 60f);
                }
                Distance = Mathf.Clamp(Distance - Input.mouseScrollDelta.y * 0.6f, 2.5f, 14f);
                if (Input.GetKey(KeyCode.Q)) Yaw -= 90f * Time.deltaTime;
                if (Input.GetKey(KeyCode.E)) Yaw += 90f * Time.deltaTime;
            }
            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 pivot = Target.position + Vector3.up * Height + rot * ShoulderOffset;
            Vector3 dir = rot * Vector3.back;
            float want = Distance;
            if (Physics.SphereCast(pivot, 0.25f, dir, out var hit, Distance, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(Target))
                want = Mathf.Max(1.2f, hit.distance - 0.1f);
            _actual = _actual <= 0 ? want : Mathf.Lerp(_actual, want, 1f - Mathf.Exp(-10f * Time.deltaTime));
            transform.position = pivot + dir * _actual;
            transform.rotation = Quaternion.LookRotation(pivot - transform.position);
        }
    }
}
