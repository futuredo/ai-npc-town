using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Town.Unity.Moonlight
{
    /// <summary>导航驱动原地人形动画；根位移仍由 Agent 决定，不让片段冒充位移回执。</summary>
    public sealed class MoonActorAnimation : MonoBehaviour
    {
        public Animator animator;
        public NavMeshAgent agent;
        public MoonPlayerController player;
        public AnimationClip idle;
        public AnimationClip walk;
        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;
        public float WalkWeight { get; private set; }

        void OnEnable()
        {
            if (!animator || !idle || !walk || !animator.avatar || !animator.avatar.isHuman) return;
            animator.runtimeAnimatorController = null; animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _graph = PlayableGraph.Create("Moonlight locomotion");
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            _graph.Connect(AnimationClipPlayable.Create(_graph, idle), 0, _mixer, 0);
            _graph.Connect(AnimationClipPlayable.Create(_graph, walk), 0, _mixer, 1);
            AnimationPlayableOutput.Create(_graph, "Body", animator).SetSourcePlayable(_mixer);
            _mixer.SetInputWeight(0, 1); _mixer.SetInputWeight(1, 0); _graph.Play();
        }

        void Update()
        {
            if (!_graph.IsValid()) return;
            bool moving = player ? player.ActualSpeed > .1f : agent && agent.isOnNavMesh && !agent.isStopped && agent.velocity.sqrMagnitude > 0.01f;
            WalkWeight = Mathf.MoveTowards(WalkWeight, moving ? 1 : 0, Time.deltaTime * 8);
            _mixer.SetInputWeight(0, 1 - WalkWeight); _mixer.SetInputWeight(1, WalkWeight);
            _graph.GetRootPlayable(0).SetSpeed(player && moving ? Mathf.Clamp(player.ActualSpeed / 1.8f, .65f, 1.7f) : 1);
        }

        void OnDisable() { if (_graph.IsValid()) _graph.Destroy(); WalkWeight = 0; }
    }
}
