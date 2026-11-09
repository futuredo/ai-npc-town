using System;
using System.Linq;
using Town.Unity.Moonlight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Town.Editor
{
    /// <summary>仅安装到选定的玩法场景；不改下载的 Prefab、旧实验场景或原作者场景。</summary>
    public static class MoonThreeCSetup
    {
        [MenuItem("Town/Moonlight/Upgrade Village Third Person Controls")]
        public static string UpgradeVillage()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("请退出运行和编译。");
            var open = SceneManager.GetSceneByPath(MoonVillageSceneBuilder.ScenePath);
            if (open.IsValid() && open.isDirty) throw new InvalidOperationException("村落场景有未保存改动，拒绝覆盖。");
            var previous = SceneManager.GetActiveScene();
            bool opened = !open.IsValid() || !open.isLoaded;
            var scene = opened ? EditorSceneManager.OpenScene(MoonVillageSceneBuilder.ScenePath, OpenSceneMode.Additive) : open;
            try
            {
                SceneManager.SetActiveScene(scene);
                Install(scene);
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
                return scene.path;
            }
            finally
            {
                if (opened) EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            }
        }

        public static void Install(Scene scene)
        {
            if (scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MoonPlayerController>()).Any())
                throw new InvalidOperationException("场景已有第三人称控制器，拒绝重复安装。");
            var execution = Find<MoonWorkshopGame>(scene);
            var hud = Find<MoonRescueHud>(scene);
            var camera = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>()).Single(c => c.enabled);
            var old = camera.GetComponent<MoonSceneCamera>(); if (old) UnityEngine.Object.DestroyImmediate(old);
            camera.orthographic = false; camera.fieldOfView = 55; camera.nearClipPlane = .1f; camera.rect = new Rect(0, 0, 1, 1);
            var rig = camera.gameObject.AddComponent<MoonThirdPersonCamera>();
            var go = new GameObject("Reader — third person player");
            var spawn = execution.worldOrigin + new Vector3(-3, 0, -2.5f);
            if (!NavMesh.SamplePosition(spawn, out var hit, 2, NavMesh.AllAreas)) throw new InvalidOperationException("玩家出生点缺可达地面。");
            go.transform.position = hit.position + Vector3.up * .08f;
            go.transform.rotation = Quaternion.Euler(0, 0, 0);
            var controller = go.AddComponent<CharacterController>();
            controller.height = 1.7f; controller.radius = .27f; controller.center = Vector3.up * .85f;
            controller.skinWidth = .035f; controller.stepOffset = .25f; controller.slopeLimit = 45; controller.minMoveDistance = 0;
            var player = go.AddComponent<MoonPlayerController>(); player.cameraBasis = camera.transform; player.hud = hud; player.execution = execution;
            var model = MoonRescueSceneBuilder.Place("Assets/LocalThirdParty/Polygonmaker/Fantasy Horde Series/Fantasy Horde - Villagers/Characters/Male_1.prefab", "Reader body", go.transform, Vector3.zero, 1.7f, true);
            var animation = model.AddComponent<MoonActorAnimation>();
            animation.animator = model.GetComponentInChildren<Animator>(); animation.player = player;
            animation.idle = MoonRescueSceneBuilder.Clip("Anim_Warrior@idle.FBX");
            animation.walk = MoonRescueSceneBuilder.Clip("Anim_Old@walk_inplace.FBX");
            if (!animation.animator || !animation.animator.avatar || !animation.animator.avatar.isHuman) throw new InvalidOperationException("玩家缺人形 Avatar。");
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 28;
            // 相机与视线忽略人物自身，不改变角色对环境的实体碰撞。
            foreach (var body in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MoonActorAnimation>()))
                foreach (var t in body.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 28;
            var finnBody = execution.finn.gameObject.AddComponent<CapsuleCollider>();
            finnBody.height = 1.35f; finnBody.radius = .22f; finnBody.center = Vector3.up * .675f;
            execution.finn.gameObject.layer = 28;
            var interaction = go.AddComponent<MoonPlayerInteraction>();
            interaction.player = player; interaction.hud = hud; interaction.execution = execution;
            var finn = execution.finn.gameObject.AddComponent<MoonInteractionPoint>(); finn.kind = MoonInteractionKind.Discuss; finn.label = "和芬恩商量借光的方法"; finn.radius = 2.8f;
            var station = scene.GetRootGameObjects().Single(r => r.name.StartsWith("Maeve's complete enchanter workstation"));
            var desk = station.AddComponent<MoonInteractionPoint>(); desk.kind = MoonInteractionKind.Discuss; desk.label = "查看借光工作台";
            // 工作台挡住身体；原作者装饰碰撞曾被剥离，这里补上本场景的交互实体。
            var bounds = new Bounds(station.transform.position, Vector3.zero);
            foreach (var renderer in station.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(renderer.bounds);
            var box = station.AddComponent<BoxCollider>(); box.center = station.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(bounds.size.x / station.transform.lossyScale.x, bounds.size.y / station.transform.lossyScale.y, bounds.size.z / station.transform.lossyScale.z);
            desk.focusOffset = Vector3.up + Vector3.back * (bounds.extents.z + .3f);
            desk.radius = 2.7f;
            var light = execution.prism.gameObject.AddComponent<MoonInteractionPoint>(); light.kind = MoonInteractionKind.Light; light.label = "切换月焰供光"; light.focusOffset = Vector3.back * .35f; light.radius = 2.3f;
            interaction.points = new[] { finn, desk, light };
            hud.immersive = true; hud.interaction = interaction;
            rig.target = go.transform; rig.hud = hud; rig.ResetView(); rig.UpdatePose(1);
        }

        static T Find<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>()).Single();
    }
}
