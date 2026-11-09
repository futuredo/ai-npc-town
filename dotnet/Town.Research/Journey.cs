using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Town.Core.Dragonsleep;
using Town.OpenAI;

internal static class Journey
{
    public static async Task<int> Run(string root, bool live, int seed=18, int nights=1)
    {
        string dir=Path.Combine(root,".local","dragonsleep","experiments",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+(live?"-journey-live":"-journey-offline"));
        Directory.CreateDirectory(dir);
        var frames=new List<(string Id,DsReplayFrame Frame)>();
        var before=new Dictionary<string,DsWorldSnapshot>();
        var traces=new List<AiTrace>(); string step="";
        using var online=live?new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),new FileRequestBudget(root,OpenAiRunProfile.SixMechanismResearch),profile:OpenAiRunProfile.SixMechanismResearch):null;
        var s=new DragonsleepSession(online??(IAiProvider)new RuleProvider(),seed);
        s.StageChanged+=(t,stage)=>{if(stage==AiStage.Context&&!before.ContainsKey(t.RequestId))before[t.RequestId]=s.CaptureState();};
        s.TraceCompleted+=t=>
        {
            if(t.CraftDecision=="pending")return;
            string id=(traces.Count+1).ToString("00")+"_"+(step=="night"?t.Kind+"_"+t.ActorId:step);
            var f=DsReplayFrame.Capture(before[t.RequestId],s.CaptureState(),t);frames.Add((id,f));traces.Add(t);
            File.WriteAllText(Path.Combine(dir,id+".replay.json"),MiniJson.Write(f.ToRecord(),true));
            File.WriteAllText(Path.Combine(dir,id+".trace.json"),MiniJson.Write(t.ToRecord(),true));
            Console.WriteLine(id+": "+t.Verdict.Kind+" "+t.FailureCode+" | "+t.Presented);
        };
        try
        {
            Console.WriteLine($"连续因果实验；{(live?OpenAiPolicy.Model:"离线基线")}；最多{6+6*nights+(nights==3?1:0)}次请求，复用同一世界，种子{seed}，{nights}夜；移动与跳过白天由测试脚本执行。");
            step="witness";await s.SpeakAsync("marta","那个裹着毛毯匆匆跑掉的小孩，有没有让你觉得哪里反常？");StopOnError(traces);
            step="buy";await s.ActAsync("我数出三枚银币交到厨娘掌心，换她那串熏鱼。");StopOnError(traces);
            s.World.PlayerPlace="workshop";
            step="craft";await s.CraftAsync("用这一串熏鱼和工坊的余烬灰，织成可以让雏龙靠近、也可以喂给它的小点心。只消耗这两种材料。");
            if(s.HasPendingCraft)s.ConfirmCraft(true);StopOnError(traces);
            var item=s.World.Items.Values.FirstOrDefault(i=>i.Crafted&&i.Holder=="player");
            if(item==null) { Console.WriteLine("未获得生成物件，停止依赖它的后续步骤。");return 1; }
            s.World.PlayerPlace="docks";
            step="use_generated";await s.ActAsync("我使用「"+item.Name+"」，让它熟悉的气味把门后的雏龙吸引过来。");StopOnError(traces);
            step="feed_generated";await s.ActAsync("我蹲低身子，轻轻把「"+item.Name+"」喂给雏龙，让它尝尝这份小点心。");StopOnError(traces);
            step="hide";await s.ActAsync("等小家伙安静下来，我用旅行斗篷把它盖好，藏在船屋角落。");StopOnError(traces);
            step="night";
            for(int n=0;n<nights&&!s.World.FinalDawn;n++)
            {
                int day=s.World.Day;
                while(s.World.Day==day&&!s.World.FinalDawn) { await s.EndPhaseAsync();StopOnError(traces); }
            }
            return 0;
        }
        catch(InvalidOperationException){Console.WriteLine("链路停止，已保存实际响应与世界状态。");return 1;}
        finally
        {
            var replay=new List<object>();foreach(var f in frames)replay.Add(new {id=f.Id,passed=await f.Frame.VerifyAsync()});
            var provider=traces.Where(t=>t.Provider!=null).Select(t=>t.Provider).ToList();
            decimal? total=provider.Any(p=>!p.EstimatedUsd.HasValue)?(decimal?)null:provider.Sum(p=>p.EstimatedUsd??0);
            File.WriteAllText(Path.Combine(dir,"summary.json"),JsonConvert.SerializeObject(new {utc=DateTime.UtcNow,live,seed,nights,rulesVersion=DsExecutor.RulesVersion,
                requests=provider.Count,estimatedUsd=total,inputTokens=provider.Sum(p=>p.InputTokens??0),outputTokens=provider.Sum(p=>p.OutputTokens??0),
                whelp=new {s.World.Whelp.Hidden,s.World.Whelp.Hunger,s.World.Whelp.TrustPlayer,s.World.Whelp.CapturedBy},
                protectedByPlayer=s.World.Flags.ContainsKey("whelp_hidden_by_player"),
                createdItems=s.World.Items.Values.Where(i=>i.Crafted).Select(i=>new {i.Id,i.Name,i.Holder,tags=i.Tags.ToArray()}),
                newBeliefs=s.World.Beliefs.Where(b=>b.Source.StartsWith("told:")).Select(b=>new {b.Id,b.Owner,b.Text,b.Source}),
                relations=s.World.Relations,replay,metrics=s.Metrics(),
                finalDay=s.World.Day,ending=s.World.FinalDawn?DsEndings.Evaluate(s.World).Title:null,
                notes="只有本链路的实际模型调用，未把预期结果写入世界；移动由实验脚本安排。各步可独立回放，非完整 Unity 寻路验收。"},Formatting.Indented));
            Console.WriteLine("报告："+dir);
        }
    }
    static void StopOnError(List<AiTrace> traces)
    {if(traces.LastOrDefault()?.Provider?.Error!=null)throw new InvalidOperationException("provider_failed");}
}
