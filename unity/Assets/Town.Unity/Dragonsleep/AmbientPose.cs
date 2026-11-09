using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 让场景中的角色以指定动画状态入镜，并错开起始帧，避免整排人同步摆动。
    /// 只负责“看起来活着”，不承载任何玩法判断。
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class AmbientPose : MonoBehaviour
    {
        public string StateName = "Idle";
        [Range(0f, 1f)] public float Phase = -1f;
        public float SpeedJitter = 0.08f;
        public Transform LookAt;

        private Animator _animator;

        private void OnEnable()
        {
            _animator = GetComponent<Animator>();
            Apply();
        }

        public void Play(string state)
        {
            StateName = state;
            Apply();
        }

        private void Apply()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            _animator.applyRootMotion = false;
            float phase = Phase >= 0f ? Phase : Random.value;
            _animator.speed = 1f + Random.Range(-SpeedJitter, SpeedJitter);
            int hash = Animator.StringToHash(StateName);
            if (_animator.HasState(0, hash)) _animator.Play(hash, 0, phase);
            if (LookAt != null)
            {
                Vector3 to = LookAt.position - transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(to);
            }
        }
    }
}
