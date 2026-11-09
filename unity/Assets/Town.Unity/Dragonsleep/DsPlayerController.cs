using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>言契师的第三人称移动：镜头相对 WASD，Shift 疾行，输入框聚焦时不移动。</summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class DsPlayerController : MonoBehaviour
    {
        public Transform CameraBasis;
        public float WalkSpeed = 3.2f;
        public float RunSpeed = 7.5f;
        public bool InputBlocked;

        CharacterController _cc;
        AmbientPose _pose;
        float _vy;
        bool _walking;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _pose = GetComponentInChildren<AmbientPose>();
        }

        void Update()
        {
            Vector2 input = InputBlocked ? Vector2.zero : new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            Vector3 fwd = CameraBasis ? Vector3.ProjectOnPlane(CameraBasis.forward, Vector3.up).normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 move = Vector3.ClampMagnitude(fwd * input.y + right * input.x, 1f);
            float speed = !InputBlocked && Input.GetKey(KeyCode.LeftShift) ? RunSpeed : WalkSpeed;
            if (move.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(move), 1f - Mathf.Exp(-12f * Time.deltaTime));
            _vy = _cc.isGrounded ? -2f : _vy + Physics.gravity.y * Time.deltaTime;
            _cc.Move((move * speed + Vector3.up * _vy) * Time.deltaTime);

            bool walking = move.sqrMagnitude > 0.01f;
            if (walking != _walking && _pose != null)
            {
                _walking = walking;
                _pose.Phase = 0f;
                _pose.Play(walking ? "Walk" : "Idle");
                var anim = _pose.GetComponent<Animator>();
                if (anim) anim.speed = walking ? speed / WalkSpeed : 1f;
            }
        }

        public void Teleport(Vector3 position)
        {
            _cc.enabled = false;
            transform.position = position;
            _cc.enabled = true;
            _vy = 0;
        }
    }
}
