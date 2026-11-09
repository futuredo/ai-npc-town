using UnityEngine;

namespace Town.Unity
{
    /// <summary>场景里的一个功能点。id 对应 Town.Core.World.Ids.Loc。</summary>
    public sealed class LocationMarker : MonoBehaviour
    {
        public string locationId;
        public string displayName;

        private void Awake()
        {
            displayName = Town.Core.World.Ids.Name(locationId);
            // 旧灰盒场景只迁移显示文案，不重建场景、材质或烘焙数据。
            var labelRoot = transform.parent != null ? transform.parent : transform;
            foreach (var text in labelRoot.GetComponentsInChildren<TextMesh>())
            {
                if (text.text == "旧井") text.text = displayName;
                if (text.text == "回音石板") text.text = "钟楼石板";
            }
        }

        /// <summary>角色站位点（地点中心前方一点，避免站进模型里）。</summary>
        public Vector3 StandPoint => transform.position + transform.forward * 2.5f;
    }
}
