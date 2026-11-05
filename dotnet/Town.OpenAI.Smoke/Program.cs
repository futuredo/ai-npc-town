using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.World;
using Town.OpenAI;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a != "--live")) { Console.Error.WriteLine("用法：不带参数预览；--live 发起最多3次真实请求。"); return 2; }
        string root = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(root, "dotnet", "Town.sln")))
        { Console.Error.WriteLine("请在仓库根目录运行。"); return 2; }
        var session = HammerEventSetup.Create();
        var cases = new[]
        {
            (Npc: Ids.Npc.Ahe, Question: "你知道锤子现在在哪里吗？请分清你看到的和你猜的。"),
            (Npc: Ids.Npc.Xiaochuan, Question: "锤子在哪里？你是怎么知道的？"),
            (Npc: Ids.Npc.Amu, Question: $"{Ids.Name(Ids.Npc.Xiaochuan)}是偷走你的锤子的吗？")
        };
        Console.WriteLine($"模型 {OpenAiPolicy.Model}；最多 {cases.Length} 次；预留估算 ${cases.Length * OpenAiPolicy.ReservationUsdPerRequest:0.000}；不自动重试。");
        if (!args.Contains("--live"))
        { Console.WriteLine("当前为离线预览，未读取密钥、未调用 API。确认试跑时添加 --live。"); return 0; }
        try
        {
            using var client = new OpenAiDialogueClient(LocalValidationAccess.ReadKey(root), new FileRequestBudget(root));
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var results = new List<object>();
            bool success = true;
            decimal total = 0;
            foreach (var c in cases)
            {
                var request = NpcDialogueRequest.From(session.Knowledge.ViewFor(c.Npc), c.Question);
                var result = await client.ReplyAsync(request, cancellation.Token);
                var cost = OpenAiPolicy.EstimateUsd(result.Usage);
                total += cost ?? 0;
                results.Add(new { npc = c.Npc, question = c.Question, result, estimatedUsd = cost });
                Console.WriteLine($"{c.Npc}: {(result.Success ? "通过" : result.ErrorCode)} | {result.Reply} | 估算USD={(cost.HasValue ? cost.Value.ToString("0.000000") : "未知")} | {result.ElapsedMilliseconds}ms");
                if (!result.Success) { success = false; break; }
            }
            var report = new { utc = DateTime.UtcNow, model = OpenAiPolicy.Model, estimatedUsd = total,
                notes = "费用为按 usage 和固定价格计算的估算；失败且缺少 usage 时费用未知。不是平台账单或额度余额。", results };
            string directory = Path.Combine(root, ".local", "openai");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented));
            Console.WriteLine($"报告：{path}；已知用量估算合计 ${total:0.000000}");
            return success ? 0 : 1;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is HttpRequestException)
        { Console.Error.WriteLine("本地配置或验证服务不可用，已停止；请检查密钥设置和 .local 目录权限。未输出异常详情以保护凭据。"); return 2; }
    }
}
