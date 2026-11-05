using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity
{
    /// <summary>玩家：鼠标左键点地面移动。灰盒阶段够用，之后再换操作方式。</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PlayerClickMover : MonoBehaviour
    {
        private NavMeshAgent _agent;
        private Camera _cam;

        private void Awake() => _agent = GetComponent<NavMeshAgent>();

        public void ResetPosition(Vector3 position)
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
            if (_agent.isOnNavMesh) _agent.ResetPath();
            _agent.Warp(position);
        }

        private void Update()
        {
            var game = TownGame.Instance;
            if (game != null)
            {
                if (!_agent.isOnNavMesh) return;
                _agent.isStopped = game.InputBlocked || game.Runtime.IsDialoguePending || game.Paused;
                if (_agent.isStopped) return;
                game.ReportPlayerPosition(transform.position);
                if (game.IsPointerOverPanel(Input.mousePosition)) return;
            }
            if (!Input.GetMouseButtonDown(0)) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 200f) && NavMesh.SamplePosition(hit.point, out var nav, 2f, NavMesh.AllAreas))
                _agent.SetDestination(nav.position);
        }
    }
}
