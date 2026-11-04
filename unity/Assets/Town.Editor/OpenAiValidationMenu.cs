using System;
using System.IO;
using System.Threading;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.World;
using Town.OpenAI;
using UnityEditor;
using UnityEngine;

namespace Town.Editor
{
    public static class OpenAiValidationMenu
    {
        private static bool _running;

        [MenuItem("Town/AI/测试阿禾认知（1次付费请求）")]
        public static async void ValidateAhe()
        {
            if (_running) return;
            _running = true;
            using var cancellation = new CancellationTokenSource();
            Action cancel = () => cancellation.Cancel();
            AssemblyReloadEvents.beforeAssemblyReload += cancel;
            EditorApplication.quitting += cancel;
            try
            {
                // Assets -> unity -> 仓库根。仅 Editor 读取本机文件，不把 Key 序列化到 Unity 资源。
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
                var session = HammerEventSetup.Create();
                var request = NpcDialogueRequest.From(session.Knowledge.ViewFor(Ids.Npc.Ahe), "锤子现在在哪里？");
                using var client = new OpenAiDialogueClient(LocalValidationAccess.ReadKey(root), new FileRequestBudget(root));
                var result = await client.ReplyAsync(request, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                Debug.Log($"[Town AI] {OpenAiPolicy.Model} | {(result.Success ? "通过" : result.ErrorCode)} | {result.Reply} | 估算USD={OpenAiPolicy.EstimateUsd(result.Usage)?.ToString("0.000000") ?? "未知"}");
            }
            catch (Exception)
            {
                Debug.LogWarning("[Town AI] 本地配置或请求失败，已停止；请检查 .env.local 和预算记录。不会输出密钥或原始错误体。");
            }
            finally
            {
                AssemblyReloadEvents.beforeAssemblyReload -= cancel;
                EditorApplication.quitting -= cancel;
                _running = false;
            }
        }

        [MenuItem("Town/AI/测试阿禾认知（1次付费请求）", true)]
        private static bool CanValidate() => !_running && !EditorApplication.isCompiling;
    }
}
