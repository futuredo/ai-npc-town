using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Town.Editor
{
    /// <summary>显式、离线的增量样板与重定向检查；不改已有场景或厂商 Prefab。</summary>
    public static class IncrementalAssetReview
    {
        const string Root = "Assets/LocalThirdParty";
        const string Horde = Root + "/Polygonmaker/Fantasy Horde Series/Fantasy Horde - Villagers";
        const string Review = Root + "/Review/Incremental";
        static readonly string[] Characters = { "Child_1", "Female_1", "Male_1", "Monk_1" };

        public static object VerifyAnimationRetargeting()
        {
            RequireIdle();
            var clips = AssetDatabase.FindAssets("t:Model", new[] { Horde + "/Animations" })
                .Select(AssetDatabase.GUIDToAssetPath).SelectMany(AssetDatabase.LoadAllAssetsAtPath)
                .OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            if (clips.Length != 10) throw new InvalidOperationException("预期十个社交/移动片段，实际：" + clips.Length);
            var targets = Characters.Select(n => Horde + "/Characters/" + n + ".prefab").ToList();
            var previous = AssetDatabase.GetAllAssetPaths().FirstOrDefault(p => p.StartsWith(Root + "/") && p.EndsWith("/SK_Merchant.prefab"));
            if (previous != null) targets.Add(previous);
            targets.Add(Root + "/Arashi_Character/Prefab/Character_Color_01.prefab");
            var scene = EditorSceneManager.NewPreviewScene();
            var results = new List<object>();
            try
            {
                foreach (var path in targets)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (!asset) throw new InvalidOperationException("角色不存在：" + path);
                    var go = UnityEngine.Object.Instantiate(asset);
                    SceneManager.MoveGameObjectToScene(go, scene);
                    RemoveMissingScripts(go);
                    try
                    {
                        var animator = go.GetComponentInChildren<Animator>();
                        if (!animator) animator = go.AddComponent<Animator>();
                        bool avatarBoundForReview = false;
                        if (!animator.avatar)
                        {
                            var candidates = AssetDatabase.GetDependencies(path).SelectMany(AssetDatabase.LoadAllAssetsAtPath)
                                .OfType<Avatar>().Where(a => a.isValid && a.isHuman).Distinct().ToArray();
                            if (candidates.Length == 1) { animator.avatar = candidates[0]; avatarBoundForReview = true; }
                        }
                        if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                            throw new InvalidOperationException("无有效 Humanoid：" + path);
                        animator.runtimeAnimatorController = null;
                        animator.applyRootMotion = false;
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        foreach (var clip in clips)
                        {
                            var graph = PlayableGraph.Create("Town asset retarget verification");
                            try
                            {
                                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                                var playable = AnimationClipPlayable.Create(graph, clip);
                                var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
                                output.SetSourcePlayable(playable);
                                graph.Play();
                                var bones = Enumerable.Range(0, (int)HumanBodyBones.LastBone)
                                    .Select(i => animator.GetBoneTransform((HumanBodyBones)i)).Where(t => t).ToArray();
                                playable.SetTime(clip.length * 0.1); graph.Evaluate(0);
                                var before = bones.Select(b => b.localRotation).ToArray();
                                float maxAngle = 0;
                                foreach (float fraction in new[] { 0.37f, 0.71f })
                                {
                                    playable.SetTime(clip.length * fraction); graph.Evaluate(0);
                                    for (int i = 0; i < bones.Length; i++)
                                    {
                                        var q = bones[i].localRotation;
                                        if (!float.IsFinite(q.x) || !float.IsFinite(q.y) || !float.IsFinite(q.z) || !float.IsFinite(q.w))
                                            throw new InvalidOperationException("非有限骨骼姿态：" + path + "/" + clip.name);
                                        maxAngle = Mathf.Max(maxAngle, Quaternion.Angle(before[i], q));
                                    }
                                }
                                results.Add(new { target = path, source = AssetDatabase.GetAssetPath(clip), clip = clip.name,
                                    bones = bones.Length, maxBoneAngle = maxAngle, poseChanged = maxAngle > 0.05f, avatarBoundForReview });
                            }
                            finally { if (graph.IsValid()) graph.Destroy(); }
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(go); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            return results;
        }

        [MenuItem("Town/Art/Build Incremental Character Review")]
        public static string Build()
        {
            RequireIdle();
            string scenePath = Review + "/IncrementalCharacters.unity";
            if (File.Exists(scenePath)) throw new InvalidOperationException("已有增量样板，拒绝覆盖：" + scenePath);
            Directory.CreateDirectory(Review);
            AssetDatabase.Refresh();
            var previousActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                var group = new GameObject("Incremental character review — offline, not gameplay");
                var paths = Characters.Select(n => Horde + "/Characters/" + n + ".prefab")
                    .Concat(new[] { Root + "/Arashi_Character/Prefab/Character_Color_01.prefab" }).ToArray();
                var idle = AssetDatabase.LoadAllAssetsAtPath(Horde + "/Animations/Anim_Warrior@idle.FBX")
                    .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
                var controller = AnimatorController.CreateAnimatorControllerAtPath(Review + "/SharedIdle.controller");
                controller.AddMotion(idle);
                for (int i = 0; i < paths.Length; i++)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]), scene);
                    RemoveMissingScripts(instance);
                    var animator = instance.GetComponentInChildren<Animator>();
                    animator.runtimeAnimatorController = controller;
                    animator.applyRootMotion = false;
                    instance.transform.SetParent(group.transform);
                    var renderers = instance.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
                    var bounds = renderers[0].bounds;
                    foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                    instance.transform.localScale *= 2.7f / Mathf.Max(0.01f, bounds.size.y);
                    bounds = renderers[0].bounds;
                    foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                    var slot = new Vector3((i - 2) * 3.5f, 0, 0);
                    instance.transform.position += slot - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                    string label = i < 4 ? Characters[i] : "Arashi — optional style";
                    var text = new GameObject("Label").AddComponent<TextMesh>();
                    text.transform.SetParent(group.transform); text.transform.position = slot + new Vector3(0, 0.03f, -1.2f);
                    text.transform.rotation = Quaternion.Euler(65, 0, 0); text.text = label;
                    text.fontSize = 32; text.characterSize = 0.08f; text.anchor = TextAnchor.MiddleCenter;
                    // 新的干净适配 Prefab 保留审阅配置，不修改含厂商脚本引用的原件。
                    PrefabUtility.SaveAsPrefabAsset(instance, Review + "/" + (i < 4 ? Characters[i] : "Arashi") + "_Review.prefab");
                }
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); floor.transform.SetParent(group.transform);
                floor.transform.position = new Vector3(0, -0.04f, 0); floor.transform.localScale = new Vector3(2.1f, 1, 0.8f);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.color = new Color(0.12f, 0.16f, 0.21f);
                AssetDatabase.CreateAsset(material, Review + "/Ground.mat"); floor.GetComponent<Renderer>().sharedMaterial = material;
                var light = new GameObject("Increment light").AddComponent<Light>(); light.transform.SetParent(group.transform);
                light.type = LightType.Directional; light.intensity = 1.1f; light.transform.rotation = Quaternion.Euler(45, -30, 0); light.cullingMask = 1 << 30;
                var camera = new GameObject("Increment camera").AddComponent<Camera>(); camera.transform.SetParent(group.transform);
                camera.transform.position = new Vector3(0, 7, -13); camera.transform.LookAt(new Vector3(0, 1.2f, 0));
                camera.orthographic = true; camera.orthographicSize = 5.3f; camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.06f, 0.08f, 0.13f);
                foreach (var t in group.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30;
                EditorSceneManager.SaveScene(scene, scenePath); AssetDatabase.SaveAssets();
                string evidence = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/asset-increment-20261107"));
                Directory.CreateDirectory(evidence);
                EditorCameraCapture.Save(camera, Path.Combine(evidence, "incremental-characters.png"), 1920, 1080);
                return "Created " + scenePath + "; five clean review prefabs and a shared idle controller. Existing scenes unchanged.";
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previousActive.IsValid()) SceneManager.SetActiveScene(previousActive);
            }
        }

        static void RequireIdle()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling)
                throw new InvalidOperationException("请在非运行、非编译状态下显式审阅素材。");
        }

        static void RemoveMissingScripts(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        }
    }
}
