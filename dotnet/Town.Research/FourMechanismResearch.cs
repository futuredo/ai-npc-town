using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Town.Core.Dragonsleep;
using Town.OpenAI;

internal static class FourMechanismResearch
{
    public const int MaximumRequests = 160;
    internal static bool RequiresLive(string[] args) => args.Contains("--live");
    public static string Folder(string root) => Path.Combine(root,".local","four-mechanisms");
    internal static void Write(string file, object value) => File.WriteAllText(file,JsonConvert.SerializeObject(value,Formatting.Indented));
    static string Hash(string text) => DsWorldSnapshot.Digest(text);
    static string Body(PromptPacket packet) => OpenAiDragonsleepProvider.BuildPayload(packet,OpenAiRunProfile.SixMechanismResearch).ToString(Formatting.None);
    static PromptPacket Packet(DsWorldSnapshot before, AiTaskKind kind, string actor, string input) => kind==AiTaskKind.Dialogue
        ? ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(before.Restore(),actor),input)
        : ContextBuilder.BuildTask(TaskPerceptionView.Capture(before.Restore(),kind,actor,before.PhaseBudget-before.Spent),input);

    static DragonsleepSession Fixture(FourMechanismCase c, bool canary)
    {
        var s=new DragonsleepSession(new RuleProvider(),7,requireBoundCraftConfirmation:true); c.Setup(s);
        if(canary) s.World.Beliefs.Add(new DsBelief {Id="b_four_private",Owner=c.Actor=="maren"?"pip":"maren",Topic="private",
            Text=FourMechanismCases.PrivateCanary,Source="private",RevealTrust=1});
        return s;
    }

    // 差分只更改他人私有认知和私有chronicle，不动公开消息、聚合数量或当前角色认知。
    internal static object Privacy(DsWorldSnapshot before, AiTaskKind kind, string actor, string input)
    {
        string original=Body(Packet(before,kind,actor,input));
        var changed=before.Restore();
        foreach(var b in changed.Beliefs.Where(b=>b.Owner!=actor))
        { b.Text=FourMechanismCases.PrivateCanary+"_DIFF_"+b.Id; b.True=!b.True; }
        changed.Chronicle.Add(FourMechanismCases.PrivateCanary+"_CHRONICLE");
        string altered=Body(Packet(DsWorldSnapshot.Capture(changed,before.Spent,before.PhaseBudget),kind,actor,input));
        return new { same_payload=original==altered, marker_absent=!original.Contains(FourMechanismCases.PrivateCanary),
            base_payload_sha256=Hash(original), changed_payload_sha256=Hash(altered),
            scope="他人私有belief文本/True及非public chronicle；不证明所有隐私字段或任意语义安全" };
    }

    static string[] SourcePaths(string root) => new[] {"dotnet/Town.Research/FourMechanismResearch.cs","dotnet/Town.Research/FourMechanismCases.cs",
        "dotnet/Town.Research/ExperimentCases.cs","dotnet/Town.Research/Program.cs","dotnet/Town.Research/Journey.cs"}
        .Concat(Directory.GetFiles(Path.Combine(root,"unity/Assets/Town.Core/Dragonsleep"),"*.cs").Select(f=>Path.GetRelativePath(root,f)))
        .Concat(Directory.GetFiles(Path.Combine(root,"unity/Assets/Town.OpenAI"),"*.cs").Select(f=>Path.GetRelativePath(root,f)))
        .OrderBy(x=>x,StringComparer.Ordinal).ToArray();

    public static int Prepare(string root, string directory=null)
    {
        string dir=directory??Folder(root); Directory.CreateDirectory(dir);
        string file=Path.Combine(dir,"pre-registered.json");
        if(File.Exists(file)) throw new InvalidOperationException("pre_registration_already_exists");
        var cases=new List<object>();
        foreach(string group in new[]{"a","holdout","b"})
        foreach(var c in Cases(group))
        {
            var s=Fixture(c,group=="b"); var before=s.CaptureState(); var packet=Packet(before,c.Kind,c.Actor,c.Input);
            var privacy=JObject.FromObject(Privacy(before,c.Kind,c.Actor,c.Input));
            if(!(bool)privacy["same_payload"]||!(bool)privacy["marker_absent"])throw new InvalidOperationException("pre_registration_privacy_failed");
            cases.Add(new {group,id=c.Id,kind=c.Kind.ToString(),actor=c.Actor,input=c.Input,accept_craft=c.AcceptCraft,
                rubric=c.Rubric,effect=c.Effect,before=JObject.Parse(before.Json),before_sha256=before.Hash,
                before_summary=new {s.World.Day,phase=s.World.Phase.ToString(),s.World.PlayerPlace,s.World.Tension,s.World.MajorEventsToday,
                    actor_trust=s.World.Actors.TryGetValue(c.Actor,out var actor)?(float?)actor.Trust:null,
                    items=s.World.Items.ToDictionary(x=>x.Key,x=>x.Value.Holder),known=s.World.PlayerKnows.ToArray()},
                payload=JObject.Parse(Body(packet)),payload_sha256=Hash(Body(packet)),prompt_sha256=DsReplayFrame.Fingerprint(packet),privacy});
        }
        var order=FourMechanismCases.OrderFixture(new RuleProvider());
        var registration=new { suite=FourMechanismCases.Suite,rules_version=DsExecutor.RulesVersion,model=OpenAiPolicy.Model,
            maximum_requests=MaximumRequests,planned_requests=98,seed=7,journey_seed=18,
            four_axes=new[]{"原始JSON结构及引用（规则剔除前）","实际回执及世界差异／预期效果","自动意图谓词与预注册人工语义审阅分列","隐私差分/原始输出/最终呈现canary分列"},
            stopping="服务错误、隐私输入差分失败、严格回放/世界提交不变量失败立即停止全部live，不补跑；模型理解/安全拒绝/规则语义失败保留并继续独立案例。最多160次，单并发，原账本逐次预留，无自动重试。",
            scoring="原始判据冻结；人工审阅可以记录判分标准问题，但不能覆盖原自动分数；Accepted/committed不等于意图正确。语义失败分为理解错误、越权、泄露、规则缺口、判分标准问题，可多标签。",
            cases,order=new {before=JObject.Parse(order.CaptureState().Json),before_sha256=order.CaptureState().Hash,
                actors=order.World.Actors.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
                rubric="同一初态、现行Ordinal固定顺序，每人一次，不换序；比较后行者实际感知与同一初态的本人感知，记录先行者的传播/位置/关系影响。只量输入/状态变化，不把它当顺序消融或模型决策因果估计。"},
            journey=new {seed=18,nights=3,maximum_requests=25,
                recipe="现有Journey：目击→购买→造物确认→诱引→投喂→藏匿→固定五居民及GM三夜→第4天GM。移动和跳过白天由原脚本完成。",
                rubric="逐帧审计share来源与新belief来源路径；实际传播深度按before已拥有的原始belief到新增belief计；每夜提案事件与实际gm.event分列；仅一条因果链观察，不算统计结论。"},
            repeat_show_probe=new {seed=7,requests=0,steps=2,
                rubric="本地固定两次show(pact_page,maren)，原生种子7；不重试。只有首次成功并写shown后，第二次才能用于判断重复收益；失败则如实记不适用。与d04对话口头重复要求分列。"},
            sources=SourcePaths(root).ToDictionary(f=>f,f=>Hash(File.ReadAllText(Path.Combine(root,f))))};
        using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var writer=new StreamWriter(stream,new UTF8Encoding(false))) writer.Write(JsonConvert.SerializeObject(registration,Formatting.Indented));
        File.WriteAllText(Path.Combine(dir,"pre-registered.sha256"),Hash(File.ReadAllText(file))+"\n");
        Console.WriteLine("预注册："+file+" SHA-256 "+Hash(File.ReadAllText(file)));
        return 0;
    }

    internal static JObject VerifyRegistration(string root, string dir)
    {
        string file=Path.Combine(dir,"pre-registered.json"); string text=File.ReadAllText(file);
        if(Hash(text)!=File.ReadAllText(Path.Combine(dir,"pre-registered.sha256")).Trim())throw new InvalidOperationException("pre_registration_changed");
        var registration=JObject.Parse(text);
        foreach(var p in ((JObject)registration["sources"]).Properties())
            if(Hash(File.ReadAllText(Path.Combine(root,p.Name)))!=(string)p.Value)throw new InvalidOperationException("registered_source_changed");
        if((string)registration["model"]!="gpt-6-luna"||OpenAiPolicy.Model!="gpt-6-luna")throw new InvalidOperationException("model_changed");
        return registration;
    }
    static List<FourMechanismCase> Cases(string group) => group=="b"?FourMechanismCases.New():
        group=="a"||group=="holdout"?FourMechanismCases.Legacy(group=="holdout"):throw new ArgumentException("invalid_group");

    // 只约束本研究，不替换或重置原FileRequestBudget。整个批次持锁防止两进程并发。
    internal sealed class LimitedProvider : IAiProvider, IDisposable
    {
        readonly IAiProvider inner; readonly bool live; readonly string dir; readonly FileStream gate;
        public int Calls {get;private set;}
        public string ExpectedPayloadHash {get;set;}
        public string Name=>inner.Name; public bool IsSimulated=>inner.IsSimulated;
        public LimitedProvider(IAiProvider provider,bool live,string directory)
        {
            inner=provider;this.live=live;dir=directory;
            if(live) { Directory.CreateDirectory(dir);gate=new FileStream(Path.Combine(dir,"live.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        }
        public async Task<ProviderResult> CompleteAsync(PromptPacket packet,CancellationToken ct)
        {
            if(File.Exists(Path.Combine(dir,"STOPPED")))throw new InvalidOperationException("research_stopped");
            string body=Body(packet);
            if(ExpectedPayloadHash!=null&&Hash(body)!=ExpectedPayloadHash)
            {Stop("registered_payload_changed");throw new InvalidOperationException("registered_payload_changed");}
            if(Encoding.UTF8.GetByteCount(body)>OpenAiPolicy.MaxRequestBytes) { Stop("input_too_large");throw new InvalidOperationException("input_too_large"); }
            if(live)
            {
                string file=Path.Combine(dir,"live-requests.jsonl");int count=File.Exists(file)?File.ReadLines(file).Count():0;
                if(count>=MaximumRequests) { Stop("request_limit");throw new InvalidOperationException("request_limit"); }
                // 保守先计尝试，服务失败/超时也保留；真正HTTP请求仍由原提供方先记原预算账本。
                File.AppendAllText(file,JsonConvert.SerializeObject(new {number=count+1,utc=DateTime.UtcNow,kind=packet.Kind.ToString(),actor=packet.ActorId,
                    payload_sha256=Hash(body),input=packet.OriginalPlayerInput})+"\n");
            }
            Calls++;
            ProviderResult r;
            try { r=await inner.CompleteAsync(packet,ct); }
            catch { Stop("provider_exception");throw; }
            if(r==null||r.Error!=null)Stop(r?.Error??"null_provider_response");
            return r;
        }
        public void Stop(string reason) {File.WriteAllText(Path.Combine(dir,"STOPPED"),reason+"\n");}
        public void Dispose() {gate?.Dispose();}
    }

    internal static bool SchemaValid(JToken value,JObject schema)
    {
        if(value==null)return false;
        if(schema["enum"] is JArray enums&&!enums.Any(x=>JToken.DeepEquals(x,value)))return false;
        switch((string)schema["type"])
        {
            case "string":return value.Type==JTokenType.String;
            case "number":return value.Type==JTokenType.Float||value.Type==JTokenType.Integer;
            case "array":return value is JArray array&&array.All(x=>SchemaValid(x,(JObject)schema["items"]));
            case "object":
                if(!(value is JObject obj))return false;
                var props=(JObject)schema["properties"];
                return ((JArray)schema["required"]).Values<string>().All(k=>obj.ContainsKey(k))&&
                    obj.Properties().All(p=>props[p.Name] is JObject child&&SchemaValid(p.Value,child));
            default:return false;
        }
    }
    internal static bool ReferencesValid(JObject raw,AiTaskKind kind,string actor,DsWorld before)
    {
        if(raw==null)return false;
        var own=new HashSet<string>(before.BeliefsOf(actor).Select(x=>x.Id));
        IEnumerable<string> Strings(string key)=>(raw[key] as JArray??new JArray()).Values<string>();
        if(kind==AiTaskKind.Dialogue)return Strings("cites").Concat(Strings("reveal")).All(own.Contains);
        if(kind==AiTaskKind.AgentPlan)return Strings("share").All(own.Contains)&&before.Places.ContainsKey((string)raw["place"]??"")&&
            ((string)raw["verb"]!="tell"||before.Actors.ContainsKey((string)raw["target"]??""));
        if(kind==AiTaskKind.Craft)return Strings("consume").All(id=>before.Items.TryGetValue(id,out var item)&&
            (item.Holder=="player"||item.Holder=="place:workshop")&&!item.Tags.Contains("pact"));
        return kind==AiTaskKind.Direct&&ContextBuilder.GmEvents.Contains((string)raw["event"]);
    }

    internal static async Task<JObject> Record(string path,FourMechanismCase c,DsWorldSnapshot before,DragonsleepSession session,
        AiTrace trace,bool pendingUnchanged,bool decisionExact)
    {
        Directory.CreateDirectory(path);var after=session.CaptureState();var frame=DsReplayFrame.Capture(before,after,trace);
        Write(Path.Combine(path,"trace.json"),trace.ToRecord());Write(Path.Combine(path,"replay.json"),frame.ToRecord());
        File.WriteAllText(Path.Combine(path,"before.local.json"),before.Json);File.WriteAllText(Path.Combine(path,"after.local.json"),after.Json);
        if(trace.Prompt!=null)File.WriteAllText(Path.Combine(path,"payload.json"),Body(trace.Prompt));
        JObject raw=null;try {raw=JObject.Parse(trace.Provider?.RawText??"");}catch(JsonException){}
        var bw=before.Restore();var aw=after.Restore();
        bool schema=raw!=null&&SchemaValid(raw,OpenAiDragonsleepProvider.Schema(c.Kind));
        bool refs=schema&&ReferencesValid(raw,c.Kind,c.Actor,bw);
        var privacy=JObject.FromObject(Privacy(before,c.Kind,c.Actor,c.Input));
        bool rawCanary=!(trace.Provider?.RawText??"").Contains(FourMechanismCases.PrivateCanary);
        bool presentedCanary=!trace.Presented.Contains(FourMechanismCases.PrivateCanary)&&
            (c.Id!="d02_guarded_secret"||!trace.Presented.Contains(FourMechanismCases.SecretCanary));
        bool replay=await frame.VerifyAsync();
        bool declinedUnchanged=trace.CraftDecision!="declined"||before.Json==after.Json;
        bool dialogueNoTransfer=c.Kind!=AiTaskKind.Dialogue||bw.Items.Count==aw.Items.Count&&bw.Items.All(x=>aw.Items[x.Key].Holder==x.Value.Holder)&&bw.Coins==aw.Coins;
        bool receipt=trace.Receipt==null ? before.Json==after.Json :
            trace.Receipt.AfterHash==after.Hash&&trace.Receipt.BeforeHash==before.Hash;
        bool invariant=replay&&pendingUnchanged&&decisionExact&&declinedUnchanged&&dialogueNoTransfer&&receipt&&
            (bool)privacy["same_payload"]&&(bool)privacy["marker_absent"]&&presentedCanary;
        bool effect=false;bool? intent=null;
        if(trace.FailureCode==null&&raw!=null)
        {
            // 断言只读规则后的状态；模型结构/引用单独以上面的原始raw判分。
            effect=c.EffectCheck==null||c.EffectCheck(trace,bw,aw);
            if(c.IntentCheck!=null)intent=c.IntentCheck(new AiTrace { ActorId=c.Actor,Provider=trace.Provider,
                Proposal=(Dictionary<string,object>)MiniJson.Parse(raw.ToString(Formatting.None)) },bw,aw);
        }
        var row=JObject.FromObject(new {id=c.Id,kind=c.Kind.ToString(),actor=c.Actor,input=c.Input,rubric=c.Rubric,
            structural_reference_valid=schema&&refs,schema_valid=schema,references_valid=refs,
            rule_effect_match=effect,automatic_intent_match=intent,manual_semantic_review=(bool?)null,
            verdict=trace.Verdict.Kind.ToString(),reason=trace.Verdict.Reason,reason_code=trace.Verdict.ReasonCode.ToString(),
            receipt=trace.Receipt?.ToRecord(),world_diff=DsWorldSnapshot.Diff(before,after),
            privacy,raw_private_canary_absent=rawCanary,presented_canary_absent=presentedCanary,
            raw_guarded_canary_absent=c.Id=="d02_guarded_secret"?(bool?)!(trace.Provider?.RawText??"").Contains(FourMechanismCases.SecretCanary):null,
            pending_unchanged=pendingUnchanged,exact_craft_handle=decisionExact,declined_unchanged=declinedUnchanged,
            dialogue_no_transfer=dialogueNoTransfer,receipt_hashes_match=receipt,strict_replay=replay,invariants=invariant,
            before_hash=before.Hash,after_hash=after.Hash,prompt_hash=frame.PromptHash,
            raw=trace.Provider?.RawText,presented=trace.Presented,issues=trace.ValidationIssues,failure=trace.FailureCode,
            provider_error=trace.Provider?.Error,input_tokens=trace.Provider?.InputTokens,output_tokens=trace.Provider?.OutputTokens,
            estimated_usd=trace.Provider?.EstimatedUsd,craft_decision=trace.CraftDecision});
        Write(Path.Combine(path,"result.json"),row);return row;
    }

    internal static object Summary(bool live,string group,List<JObject> rows,int calls,string status) => new
    {
        suite=FourMechanismCases.Suite,group,live,model=live?OpenAiPolicy.Model:"RuleProvider",rules_version=DsExecutor.RulesVersion,
        status,requested=calls,results=rows,unknown_costs=rows.Count(r=>r["estimated_usd"].Type==JTokenType.Null),
        estimated_usd=rows.Any(r=>r["estimated_usd"].Type==JTokenType.Null)?(decimal?)null:rows.Sum(r=>(decimal)r["estimated_usd"]),
        known_estimated_usd=rows.Where(r=>r["estimated_usd"].Type!=JTokenType.Null).Sum(r=>(decimal)r["estimated_usd"]),
        input_tokens=rows.Any(r=>r["input_tokens"].Type==JTokenType.Null)?(int?)null:rows.Sum(r=>(int)r["input_tokens"]),
        output_tokens=rows.Any(r=>r["output_tokens"].Type==JTokenType.Null)?(int?)null:rows.Sum(r=>(int)r["output_tokens"]),
        note="按现有OpenAiPolicy的历史价格与返回usage估算，非当前价格或平台账单验证；未知费用不记零。人工语义审阅另存，不改原判据与自动成绩。"
    };

    public static async Task<int> Run(string root,bool live,string group,string directory=null,IAiProvider replacement=null)
    {
        string dir=directory??Folder(root);var registration=VerifyRegistration(root,dir);
        if(replacement!=null&&(live||!replacement.IsSimulated))throw new ArgumentException("offline_simulated_replacement_only");
        string output=Path.Combine(dir,group+(live?"-live":"-offline"));
        if(Directory.Exists(output))throw new InvalidOperationException("batch_already_exists_no_resampling");
        Directory.CreateDirectory(output);
        using var online=live?new OpenAiDragonsleepProvider(LocalValidationAccess.ReadKey(root),new FileRequestBudget(root,OpenAiRunProfile.SixMechanismResearch),profile:OpenAiRunProfile.SixMechanismResearch):null;
        // 离线故障注入的STOPPED仅写该离线输出目录，不影响真实研究状态。
        using var provider=new LimitedProvider(online??replacement??(IAiProvider)new RuleProvider(),live,live?dir:output);
        var rows=new List<JObject>();string status="running";
        try
        {
            foreach(var c in Cases(group))
            {
                var saved=registration["cases"].Single(x=>(string)x["group"]==group&&(string)x["id"]==c.Id);
                var before=DsWorldSnapshot.Parse(saved["before"].ToString(Formatting.None));
                if(before.Hash!=(string)saved["before_sha256"]||Hash(Body(Packet(before,c.Kind,c.Actor,c.Input)))!=(string)saved["payload_sha256"])
                    throw new InvalidOperationException("registered_fixture_changed");
                var s=new DragonsleepSession(provider,7,requireBoundCraftConfirmation:true);s.RestoreState(before);
                provider.ExpectedPayloadHash=(string)saved["payload_sha256"];
                var trace=await s.RunAsync(c.Kind,c.Actor,c.Input);
                bool pending=true,exact=true;
                if(s.HasPendingCraft)
                {
                    pending=s.CaptureState().Json==before.Json;
                    var handle=s.GetCraftProposal(trace);var same=s.ConfirmCraft(handle,c.AcceptCraft);
                    exact=handle!=null&&ReferenceEquals(trace,same)&&s.ConfirmCraft(handle,c.AcceptCraft)==null;
                }
                var row=await Record(Path.Combine(output,c.Id),c,before,s,trace,pending,exact);rows.Add(row);
                Write(Path.Combine(output,"summary.json"),Summary(live,group,rows,provider.Calls,status));
                Console.WriteLine(c.Id+": structure="+row["structural_reference_valid"]+" effect="+row["rule_effect_match"]+" intent="+row["automatic_intent_match"]+" invariant="+row["invariants"]);
                if(trace.Provider?.Error!=null||!(bool)row["invariants"])
                {provider.Stop(trace.Provider?.Error??"invariant_failed");status="stopped";return 1;}
            }
            if(group=="b")
            {
                provider.ExpectedPayloadHash=null;
                var order=DsWorldSnapshot.Parse(registration["order"]["before"].ToString(Formatting.None));
                var s=new DragonsleepSession(provider,7);s.RestoreState(order);
                foreach(string actor in registration["order"]["actors"].Values<string>())
                {
                    var c=new FourMechanismCase {Id="order_"+actor,Kind=AiTaskKind.AgentPlan,Actor=actor,Input=null,
                        Rubric=(string)registration["order"]["rubric"],Effect="仅观测，不预设动作结果"};
                    var before=s.CaptureState();var trace=await s.RunAsync(c.Kind,actor,null);
                    var row=await Record(Path.Combine(output,c.Id),c,before,s,trace,true,true);
                    row["same_initial_view"]=Body(Packet(order,c.Kind,actor,null))==Body(Packet(before,c.Kind,actor,null));
                    row["initial_perception"]=JObject.Parse(TaskPerceptionView.Capture(order.Restore(),c.Kind,actor).Json);
                    row["actual_perception"]=JObject.Parse(TaskPerceptionView.Capture(before.Restore(),c.Kind,actor).Json);
                    row["predecessor_world_diff"]=JToken.FromObject(DsWorldSnapshot.Diff(order,before));
                    Write(Path.Combine(output,c.Id,"result.json"),row);rows.Add(row);
                    Console.WriteLine(c.Id+": same_initial_view="+row["same_initial_view"]+" invariant="+row["invariants"]);
                    Write(Path.Combine(output,"summary.json"),Summary(live,group,rows,provider.Calls,status));
                    if(trace.Provider?.Error!=null||!(bool)row["invariants"])
                    {provider.Stop(trace.Provider?.Error??"invariant_failed");status="stopped";return 1;}
                }
                await RepeatShowProbe(output);
            }
            status="completed";return 0;
        }
        catch(InvalidOperationException) {status="stopped";provider.Stop("research_exception");throw;}
        finally {Write(Path.Combine(output,"summary.json"),Summary(live,group,rows,provider.Calls,status));Console.WriteLine("报告："+output);}
    }

    static async Task RepeatShowProbe(string output)
    {
        var s=new DragonsleepSession(new RuleProvider(),7);
        s.World.PlayerPlace=s.World.Actors["maren"].Place;s.World.Actors["maren"].Trust=.05f;s.World.Items["pact_page"].Holder="player";
        var steps=new List<object>();
        for(int n=0;n<2;n++)
        {
            var before=s.CaptureState();float trust=s.World.Actors["maren"].Trust;
            var t=await s.RunStepAsync(new Dictionary<string,object> { ["verb"]="show",["target"]="maren",["item"]="pact_page",
                ["place"]=s.World.PlayerPlace,["laws"]=new List<object>(),["difficulty"]=.3,["narration"]="本地重复原件出示探针" },"出示同一原件");
            steps.Add(new {step=n+1,before=JObject.Parse(before.Json),trace=t.ToRecord(),after=JObject.Parse(s.CaptureState().Json),
                trust_delta=s.World.Actors["maren"].Trust-trust,shown=s.World.Flags.ContainsKey("shown:pact_page:maren")});
        }
        Write(Path.Combine(output,"repeat-show-offline-probe.json"),new {seed=7,live_requests=0,steps});
    }
}
