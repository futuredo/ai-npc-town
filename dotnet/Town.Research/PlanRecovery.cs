using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;

/// <summary>固定失败状态探针。完整快照只在本地恢复；模型仍只看到 PlanAsync 重建的玩家感知。</summary>
internal static class PlanRecovery
{
    public static async Task<int> Run(string root, string file, bool live)
    {
        var fixturesText = File.ReadAllText(file);
        var fixtures = JArray.Parse(fixturesText);
        if (fixtures.Count == 0 || fixtures.Count > 100) throw new InvalidOperationException("invalid_fixture_count");
        string dir = Path.Combine(root, ".local", "dragonsleep", "experiments", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + (live ? "-recovery-live" : "-recovery-offline"));
        Directory.CreateDirectory(dir);
        File.Copy(file, Path.Combine(dir, "fixtures.json"));
        var rows = new List<JObject>();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        OpenAiDragonsleepProvider online = null;
        string status = "running";
        string promptHash = DsWorldSnapshot.Digest(File.ReadAllText(Path.Combine(root, "unity/Assets/Town.Core/Dragonsleep/ContextBuilder.cs")));
        void Save() => File.WriteAllText(Path.Combine(dir, "summary.json"), JsonConvert.SerializeObject(new
        {
            status, live, rulesVersion = DsExecutor.RulesVersion,
            fixtureFingerprint = DsWorldSnapshot.Digest(fixturesText),
            promptSourceFingerprint = promptHash,
            total = rows.Count,
            byFailure = rows.GroupBy(r => (string)r["failure"] + ((bool)r["deadEnd"] ? "DeadEnd" : "Recoverable")).ToDictionary(g => g.Key,
                g => new { total = g.Count(), executable = g.Count(r => (bool)r["executable"]), reachesGoal = g.Count(r => (bool)r["reachesGoal"]), repeatsFailedStep = g.Count(r => (bool)r["repeatsFailedStep"]) }),
            estimatedUsd = rows.Any(r => r["trace"]["estimated_usd"].Type == JTokenType.Null) ? (decimal?)null : rows.Sum(r => (decimal)r["trace"]["estimated_usd"]),
            rows,
            notes = "来自离线基线失败记录的固定状态探针；每个样本只请求一次，不确认执行，不代表模型自然走出的整局。死局标签只留本地。",
        }, Formatting.Indented));
        try
        {
            if (live) online = new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root), new FileRequestBudget(root, OpenAiRunProfile.SixMechanismResearch), profile: OpenAiRunProfile.SixMechanismResearch);
            foreach (JObject f in fixtures)
            {
                var snapshot = DsWorldSnapshot.Parse(f["before"].ToString(Formatting.None));
                var baseline = new DragonsleepSession(new RuleProvider()); baseline.RestoreState(snapshot);
                var bt = await baseline.PlanAsync((string)f["input"], (string)f["context"], cancellation.Token);
                var session = new DragonsleepSession(online ?? (IAiProvider)new RuleProvider()); session.RestoreState(snapshot);
                var t = live ? await session.PlanAsync((string)f["input"], (string)f["context"], cancellation.Token) : bt;
                var context = JObject.Parse((string)f["context"]);
                string failed = (string)context["failed_step"] ?? "";
                // 只标记字面重复供审阅；掷骰失败后的合理再尝试不能据此判错。
                string WithoutId(string brief)
                {
                    var text = brief.Contains(' ') ? brief.Substring(brief.IndexOf(' ') + 1) : brief;
                    int condition = text.IndexOf(" [", StringComparison.Ordinal);
                    return condition < 0 ? text : text.Substring(0, condition);
                }
                bool repeats = t.PlanReport?.Plan?.ActiveSteps(t.PlanReport.Plan.DefaultSelection()).Any(s => WithoutId(s.Brief()) == WithoutId(failed)) == true;
                rows.Add(new JObject
                {
                    ["id"] = f["id"], ["failure"] = f["failure"], ["deadEnd"] = f["deadEnd"],
                    ["executable"] = t.FailureCode == null && t.PlanReport?.Executable == true,
                    ["reachesGoal"] = t.PlanReport?.Default?.ReachesGoal == true,
                    ["baselineExecutable"] = bt.FailureCode == null && bt.PlanReport?.Executable == true,
                    ["repeatsFailedStep"] = repeats, ["failedStep"] = failed,
                    ["worldUnchanged"] = snapshot.Hash == session.CaptureState().Hash,
                    ["trace"] = JObject.Parse(MiniJson.Write(t.ToRecord())),
                });
                File.WriteAllText(Path.Combine(dir, rows.Count.ToString("000") + ".json"), rows.Last().ToString());
                Save();
                Console.WriteLine($"{f["id"]}: 可执行={rows.Last()["executable"]} 费用估算={t.Provider?.EstimatedUsd?.ToString() ?? "未知"} {t.FailureCode}");
                if (t.Provider?.Error != null && t.Provider.Error != "incomplete_response" && t.Provider.Error != "refusal") { status = "provider_failed"; return 1; }
            }
            status = "completed";
            return 0;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is OperationCanceledException)
        { status = "stopped_" + e.GetType().Name; Console.Error.WriteLine("探针已停止，已完成样本保留，未输出异常详情。"); return 2; }
        finally { online?.Dispose(); Save(); Console.WriteLine("报告：" + dir); }
    }
}
