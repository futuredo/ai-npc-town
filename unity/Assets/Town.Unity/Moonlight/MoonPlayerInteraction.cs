using UnityEngine;

namespace Town.Unity.Moonlight
{
    /// <summary>距离、视线、朝向共同选定目标。按键不会隔墙操作或替角色确认。</summary>
    public sealed class MoonPlayerInteraction : MonoBehaviour
    {
        public MoonPlayerController player;
        public MoonRescueHud hud;
        public MoonWorkshopGame execution;
        public MoonInteractionPoint[] points;
        public MoonInteractionPoint Current { get; private set; }
        public bool readHardwareInput = true;

        void Update()
        {
            RefreshTarget();
            if (!readHardwareInput) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { hud.SetModal(false); return; }
            if (Input.GetKeyDown(KeyCode.E)) TryInteract();
        }

        public void RefreshTarget()
        {
            Current = null;
            if (!player || points == null || !hud || hud.IsModalOpen) return;
            var from = player.transform.position + Vector3.up;
            float best = float.MaxValue;
            foreach (var point in points)
            {
                if (!point || !point.gameObject.activeInHierarchy) continue;
                var delta = point.Focus - from;
                if (delta.magnitude > point.radius || Vector3.Dot(player.transform.forward, delta.normalized) < -.2f) continue;
                if (Physics.Linecast(from, point.Focus, out var hit, ~(1 << 28), QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(point.transform)) continue;
                if (delta.sqrMagnitude >= best) continue;
                Current = point; best = delta.sqrMagnitude;
            }
        }

        public bool TryInteract()
        {
            RefreshTarget();
            if (!Current) return false;
            switch (Current.kind)
            {
                case MoonInteractionKind.Discuss: hud.SetModal(true); break;
                case MoonInteractionKind.Light: execution.ToggleLight(); break;
                case MoonInteractionKind.Occluder: execution.ToggleOccluder(); break;
            }
            return true;
        }
    }
}
