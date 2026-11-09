using System;
using System.IO;
using System.Linq;
using Town.Unity.Moonlight;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Town.Editor
{
    /// <summary>在购买的完整场景副本中安装玩法；保留作者布局，不覆盖素材原场景。</summary>
    public static class MoonVillageSceneBuilder
    {
        public const string ScenePath = "Assets/LocalThirdParty/Gameplay/Moonlight_Village.unity";
        const string Native = "Assets/LocalThirdParty/Polyart/PolyartStudio/DreamscapeCastle/Scenes/DemoExterior.unity";
        const string Station = "Assets/LocalThirdParty/Aquarius Fantasy - Ultimate Workshops/FBX/Prefabs/Themed Workshops/Workstation Modules/Enchanters_WorkStation_A.prefab";
        const string NavigationPath = "Assets/LocalThirdParty/Gameplay/VillageNavMesh.asset";

        [MenuItem("Town/Moonlight/Build Native Village Demo")]
        public static string Build()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("请退出运行或编译。");
            if (File.Exists(ScenePath) || File.Exists(NavigationPath)) throw new InvalidOperationException("拒绝覆盖已有村落 Demo 或导航资产。");
            if (!AssetDatabase.CopyAsset(Native, ScenePath)) throw new IOException("缺少已导入的 KINGDOM 完整室外场景。");
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var source = default(Scene);
            try
            {
                SceneManager.SetActiveScene(scene);
                var nativeRoots = scene.GetRootGameObjects();
                foreach (var root in nativeRoots)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    foreach (var camera in root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
                    foreach (var listener in root.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.DestroyImmediate(listener);
                    // 废弃的内置管线粒子不等于 URP 功能特效；保留资源但在本 Demo 中不播放。
                    foreach (var particle in root.GetComponentsInChildren<ParticleSystem>(true)) particle.gameObject.SetActive(false);
                }
                var origin = new Vector3(310, 6.5f, 470);
                var navigation = new GameObject("Local navigation — native terrain and collision");
                navigation.transform.position = origin;
                var surface = navigation.AddComponent<NavMeshSurface>();
                surface.collectObjects = CollectObjects.Volume; surface.center = Vector3.zero;
                surface.size = new Vector3(36, 28, 36); surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                surface.overrideVoxelSize = true; surface.voxelSize = 0.1f;
                surface.BuildNavMesh();
                if (!surface.navMeshData) throw new InvalidOperationException("原生场景局部导航烘焙失败。");
                AssetDatabase.CreateAsset(surface.navMeshData, NavigationPath);
                source = EditorSceneManager.OpenScene(MoonRescueSceneBuilder.ScenePath, OpenSceneMode.Additive);
                // 复用已验证的玩法组件和人物绑定，不把技术平台搬进原生场景。
                foreach (var root in source.GetRootGameObjects())
                {
                    if (root.name == "Fixed dragon-scale paths" || root.name == "Distant moon") continue;
                    if (root.name == "Local art — no navigation or rule authority")
                    {
                        foreach (Transform child in root.transform.Cast<Transform>().ToArray())
                            if (child.name == "Moonlight workshop backdrop" || child.name == "Borrowing table") UnityEngine.Object.DestroyImmediate(child.gameObject);
                    }
                    SceneManager.MoveGameObjectToScene(root, scene);
                    root.transform.position += origin;
                }
                SceneManager.SetActiveScene(scene);
                var execution = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MoonWorkshopGame>()).Single();
                execution.worldOrigin = origin;
                foreach (var agent in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<NavMeshAgent>()))
                {
                    var position = agent.transform.position;
                    if (!NavMesh.SamplePosition(position, out var hit, 3, agent.areaMask)) throw new InvalidOperationException("角色或漂流灯不在原生地面导航上：" + agent.name);
                    agent.transform.position = hit.position;
                }
                // 原生地形有坡度：两条路线必须逐段可达，不能靠角色台词宣告畅通。
                foreach (var middle in new[] { new Vector3(0, 0, -3), new Vector3(0, 0, 3) })
                {
                    var a = Ground(origin + new Vector3(-4, 0, 0));
                    var b = Ground(origin + middle); var c = Ground(origin + new Vector3(4, 0, 0));
                    CheckPath(a, b); CheckPath(b, c);
                }
                var station = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Station));
                PrefabUtility.UnpackPrefabInstance(station, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                station.name = "Maeve's complete enchanter workstation — purchased layout";
                foreach (var t in station.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                foreach (var b in station.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(b);
                foreach (var c in station.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
                var bounds = BoundsOf(station);
                station.transform.localScale *= 2.6f / Mathf.Max(.01f, bounds.size.y);
                bounds = BoundsOf(station);
                var anchor = Ground(origin + new Vector3(-5.5f, 0, 3));
                station.transform.position += anchor - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                foreach (var animation in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MoonActorAnimation>()))
                    if (!animation.agent) animation.transform.position = Ground(animation.transform.position);
                var cameraMain = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>()).Single(c => c.enabled);
                cameraMain.transform.position = origin + new Vector3(0, 13, -15);
                cameraMain.transform.LookAt(origin + Vector3.up);
                cameraMain.orthographicSize = 10; cameraMain.farClipPlane = 350;
                RenderSettings.fog = true; RenderSettings.fogColor = new Color(.38f, .5f, .55f);
                RenderSettings.fogMode = FogMode.ExponentialSquared; RenderSettings.fogDensity = .006f;
                MoonThreeCSetup.Install(scene);
                EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
                if (!EditorBuildSettings.scenes.Any(s => s.path == ScenePath))
                    EditorBuildSettings.scenes = EditorBuildSettings.scenes.Concat(new[] { new EditorBuildSettingsScene(ScenePath, true) }).ToArray();
                return ScenePath;
            }
            finally
            {
                if (source.IsValid()) EditorSceneManager.CloseScene(source, true);
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            }
        }

        static Vector3 Ground(Vector3 position)
        {
            if (!NavMesh.SamplePosition(position, out var hit, 3, NavMesh.AllAreas)) throw new InvalidOperationException("现场地面未覆盖导航：" + position);
            return hit.position;
        }
        static void CheckPath(Vector3 a, Vector3 b)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                throw new InvalidOperationException("原生场景路线不完整：" + a + " → " + b);
        }
        static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds; foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds); return bounds;
        }
    }
}
