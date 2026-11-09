using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    /// <summary>
    /// 远景母龙：绕雪峰盘旋，带倾斜与高度起伏。纯表现组件；
    /// 何时逼近、何时降落由 GM 状态驱动（见 Town.Core.Dragonsleep）。
    /// </summary>
    public sealed class DragonCircle : MonoBehaviour
    {
        public Vector3 Center;
        public float Radius = 120f;
        public float Height = 90f;
        public float DegreesPerSecond = 9f;
        public float BankDegrees = 18f;
        public float BobAmplitude = 6f;
        public float StartAngle;
        public string FlyState = "Fly_Idle";

        private float _angle;

        private void OnEnable()
        {
            _angle = StartAngle;
            var animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                animator.applyRootMotion = false;
                int hash = Animator.StringToHash(FlyState);
                if (animator.HasState(0, hash)) animator.Play(hash, 0, Random.value);
            }
            Place(0f);
        }

        private void Update() => Place(Time.deltaTime);

        public void Place(float dt)
        {
            _angle += DegreesPerSecond * dt;
            float rad = _angle * Mathf.Deg2Rad;
            float bob = Mathf.Sin(Time.time * 0.35f) * BobAmplitude;
            transform.position = Center + new Vector3(Mathf.Cos(rad) * Radius, Height + bob, Mathf.Sin(rad) * Radius);
            // 切线方向（逆时针）
            Vector3 forward = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * Mathf.Sign(DegreesPerSecond);
            transform.rotation = Quaternion.LookRotation(forward) * Quaternion.Euler(0f, 0f, BankDegrees * Mathf.Sign(DegreesPerSecond));
        }
    }
}
