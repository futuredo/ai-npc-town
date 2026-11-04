using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity
{
    /// <summary>NPC 的表现层：寻路、头顶名牌与状态。不持有任何世界事实，只执行 Core 下发的移动。</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class NpcAgent : MonoBehaviour
    {
        public string npcId;
        public string displayName;
        public BillboardLabel label;

        private NavMeshAgent _agent;
        private string _status = "";

        public bool IsMoving => _agent != null && _agent.hasPath && _agent.remainingDistance > _agent.stoppingDistance + 0.05f;

        private void Awake() => _agent = GetComponent<NavMeshAgent>();

        public void WarpTo(Vector3 p)
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
            _agent.Warp(p);
        }

        public void MoveTo(Vector3 p)
        {
            if (_agent == null) _agent = GetComponent<NavMeshAgent>();
            _agent.SetDestination(p);
        }

        public void SetStatus(string status)
        {
            _status = status ?? "";
            RefreshLabel();
        }

        private void Start() => RefreshLabel();

        private void RefreshLabel()
        {
            if (label != null) label.SetText(string.IsNullOrEmpty(_status) ? displayName : $"{displayName}\n<size=28>{_status}</size>");
        }
    }
}
