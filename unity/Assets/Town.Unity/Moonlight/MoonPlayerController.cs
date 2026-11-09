using UnityEngine;

namespace Town.Unity.Moonlight
{
    /// <summary>读者的直接操控。角色碰撞与位移属于玩家，不接管芬恩的导航或到达回执。</summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class MoonPlayerController : MonoBehaviour
    {
        public Transform cameraBasis;
        public MoonRescueHud hud;
        public MoonWorkshopGame execution;
        public bool readHardwareInput = true;
        public float walkSpeed = 2.2f;
        public float runSpeed = 3f;
        public Vector3 PlanarVelocity { get; private set; }
        public bool Grounded => _controller && _controller.isGrounded;
        public bool InputBlocked => hud && hud.IsModalOpen || execution && execution.Paused;
        CharacterController _controller;
        Vector3 _spawn;
        Quaternion _spawnRotation;
        float _vertical;
        int _generation;
        bool _focused = true;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _spawn = transform.position; _spawnRotation = transform.rotation;
        }

        void Update()
        {
            if (execution && execution.Workshop != null && _generation != execution.Workshop.Generation)
            {
                if (_generation != 0) ResetPosition();
                _generation = execution.Workshop.Generation;
            }
            if (!readHardwareInput) return;
            var move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            // 卡帧时仍走有界的小步碰撞，不整帧丢弃输入，也不一次穿过薄墙。
            float remaining = Mathf.Min(Time.deltaTime, .2f);
            bool fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            while (remaining > .0001f)
            {
                float step = Mathf.Min(remaining, .04f);
                SimulateMotion(move, fast, step); remaining -= step;
            }
        }

        // 测试和硬件输入走同一条碰撞/加速路径；不提供绕开控制器的“测试移动”。
        public void SimulateMotion(Vector2 input, bool running, float seconds)
        {
            if (!_controller || !_controller.enabled || seconds <= 0 || seconds > .1f) return;
            bool blocked = !_focused || InputBlocked;
            input = blocked ? Vector2.zero : Vector2.ClampMagnitude(input, 1);
            var forward = cameraBasis ? cameraBasis.forward : Vector3.forward;
            forward.y = 0; forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            var desired = (right * input.x + forward * input.y) * (running ? runSpeed : walkSpeed);
            if (blocked) PlanarVelocity = Vector3.zero;
            else PlanarVelocity = Vector3.MoveTowards(PlanarVelocity, desired, (input.sqrMagnitude > .001f ? 14 : 22) * seconds);
            if (PlanarVelocity.sqrMagnitude > .04f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(PlanarVelocity), 600 * seconds);
            if (_controller.isGrounded && _vertical < 0) _vertical = -2;
            _vertical = Mathf.Max(-24, _vertical - 22 * seconds);
            var before = transform.position;
            _controller.Move((PlanarVelocity + Vector3.up * _vertical) * seconds);
            // 动画使用实际位移：顶墙时不能原地跑步冒充行进。
            var actual = (transform.position - before) / seconds; actual.y = 0;
            ActualSpeed = actual.magnitude;
            if (transform.position.y < _spawn.y - 12) ResetPosition();
        }

        public float ActualSpeed { get; private set; }
        public void ResetPosition()
        {
            if (!_controller) return;
            _controller.enabled = false;
            transform.SetPositionAndRotation(_spawn, _spawnRotation);
            _controller.enabled = true;
            PlanarVelocity = Vector3.zero; ActualSpeed = 0; _vertical = -2;
        }

        void OnApplicationFocus(bool focused)
        { _focused = focused; if (!focused) { PlanarVelocity = Vector3.zero; ActualSpeed = 0; } }
    }
}
