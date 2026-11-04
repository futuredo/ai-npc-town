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

        private void Update()
        {
            if (TownGame.Instance != null && TownGame.Instance.InputBlocked) return;
            if (!Input.GetMouseButtonDown(0)) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 200f) && NavMesh.SamplePosition(hit.point, out var nav, 2f, NavMesh.AllAreas))
                _agent.SetDestination(nav.position);
        }
    }
}
