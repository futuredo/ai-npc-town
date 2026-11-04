using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Town.Core.Brain;
using Town.Core.Content;
using Town.Core.Knowledge;
using Town.Core.World;
using Town.OpenAI;

namespace Town.Core.Tests
{
    // 所有网络均使用内存 handler，测试不读取真实 key，也不会请求 OpenAI。
    public class OpenAiDialogueTests
    {
        private sealed class Budget : IRequestBudget
        {
            public int Calls;
            public bool Deny;
            public bool TryReserve(out string reason)
            { Calls++; reason = Deny ? "budget_exhausted" : null; return !Deny; }
        }

        private sealed class FakeHttp : HttpMessageHandler
        {
            public int Calls;
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { Calls++; return Handler(request, token); }
        }

        private static NpcDialogueRequest Request(string npc = Ids.Npc.Ahe) =>
            NpcDialogueRequest.From(HammerEventSetup.Create().Knowledge.ViewFor(npc), "锤子在哪里？");

        private static HttpResponseMessage Response(string text = "我不知道锤子在哪里。", string citation = null,
            string status = "completed", string action = "none", bool refuse = false)
        {
            var reply = new JObject { ["reply"] = text, ["action"] = action,
                ["cited_beliefs"] = citation == null ? new JArray() : new JArray(citation) };
            var content = refuse ? new JObject { ["type"] = "refusal", ["refusal"] = "拒绝" } :
                new JObject { ["type"] = "output_text", ["text"] = reply.ToString(Formatting.None) };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JObject
                {
                    ["status"] = status,
                    ["output"] = new JArray(new JObject { ["type"] = "message", ["content"] = new JArray(content) }),
                    ["usage"] = new JObject { ["input_tokens"] = 1000, ["output_tokens"] = 100,
                        ["input_tokens_details"] = new JObject { ["cached_tokens"] = 200 } }
                }.ToString())
            };
        }

        [Test]
        public async Task 发送当前角色认知且不包含隐藏位置或真值()
        {
            var fake = new FakeHttp();
            fake.Handler = async (message, _) =>
            {
                Assert.AreEqual("https://api.openai.com/v1/responses", message.RequestUri.ToString());
                var body = JObject.Parse(await message.Content.ReadAsStringAsync());
                Assert.AreEqual(OpenAiPolicy.Model, (string)body["model"]);
                Assert.AreEqual(false, (bool)body["store"]);
                Assert.AreEqual("none", (string)body["reasoning"]["effort"]);
                Assert.AreEqual("default", (string)body["service_tier"]);
                Assert.AreEqual(OpenAiPolicy.MaxOutputTokens, (int)body["max_output_tokens"]);
                var data = JObject.Parse((string)body["input"][0]["content"]);
                var beliefs = (JArray)data["known_beliefs"];
                CollectionAssert.AreEquivalent(new[] { "P1", "P5" }, beliefs.Select(x => (string)x["proposition"]));
                StringAssert.DoesNotContain("雨棚", data.ToString());
                StringAssert.DoesNotContain("TruthInWorld", data.ToString());
                return Response();
            };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget(), fake);
            Assert.IsTrue((await client.ReplyAsync(Request(), CancellationToken.None)).Success);
        }

        [Test]
        public void 请求快照不受之后认知写入影响()
        {
            var session = HammerEventSetup.Create();
            var view = session.Knowledge.ViewFor(Ids.Npc.Ahe);
            var request = NpcDialogueRequest.From(view, "你知道什么？");
            session.Knowledge.Learn(Ids.Npc.Ahe, Propositions.Location, true, 1, SourceType.SawEvidence,
                GameTime.At(1, 10), evidenceId: "C1");
            Assert.IsFalse(request.Beliefs.Any(b => b.PropositionId == Propositions.Location));
            Assert.IsTrue(view.Knows(Propositions.Location));
        }

        [TestCase("雨棚的收纳箱里。", null, "completed", "none", "knowledge_leak")]
        [TestCase("不知道。", "B9999", "completed", "none", "unknown_citation")]
        [TestCase("不知道。", null, "incomplete", "none", "incomplete_response")]
        [TestCase("我去拿。", null, "completed", "fetch_item", "invalid_output")]
        public async Task 不接受越权或不完整回复(string text, string citation, string status, string action, string error)
        {
            var fake = new FakeHttp { Handler = (_, __) => Task.FromResult(Response(text, citation, status, action)) };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget(), fake);
            var result = await client.ReplyAsync(Request(), CancellationToken.None);
            Assert.IsFalse(result.Success);
            Assert.AreEqual(error, result.ErrorCode);
            Assert.AreEqual(DialogueResult.Fallback, result.Reply);
            Assert.AreEqual(1000, result.Usage.InputTokens, "拒绝输出也保留收费 token 计数");
        }

        [Test]
        public async Task 已知位置和合法引用允许通过并记录费用估算()
        {
            var request = Request(Ids.Npc.Xiaochuan);
            string id = request.Beliefs.Single(b => b.PropositionId == Propositions.Location).Id;
            var fake = new FakeHttp { Handler = (_, __) => Task.FromResult(Response("在公共雨棚的收纳箱里。", id)) };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget(), fake);
            var result = await client.ReplyAsync(request, CancellationToken.None);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0.000132m, OpenAiPolicy.EstimateUsd(result.Usage));
        }

        [Test]
        public async Task 服务拒绝和格式错误不展示原始内容()
        {
            foreach (bool refuse in new[] { true, false })
            {
                var fake = new FakeHttp { Handler = (_, __) => Task.FromResult(refuse ? Response(refuse: true) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("private-error-data") }) };
                using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget(), fake);
                var result = await client.ReplyAsync(Request(), CancellationToken.None);
                Assert.AreEqual(refuse ? "refusal" : "invalid_response", result.ErrorCode);
                Assert.AreEqual(DialogueResult.Fallback, result.Reply);
            }
        }

        [TestCase(401)]
        [TestCase(429)]
        [TestCase(500)]
        public async Task HTTP错误只发一次且不回显错误体(int status)
        {
            var budget = new Budget();
            var fake = new FakeHttp { Handler = (_, __) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent("secret-should-never-be-returned") }) };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", budget, fake);
            var result = await client.ReplyAsync(Request(), CancellationToken.None);
            Assert.AreEqual("http_" + status, result.ErrorCode);
            Assert.AreEqual(1, fake.Calls);
            Assert.AreEqual(1, budget.Calls);
            Assert.AreEqual(DialogueResult.Fallback, result.Reply);
        }

        [Test]
        public async Task 超预算或预先取消不发网络请求()
        {
            var fake = new FakeHttp { Handler = (_, __) => throw new Exception("不应发送") };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget { Deny = true }, fake);
            Assert.AreEqual("budget_exhausted", (await client.ReplyAsync(Request(), CancellationToken.None)).ErrorCode);
            Assert.AreEqual("cancelled", (await client.ReplyAsync(Request(), new CancellationToken(true))).ErrorCode);
            Assert.AreEqual(0, fake.Calls);
        }

        [Test]
        public async Task 并发点击不排队且网络取消不重试()
        {
            var entered = new TaskCompletionSource<bool>();
            var fake = new FakeHttp { Handler = async (_, token) =>
                { entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); return Response(); } };
            using var client = new OpenAiDialogueClient("test-not-a-real-key", new Budget(), fake);
            using var cancel = new CancellationTokenSource();
            var first = client.ReplyAsync(Request(), cancel.Token);
            await entered.Task;
            Assert.AreEqual("busy", (await client.ReplyAsync(Request(), CancellationToken.None)).ErrorCode);
            cancel.Cancel();
            Assert.AreEqual("cancelled", (await first).ErrorCode);
            Assert.AreEqual(1, fake.Calls);
        }

        [Test]
        public void 持久化预算限制单轮和当日请求_重建客户端不能重置日额度()
        {
            string root = Path.Combine(Path.GetTempPath(), "town-budget-test-" + Guid.NewGuid());
            try
            {
                var first = new FileRequestBudget(root);
                for (int i = 0; i < 3; i++) Assert.IsTrue(first.TryReserve(out _));
                Assert.IsFalse(first.TryReserve(out _));
                for (int i = 3; i < 20; i++) Assert.IsTrue(new FileRequestBudget(root).TryReserve(out _));
                Assert.IsFalse(new FileRequestBudget(root).TryReserve(out _));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void 损坏的预算文件不会自动清零()
        {
            string root = Path.Combine(Path.GetTempPath(), "town-budget-test-" + Guid.NewGuid());
            try
            {
                string directory = Path.Combine(root, ".local", "openai");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "budget-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".json"), "broken");
                Assert.IsFalse(new FileRequestBudget(root).TryReserve(out string reason));
                Assert.AreEqual("invalid_budget_ledger", reason);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
