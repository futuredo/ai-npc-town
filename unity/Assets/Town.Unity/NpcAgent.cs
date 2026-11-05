using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity
{
    /// <summary>
    /// NPC 的表现层：寻路、头顶名牌与状态。不持有任何世界事实——
    /// 何时出发、何时到达由 Core 按游戏时间判定，这里只负责把人走过去。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class NpcAgent : MonoBehaviour
    {
        public string npcId;
        public string displayName;
        public BillboardLabel label;

        private NavMeshAgent _agent;
        private string _status = "";
        private string _targetId;
        private float _baseSpeed = -1f;

        public bool IsMoving => _agent != null && _agent.hasPath && _agent.remainingDistance > _agent.stoppingDistance + 0.05f;

        private void Awake() => _agent = GetComponent<NavMeshAgent>();

        private NavMeshAgent Agent
        {
            get
            {
                if (_agent == null) _agent = GetComponent<NavMeshAgent>();
                if (_baseSpeed < 0) _baseSpeed = _agent.speed;
                return _agent;
            }
        }

        public void WarpTo(Vector3 p, string targetId = null)
        {
            if (Agent.isOnNavMesh) Agent.ResetPath();
            Agent.Warp(p);
            _targetId = targetId;
        }

        public void MoveTo(Vector3 p)
        {
            if (Agent.isOnNavMesh) Agent.SetDestination(p);
        }

        /// <summary>每帧由 TownGame 调用：Core 里的目标地点变了才重新寻路；速度随时间倍率变化。</summary>
        public void Sync(string targetId, Vector3 point, float speedMultiplier, bool stopped)
        {
            var agent = Agent;
            agent.speed = _baseSpeed * Mathf.Max(1f, speedMultiplier);
            if (agent.isOnNavMesh) agent.isStopped = stopped;
            if (targetId == _targetId) return;
            _targetId = targetId;
            MoveTo(point);
        }

        public void SetStatus(string status)
        {
            status = status ?? "";
            if (status == _status) return;
            _status = status;
            RefreshLabel();
        }

        private void Start() => RefreshLabel();

        private void RefreshLabel()
        {
            if (label != null) label.SetText(string.IsNullOrEmpty(_status) ? displayName : $"{displayName}\n<size=28>{_status}</size>");
        }
    }
}
