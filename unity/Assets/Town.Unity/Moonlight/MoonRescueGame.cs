using System.Linq;
using Town.Core.Moonlight;
using UnityEngine;
using UnityEngine.AI;

namespace Town.Unity.Moonlight
{
    /// <summary>把短局目标接到场内灯群；只消费真实的路径、供光和到达，不调用模型。</summary>
    public sealed class MoonRescueGame : MonoBehaviour
    {
        public MoonWorkshopGame execution;
        public NavMeshAgent[] lights;
        public MoonRescueChapter Chapter { get; private set; } = new MoonRescueChapter();
        public bool HasBegun => Chapter.Phase != RescuePhase.Preparation;
        int _generation;
        int[] _waypoints;
        Vector3[] _origins;
        float _stableLight;

        void Start()
        {
            _origins = lights.Select(a => a.transform.position).ToArray();
            ResetFollowers();
        }

        void ResetFollowers()
        {
            _generation = execution.Workshop.Generation;
            Chapter = new MoonRescueChapter(); _waypoints = Enumerable.Repeat(1, lights.Length).ToArray(); _stableLight = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                var agent = lights[i];
                if (agent.isOnNavMesh) { agent.ResetPath(); agent.Warp(_origins[i]); agent.isStopped = true; }
            }
        }

        public bool ConfirmDeparture()
        {
            execution.ConfirmAgreement();
            return Chapter.Start(execution.Workshop);
        }

        public void FinishNow()
        {
            if (Chapter.Phase != RescuePhase.Escort) return;
            Chapter.ObserveFinn(execution.Workshop);
            Chapter.Settle();
            if (execution.Workshop.State == MoonTaskState.Travelling) execution.Workshop.Stop();
            foreach (var agent in lights) if (agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); }
        }

        void LateUpdate()
        {
            if (execution.Workshop == null || _origins == null) return;
            if (_generation != execution.Workshop.Generation) ResetFollowers();
            bool split = execution.Workshop.Agreement?.Method == LightMethod.SplitBeam;
            bool lit = execution.LightVerified && execution.Workshop.LightOn;
            if (!lit) _stableLight = 0;
            else if (!execution.Paused) _stableLight = Mathf.Min(2, _stableLight + Time.deltaTime);
            var route = execution.Workshop.Route;
            bool safe = lit && (!split || _stableLight >= 2);
            for (int i = 0; i < lights.Length; i++)
            {
                var agent = lights[i];
                if (!agent.isOnNavMesh) continue;
                bool nearBeacon = split || Vector3.Distance(agent.transform.position + Vector3.up * 1.2f, execution.beacon.position) <= 2.5f;
                bool blocked = !split && Physics.Linecast(agent.transform.position + Vector3.up * 1.2f, execution.beacon.position, 1 << 29, QueryTriggerInteraction.Ignore);
                bool moving = Chapter.Phase == RescuePhase.Escort && !execution.Paused && safe && nearBeacon && !blocked && _waypoints[i] < route.Length;
                agent.isStopped = !moving;
                if (!moving) continue;
                var destination = execution.RoutePosition(_waypoints[i]);
                if (!agent.hasPath || Vector3.Distance(agent.destination, destination) > 0.1f) agent.SetDestination(destination);
                var delta = agent.transform.position - destination; delta.y = 0;
                if (delta.magnitude > 0.35f) continue;
                _waypoints[i]++;
                if (_waypoints[i] != route.Length) continue;
                agent.isStopped = true; agent.ResetPath();
                var local = agent.transform.position - execution.worldOrigin;
                Chapter.ObserveArrival(i, true, local.x, local.z);
            }
            Chapter.ObserveFinn(execution.Workshop);
            if (execution.Workshop.State == MoonTaskState.Failed || execution.Workshop.State == MoonTaskState.Cancelled) FinishNow();
        }
    }
}
