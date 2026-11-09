using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity
{
    /// <summary>读者控制：点地寻路 / 相机相对 WASD / Shift 快走。面板、暂停与对话共享输入闸门。</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PlayerClickMover : MonoBehaviour
    {
        private NavMeshAgent _agent;
        private Camera _cam;
        public TownNavigation Navigation { get; private set; }
        public float walkSpeed = 6f;
        public bool ControlsBlocked => TownGame.Instance != null && (TownGame.Instance.InputBlocked ||
            TownGame.Instance.Runtime.IsDialoguePending || TownGame.Instance.Paused);

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            Navigation = GetComponent<TownNavigation>() ?? gameObject.AddComponent<TownNavigation>();
        }

        public void ResetPosition(Vector3 position)
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
            if (Navigation == null) Navigation = GetComponent<TownNavigation>() ?? gameObject.AddComponent<TownNavigation>();
            Navigation.Warp(position);
        }

        private void Update()
        {
            var game = TownGame.Instance;
            if (game != null)
            {
                if (!_agent.isOnNavMesh) return;
                Navigation.Suspend(ControlsBlocked);
                if (_agent.isStopped) return;
                game.ReportPlayerPosition(transform.position);
            }
            float x = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            float y = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            if (x != 0 || y != 0)
            {
                if (_cam == null) _cam = Camera.main;
                Vector3 forward = _cam != null ? _cam.transform.forward : Vector3.forward;
                forward.y = 0;
                forward.Normalize();
                Vector3 direction = Vector3.ClampMagnitude(forward * y + Vector3.Cross(Vector3.up, forward) * x, 1f);
                Navigation.Move(direction * walkSpeed * (Input.GetKey(KeyCode.LeftShift) ? 1.5f : 1f) * Time.deltaTime);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), 720 * Time.deltaTime);
                return;
            }
            if (!Input.GetMouseButtonDown(0) || (game != null && game.IsPointerOverPanel(Input.mousePosition))) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 200f) && NavMesh.SamplePosition(hit.point, out var nav, 2f, NavMesh.AllAreas))
                Navigation.TryNavigate(nav.position);
        }

        private void OnDrawGizmosSelected()
        {
            if (Navigation == null || Navigation.State != NavigationState.Moving) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(Navigation.Destination, 0.35f);
        }
    }
}
