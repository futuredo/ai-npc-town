using System.Collections.Generic;
using System.IO;
using System.Linq;
using Town.Unity.Dragonsleep;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Town.Editor
{
    /// <summary>
    /// 《龙眠镇》主场景生成器（v6）。
    /// 基于 KINGDOM 原作者的 DemoExterior 复制出玩法场景，再叠加：统一调色与光照、
    /// 角色站位与动作、母龙/雏龙、关键地点标记。可重复执行：每次只重建 "__Dragonsleep" 根节点。
    /// 商业素材与生成的场景都在被忽略的 LocalThirdParty 下；本脚本是可提交的“配方”。
    /// </summary>
    public static class DragonsleepSceneBuilder
    {
        public const string Root = "Assets/LocalThirdParty/Dragonsleep";
        public const string ScenePath = Root + "/Dragonsleep_Town.unity";
        const string SourceScene = "Assets/LocalThirdParty/Polyart/PolyartStudio/DreamscapeCastle/Scenes/DemoExterior.unity";
        const string VolumePath = Root + "/Dragonsleep_Town_Volume.asset";
        const string ControllerPath = Root + "/Dragonsleep_Villager.controller";
        const string RigName = "__Dragonsleep";

        const string Horde = "Assets/LocalThirdParty/Polygonmaker/Fantasy Horde Series/Fantasy Horde - Villagers/";
        const string Npc = "Assets/LocalThirdParty/Medieval_NPC_Pack_2/";
        const string Dragons = "Assets/LocalThirdParty/StylizedDragonPack/Prefabs/URP/";
        const string Afs = "Assets/LocalThirdParty/Aquarius Fantasy - Ultimate Workshops/FBX/Prefabs/";

        /// <summary>地点（来自 KINGDOM 原场景布局，单位米，y 由地面射线决定）。</summary>
        public static readonly Dictionary<string, Vector3> Places = new Dictionary<string, Vector3>
        {
            { "market", new Vector3(268f, 0f, 352f) },
            { "cathedral", new Vector3(122f, 0f, 350f) },
            { "catacombs", new Vector3(20f, 0f, 372f) },
            { "docks", new Vector3(397.5f, 0f, 653f) },
            { "dragon_skull", new Vector3(126f, 0f, 533f) },
            { "castle_gate", new Vector3(223f, 0f, 248f) },
            { "workshop", new Vector3(250f, 0f, 340f) },
        };

        /// <summary>抬高的平台（教堂广场）地形射线无法区分，手工指定站立高度。</summary>
        static readonly Dictionary<string, float> FixedHeights = new Dictionary<string, float> { { "cathedral", 25.5f } };

        static Vector3 PlacePos(string place, Vector3 offset)
        {
            var p = Places[place] + offset;
            return FixedHeights.TryGetValue(place, out var y) ? new Vector3(p.x, y + offset.y, p.z) : Ground(p);
        }

        [MenuItem("Town/Dragonsleep/Build Town Scene")]
        public static void BuildMenu() => Debug.Log(Build());

        public static string Build()
        {
            Directory.CreateDirectory(Root);
            if (!File.Exists(ScenePath))
            {
                if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
                    return "复制 KINGDOM DemoExterior 失败：" + SourceScene;
            }
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var old = GameObject.Find(RigName);
            if (old != null) Object.DestroyImmediate(old);
            var rig = new GameObject(RigName);

            var report = new List<string>();
            Atmosphere(rig.transform, report);
            var controller = VillagerController(report);
            Cast(rig.transform, controller, report);
            var whelp = DragonsAndWhelp(rig.transform, report);
            Loom(rig.transform, report);
            var places = Markers(rig.transform);
            Gameplay(rig.transform, places, whelp, report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            return "龙眠镇场景已生成：" + ScenePath + "\n" + string.Join("\n", report);
        }

        // ---------- 光照与调色 ----------

        static void Atmosphere(Transform rig, List<string> report)
        {
            var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                // 午后偏金色的侧逆光：长影、暖高光，建筑轮廓更清楚。
                sun.transform.rotation = Quaternion.Euler(30f, 318f, 0f);
                sun.color = new Color(1f, 0.91f, 0.79f);
                sun.intensity = 1.3f;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.72f;
                report.Add("太阳：30°/318°，暖色 1.3");
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.57f, 0.66f, 0.72f);
            RenderSettings.fogDensity = 0.0015f;

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumePath);
            }
            foreach (var c in profile.components.ToArray()) Object.DestroyImmediate(c, true);
            profile.components.Clear();

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.45f);
            color.contrast.Override(14f);
            color.saturation.Override(10f);
            color.colorFilter.Override(new Color(1f, 0.985f, 0.96f));
            var wb = profile.Add<WhiteBalance>(true);
            wb.temperature.Override(8f);
            wb.tint.Override(2f);
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.93f, 0.97f, 1.08f, 0f));   // 阴影偏冷
            smh.highlights.Override(new Vector4(1.05f, 1.0f, 0.93f, 0f)); // 高光偏暖
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.55f);
            bloom.scatter.Override(0.7f);
            bloom.tint.Override(new Color(1f, 0.92f, 0.8f));
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.24f);
            vignette.smoothness.Override(0.45f);
            DragonsleepArtReview.ApplyProfile(profile);
            foreach (var c in profile.components) { c.name = c.GetType().Name; AssetDatabase.AddObjectToAsset(c, profile); }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var volumeGo = new GameObject("Atmosphere (Global Volume)");
            volumeGo.transform.SetParent(rig, false);
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10;
            volume.sharedProfile = profile;

            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }
            report.Add("后期：ACES、冷暗暖亮分离、轻 Bloom 与暗角");

            // 水面需要深度/不透明贴图做折射与岸边渐变，否则整片湖是黑的。
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset urp &&
                (!urp.supportsCameraDepthTexture || !urp.supportsCameraOpaqueTexture))
            {
                urp.supportsCameraDepthTexture = true;
                urp.supportsCameraOpaqueTexture = true;
                EditorUtility.SetDirty(urp);
                report.Add("URP：开启深度/不透明贴图（湖面折射）");
            }
            FixTerrainLayers(report);

            // 原湖面略低于码头木桩，抬高 0.6 米让栈桥真正“立在水里”。
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r.sharedMaterial == null || r.sharedMaterial.name != "MI_Water_Lake") continue;
                var pos = r.transform.position;
                if (pos.y < 2.5f) { r.transform.position = new Vector3(pos.x, 2.6f, pos.z); report.Add("湖面：抬高至 2.6 米"); }
            }
        }

        /// <summary>
        /// KINGDOM 的部分地形层把 ORM 贴图挂在 URP 的 Mask Map 上：URP 把 R 通道当金属度，
        /// 结果沙地/石板渲染成黑色金属。这里把金属度通道压到 0，保留 AO 与平滑度。
        /// </summary>
        static void FixTerrainLayers(List<string> report)
        {
            var terrain = Terrain.activeTerrain;
            if (terrain == null) return;
            int fixedCount = 0;
            foreach (var layer in terrain.terrainData.terrainLayers)
            {
                if (layer == null || layer.maskMapTexture == null || !layer.maskMapTexture.name.Contains("ORM")) continue;
                if (layer.maskMapRemapMax.x == 0f) continue;
                layer.maskMapRemapMin = new Vector4(0f, 0f, 0f, 0f);
                layer.maskMapRemapMax = new Vector4(0f, 1f, 1f, 0.35f);
                EditorUtility.SetDirty(layer);
                fixedCount++;
            }
            if (fixedCount > 0) report.Add($"地形层：修正 {fixedCount} 个 ORM 遮罩的金属度");
        }

        // ---------- 角色 ----------

        static AnimatorController VillagerController(List<string> report)
        {
            var clips = new (string state, string file)[]
            {
                ("Idle", "Anim_Warrior@idle"), ("IdleBreak", "Anim_Warrior@idlebreak"),
                ("Talk", "Anim_Warrior@talk"), ("OldIdle", "Anim_Old@idle"),
                ("Walk", "Anim_Old@walk_inplace"), ("Sit", "Anim_Sit@idle"),
                ("SitTalk", "Anim_Sit@talk"), ("Cheer", "Anim_Warrior@comemoration"),
                ("Decline", "Anim_Sit@decline"),
            };
            if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var sm = controller.layers[0].stateMachine;
            int found = 0;
            foreach (var (state, file) in clips)
            {
                string path = Horde + "Animations/" + file + ".FBX";
                EnsureLoop(path);
                var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview"));
                var s = sm.AddState(state);
                s.motion = clip;
                if (clip != null) found++;
                if (state == "Idle") sm.defaultState = s;
            }
            report.Add($"村民动作控制器：{found}/{clips.Length} 段动作");
            return controller;
        }

        static void EnsureLoop(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) return;
            var clips = mi.clipAnimations.Length > 0 ? mi.clipAnimations : mi.defaultClipAnimations;
            bool changed = false;
            foreach (var c in clips)
            {
                bool loop = !path.Contains("decline") && !path.Contains("comemoration");
                if (c.loopTime != loop || !c.lockRootRotation || !c.lockRootHeightY || !c.lockRootPositionXZ)
                {
                    c.loopTime = loop; c.lockRootRotation = true; c.lockRootHeightY = true; c.lockRootPositionXZ = true;
                    c.keepOriginalOrientation = true; c.keepOriginalPositionY = true; c.keepOriginalPositionXZ = true;
                    changed = true;
                }
            }
            if (!changed) return;
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }

        struct Actor
        {
            public string Id, Name, Prefab, Avatar, Place, State, Face;
            public Vector3 Offset;
        }

        static readonly Actor[] Actors =
        {
            new Actor { Id = "osric", Name = "奥斯里克·灰岩（王室总管）", Prefab = Npc + "Merchant/Prefab/SK_Merchant.prefab",
                Avatar = Npc + "Merchant/Base Mesh/SK_Merchant.fbx", Place = "market", Offset = new Vector3(-4.5f, 0, -2.0f), State = "Talk", Face = "vesna" },
            new Actor { Id = "vesna", Name = "维丝娜（外乡商人）", Prefab = Npc + "Peasant_woman/Prefab/SK_Peasant_woman skin3.prefab",
                Avatar = Npc + "Peasant_woman/Base mesh/SK_Peasant_woman.fbx", Place = "market", Offset = new Vector3(-2.6f, 0, -0.6f), State = "Idle", Face = "osric" },
            new Actor { Id = "marta", Name = "玛尔妲（厨娘）", Prefab = Npc + "Cook/Prefab/SK_Cook_Skin1.prefab",
                Avatar = Npc + "Cook/Base Mesh/SK_Cook_Skin1.fbx", Place = "market", Offset = new Vector3(4.0f, 0, 4.5f), State = "OldIdle", Face = "camera" },
            new Actor { Id = "maren", Name = "梅伦修女（大教堂看守）", Prefab = Npc + "Peasant_woman/Prefab/SK_Peasant_woman skin1.prefab",
                Avatar = Npc + "Peasant_woman/Base mesh/SK_Peasant_woman.fbx", Place = "cathedral", Offset = Vector3.zero, State = "Idle", Face = "camera" },
            new Actor { Id = "pip", Name = "皮普（码头孤儿）", Prefab = Horde + "Characters/Child_1.prefab",
                Avatar = null, Place = "docks", Offset = new Vector3(-1.2f, 0, -0.6f), State = "Talk", Face = "whelp" },
            new Actor { Id = "player", Name = "言契师（玩家）", Prefab = Npc + "Peasant_man/Prefab/Character/Peasant_man_skin2.prefab",
                Avatar = Npc + "Peasant_man/Base Mesh/Peasant_man_skin1.fbx", Place = "market", Offset = new Vector3(-6.0f, 0, -6.5f), State = "Idle", Face = "osric" },
        };

        static void Cast(Transform rig, AnimatorController controller, List<string> report)
        {
            var parent = new GameObject("Cast").transform;
            parent.SetParent(rig, false);
            var made = new Dictionary<string, Transform>();
            foreach (var a in Actors)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(a.Prefab);
                if (prefab == null)
                {
                    // 额外皮肤未导入时退回同角色的一号皮肤，保证场景可生成。
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(a.Prefab.Replace("skin3", "skin1").Replace("skin2", "skin1"));
                    report.Add($"缺少 {Path.GetFileName(a.Prefab)}，暂用一号皮肤");
                }
                if (prefab == null) { report.Add("跳过 " + a.Id); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                go.name = a.Name;
                go.transform.position = PlacePos(a.Place, a.Offset);
                var animator = go.GetComponentInChildren<Animator>(); if (animator == null) animator = go.AddComponent<Animator>();
                if (animator.avatar == null && a.Avatar != null)
                    animator.avatar = AssetDatabase.LoadAllAssetsAtPath(a.Avatar).OfType<Avatar>().FirstOrDefault();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                var pose = Ensure<AmbientPose>(animator.gameObject);
                pose.StateName = a.State;
                made[a.Id] = go.transform;
                go.AddComponent<DragonsleepActorTag>().ActorId = a.Id;
            }
            // 朝向在全部生成后再设置。
            foreach (var a in Actors)
            {
                if (!made.TryGetValue(a.Id, out var t)) continue;
                Vector3 target = a.Face == "camera" ? t.position + Vector3.back * 5f
                    : a.Face == "whelp" ? Places["docks"] + new Vector3(0.6f, 0, 0.8f)
                    : made.TryGetValue(a.Face ?? "", out var other) ? other.position : t.position + Vector3.forward;
                Vector3 dir = target - t.position; dir.y = 0;
                if (dir.sqrMagnitude > 0.01f) t.rotation = Quaternion.LookRotation(dir);
            }
            report.Add($"角色：{made.Count}/{Actors.Length} 名入场，均带动作");

            // 背景人群：Horde 村民，只做日常动作，不接 AI。
            var crowd = new[] { ("Male_1", new Vector3(10f, 0, -6f), "Sit"), ("Female_1", new Vector3(11.2f, 0, -5.2f), "SitTalk"),
                                ("Monk_1", new Vector3(-12f, 0, 8f), "OldIdle") };
            int extras = 0;
            foreach (var (name, offset, state) in crowd)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Horde + "Characters/" + name + ".prefab");
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                go.name = "镇民 " + name;
                go.transform.position = Ground(Places["market"] + offset);
                go.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360), 0);
                var animator = go.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                Ensure<AmbientPose>(animator.gameObject).StateName = state;
                extras++;
            }
            report.Add($"背景镇民：{extras}");
        }

        // ---------- 龙 ----------

        static Transform DragonsAndWhelp(Transform rig, List<string> report)
        {
            var mother = AssetDatabase.LoadAssetAtPath<GameObject>(Dragons + "Dragon/Dragon_Bl.prefab");
            if (mother != null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(mother, rig);
                go.name = "苍穹之母（母龙）";
                go.transform.localScale = Vector3.one * 2.2f;
                var circle = go.AddComponent<DragonCircle>();
                circle.Center = new Vector3(200f, 60f, 650f);
                circle.Radius = 140f;
                circle.Height = 70f;
                circle.StartAngle = 205f;
                circle.Place(0f);
                report.Add("母龙：盘旋于北侧雪峰上空");
            }
            var whelp = AssetDatabase.LoadAssetAtPath<GameObject>(Dragons + "DragonWhelp/DragonWhelp_Bl.prefab");
            if (whelp != null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(whelp, rig);
                go.name = "小烬（雏龙）";
                go.transform.position = Ground(Places["docks"] + new Vector3(0.6f, 0, 0.8f)) + Vector3.up * 0.9f;
                go.transform.rotation = Quaternion.LookRotation(Vector3.left);
                var animator = go.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    animator.applyRootMotion = false;
                    Ensure<AmbientPose>(animator.gameObject).StateName = "Fly_Idle";
                }
                report.Add("雏龙：码头旧船屋旁");
                return go.transform;
            }
            return null;
        }

        // ---------- 织言机（附魔工坊） ----------

        static void Loom(Transform rig, List<string> report)
        {
            var station = AssetDatabase.LoadAssetAtPath<GameObject>(Afs + "Themed Workshops/Workstation Modules/Enchanters_WorkStation_A.prefab");
            if (station == null) { report.Add("缺少附魔工作站"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(station, rig);
            go.name = "织言机（附魔工坊）";
            go.transform.position = Ground(Places["workshop"]);
            go.transform.rotation = Quaternion.Euler(0, 40f, 0);
            var glow = new GameObject("织言之光");
            glow.transform.SetParent(go.transform, false);
            glow.transform.localPosition = new Vector3(0, 1.6f, 0);
            var light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.45f, 0.75f, 1f);
            light.intensity = 3.5f;
            light.range = 6f;
            report.Add("织言机：附魔工作站 + 蓝色辉光");
        }

        static Transform Markers(Transform rig)
        {
            var parent = new GameObject("Places").transform;
            parent.SetParent(rig, false);
            foreach (var kv in Places)
            {
                var m = new GameObject("Place_" + kv.Key);
                m.transform.SetParent(parent, false);
                m.transform.position = PlacePos(kv.Key, Vector3.zero);
            }
            return parent;
        }

        // ---------- 可玩部分：玩家、镜头、织言镜 ----------

        const string PanelPath = Root + "/Dragonsleep_Panel.asset";
        const string ThemePath = "Assets/Town.Unity/Moonlight/MoonRuntimeTheme.tss";

        static void Gameplay(Transform rig, Transform places, Transform whelp, List<string> report)
        {
            var playerTag = Object.FindObjectsByType<DragonsleepActorTag>(FindObjectsSortMode.None).FirstOrDefault(t => t.ActorId == "player");
            if (playerTag == null) { report.Add("缺少玩家角色，跳过可玩部分"); return; }
            var player = playerTag.gameObject;
            var cc = Ensure<CharacterController>(player);
            cc.height = 1.8f; cc.radius = 0.32f; cc.center = new Vector3(0, 0.92f, 0); cc.stepOffset = 0.4f; cc.slopeLimit = 50f;
            player.transform.position += Vector3.up * 0.05f;
            var controller = Ensure<DsPlayerController>(player);

            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                cam.gameObject.SetActive(false);
            }
            var camGo = new GameObject("DS Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(rig, false);
            var camera = camGo.AddComponent<Camera>();
            camera.fieldOfView = 50f; camera.nearClipPlane = 0.15f; camera.farClipPlane = 3000f;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
            camGo.AddComponent<AudioListener>();
            var follow = camGo.AddComponent<DsFollowCamera>();
            follow.Target = player.transform;
            follow.Yaw = player.transform.eulerAngles.y;
            controller.CameraBasis = camGo.transform;
            camGo.transform.position = player.transform.position + Quaternion.Euler(16f, follow.Yaw, 0) * Vector3.back * 6.5f + Vector3.up * 1.6f;
            camGo.transform.LookAt(player.transform.position + Vector3.up * 1.6f);

            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
                panel.scaleMode = UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1600, 900);
                panel.match = 0.5f;
                panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(ThemePath);
                AssetDatabase.CreateAsset(panel, PanelPath);
            }
            panel.referenceResolution = new Vector2Int(1600, 900);
            EditorUtility.SetDirty(panel);
            var gameGo = new GameObject("Dragonsleep Game");
            gameGo.transform.SetParent(rig, false);
            gameGo.SetActive(false);
            var game = gameGo.AddComponent<DragonsleepGame>();
            game.Player = controller; game.PlacesRoot = places; game.Whelp = whelp;
            var doc = gameGo.AddComponent<UnityEngine.UIElements.UIDocument>();
            doc.panelSettings = panel;
            var hud = gameGo.AddComponent<LoomLensHud>();
            hud.Game = game;
            hud.Skin = UiSkin(report);
            game.Hud = hud;
            gameGo.SetActive(true);
            report.Add("可玩：第三人称言契师 + 越肩镜头 + 织言镜（离线提供方）");
        }

        const string UiRoot = "Assets/LocalThirdParty/MedievalKingdomUI/Artworks/";
        const string SkinPath = Root + "/Dragonsleep_UiSkin.asset";

        /// <summary>Medieval Kingdom UI 白金皮肤 + 人物头像 → 界面皮肤资产。</summary>
        static DsUiSkin UiSkin(List<string> report)
        {
            Texture2D T(string rel) => AssetDatabase.LoadAssetAtPath<Texture2D>(UiRoot + rel + ".png");
            var skin = AssetDatabase.LoadAssetAtPath<DsUiSkin>(SkinPath);
            if (skin == null) { skin = ScriptableObject.CreateInstance<DsUiSkin>(); AssetDatabase.CreateAsset(skin, SkinPath); }
            const string W = "White_gold_Skin/UI_elements_W/", B = "White_gold_Skin/Buttons_W/", P = "Characters/Portraits/";
            skin.TitlePlate = T(W + "Title_frame_medium");
            skin.TitlePlateEpic = T(W + "Title_frame_medium_epic");
            skin.Parchment = T(W + "Main_bar_story_hori");
            skin.PaperWindow = T(W + "Popup_window_paper");
            skin.DarkPanel = T(W + "Main_bar");
            skin.NavyBar = T(W + "dialog_frame_a");
            skin.NavyWindow = T(W + "Popup_window");
            skin.RoundFrame = T(W + "icon_frame_round");
            skin.ButtonDark = T(B + "Button_middle");
            skin.ButtonRed = T(B + "Button_middle_red");
            skin.ButtonRound = T(B + "Button_round");
            skin.ButtonRoundRed = T(B + "Button_round_red");
            skin.Portraits = new[]
            {
                new DsUiSkin.Portrait { ActorId = "osric", Image = T(P + "Franks_icon") },
                new DsUiSkin.Portrait { ActorId = "maren", Image = T(P + "Paladin_icon") },
                new DsUiSkin.Portrait { ActorId = "marta", Image = T(P + "Villager_woman_icon") },
                new DsUiSkin.Portrait { ActorId = "vesna", Image = T(P + "archer_icon") },
                new DsUiSkin.Portrait { ActorId = "pip", Image = T(P + "young_warrior_icon") },
                new DsUiSkin.Portrait { ActorId = "player", Image = T(P + "Villager_man_icon") },
            };
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
            int missing = new[] { skin.TitlePlate, skin.Parchment, skin.PaperWindow, skin.DarkPanel, skin.NavyBar, skin.NavyWindow, skin.RoundFrame, skin.ButtonRed, skin.ButtonRound }.Count(x => x == null)
                + skin.Portraits.Count(x => x.Image == null);
            report.Add($"界面皮肤：Medieval Kingdom UI 白金皮肤 + 6 张人物头像（缺失 {missing}）");
            return skin;
        }

        static T Ensure<T>(Component c) where T : Component => Ensure<T>(c.gameObject);

        static T Ensure<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        public static Vector3 Ground(Vector3 p)
        {
            var origin = new Vector3(p.x, 400f, p.z);
            var hits = Physics.RaycastAll(origin, Vector3.down, 800f, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return p;
            // 取“离地形最近的可站立面”：码头木板、台阶算，雕像顶、屋顶不算。
            float terrainY = Terrain.activeTerrain != null ? Terrain.activeTerrain.SampleHeight(p) + Terrain.activeTerrain.transform.position.y : float.NegativeInfinity;
            var walkable = hits.Where(h => h.normal.y > 0.7f && h.point.y <= terrainY + 4f).OrderByDescending(h => h.point.y).ToArray();
            var top = walkable.Length > 0 ? walkable[0] : hits.OrderBy(h => h.distance).First();
            return new Vector3(p.x, top.point.y + p.y, p.z);
        }
    }
}
