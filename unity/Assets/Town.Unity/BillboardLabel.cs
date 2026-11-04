using UnityEngine;

namespace Town.Unity
{
    /// <summary>头顶文字，始终朝向相机。</summary>
    [RequireComponent(typeof(TextMesh))]
    public sealed class BillboardLabel : MonoBehaviour
    {
        private TextMesh _text;
        private Camera _cam;

        public void SetText(string s)
        {
            if (_text == null) _text = GetComponent<TextMesh>();
            _text.text = s;
        }

        private void LateUpdate()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam != null) transform.rotation = _cam.transform.rotation;
        }
    }
}
