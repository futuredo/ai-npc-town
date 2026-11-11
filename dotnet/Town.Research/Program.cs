using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Town.Core.Dragonsleep;
using Town.OpenAI;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        string root = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(root,"dotnet","Town.sln"))) { Console.Error.WriteLine("请在仓库根目录运行。"); return 2; }
        if (args.Length==2 && (args[0]=="--replay" || args[0]=="--replay-execution")) return await Replay(args[1],args[0]=="--replay-execution");
        if (args.Contains("--action-kernel"))
        {
            if (args.Any(x => x != "--action-kernel" && x != "--live")) return 2;
            return ActionKernelResearch.Run(root, args.Contains("--live"));
        }
        if (args.Contains("--plan-intent"))
        {
            var count = args.FirstOrDefault(x => x.StartsWith("--seeds="));
            int seeds = 1;
            if ((count != null && !int.TryParse(count.Substring(8), out seeds)) || seeds < 1 || seeds > 3 ||
                args.Any(x => x != "--plan-intent" && x != "--live" && !x.StartsWith("--seeds="))) return 2;
            return await PlanIntentResearch.Run(root, args.Contains("--live"), seeds);
        }
        int seed = 7;
        var seedArg=args.FirstOrDefault(x=>x.StartsWith("--seed="));
        if(seedArg!=null&&!int.TryParse(seedArg.Substring(7),out seed)) return 2;
        if (args.Contains("--journey")&&args.All(x=>x=="--journey"||x=="--live"||x=="--three-nights"||x.StartsWith("--seed=")))
            return await Journey.Run(root,args.Contains("--live"),seedArg==null?18:seed,args.Contains("--three-nights")?3:1);
        if (args.Contains("--plan"))
        {
            var seedsArg = args.FirstOrDefault(x => x.StartsWith("--seeds="));
            int seeds = 10;
            if ((seedsArg != null && !int.TryParse(seedsArg.Substring(8), out seeds)) || seeds < 1 ||
                args.Any(x => x != "--plan" && x != "--live" && x != "--holdout" && x != "--transfer" && !x.StartsWith("--seeds=")) ||
                (args.Contains("--holdout") && args.Contains("--transfer"))) { Console.Error.WriteLine("用法：--plan [--holdout|--transfer] [--seeds=N] [--live]"); return 2; }
            return await PlanResearch.Run(root, args.Contains("--live"), seeds, args.Contains("--holdout"), args.Contains("--transfer"));
        }
        var recovery = args.FirstOrDefault(x => x.StartsWith("--plan-recovery="));
        if (recovery != null)
        {
            if (args.Any(x => x != recovery && x != "--live")) return 2;
            return await PlanRecovery.Run(root, recovery.Substring(16), args.Contains("--live"));
        }
        bool live = args.Contains("--live");
        string filter = args.FirstOrDefault(x=>x.StartsWith("--cases="))?.Substring(8);
        if (args.Any(x=>x!="--live"&&x!="--holdout"&&!x.StartsWith("--cases=")&&!x.StartsWith("--seed="))) { Console.Error.WriteLine("用法：无参数离线；--live 显式真实实验；--cases=id,id；--holdout 新措辞集；--seed=N；--journey --three-nights；--replay 目录"); return 2; }
        var cases = args.Contains("--holdout")?ExperimentCases.Holdout():ExperimentCases.All();
        if (filter!=null) { var ids=filter.Split(','); cases=cases.Where(c=>ids.Contains(c.Id)).ToList(); if(cases.Count!=ids.Length) return 2; }
        var profile = OpenAiRunProfile.SixMechanismResearch;
        Console.WriteLine($"{(live?"真实六机制实验":"离线基线")}；模型 {OpenAiPolicy.Model}；本批 {cases.Count}，种子 {seed}；输出≤768 tokens；单并发，无自动重试。记账预留估算 USD {cases.Count*OpenAiPolicy.ReservationUsdPerRequest:0.000}。");
        var dir=Path.Combine(root,".local","dragonsleep","experiments",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+(live?"-live":"-offline"));
        Directory.CreateDirectory(dir);
        var rows=new List<Dictionary<string,object>>();
        OpenAiDragonsleepProvider online=null;
        using var cancellation=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        try
        {
            // 唯一读密钥与联网的分支；默认运行与所有单元测试不会进入。
            if(live) online=new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),new FileRequestBudget(root,profile),profile:profile);
            foreach(var c in cases)
            {
                var baseline=new DragonsleepSession(new RuleProvider(),seed); c.Setup(baseline);
                var before=baseline.CaptureState();
                var baselineTrace=await baseline.RunAsync(c.Kind,c.Actor,c.Input,cancellation.Token);
                if(baseline.HasPendingCraft) baselineTrace=baseline.ConfirmCraft(!c.Id.Contains("no_material"));
                var s=new DragonsleepSession(online ?? (IAiProvider)new RuleProvider()); s.RestoreState(before);
                var trace=await s.RunAsync(c.Kind,c.Actor,c.Input,cancellation.Token);
                if(s.HasPendingCraft) trace=s.ConfirmCraft(!c.Id.Contains("no_material"));
                bool passed=trace.FailureCode==null && c.Check(trace,s.World);
                var frame=DsReplayFrame.Capture(before,s.CaptureState(),trace);
                bool replay=await frame.VerifyAsync(cancellation.Token);
                var row=new Dictionary<string,object>
                {
                    ["id"]=c.Id,["mechanism"]=c.Mechanism,["criterion"]=c.Criterion,["passed"]=passed,
                    ["baseline_passed"]=baselineTrace.FailureCode==null&&c.Check(baselineTrace,baseline.World),
                    ["replay_passed"]=replay,["ms"]=trace.TotalMillis,["input_tokens"]=trace.Provider?.InputTokens,
                    ["output_tokens"]=trace.Provider?.OutputTokens,["estimated_usd"]=trace.Provider?.EstimatedUsd,
                    ["failure"]=trace.FailureCode,["verdict"]=trace.Verdict.Kind.ToString(),["presented"]=trace.Presented
                };
                rows.Add(row);
                File.WriteAllText(Path.Combine(dir,c.Id+".replay.json"),MiniJson.Write(frame.ToRecord(),true));
                File.WriteAllText(Path.Combine(dir,c.Id+".trace.json"),MiniJson.Write(trace.ToRecord(),true));
                File.WriteAllText(Path.Combine(dir,c.Id+".baseline.json"),MiniJson.Write(baselineTrace.ToRecord(),true));
                WriteSummary(dir,rows,live);
                Console.WriteLine($"{c.Id}: 机制={(passed?"通过":"待适配")} 基线={row["baseline_passed"]} 回放={replay} {trace.TotalMillis:0}ms 费用估算={(trace.Provider?.EstimatedUsd.HasValue==true?trace.Provider.EstimatedUsd.Value.ToString("0.000000"):"未知")} {trace.FailureCode}");
                // 服务/预算故障立即结束批次；结构与玩法失败保留样本，继续其他独立案例。
                if(trace.Provider?.Error!=null || !replay) { Console.WriteLine("批次停止，已保存全部已完成样本。"); return 1; }
            }
            return 0;
        }
        catch(Exception e) when(e is IOException||e is UnauthorizedAccessException||e is InvalidOperationException||e is OperationCanceledException)
        { Console.Error.WriteLine("实验已停止；已完成样本保存在报告目录，未输出可能含凭据的异常详情。"); return 2; }
        finally { online?.Dispose(); Console.WriteLine("报告："+dir); }
    }

    static void WriteSummary(string dir,List<Dictionary<string,object>> rows,bool live)
    {
        bool unknown=rows.Any(r=>r["estimated_usd"]==null);
        decimal known=rows.Where(r=>r["estimated_usd"]!=null).Sum(r=>(decimal)r["estimated_usd"]);
        File.WriteAllText(Path.Combine(dir,"summary.json"),JsonConvert.SerializeObject(new
        { utc=DateTime.UtcNow,live,model=OpenAiPolicy.Model,rulesVersion=DsExecutor.RulesVersion,
          requested=rows.Count,passed=rows.Count(r=>(bool)r["passed"]),baselinePassed=rows.Count(r=>(bool)r["baseline_passed"]),
          replayPassed=rows.Count(r=>(bool)r["replay_passed"]),estimatedUsd=unknown?(decimal?)null:known,knownEstimatedUsd=known,
          notes="按 usage 和 2026-11-08 官方价格估算；未知费用不记零。机制检查为预先定义的有限断言，需人工审阅原文，不等于玩法或安全性普遍证明。",results=rows },Formatting.Indented));
    }

    static async Task<int> Replay(string directory, bool executionOnly)
    {
        int total=0,passed=0;
        foreach(var file in Directory.GetFiles(directory,"*.replay.json").OrderBy(x=>x,StringComparer.Ordinal))
        { total++; var frame=DsReplayFrame.Parse(File.ReadAllText(file)); bool ok=executionOnly?await frame.VerifyExecutionAsync():await frame.VerifyAsync(); if(ok)passed++;Console.WriteLine(Path.GetFileName(file)+": "+ok); }
        Console.WriteLine($"离线{(executionOnly?"执行":"严格")}回放 {passed}/{total}，未读取密钥、未联网。{(executionOnly?"执行回放不验证当前提示词与历史提示相同。":"")}");
        return total>0&&passed==total?0:1;
    }
}
