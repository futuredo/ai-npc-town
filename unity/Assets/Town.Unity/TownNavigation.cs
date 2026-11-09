using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity
{
    public enum NavigationState { Idle, Moving, Arrived, Unreachable, Blocked }

    /// <summary>共享寻路边界：只接受完整路径，明确报告不可达/卡住；暂停不累计卡住时间。</summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class TownNavigation : MonoBehaviour
    {
        public NavigationState State { get; private set; }
        public string Feedback { get; private set; } = "";
        public Vector3 Destination { get; private set; }
        public bool Suspended { get; private set; }
        private NavMeshAgent _agent;
        private Vector3 _lastProgress;
        private float _stalled;
        private bool _retried;
        public NavMeshAgent Agent => _agent != null ? _agent : (_agent = GetComponent<NavMeshAgent>());

        public bool TryNavigate(Vector3 point)
        {
            if (!Agent.isOnNavMesh || !NavMesh.SamplePosition(point, out var hit, 2f, Agent.areaMask))
                return Reject("那里没有可走的路。");
            var path = new NavMeshPath();
            if (!Agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete)
                return Reject("通往那里的路被挡住了。");
            if (!Agent.SetPath(path)) return Reject("暂时无法开始移动。");
            Destination = hit.position;
            State = NavigationState.Moving;
            Feedback = "正在前往目标";
            _lastProgress = transform.position;
            _stalled = 0;
            _retried = false;
            return true;
        }

        private bool Reject(string message)
        {
            Cancel();
            State = NavigationState.Unreachable;
            Feedback = message;
            return false;
        }

        public void Suspend(bool value)
        {
            if (Suspended == value) return;
            Suspended = value;
            if (!Agent.isOnNavMesh) return;
            Agent.isStopped = value;
            // isStopped 只停止沿路径前进，局部避障仍可能推移角色。暂停时断开位置同步，
            // 恢复时以可见位置重同步，避免对话中漂移、解除暂停后跳到模拟位置。
            if (value) Agent.velocity = Vector3.zero;
            else Agent.nextPosition = transform.position;
            Agent.updatePosition = !value;
        }

        public void Cancel()
        {
            if (Agent.isOnNavMesh) Agent.ResetPath();
            State = NavigationState.Idle;
            _stalled = 0;
            Feedback = "";
        }

        public bool Warp(Vector3 point)
        {
            if (!NavMesh.SamplePosition(point, out var hit, 2f, Agent.areaMask)) return false;
            Cancel();
            // 参数是角色根节点位置；投影点是地面，两者之间有 baseOffset，不能每次读档少一米。
            return Agent.Warp(hit.position + Vector3.up * Agent.baseOffset);
        }

        public void Move(Vector3 displacement)
        {
            if (Suspended || !Agent.isOnNavMesh) return;
            Cancel();
            Agent.Move(displacement);
        }

        private void Update()
        {
            if (Suspended || State != NavigationState.Moving || !Agent.isOnNavMesh || Agent.pathPending) return;
            if (Agent.pathStatus != NavMeshPathStatus.PathComplete) { Reject("路线已经改变，请换一个落脚点。"); return; }
            if (Agent.remainingDistance <= Agent.stoppingDistance + 0.1f)
            {
                State = NavigationState.Arrived;
                Feedback = "已到达";
                return;
            }
            if ((transform.position - _lastProgress).sqrMagnitude > 0.04f)
            { _lastProgress = transform.position; _stalled = 0; return; }
            _stalled += Time.deltaTime;
            if (_stalled < 4f) return;
            if (!_retried)
            {
                var path = new NavMeshPath();
                if (Agent.CalculatePath(Destination, path) && path.status == NavMeshPathStatus.PathComplete) Agent.SetPath(path);
                _retried = true;
                _stalled = 0;
            }
            else
            {
                Cancel();
                State = NavigationState.Blocked;
                Feedback = "前面走不通了，请换一条路。";
            }
        }
    }
}
