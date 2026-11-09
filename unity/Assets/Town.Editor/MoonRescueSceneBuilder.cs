using System;
using System.IO;
using System.Linq;
using Town.Unity.Moonlight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Town.Editor
{
    /// <summary>在本机商业素材目录另建整合样板，不覆盖原技术场景或素材审阅场景。</summary>
    public static class MoonRescueSceneBuilder
    {
        public const string ScenePath = "Assets/LocalThirdParty/Gameplay/Moonlight_Rescue.unity";
        const string Root = "Assets/LocalThirdParty/Gameplay";
        const string Horde = "Assets/LocalThirdParty/Polygonmaker/Fantasy Horde Series/Fantasy Horde - Villagers/";
        const string Props = "Assets/LocalThirdParty/Aquarius Fantasy - Ultimate Workshops/FBX/Prefabs/";
        const string Ui = "Assets/LocalThirdParty/MedievalKingdomUI/Artworks/White_gold_Skin/";

        [MenuItem("Town/Moonlight/Build Local Rescue Chapter")]
        public static string Build()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("请先退出运行或编译。");
            if (File.Exists(ScenePath)) throw new InvalidOperationException("拒绝覆盖已存在的局内样板，请先另存。");
            Directory.CreateDirectory(Root); AssetDatabase.Refresh();
            if (!AssetDatabase.CopyAsset(MoonWorkshopSceneBuilder.ScenePath, ScenePath)) throw new IOException("复制技术场景失败。");
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                var execution = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MoonWorkshopGame>()).Single();
                execution.showDeveloperPanel = false;
                // 固定光路的灯群是无实体碰撞的引路灵，不应把等待验光的角色挤走。
                // 不把局部避障挤出的位移当作角色同意后的自主移动。
                execution.finn.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
                var visual = new GameObject("Local art — no navigation or rule authority");
                // 不改变原导航几何，装饰的碰撞、脚本和根运动不能提供虚假到达。
                foreach (Transform child in execution.finn.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
                Character("Finn — half-written wayfinder", "Child_1", execution.finn.transform, Vector3.zero, 1.35f, execution.finn);
                Character("Maeve — keeper of borrowed light", "Female_1", visual.transform, new Vector3(-5.4f, 0, 1.5f), 1.65f, null);
                Character("Sern — waiting at the safe platform", "Monk_1", visual.transform, new Vector3(5.1f, 0, 1.1f), 1.8f, null);
                var lantern = Props + "Small and Tabletop Props/Lanterns, Candles/Fae_Lantern_1A1.prefab";
                var beaconRenderer = execution.beacon.GetComponent<Renderer>(); beaconRenderer.enabled = false;
                Place(lantern, "Borrowed moon — light source", execution.beacon, Vector3.zero, 0.55f, true);
                var agents = new NavMeshAgent[3];
                for (int i = 0; i < agents.Length; i++)
                {
                    var go = new GameObject("Drifting lantern " + (i + 1));
                    go.transform.position = new Vector3(-4.5f, 0, (i - 1) * 0.3f);
                    var agent = go.AddComponent<NavMeshAgent>(); agents[i] = agent;
                    agent.speed = 1.9f; agent.acceleration = 16; agent.angularSpeed = 720;
                    agent.radius = 0.1f; agent.height = 0.8f; agent.stoppingDistance = 0.1f;
                    agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
                    Place(lantern, "Lantern body", go.transform, Vector3.up * 0.55f, 0.4f, true);
                }
                var table = Place(Props + "Workshop Furniture/Workshop Tables/Workshop_Table_1A1.prefab", "Borrowing table", visual.transform, new Vector3(-5.5f, 0, -1.5f), 1, true);
                Place(Props + "Themed Workshops/Enchanter/Book_Open_1A1.prefab", "Book of witnessed routes", table.transform, new Vector3(0, 1, 0), 0.3f, true);
                Place(Props + "Small and Tabletop Props/Crystals/Crystal_Blue_1A2.prefab", "Moonlight reservoir", table.transform, new Vector3(0.4f, 1, 0), 0.35f, true);
                Place("Assets/LocalThirdParty/Polyart/PolyartStudio/DreamscapeCastle/Prefabs/BuildingsFull/PF_CastleHouse_01.prefab", "Moonlight workshop backdrop", visual.transform, new Vector3(-6, -0.1f, 5), 3.3f, false);
                Place("Assets/LocalThirdParty/StylizedDragonPack/Prefabs/URP/Dragon/Dragon_Bl.prefab", "Oro — distant sleeping dragon symbol", visual.transform, new Vector3(2, -1, 7), 3.5f, false);
                var rescue = new GameObject("MoonRescueGame").AddComponent<MoonRescueGame>();
                rescue.execution = execution; rescue.lights = agents;
                var settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1440, 900);
                settings.themeStyleSheet = Required<ThemeStyleSheet>("Assets/Town.Unity/Moonlight/MoonRuntimeTheme.tss");
                AssetDatabase.CreateAsset(settings, Root + "/MoonRescuePanel.asset");
                var uiGo = new GameObject("MoonRescue HUD");
                var document = uiGo.AddComponent<UIDocument>(); document.panelSettings = settings;
                var hud = uiGo.AddComponent<MoonRescueHud>(); hud.game = rescue;
                hud.paperTexture = Required<Texture2D>(Ui + "UI_elements_W/Popup_window_paper 1.png");
                hud.buttonTexture = Required<Texture2D>(Ui + "Buttons_W/Button_middle_Fr.png");
                hud.titleTexture = Required<Texture2D>(Ui + "UI_elements_W/Title_frame_long.png");
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

        static void Character(string name, string file, Transform parent, Vector3 position, float height, NavMeshAgent agent)
        {
            var model = Place(Horde + "Characters/" + file + ".prefab", name, parent, position, height, true);
            var animator = model.GetComponentInChildren<Animator>();
            if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("人物缺有效人形 Avatar：" + file);
            var animation = model.AddComponent<MoonActorAnimation>();
            animation.animator = animator; animation.agent = agent;
            animation.idle = Clip("Anim_Warrior@idle.FBX");
            animation.walk = Clip("Anim_Old@walk_inplace.FBX");
        }

        internal static AnimationClip Clip(string file) => AssetDatabase.LoadAllAssetsAtPath(Horde + "Animations/" + file)
            .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview"));

        internal static GameObject Place(string path, string name, Transform parent, Vector3 position, float height, bool local)
        {
            var source = Required<GameObject>(path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = name; instance.transform.SetParent(parent, false);
            foreach (var t in instance.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) { animator.runtimeAnimatorController = null; animator.applyRootMotion = false; }
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) throw new InvalidOperationException("素材没有可见几何：" + path);
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            instance.transform.localScale *= height / Mathf.Max(0.01f, bounds.size.y);
            // 按可见边界把脚/物体底部对齐锚点；不改原模型、Avatar 或原 Prefab。
            bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            var offset = new Vector3(bounds.center.x - instance.transform.position.x, bounds.min.y - instance.transform.position.y, bounds.center.z - instance.transform.position.z);
            if (local) instance.transform.localPosition = position - (parent ? parent.InverseTransformVector(offset) : offset);
            else instance.transform.position = position - offset;
            return instance;
        }
        static T Required<T>(string path) where T : UnityEngine.Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new FileNotFoundException("缺少已筛选的本机素材", path);
    }
}
