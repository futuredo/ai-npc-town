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
    /// <summary>只生成隔离切片，不覆盖旧灰盒、商业预览或任何已存在的新场景。</summary>
    public static class MoonWorkshopSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Moonlight_Workshop.unity";
        const string Root = "Assets/MoonlightSlice";
        [MenuItem("Town/Moonlight/Build First Offline Slice")]
        public static string Build()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("请先退出运行或编译。");
            if (File.Exists(ScenePath)) throw new InvalidOperationException("拒绝覆盖已有月光工坊，请另存后再生成。");
            Directory.CreateDirectory(Root); AssetDatabase.Refresh();
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                var dark = Material("InkStone", new Color(0.08f, 0.17f, 0.23f));
                var paper = Material("Paper", new Color(0.72f, 0.79f, 0.72f));
                var gold = Material("MoonGold", new Color(0.95f, 0.72f, 0.29f), true);
                var mint = Material("RuneMint", new Color(0.22f, 0.65f, 0.61f), true);
                var env = new GameObject("Fixed dragon-scale paths");
                Vector3 start = new Vector3(-4, 0, 0), end = new Vector3(4, 0, 0);
                foreach (var point in new[] { start, end, new Vector3(0, 0, -3), new Vector3(0, 0, 3) })
                    Primitive("Scale platform", PrimitiveType.Cylinder, env.transform, point + Vector3.down * 0.2f, new Vector3(2.7f, 0.2f, 2.7f), dark);
                foreach (var middle in new[] { new Vector3(0, 0, -3), new Vector3(0, 0, 3) })
                {
                    Bridge(env.transform, start, middle, paper); Bridge(env.transform, middle, end, paper);
                }
                var surface = env.AddComponent<NavMeshSurface>(); surface.collectObjects = CollectObjects.Children;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; surface.BuildNavMesh();
                AssetDatabase.CreateAsset(surface.navMeshData, Root + "/WorkshopNavMesh.asset");
                var actor = new GameObject("Finn — isolated execution proxy"); actor.transform.position = start;
                var agent = actor.AddComponent<NavMeshAgent>(); agent.speed = 1.6f; agent.angularSpeed = 720;
                agent.acceleration = 12; agent.radius = 0.25f; agent.height = 1.8f; agent.stoppingDistance = 0.12f;
                Primitive("Paper silhouette", PrimitiveType.Capsule, actor.transform, start + Vector3.up * 0.9f, new Vector3(0.6f, 0.9f, 0.6f), paper, false);
                Primitive("Half-written moon", PrimitiveType.Sphere, actor.transform, start + new Vector3(0, 1.95f, 0), Vector3.one * 0.18f, mint, false);
                var game = new GameObject("MoonWorkshopGame").AddComponent<MoonWorkshopGame>(); game.finn = agent;
                game.beacon = Primitive("Little moon beacon", PrimitiveType.Sphere, null, start + new Vector3(1, 1.2f, 0), Vector3.one * 0.3f, gold, false).transform;
                game.prism = Primitive("Prism", PrimitiveType.Cube, null, new Vector3(-1.5f, 1.2f, 1.5f), Vector3.one * 0.35f, mint, false).transform;
                game.prism.rotation = Quaternion.Euler(0, 45, 25);
                game.runeA = Primitive("Rune A", PrimitiveType.Sphere, null, new Vector3(0, 1.2f, 3), Vector3.one * 0.3f, mint, false).transform;
                game.runeB = Primitive("Rune B", PrimitiveType.Sphere, null, new Vector3(4, 1.2f, 0), Vector3.one * 0.3f, mint, false).transform;
                game.runeRenderers = new[] { game.runeA.GetComponent<Renderer>(), game.runeB.GetComponent<Renderer>() };
                game.beamA = Beam("Moon beam A", gold); game.beamB = Beam("Moon beam B", gold);
                game.beamBlocker = Primitive("Test occluder — not a resource", PrimitiveType.Cube, null,
                    new Vector3(-0.75f, 1.2f, 2.25f), new Vector3(0.75f, 2.4f, 0.75f), dark);
                game.beamBlocker.layer = 29; game.beamBlocker.SetActive(false);
                // 远景只用自制几何符号，不引用尚未核清发布许可的商业模型。
                Primitive("Distant moon", PrimitiveType.Sphere, null, new Vector3(2, 5, 8), Vector3.one * 2.5f, gold, false);
                var light = new GameObject("Moon key light").AddComponent<Light>(); light.type = LightType.Directional;
                light.intensity = 1.3f; light.color = new Color(0.8f, 0.88f, 1); light.transform.rotation = Quaternion.Euler(45, -25, 0);
                var camera = new GameObject("Workshop Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
                camera.transform.position = new Vector3(-1, 12, -13); camera.transform.LookAt(new Vector3(0, 0.5f, 0.5f));
                camera.orthographic = true; camera.orthographicSize = 7.6f; camera.rect = new Rect(0, 0, 0.68f, 1);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.025f, 0.045f, 0.09f);
                camera.gameObject.AddComponent<AudioListener>();
                EditorSceneManager.SaveScene(scene, ScenePath); AssetDatabase.SaveAssets();
                if (!EditorBuildSettings.scenes.Any(s => s.path == ScenePath))
                    EditorBuildSettings.scenes = EditorBuildSettings.scenes.Concat(new[] { new EditorBuildSettingsScene(ScenePath, true) }).ToArray();
                return ScenePath;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            }
        }

        static void Bridge(Transform parent, Vector3 start, Vector3 end, Material material)
        {
            var delta = end - start;
            var go = Primitive("Paper passage", PrimitiveType.Cube, parent, (start + end) * 0.5f + Vector3.down * 0.12f,
                new Vector3(1.7f, 0.24f, delta.magnitude), material);
            go.transform.rotation = Quaternion.LookRotation(delta);
        }
        static GameObject Primitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material, bool collision = true)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent);
            go.transform.position = position; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = material;
            if (!collision) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            else if (type == PrimitiveType.Cylinder)
            {
                // Unity 圆柱默认使用 CapsuleCollider，非等比压扁后半径仍撑高胶囊。
                // 静态平台采用与可见圆柱一致的网格碰撞，避免烘出悬空导航面。
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            }
            return go;
        }
        static Material Material(string name, Color color, bool emissive = false)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.color = color;
            if (emissive) { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", color * 0.5f); }
            AssetDatabase.CreateAsset(material, Root + "/" + name + ".mat"); return material;
        }
        static LineRenderer Beam(string name, Material material)
        {
            var line = new GameObject(name).AddComponent<LineRenderer>(); line.sharedMaterial = material;
            line.positionCount = 2; line.startWidth = line.endWidth = 0.07f; line.useWorldSpace = true; line.enabled = false;
            return line;
        }
    }
}
