using System;
using System.IO;
using Town.Core.World;
using Town.Unity;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Town.Editor
{
    /// <summary>
    /// 一键生成灰盒小镇场景（4 个功能点 + 住宅门口 + 3 名 NPC + 玩家 + 相机 + NavMesh）。
    /// 菜单：Town/生成灰盒场景；命令行：-executeMethod Town.Editor.GrayboxSceneBuilder.BuildFromCommandLine
    /// 重复执行会覆盖场景，手动摆放的内容请放到别的场景或预制体里。
    /// </summary>
    public static class GrayboxSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Town_Graybox.unity";
        private const string MatDir = "Assets/Graybox/Materials";
        private const string NavMeshPath = "Assets/Scenes/Town_Graybox_NavMesh.asset";

        [MenuItem("Town/生成灰盒场景")]
        public static void BuildMenu()
        {
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("覆盖场景", $"{ScenePath} 已存在，重新生成会覆盖。继续？", "覆盖", "取消"))
                return;
            Build();
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static void Build()
        {
            Directory.CreateDirectory(MatDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // —— 光照与相机 ——
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0, 34, -30);
            cam.transform.LookAt(new Vector3(0, 0, -2));
            cam.fieldOfView = 45;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.65f, 0.75f);

            // —— 环境（NavMesh 只收集这个节点下的物体） ——
            var env = new GameObject("Environment");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(env.transform);
            ground.transform.localScale = new Vector3(6, 1, 4.4f); // 60 x 44
            ground.GetComponent<Renderer>().sharedMaterial = Mat("Ground", new Color(0.62f, 0.66f, 0.55f));

            // 功能点：建筑中心、尺寸、颜色、交互物
            Building(env, Ids.Loc.Workshop, "符文铁匠铺", new Vector3(-17, 0, 9), new Vector3(8, 4, 6), new Color(0.65f, 0.48f, 0.32f), "铁砧");
            Building(env, Ids.Loc.Store, "醉月旅店", new Vector3(17, 0, 9), new Vector3(8, 4, 6), new Color(0.85f, 0.75f, 0.45f), "酒桶");
            Building(env, Ids.Loc.Shelter, "旧哨塔石亭", new Vector3(14, 0, -13), new Vector3(6, 3.5f, 4), new Color(0.55f, 0.58f, 0.62f), "箱子");
            Building(env, Ids.Loc.Homes, "民居", new Vector3(-17, 0, -13), new Vector3(9, 5, 5), new Color(0.75f, 0.7f, 0.68f), null);
            Building(env, Ids.Loc.Well, "旧井", new Vector3(0, 0, -15), new Vector3(2.4f, 1.2f, 2.4f), new Color(0.4f, 0.42f, 0.5f), null);
            Building(env, Ids.Loc.BellTower, "钟楼", new Vector3(0, 0, 16), new Vector3(4.5f, 12, 4.5f), new Color(0.6f, 0.55f, 0.7f), "回音石板");
            Plaza(env);

            var surface = env.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
            surface.BuildNavMesh();
            if (File.Exists(NavMeshPath)) AssetDatabase.DeleteAsset(NavMeshPath);
            AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

            // —— 角色 ——
            Character(Ids.Npc.Amu, "布伦", new Color(0.55f, 0.35f, 0.2f), new Vector3(-3, 0, 2));
            Character(Ids.Npc.Ahe, "梅芙", new Color(0.9f, 0.55f, 0.2f), new Vector3(3, 0, 2));
            Character(Ids.Npc.Xiaochuan, "芬恩", new Color(0.3f, 0.6f, 0.35f), new Vector3(0, 0, 4));
            var player = Character(Ids.Player, "你", new Color(0.25f, 0.45f, 0.9f), new Vector3(0, 0, -4));
            UnityEngine.Object.DestroyImmediate(player.GetComponent<NpcAgent>());
            player.AddComponent<PlayerClickMover>();

            new GameObject("TownGame").AddComponent<TownGame>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[Town] 灰盒场景已生成：{ScenePath}");
        }

        private static void Building(GameObject env, string id, string name, Vector3 center, Vector3 size, Color color, string propName)
        {
            var root = new GameObject($"Loc_{id}");
            root.transform.SetParent(env.transform);
            root.transform.position = center;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Building";
            body.transform.SetParent(root.transform);
            body.transform.localPosition = new Vector3(0, size.y / 2, 0);
            body.transform.localScale = size;
            body.GetComponent<Renderer>().sharedMaterial = Mat(id, color);

            // 朝向广场的一侧作为门口
            Vector3 toPlaza = (-center).normalized;
            toPlaza.y = 0;
            float half = Mathf.Abs(toPlaza.x) > Mathf.Abs(toPlaza.z) ? size.x / 2 : size.z / 2;
            var marker = new GameObject($"Marker_{id}").AddComponent<LocationMarker>();
            marker.transform.SetParent(root.transform);
            marker.transform.position = center + toPlaza * (half + 0.2f);
            marker.transform.rotation = Quaternion.LookRotation(toPlaza);
            marker.locationId = id;
            marker.displayName = name;

            Label(root.transform, name, new Vector3(0, size.y + 1.2f, 0), 0.25f);

            if (propName != null)
            {
                var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prop.name = $"Prop_{propName}";
                prop.transform.SetParent(root.transform);
                prop.transform.position = marker.transform.position + marker.transform.right * 2.2f + Vector3.up * 0.4f;
                prop.transform.localScale = new Vector3(1.2f, 0.8f, 0.8f);
                prop.GetComponent<Renderer>().sharedMaterial = Mat("Prop", new Color(0.35f, 0.3f, 0.28f));
                Label(prop.transform, propName, new Vector3(0, 1.4f, 0), 0.12f, scaleCompensate: true);
            }
        }

        private static void Plaza(GameObject env)
        {
            var root = new GameObject($"Loc_{Ids.Loc.Plaza}");
            root.transform.SetParent(env.transform);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "PlazaFloor";
            floor.transform.SetParent(root.transform);
            floor.transform.localScale = new Vector3(12, 0.02f, 12);
            floor.GetComponent<Renderer>().sharedMaterial = Mat("Plaza", new Color(0.8f, 0.78f, 0.72f));

            var bench = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bench.name = "Prop_灯节拱门";
            bench.transform.SetParent(root.transform);
            bench.transform.position = new Vector3(0, 1.4f, 1.5f);
            bench.transform.localScale = new Vector3(4f, 2.8f, 0.5f);
            bench.GetComponent<Renderer>().sharedMaterial = Mat("Bench", new Color(0.5f, 0.35f, 0.25f));
            Label(bench.transform, "灯节拱门", new Vector3(0, 0.7f, 0), 0.12f, scaleCompensate: true);

            var marker = new GameObject($"Marker_{Ids.Loc.Plaza}").AddComponent<LocationMarker>();
            marker.transform.SetParent(root.transform);
            marker.transform.position = new Vector3(0, 0, -1.5f);
            marker.transform.rotation = Quaternion.LookRotation(Vector3.back);
            marker.locationId = Ids.Loc.Plaza;
            marker.displayName = "灯节广场";

            Label(root.transform, "灯节广场", new Vector3(0, 0.1f, -5), 0.25f);
        }

        private static GameObject Character(string id, string name, Color color, Vector3 pos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = id == Ids.Player ? "Player" : $"NPC_{id}";
            go.transform.position = pos + Vector3.up;
            go.GetComponent<Renderer>().sharedMaterial = Mat($"Char_{id}", color);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<CapsuleCollider>());

            var agent = go.AddComponent<NavMeshAgent>();
            agent.speed = id == Ids.Player ? 6f : 3.5f;
            agent.angularSpeed = 720;
            agent.stoppingDistance = 0.3f;

            var npc = go.AddComponent<NpcAgent>();
            npc.npcId = id;
            npc.displayName = name;
            npc.label = Label(go.transform, name, new Vector3(0, 1.6f, 0), 0.12f);
            return go;
        }

        private static BillboardLabel Label(Transform parent, string text, Vector3 localPos, float charSize, bool scaleCompensate = false)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            if (scaleCompensate)
            {
                var s = parent.lossyScale;
                go.transform.localScale = new Vector3(1 / s.x, 1 / s.y, 1 / s.z);
            }
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
            tm.fontSize = 64;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.black;
            var label = go.AddComponent<BillboardLabel>();
            return label;
        }

        private static Material Mat(string name, Color color)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
