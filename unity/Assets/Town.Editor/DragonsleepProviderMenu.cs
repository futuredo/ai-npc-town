using System.IO;
using Town.Core.Dragonsleep;
using Town.OpenAI;
using Town.Unity.Dragonsleep;
using UnityEditor;
using UnityEngine;

namespace Town.Editor
{
    /// <summary>每次在线验证都需要显式授权；旧偏好、再次 Play、重开游戏均不重新授予预算。</summary>
    [InitializeOnLoad]
    public static class DragonsleepProviderMenu
    {
        const string ArmedKey = "Dragonsleep.OnlineDialogue.ArmedOnce";
        const string ResearchKey = "Dragonsleep.OnlineResearch.ArmedOnce";
        const string OfflineMenu = "Town/Dragonsleep/AI 提供方/离线规则（不收费）";
        const string OnlineMenu = "Town/Dragonsleep/AI 提供方/授权下一局对话验证（最多 3 次）";
        const string ResearchMenu = "Town/Dragonsleep/AI 提供方/下一局六机制真实模型（研究模式）";
        public static string Mode => SessionState.GetBool(ResearchKey, false) ? "research_once" : SessionState.GetBool(ArmedKey, false) ? "dialogue_once" : "offline";

        static DragonsleepProviderMenu()
        {
            DragonsleepGame.ProviderFactory = Create;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode) SetMode("offline");
            };
        }

        [MenuItem(OfflineMenu)] static void SetOffline() => SetMode("offline");
        [MenuItem(OfflineMenu, true)] static bool OfflineChecked() { Menu.SetChecked(OfflineMenu, Mode == "offline"); return true; }
        [MenuItem(OnlineMenu, true)] static bool CanArm() => !EditorApplication.isPlayingOrWillChangePlaymode;
        [MenuItem(OnlineMenu)] static void ArmDialogue()
        {
            if (EditorUtility.DisplayDialog("授权一次对话验证", "下一次 Play 中，最多 3 次居民对话使用 gpt-6-luna。每次最多 384 输出 tokens，预留合计 USD 0.006（预算预留，不是实际费用）。\n\n行动、造物、夜间与 GM 仍为离线模拟。预算耗尽会明确报错，不自动改用模拟结果。退出 Play 后授权失效；之后需重新授权。", "授权下一局", "保持离线"))
            { SessionState.SetBool(ResearchKey, false); SessionState.SetBool(ArmedKey, true); }
        }

        [MenuItem(ResearchMenu, true)] static bool CanResearch() => !EditorApplication.isPlayingOrWillChangePlaymode;
        [MenuItem(ResearchMenu)] static void ArmResearch()
        {
            // 用户已明确授权本项目研究无费用上限；点击此菜单只开启下一局，退出后清除。
            SessionState.SetBool(ArmedKey, false); SessionState.SetBool(ResearchKey, true);
            Debug.Log("[龙眠镇] 下一局六机制均使用 gpt-6-luna，持续记录 usage；本项研究不设费用上限。退出 Play 清除，不自动重试。");
        }

        /// <summary>自动化只能解除授权，不能通过旧的持久偏好静默开启付费。</summary>
        public static void SetMode(string mode)
        {
            SessionState.SetBool(ArmedKey, false);
            SessionState.SetBool(ResearchKey, false);
            EditorPrefs.DeleteKey("Dragonsleep.Provider");
        }

        static IAiProvider Create()
        {
            var offline = new RuleProvider();
            bool armed = SessionState.GetBool(ArmedKey, false);
            bool research = SessionState.GetBool(ResearchKey, false);
            SessionState.SetBool(ArmedKey, false); // 消耗一次性授权，重建 Game 不重新获得额度
            SessionState.SetBool(ResearchKey, false);
            if (!armed && !research) return offline;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string key;
            try { key = LocalValidationAccess.ReadKey(root); }
            catch (System.InvalidOperationException)
            {
                Debug.LogWarning("[龙眠镇] 本工作区未配置密钥，本局保持离线。未发起请求。");
                return offline;
            }
            var profile = research ? OpenAiRunProfile.SixMechanismResearch : OpenAiRunProfile.Dialogue;
            var online = new OpenAiDragonsleepProvider(key, new FileRequestBudget(root, profile), profile: profile);
            if (research)
            {
                Debug.Log("[龙眠镇] 本局六机制真实模型已启用；台词、行动、居民计划、导演与织造都记录实际来源。");
                return online;
            }
            Debug.Log("[龙眠镇] 本局已授权最多 3 次在线居民对话；其余任务离线。不会自动重试或预算降级。");
            return new RoutingProvider(online, offline, new[] { AiTaskKind.Dialogue }) { FallbackOnBudget = false };
        }
    }
}
