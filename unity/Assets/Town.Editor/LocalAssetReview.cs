using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Town.Editor
{
    /// <summary>Explicit, offline review of locally acquired assets. Never runs on project load.</summary>
    public static class LocalAssetReview
    {
        const string Root = "Assets/LocalThirdParty";
        const string Review = Root + "/Review";
        [MenuItem("Town/Art/Build Local Asset Review")]
        public static string Build()
        {
            if (!Directory.Exists(Root)) return "Local assets are absent; see docs/本地素材筛选与接入清单.md";
            Directory.CreateDirectory(Review);
            AssetDatabase.Refresh();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var existing = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(existing.path))
                    EditorSceneManager.SaveScene(existing, Review + "/PreservedUntitled_" + Guid.NewGuid().ToString("N") + ".unity");
            }
            string reviewPath = Review + "/AssetReview.unity";
            for (int i = UnityEngine.SceneManagement.SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var previousReview = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (previousReview.path == reviewPath)
                {
                    if (previousReview.isDirty) EditorSceneManager.SaveScene(previousReview, Review + "/PreservedReview_" + Guid.NewGuid().ToString("N") + ".unity", true);
                    EditorSceneManager.CloseScene(previousReview, true);
                }
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var group = new GameObject("Local asset review — not gameplay");
            var names = new[]{"SK_Merchant.prefab", "SK_Cook_Skin1.prefab", "SK_Peasant_woman skin1.prefab", "Peasant_man_skin1.prefab", "Dragon_Bl.prefab", "DragonWhelp_Bl.prefab", "PF_CastleHouse_01.prefab", "Prefab_Table_Exterior_Round.prefab", "Prefab_Barrel.prefab", "P_FW01_Chest_B_01.prefab", "P_FW01_Barrel_B_01.prefab", "P_FW01_Tree_B_01.prefab", "P_fwOF_Rock_01.prefab", "P_FW01_CampProp_Pot_B_Handle_01.prefab", "Characters_rig.fbx"};
            var paths = AssetDatabase.GetAllAssetPaths().Where(p=>p.StartsWith(Root+"/") && !p.StartsWith(Review+"/")).ToArray();
            var mats = new Dictionary<Material, Material>();
            int index = 0;
            foreach (var name in names)
            {
                var path = paths.FirstOrDefault(p=>p.EndsWith("/"+name, StringComparison.Ordinal));
                if(path==null) continue;
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if(!asset) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                go.transform.SetParent(group.transform);
                go.name = name;
                if (path.Contains("Medieval_NPC_Pack_2") || path.Contains("SongOfAlcazar")) go.transform.Rotate(0,180,0);
                int missingScripts = go.GetComponentsInChildren<Transform>(true).Sum(t=>GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
                foreach(var t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                foreach(var anim in go.GetComponentsInChildren<Animator>(true)) anim.runtimeAnimatorController = null;
                var renderers = go.GetComponentsInChildren<Renderer>().Where(r=>r.enabled).ToArray();
                foreach(var r in renderers) r.sharedMaterials = r.sharedMaterials.Select(m=>Fallback(m,mats)).ToArray();
                var bound = new Bounds(go.transform.position, Vector3.zero);
                if(renderers.Length>0){bound=renderers[0].bounds;foreach(var r in renderers.Skip(1))bound.Encapsulate(r.bounds);}
                float factor = 3.3f/Mathf.Max(0.01f, Mathf.Max(bound.size.x, Mathf.Max(bound.size.y,bound.size.z)));
                go.transform.localScale *= factor;
                bound = renderers.Length>0?renderers[0].bounds:new Bounds(go.transform.position,Vector3.one);
                foreach(var r in renderers.Skip(1)) bound.Encapsulate(r.bounds);
                Vector3 slot = new Vector3((index%5-2)*5.2f,0,(index/5)*5.4f);
                go.transform.position += slot-new Vector3(bound.center.x,bound.min.y,bound.center.z);
                var label = new GameObject("Label");label.transform.SetParent(group.transform);label.transform.position=slot+new Vector3(0,0.05f,-2.1f);label.transform.rotation=Quaternion.Euler(65,0,0);
                var shortNames=new[]{"Merchant","Cook","Villager F","Villager M","Dragon","Dragon Whelp","House","Table","Barrel","Chest","Barrel B","Tree","Rock","Camp Pot","Alcazar rig"};
                var text=label.AddComponent<TextMesh>();text.text=(index+1)+"  "+shortNames[Array.IndexOf(names,name)];text.characterSize=0.12f;text.fontSize=36;text.anchor=TextAnchor.MiddleCenter;text.color=Color.white;
                index++;
            }
            var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);floor.transform.SetParent(group.transform);floor.transform.position=new Vector3(0,-0.06f,5.4f);floor.transform.localScale=new Vector3(3,1,2.3f);
            var ground=new Material(Shader.Find("Universal Render Pipeline/Lit"));ground.color=new Color(0.16f,0.19f,0.22f);AssetDatabase.CreateAsset(ground,Review+"/Ground_"+Guid.NewGuid().ToString("N")+".mat");floor.GetComponent<Renderer>().sharedMaterial=ground;
            var light=new GameObject("Review light").AddComponent<Light>();light.transform.SetParent(group.transform);light.type=LightType.Directional;light.intensity=0.85f;light.transform.rotation=Quaternion.Euler(50,-35,0);light.cullingMask=1<<31;
            var camera=new GameObject("Review camera").AddComponent<Camera>();camera.transform.SetParent(group.transform);camera.transform.position=new Vector3(0,21,-24);camera.transform.LookAt(new Vector3(0,0,5.4f));camera.orthographic=true;camera.orthographicSize=10.5f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(0.09f,0.11f,0.15f);camera.cullingMask=1<<31;camera.farClipPlane=100;
            foreach(var t in group.GetComponentsInChildren<Transform>(true))t.gameObject.layer=31;
            EditorSceneManager.SaveScene(scene,Review+"/AssetReview.unity");AssetDatabase.SaveAssets();
            string evidence=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.local/asset-audit-20261107"));Directory.CreateDirectory(evidence);
            EditorCameraCapture.Save(camera, Path.Combine(evidence,"asset-review.png"), 1920, 1080);
            return "Saved "+Review+"/AssetReview.unity; reviewed "+index+" objects. URP materials, static inspection only.";
        }
        static Material Fallback(Material source, Dictionary<Material,Material> cache)
        {
            if(!source)return null;
            if(source.shader && source.GetTag("RenderPipeline", false, "") == "UniversalPipeline")return source;
            if(cache.TryGetValue(source,out var result))return result;
            result=new Material(Shader.Find("Universal Render Pipeline/Lit"));result.name="Review_"+source.name;
            var serialized=new SerializedObject(source);var textures=serialized.FindProperty("m_SavedProperties.m_TexEnvs");
            if(textures!=null)for(int i=0;i<textures.arraySize;i++)
            {
                var e=textures.GetArrayElementAtIndex(i);string key=e.FindPropertyRelative("first").stringValue.ToLowerInvariant();
                if(!key.Contains("normal") && !key.Contains("mask") && (key.Contains("base")||key.Contains("albedo")||key.Contains("color")||key=="_maintex"||key.Contains("diffuse")))
                {var texture=e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;if(texture){result.mainTexture=texture;break;}}
            }
            if(source.HasProperty("_Color"))result.color=source.color;
            if(!result.mainTexture)
            {
                string stem=source.name.Length>2?source.name.Substring(2):source.name;
                var texturePath=AssetDatabase.GetAllAssetPaths().FirstOrDefault(p=>p.StartsWith(Root+"/") && (Path.GetFileNameWithoutExtension(p).Equals("M_"+stem+"_BC",StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(p).Equals("T_"+stem+"_BC",StringComparison.OrdinalIgnoreCase)));
                if(texturePath!=null)result.mainTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            }
            result.SetFloat("_Smoothness",0.15f);
            if(source.name.ToLowerInvariant().Contains("hair")){result.SetFloat("_AlphaClip",1);result.SetFloat("_Cull",0);}
            UnityEditor.BaseShaderGUI.SetMaterialKeywords(result, UnityEditor.Rendering.Universal.ShaderGUI.LitGUI.SetMaterialKeywords);
            AssetDatabase.CreateAsset(result,Review+"/"+Guid.NewGuid().ToString("N")+".mat");cache[source]=result;return result;
        }
    }
}
