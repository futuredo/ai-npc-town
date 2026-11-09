using UnityEngine;

namespace Town.Unity.Moonlight
{
    public enum MoonInteractionKind { Discuss, Light, Occluder }
    public sealed class MoonInteractionPoint : MonoBehaviour
    {
        public MoonInteractionKind kind;
        public string label;
        public float radius = 2.5f;
        public Vector3 focusOffset = Vector3.up;
        public Vector3 Focus => transform.position + focusOffset;
    }
}
