using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Town.Editor
{
    /// <summary>只更新龙眠镇的美术参数，不重建角色、玩法节点或地图。</summary>
    public static class DragonsleepArtReview
    {
        [MenuItem("Town/Dragonsleep/Apply Medieval Art Direction")]
        public static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.path != DragonsleepSceneBuilder.ScenePath)
                throw new System.InvalidOperationException("请在龙眠镇主场景的编辑模式应用美术设置。");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DragonsleepSceneBuilder.Root + "/Dragonsleep_Town_Volume.asset");
            if (profile == null) throw new System.InvalidOperationException("未找到龙眠镇后期配置。");
            ApplyProfile(profile);
            var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                Undo.RecordObject(sun, "中世纪奇幻光照调整");
                sun.color = new Color(1f, 0.91f, 0.79f); sun.intensity = 1.3f; sun.shadowStrength = 0.72f;
                EditorUtility.SetDirty(sun);
            }
            RenderSettings.fogColor = new Color(0.57f, 0.66f, 0.72f);
            RenderSettings.fogDensity = 0.0015f;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Town.Content/UI/Resources/Dragonsleep/TitleArt.png");
            if (texture != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
            {
                importer.mipmapEnabled = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 2048; importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
            var doc = Object.FindFirstObjectByType<UnityEngine.UIElements.UIDocument>();
            if (doc != null && doc.panelSettings != null)
            {
                doc.panelSettings.referenceResolution = new Vector2Int(1600, 900);
                EditorUtility.SetDirty(doc.panelSettings);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("龙眠镇美术参数已更新：暖石、冷灰阴影、克制饱和度与柔和辉光。");
        }

        public static void ApplyProfile(VolumeProfile profile)
        {
            if (profile.TryGet<ColorAdjustments>(out var color))
            {
                color.postExposure.Override(0.25f); color.contrast.Override(6f); color.saturation.Override(-12f);
                color.colorFilter.Override(new Color(0.98f, 0.99f, 1f));
            }
            if (profile.TryGet<WhiteBalance>(out var balance)) { balance.temperature.Override(3f); balance.tint.Override(0f); }
            if (profile.TryGet<Bloom>(out var bloom)) { bloom.intensity.Override(0.22f); bloom.threshold.Override(1.15f); bloom.scatter.Override(0.55f); }
            if (profile.TryGet<Vignette>(out var vignette)) { vignette.intensity.Override(0.16f); vignette.smoothness.Override(0.5f); }
            foreach (var c in profile.components) EditorUtility.SetDirty(c);
            EditorUtility.SetDirty(profile);
        }
    }
}
