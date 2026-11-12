using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Dragonsleep;

namespace Town.Core.Tests
{
    public class DragonsleepFourMechanismResearchTests
    {
        static string Root()
        {
            var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while(d!=null&&!File.Exists(Path.Combine(d.FullName,"dotnet","Town.sln")))d=d.Parent;
            return d?.FullName??throw new InvalidOperationException("repository_required");
        }
        static string Output()=>Path.Combine(Root(),".local","four-mechanisms-tests",Guid.NewGuid().ToString("N"));
        sealed class Failure : IAiProvider
        {
            public int Calls;
            public string Name=>"离线错误桩";
            public bool IsSimulated=>true;
            public Task<ProviderResult> CompleteAsync(PromptPacket p,CancellationToken ct)
            {Calls++;return Task.FromResult(new ProviderResult{ProviderName=Name,Simulated=true,Error="test_failure"});}
        }
        [Test]
        public void 默认无参离线_显式过滤不改变历史默认套题()
        {
            Assert.False(FourMechanismResearch.RequiresLive(Array.Empty<string>()));
            Assert.False(FourMechanismResearch.RequiresLive(new[]{"--four-mechanisms"}));
            Assert.True(FourMechanismResearch.RequiresLive(new[]{"--four-mechanisms","--live"}));
            CollectionAssert.AreEqual(ExperimentCases.All().Select(x=>x.Id),FourMechanismCases.Select(false).Select(x=>x.Id));
            CollectionAssert.AreEqual(ExperimentCases.Holdout().Select(x=>x.Id),FourMechanismCases.Select(true).Select(x=>x.Id));
            Assert.AreEqual(22,FourMechanismCases.Select(false,FourMechanismCases.Mechanisms).Count);
            Assert.AreEqual(14,FourMechanismCases.Select(true,FourMechanismCases.Mechanisms).Count);
            Assert.Throws<ArgumentException>(()=>FourMechanismCases.Select(false,"dialogue,typo"));
            Assert.Throws<ArgumentException>(()=>FourMechanismCases.Select(false,"dialogue,dialogue"));
        }
        [Test]
        public async Task 新套题完整离线_四轴分离_确切图样放弃_隐私差分()
        {
            string dir=Output();FourMechanismResearch.Prepare(Root(),dir);
            Assert.AreEqual(0,await FourMechanismResearch.Run(Root(),false,"b",dir));
            var report=JObject.Parse(File.ReadAllText(Path.Combine(dir,"b-offline","summary.json")));
            Assert.False((bool)report["live"]);Assert.AreEqual(37,(int)report["requested"]);
            var rows=(JArray)report["results"];
            Assert.AreEqual(32,rows.Count(x=>!((string)x["id"]).StartsWith("order_")));
            Assert.True(rows.All(x=>(bool)x["invariants"]));
            Assert.True(rows.Any(x=>x["automatic_intent_match"].Type==JTokenType.Boolean&&!(bool)x["automatic_intent_match"]));
            Assert.True(rows.All(x=>x["manual_semantic_review"].Type==JTokenType.Null));
            var light=rows.Single(x=>(string)x["id"]=="c07_light_decline");
            Assert.AreEqual("declined",(string)light["craft_decision"]);Assert.True((bool)light["declined_unchanged"]);
            var quiet=rows.Single(x=>(string)x["id"]=="c08_quiet_decline");
            Assert.AreEqual(JTokenType.Null,quiet["craft_decision"].Type,"RuleProvider不识别该新措辞，不得伪造已经放弃了一个合法图样");
            Assert.False((bool)quiet["rule_effect_match"]);Assert.False((bool)quiet["automatic_intent_match"]);
            Assert.False(File.Exists(Path.Combine(dir,"live-requests.jsonl")));
        }
        [Test]
        public async Task 提供方失败只调用一次_缺Usage费用保持未知_不得补跑同批()
        {
            string dir=Output();FourMechanismResearch.Prepare(Root(),dir);var failure=new Failure();
            Assert.AreEqual(1,await FourMechanismResearch.Run(Root(),false,"b",dir,failure));
            Assert.AreEqual(1,failure.Calls);
            var report=JObject.Parse(File.ReadAllText(Path.Combine(dir,"b-offline","summary.json")));
            Assert.AreEqual(1,(int)report["requested"]);Assert.AreEqual(1,(int)report["unknown_costs"]);
            Assert.AreEqual(JTokenType.Null,report["estimated_usd"].Type);
            Assert.AreEqual(JTokenType.Null,report["input_tokens"].Type);
            Assert.ThrowsAsync<InvalidOperationException>(async()=>await FourMechanismResearch.Run(Root(),false,"b",dir,failure));
            Assert.AreEqual(1,failure.Calls);
        }
        [Test]
        public void 预注册不可覆盖_篡改后离线也拒绝()
        {
            string dir=Output();FourMechanismResearch.Prepare(Root(),dir);
            Assert.Throws<InvalidOperationException>(()=>FourMechanismResearch.Prepare(Root(),dir));
            File.AppendAllText(Path.Combine(dir,"pre-registered.json")," ");
            Assert.Throws<InvalidOperationException>(()=>FourMechanismResearch.VerifyRegistration(Root(),dir));
        }
        [Test]
        public async Task 请求上限在调用提供方前拦截_不重置账本()
        {
            string dir=Output();Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir,"live-requests.jsonl"),Enumerable.Repeat("{}",160));
            var fake=new Failure();using var limited=new FourMechanismResearch.LimitedProvider(fake,true,dir);
            var packet=ContextBuilder.BuildDialogue(NpcPerceptionView.Capture(DsContent.NewWorld(7),"marta"),"你好");
            Assert.ThrowsAsync<InvalidOperationException>(async()=>await limited.CompleteAsync(packet,CancellationToken.None));
            Assert.AreEqual(0,fake.Calls);Assert.AreEqual(160,File.ReadLines(Path.Combine(dir,"live-requests.jsonl")).Count());
            await Task.CompletedTask;
        }
        [Test]
        public void 原始引用非法不能因规则剔除而算结构通过()
        {
            var world=DsContent.NewWorld(7);
            var raw=JObject.Parse("{\"say\":\"不知道\",\"cites\":[\"b_maren_page\"],\"reveal\":[],\"trust_delta\":0,\"persuaded_by\":[],\"action\":\"none\"}");
            Assert.False(FourMechanismResearch.ReferencesValid(raw,AiTaskKind.Dialogue,"marta",world));
        }
    }
}
