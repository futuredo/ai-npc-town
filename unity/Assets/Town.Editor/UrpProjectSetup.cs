using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Town.Editor
{
    /// <summary>显式执行的工程迁移；不会在导入、打开工程或进入 Play 时修改资源。</summary>
    public static class UrpProjectSetup
    {
        public const string PipelinePath = "Assets/Settings/Rendering/Town_URP.asset";
        public const string RendererPath = "Assets/Settings/Rendering/Town_URP_Renderer.asset";

        [MenuItem("Town/Rendering/Configure URP")]
        public static void Configure()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("请先退出 Play。");
            Directory.CreateDirectory("Assets/Settings/Rendering");
            AssetDatabase.Refresh();
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (!pipeline)
            {
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
                if (!renderer)
                {
                    renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                    AssetDatabase.CreateAsset(renderer, RendererPath);
                }
                ResourceReloader.ReloadAllNullIn(renderer, UniversalRenderPipelineAsset.packagePath);
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.name = "Town_URP";
                pipeline.msaaSampleCount = 4;
                pipeline.renderScale = 1;
                pipeline.supportsHDR = true;
                pipeline.shadowDistance = 60;
                pipeline.mainLightShadowmapResolution = 2048;
                pipeline.supportsCameraDepthTexture = false;
                pipeline.supportsCameraOpaqueTexture = false;
                pipeline.useSRPBatcher = true;
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rendererData && !rendererData.postProcessData)
            {
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    UniversalRenderPipelineAsset.packagePath + "/Runtime/Data/PostProcessData.asset");
                EditorUtility.SetDirty(rendererData);
            }
            GraphicsSettings.defaultRenderPipeline = pipeline;
            int originalQuality = QualitySettings.GetQualityLevel();
            try
            {
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = pipeline;
                }
            }
            finally { QualitySettings.SetQualityLevel(originalQuality, false); }
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            Debug.Log("[Town URP] 已配置默认管线及全部质量级别；不会重建场景或 NavMesh。");
        }

        [MenuItem("Town/Rendering/Upgrade Built-in Materials")]
        public static void UpgradeMaterials()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("请先退出 Play。");
            var upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));
            var report = new List<string> { "path\tprevious_shader\tresult" };
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                string shader = material.shader ? material.shader.name : "<missing>";
                if (shader == "2Side")
                {
                    // 此 Shader 的 _Glossiness 实为粗糙度，贴图来自 _SpecGlossMap.r。
                    float roughness = material.GetFloat("_Glossiness");
                    var roughnessMap = material.IsKeywordEnabled("_SPECGLOSSMAP") ? material.GetTexture("_SpecGlossMap") : null;
                    var metallicMap = material.IsKeywordEnabled("_METALLICGLOSSMAP") ? material.GetTexture("_MetallicGlossMap") : null;
                    new StandardUpgrader(shader).Upgrade(material, MaterialUpgrader.UpgradeFlags.None);
                    ApplyTwoSidedRoughness(material, roughness, roughnessMap, metallicMap);
                    report.Add(path + "\t" + shader + "\tdouble-sided Lit; roughness packed into smoothness alpha");
                }
                else if (shader == "Standard" || shader == "Standard (Specular setup)" || shader == "Autodesk Interactive")
                {
                    string message = "";
                    if (!MaterialUpgrader.Upgrade(material, upgraders, MaterialUpgrader.UpgradeFlags.None, ref message))
                        throw new InvalidOperationException(path + ": " + message);
                    report.Add(path + "\t" + shader + "\t" + material.shader.name);
                }
                else
                {
                    // 不覆盖已兼容的 Shader Graph、植被风动、混合层或原生 URP 材质。
                    report.Add(path + "\t" + shader + "\tretained");
                    continue;
                }
                EditorUtility.SetDirty(material);
            }
            AssetDatabase.SaveAssets();
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/urp-migration-20261107"));
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "material-conversion.tsv"), report);
            Debug.Log("[Town URP] 材质转换记录：" + directory);
        }

        /// <summary>保留旧 RoughnessSetup 的金属度和 1-roughness，不改源贴图的导入设置。</summary>
        public static void ApplyTwoSidedRoughness(Material material, float roughness, Texture roughnessMap, Texture metallicMap)
        {
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.doubleSidedGI = true;
            material.SetFloat("_SmoothnessTextureChannel", 0);
            if (roughnessMap || metallicMap)
            {
                string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(material)) + "/URPGenerated";
                Directory.CreateDirectory(folder);
                int width = Mathf.Max(roughnessMap ? roughnessMap.width : 1, metallicMap ? metallicMap.width : 1);
                int height = Mathf.Max(roughnessMap ? roughnessMap.height : 1, metallicMap ? metallicMap.height : 1);
                var roughPixels = roughnessMap ? ReadLinear(roughnessMap, width, height) : null;
                var metalPixels = metallicMap ? ReadLinear(metallicMap, width, height) : null;
                var pixels = new Color32[width * height];
                byte constantMetal = (byte)Mathf.RoundToInt(Mathf.Clamp01(material.GetFloat("_Metallic")) * 255);
                byte constantSmooth = (byte)Mathf.RoundToInt((1 - Mathf.Clamp01(roughness)) * 255);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(metalPixels != null ? metalPixels[i].r : constantMetal, 0, 0,
                        roughPixels != null ? (byte)(255 - roughPixels[i].r) : constantSmooth);
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                string path = folder + "/" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material)) + "_MetallicSmoothness.png";
                try
                {
                    image.SetPixels32(pixels);
                    image.Apply();
                    File.WriteAllBytes(path, image.EncodeToPNG());
                }
                finally { UnityEngine.Object.DestroyImmediate(image); }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(width, height));
                importer.SaveAndReimport();
                material.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                material.SetFloat("_Smoothness", 1);
            }
            else
            {
                material.SetTexture("_MetallicGlossMap", null);
                material.SetFloat("_Smoothness", 1 - Mathf.Clamp01(roughness));
            }
            BaseShaderGUI.SetMaterialKeywords(material, LitGUI.SetMaterialKeywords);
            EditorUtility.SetDirty(material);
        }

        private static Color32[] ReadLinear(Texture source, int width, int height)
        {
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                return image.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
