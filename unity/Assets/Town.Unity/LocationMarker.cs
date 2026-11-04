using UnityEngine;

namespace Town.Unity
{
    /// <summary>场景里的一个功能点。id 对应 Town.Core.World.Ids.Loc。</summary>
    public sealed class LocationMarker : MonoBehaviour
    {
        public string locationId;
        public string displayName;

        /// <summary>角色站位点（地点中心前方一点，避免站进模型里）。</summary>
        public Vector3 StandPoint => transform.position + transform.forward * 2.5f;
    }
}
