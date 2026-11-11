using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Kernel;
using Town.Core.Dragonsleep;
using Town.PieGame;

namespace Town.Core.Tests
{
    /// <summary>完全离线，使用真实 Kernel。写能力由反射模拟 Core 内 Session 签发，不增加生产公开构造入口。</summary>
    public sealed class DragonsleepActionKernelIntegrationTests
    {
        const string Buy = "{\"verb\":\"buy\",\"target\":\"marta\",\"item\":\"smoked_fish\",\"place\":\"market\","
            + "\"laws\":[],\"difficulty\":0.1,\"narration\":\"买鱼\"}";
        const string Unsupported = "{\"verb\":\"unsupported\",\"target\":\"\",\"item\":\"\",\"place\":\"\","
            + "\"laws\":[],\"difficulty\":0.1,\"narration\":\"尝试\"}";
        const string Sneak = "{\"verb\":\"sneak\",\"target\":\"\",\"item\":\"\",\"place\":\"market\","
            + "\"laws\":[],\"difficulty\":0.6,\"narration\":\"潜行\"}";

        static PromptPacket Packet(DsWorld world) =>
            ContextBuilder.BuildTask(TaskPerceptionView.Capture(world, AiTaskKind.Adjudicate, "player"), "买一串鱼");

        [Test]
        public void 冻结只含玩家可见提示_异地居民与雏龙位置变化不改变字节()
        {
            var world = DsContent.NewWorld();
            var first = ActionPromptCodec.Freeze(Packet(world));
            world.Actors["pip"].Name = "PRIVATE_ACTOR_NAME";
            world.Actors["pip"].Goal = "PRIVATE_ACTOR_GOAL";
            world.Actors["pip"].Place = "PRIVATE_ACTOR_LOCATION";
            world.Whelp.Place = "PRIVATE_WHELP_LOCATION";
            var second = ActionPromptCodec.Freeze(Packet(world));
            CollectionAssert.AreEqual(first.Bytes, second.Bytes);
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            string text = Encoding.UTF8.GetString(first.Bytes);
            Assert.That(text, Does.Not.Contain("PRIVATE_ACTOR"));
            Assert.That(text, Does.Not.Contain("PRIVATE_WHELP_LOCATION"));
            Assert.That(text, Does.Not.Contain("b_pip_whelp_place"));
            Assert.That(text, Does.Not.Contain(DsWorldSnapshot.Format));
            Assert.That(first.Binding, Does.Contain("Adjudicate|player|" + Protocol.Version));
            Assert.That(first.Binding, Does.Contain(DsExecutor.RulesVersion));
        }

        [Test]
        public void FrozenPayload复制传入和传出的字节()
        {
            var bytes = ActionPromptCodec.Encode(Packet(DsContent.NewWorld()));
            var frozen = new FrozenPayload(ActionPromptCodec.Binding, bytes);
            byte original = frozen.Bytes[0];
            bytes[0] ^= 0x7f;
            var copy = frozen.Bytes; copy[0] ^= 0x7f;
            Assert.AreEqual(original, frozen.Bytes[0]);
            Assert.AreEqual(frozen.Fingerprint, new FrozenPayload(frozen.Binding, frozen.Bytes).Fingerprint);
        }

        [TestCase("instructions")]
        [TestCase("section")]
        [TestCase("actor")]
        [TestCase("schema")]
        public async Task Begin后修改公开trace提示不改变私有冻结packet或发送字节(string field)
        {
            var host = new HostPort();
            var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var packet = Packet(host.World);
            var original = ActionPromptCodec.Encode(packet);
            var request = transaction.Begin("freeze-" + field, packet);
            switch (field)
            {
                case "instructions": packet.Instructions += "changed"; break;
                case "section": packet.Sections[0].Text += "changed"; break;
                case "actor": packet.ActorId = "gm"; break;
                case "schema": packet.SchemaName += "changed"; break;
            }
            var provider = new Provider(Buy);
            var result = await Prepare(transaction, request, provider);
            Assert.AreEqual("ready", result.KernelCode);
            Assert.IsNull(result.FailureCode);
            Assert.AreEqual(1, provider.Calls);
            Assert.IsNotNull(result.Evaluation);
            CollectionAssert.AreEqual(original, ActionPromptCodec.Encode(provider.Packet));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task 提供方收到被替换的冻结字节或binding会拒绝且不调用原提供方(bool changeBinding)
        {
            var host = new HostPort();
            var transaction = new KernelActionTransaction(new PayloadChangingKernel(host, changeBinding));
            var request = transaction.Begin("tampered-payload", Packet(host.World));
            var provider = new Provider(Buy);
            var result = await Prepare(transaction, request, provider);
            Assert.AreEqual("provider_error", result.KernelCode);
            Assert.AreEqual("frozen_payload_mismatch", result.FailureCode);
            Assert.AreEqual(0, provider.Calls); Assert.IsNull(result.Evaluation);
        }

        [Test]
        public void 原同一PromptPacket交给提供方_准备阶段不读live且不写世界()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            var packet = Packet(host.World); var before = host.Read();
            var request = transaction.Begin("packet-identity", packet);
            host.ReadForbidden = true;
            var provider = new Provider(Buy);
            var prepared = Task.Run(() => Prepare(transaction, request, provider)).GetAwaiter().GetResult();
            Assert.AreNotSame(packet, provider.Packet, "提供方收到 Begin 时创建的同一份私有冻结 packet，而非公开 trace 对象");
            CollectionAssert.AreEqual(ActionPromptCodec.Encode(packet), ActionPromptCodec.Encode(provider.Packet));
            Assert.AreEqual("ready", prepared.KernelCode);
            Assert.IsNull(prepared.FailureCode);
            Assert.AreEqual(VerdictKind.Accepted, prepared.Evaluation.Kind);
            host.ReadForbidden = false;
            Assert.AreEqual(before, host.Read()); Assert.AreEqual(0, host.Applies);
        }

        [TestCase(Buy)]
        [TestCase(Sneak)]
        public async Task 相同before和raw多线程规则候选字节完全一致(string raw)
        {
            var before = DsWorldSnapshot.Capture(DsContent.NewWorld(), 0, 4).Json;
            var handler = new ActionTaskHandler();
            var plans = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => handler.Evaluate(before, raw))));
            Assert.That(plans.Select(plan => plan.BeforeJson).Distinct().ToArray(), Is.EqualTo(new[] { before }));
            Assert.AreEqual(1, plans.Select(plan => plan.AfterJson).Distinct().Count());
            var candidates = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => handler.Prepare(before, raw))));
            Assert.AreEqual(1, candidates.Select(candidate => candidate.Identity).Distinct().Count());
            Assert.AreEqual(Protocol.Hash(plans[0].AfterJson), candidates[0].AfterDigest);
        }

        [Test]
        public async Task 并发准备的评估旁路不串请求()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var requests = Enumerable.Range(0, 16).Select(i => transaction.Begin("parallel-" + i, Packet(host.World))).ToArray();
            var results = await Task.WhenAll(requests.Select((request, i) => Task.Run(async () =>
            {
                var provider = new Provider(i % 2 == 0 ? Buy : Unsupported) { Yield = true };
                return await Prepare(transaction, request, provider);
            })));
            for (int i = 0; i < results.Length; i++)
            {
                Assert.AreEqual(requests[i].RequestId, results[i].Request.RequestId);
                Assert.AreEqual(requests[i].RequestId, Plan(results[i]).RequestId, "Session 旧回执必须使用真实请求编号而非 worker 占位符");
                Assert.AreEqual(i % 2 == 0 ? VerdictKind.Accepted : VerdictKind.Rejected, results[i].Evaluation.Kind);
                Assert.AreEqual(i % 2 == 0 ? 2 : 0, results[i].Evaluation.TimeCost);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 成功与掷骰后拒绝均返回含spent变化的Candidate并由Kernel提交(bool insufficientCoins)
        {
            var host = new HostPort(); if (insufficientCoins) host.World.Coins = 0;
            var kernel = new ActionKernelAdapter(host); var transaction = new KernelActionTransaction(kernel);
            var request = transaction.Begin("spent-" + insufficientCoins, Packet(host.World));
            var preparation = Prepare(transaction, request, new Provider(Buy)).GetAwaiter().GetResult();
            Assert.AreEqual("ready", preparation.KernelCode); Assert.IsNull(preparation.FailureCode);
            Assert.AreEqual(insufficientCoins ? VerdictKind.Rejected : VerdictKind.Accepted, preparation.Evaluation.Kind);
            Assert.AreEqual(2, preparation.Evaluation.TimeCost);
            var plan = Plan(preparation); Assert.AreEqual(2, DsWorldSnapshot.Parse(plan.AfterJson).Spent);
            var write = host.NewWrite(preparation.Plan);
            var committed = transaction.Commit(preparation, write);
            Assert.AreEqual("committed", committed.KernelCode); Assert.IsTrue(committed.Applied);
            Assert.IsNull(committed.FailureCode); Assert.AreEqual(2, host.Spent);
            Assert.AreEqual(plan.AfterJson, kernel.Capture());
            Assert.AreEqual(1, host.Applies); Assert.AreEqual(0, host.ExternalWrites);
            Assert.AreEqual(insufficientCoins ? "marta" : "player", host.World.Items["smoked_fish"].Holder);
        }

        [Test]
        public void 真实种子掷骰失败仍由同一Kernel候选提交随机计数和时间()
        {
            var host = new HostPort();
            host.World.Seed = Enumerable.Range(1, 1000).First(seed => DsSeededRoll.Instance.Next(seed, 1) < 0.6);
            var kernel = new ActionKernelAdapter(new DsKernelStatePort(host.Capture));
            var transaction = new KernelActionTransaction(kernel);
            var result = Prepare(transaction, transaction.Begin("seeded-failure", Packet(host.World)), new Provider(Sneak))
                .GetAwaiter().GetResult();
            Assert.AreEqual("ready", result.Code); Assert.AreEqual(VerdictKind.Rejected, result.Evaluation.Kind);
            Assert.AreEqual("roll_failed", result.Evaluation.ReasonCode.Code);
            Assert.Less(result.Evaluation.Roll.Value, result.Evaluation.Difficulty.Value);
            Assert.AreEqual(2, DsWorldSnapshot.Parse(result.Plan.AfterJson).Spent);
            Assert.AreEqual(1, DsWorldSnapshot.Parse(result.Plan.AfterJson).Restore().RollCounter);
            Assert.AreEqual(0, host.World.RollCounter); Assert.AreEqual(0, host.Spent);
            Assert.AreEqual("committed", transaction.Commit(result, host.NewWrite(result.Plan)).Code);
            Assert.AreEqual(1, host.World.RollCounter); Assert.AreEqual(2, host.Spent);
            Assert.AreEqual(result.Plan.AfterJson, kernel.Capture());
        }

        [Test]
        public async Task 提供方Error与status及不完整信息usage和未知费用在Kernel失败后仍保留()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var response = new ProviderResult
            {
                ProviderName = "offline-error", Error = "output_incomplete", RawText = "partial",
                ResponseStatus = "incomplete", IncompleteReason = "max_output_tokens", InputTokens = 11,
                OutputTokens = 7, EstimatedUsd = null, Simulated = true
            };
            var request = transaction.Begin("provider-failed", Packet(host.World));
            var result = await Prepare(transaction, request, new Provider(response));
            Assert.AreEqual("provider_error", result.KernelCode); Assert.AreEqual("output_incomplete", result.FailureCode);
            Assert.IsNull(result.Evaluation);
            Assert.AreEqual(response.Error, result.Provider.Error); Assert.AreEqual(response.RawText, result.Provider.RawText);
            Assert.AreEqual(response.ResponseStatus, result.Provider.ResponseStatus);
            Assert.AreEqual(response.IncompleteReason, result.Provider.IncompleteReason);
            Assert.AreEqual(11, result.Provider.InputTokens); Assert.AreEqual(7, result.Provider.OutputTokens);
            Assert.IsNull(result.Provider.EstimatedUsd); Assert.IsTrue(result.Provider.Simulated);
            result.Provider.Error = "changed"; response.Error = "changed";
            Assert.AreEqual("output_incomplete", result.Provider.Error, "结果必须独立于外部可变 ProviderResult");
            Assert.AreEqual(0, host.Applies);
        }

        [Test]
        public async Task Kernel路径缺少完整usage时费用副本归一为未知且不修改提供方原结果()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var request = transaction.Begin("partial-usage", Packet(host.World));
            var response = new ProviderResult
            { ProviderName = "offline", RawText = Buy, InputTokens = 5, OutputTokens = null, EstimatedUsd = 0.00013m };
            var result = await Prepare(transaction, request, new Provider(response));
            Assert.AreEqual("ready", result.KernelCode);
            Assert.AreEqual(5, result.Provider.InputTokens); Assert.IsNull(result.Provider.OutputTokens);
            Assert.IsNull(result.Provider.EstimatedUsd);
            Assert.AreEqual(0.00013m, response.EstimatedUsd, "归一化不能改写原 ProviderResult");
        }

        [TestCase(null, null)]
        [TestCase(3, null)]
        [TestCase(null, 2)]
        public void Kernel路径无完整usage时即使离线费用给零仍是未知(int? input, int? output)
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var response = new ProviderResult
            { ProviderName = "offline", RawText = Buy, InputTokens = input, OutputTokens = output, EstimatedUsd = 0m, Simulated = true };
            var result = Prepare(transaction, transaction.Begin("no-usage", Packet(host.World)), new Provider(response)).GetAwaiter().GetResult();
            Assert.IsNull(result.Provider.EstimatedUsd); Assert.AreEqual(0m, response.EstimatedUsd);
        }

        [Test]
        public void 完整usage保留原估算费用包括零()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var response = new ProviderResult
            { ProviderName = "offline", RawText = Buy, InputTokens = 5, OutputTokens = 3, EstimatedUsd = 0.00013m };
            var result = Prepare(transaction, transaction.Begin("full-usage", Packet(host.World)), new Provider(response)).GetAwaiter().GetResult();
            Assert.AreEqual(0.00013m, result.Provider.EstimatedUsd);
            response.EstimatedUsd = 0m;
            var zero = Prepare(transaction, transaction.Begin("full-usage-zero", Packet(host.World)), new Provider(response)).GetAwaiter().GetResult();
            Assert.AreEqual(0m, zero.Provider.EstimatedUsd);
        }

        [Test]
        public void 真实Kernel重复提交只Apply一次_写能力本身也不可重复使用()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            var preparation = Prepare(transaction, transaction.Begin("duplicate", Packet(host.World)), new Provider(Buy)).GetAwaiter().GetResult();
            var write = host.NewWrite(preparation.Plan);
            Assert.AreEqual("committed", transaction.Commit(preparation, write).KernelCode);
            string after = kernel.Capture();
            var duplicate = transaction.Commit(preparation, write);
            Assert.AreEqual("duplicate", duplicate.KernelCode); Assert.IsFalse(duplicate.Applied);
            Assert.IsNull(duplicate.FailureCode); Assert.AreEqual(1, host.Applies); Assert.AreEqual(after, kernel.Capture());
            Assert.Throws<InvalidOperationException>(() => write.Apply(after));
        }

        [Test]
        public void 同一真实Kernel请求不同候选提交被判request_conflict且不再次Apply()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            var request = transaction.Begin("request-conflict", Packet(host.World));
            var first = Prepare(transaction, request, new Provider(Buy)).GetAwaiter().GetResult();
            var second = Prepare(transaction, request, new Provider(Unsupported)).GetAwaiter().GetResult();
            Assert.AreEqual("committed", transaction.Commit(first, host.NewWrite(first.Plan)).KernelCode);
            string after = kernel.Capture();
            var write = host.NewWrite(second.Plan);
            var result = transaction.Commit(second, write);
            Assert.AreEqual("request_conflict", result.KernelCode); Assert.AreEqual("request_conflict", result.FailureCode);
            Assert.IsFalse(write.Applied); Assert.AreEqual(1, host.Applies); Assert.AreEqual(after, kernel.Capture());
        }

        [Test]
        public void 写能力只接受Kernel传入的候选_不依赖调用方另建计划()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            string before = kernel.Capture();
            var preparation = Prepare(transaction, transaction.Begin("kernel-write", Packet(host.World)), new Provider(Buy)).GetAwaiter().GetResult();
            var wrongPlan = new ActionTaskHandler().Evaluate(before, Unsupported);
            var wrongWrite = host.NewWrite(wrongPlan);
            Assert.Throws<InvalidOperationException>(() => transaction.Commit(preparation, wrongWrite));
            Assert.AreEqual(before, kernel.Capture());
            var write = host.NewWrite(preparation.Plan);
            Assert.AreEqual("committed", transaction.Commit(preparation, write).Code);
            Assert.AreNotEqual(wrongPlan.AfterJson, kernel.Capture());
            Assert.AreEqual(preparation.Plan.AfterJson, kernel.Capture());
            Assert.IsTrue(write.Applied); Assert.AreEqual(1, host.Applies);
        }

        [Test]
        public void Kernel提交后的取消不能抹掉已提交事实()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            using (var cancelled = new CancellationTokenSource())
            {
                var preparation = Prepare(transaction, transaction.Begin("cancel-after", Packet(host.World)),
                    new Provider(Buy), cancelled.Token).GetAwaiter().GetResult();
                var write = host.NewWrite(preparation.Plan);
                Assert.AreEqual("committed", transaction.Commit(preparation, write).KernelCode);
                cancelled.Cancel();
                var duplicate = transaction.Commit(preparation, write);
                Assert.AreEqual("duplicate", duplicate.KernelCode); Assert.IsTrue(duplicate.CancellationAfterCommit);
                Assert.AreEqual(1, host.Applies);
            }
        }

        [TestCase(false, "stale_state")]
        [TestCase(true, "stale_session")]
        public void 真实Kernel状态变化与相同字节Reset分别拒绝旧准备且不Apply(bool reset, string expected)
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            var before = kernel.Capture();
            var preparation = Prepare(transaction, transaction.Begin("stale", Packet(host.World)), new Provider(Buy)).GetAwaiter().GetResult();
            if (reset) kernel.Reset(before);
            else
            {
                var stamp = kernel.CaptureState(); var changed = host.WorldClone(); changed.Coins--;
                Assert.AreEqual("committed", kernel.TransactScene("external", stamp,
                    DsWorldSnapshot.Capture(changed, 0, 4).Json, host.NewSceneWrite(changed)).Code);
            }
            string actual = kernel.Capture();
            var write = host.NewWrite(preparation.Plan);
            var result = transaction.Commit(preparation, write);
            Assert.AreEqual(expected, result.KernelCode); Assert.AreEqual(expected, result.FailureCode);
            Assert.IsFalse(result.Applied); Assert.IsFalse(write.Applied);
            Assert.AreEqual(0, host.Applies); Assert.AreEqual(actual, kernel.Capture());
        }

        [TestCase("not json", "parse_failed", "parse_error")]
        [TestCase("[]", "parse_failed", "parse_error")]
        [TestCase("{}", "host_rejected", "schema_invalid")]
        public async Task Kernel解析与HostRejection码映射并保留可用评估(string raw, string kernelCode, string failureCode)
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var result = await Prepare(transaction, transaction.Begin("failure", Packet(host.World)), new Provider(raw));
            Assert.AreEqual(kernelCode, result.KernelCode); Assert.AreEqual(failureCode, result.FailureCode);
            Assert.AreEqual(raw, result.Provider.RawText);
            if (kernelCode == "host_rejected")
            {
                Assert.IsNotNull(result.Evaluation); Assert.AreEqual("schema_invalid", result.Evaluation.FailureCode);
                Assert.AreEqual(Plan(result).BeforeJson, Plan(result).AfterJson);
            }
            else Assert.IsNull(result.Evaluation);
            Assert.AreEqual(0, host.Applies);
        }

        [Test]
        public async Task 未知引用保存评估并映射host_rejected而非吞掉规则失败()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var raw = Buy.Replace("smoked_fish", "unknown_item");
            var result = await Prepare(transaction, transaction.Begin("unknown-ref", Packet(host.World)), new Provider(raw));
            Assert.AreEqual("host_rejected", result.KernelCode); Assert.AreEqual("unknown_entity", result.FailureCode);
            Assert.AreEqual("unknown_entity", result.Evaluation.FailureCode);
            Assert.IsNotEmpty(result.Evaluation.ValidationIssues);
        }

        [Test]
        public void 真实Kernel提交HostRejection仍保留错误且不调用Apply()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            string before = kernel.Capture();
            var preparation = Prepare(transaction, transaction.Begin("rejected-commit", Packet(host.World)), new Provider("{}")).GetAwaiter().GetResult();
            var write = host.NewWrite(preparation.Plan);
            var result = transaction.Commit(preparation, write);
            Assert.AreEqual("host_rejected", result.KernelCode); Assert.AreEqual("schema_invalid", result.FailureCode);
            Assert.IsFalse(result.Applied); Assert.IsFalse(write.Applied); Assert.AreEqual(0, host.Applies);
            Assert.AreEqual(before, kernel.Capture());
        }

        [Test]
        public void 无Plan的失败准备可用空写能力取回Kernel回执_成功准备禁止空写能力()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            var failed = transaction.PrepareAsync(transaction.Begin("null-write-failed", Packet(host.World)), new Provider("bad json"))
                .GetAwaiter().GetResult();
            Assert.IsNull(failed.Plan); Assert.AreEqual("parse_error", failed.Code);
            Assert.AreEqual("parse_error", transaction.Commit(failed, null).Code);
            var ready = transaction.PrepareAsync(transaction.Begin("null-write-ready", Packet(host.World)), new Provider(Buy)).GetAwaiter().GetResult();
            var error = Assert.Throws<InvalidOperationException>(() => transaction.Commit(ready, null));
            Assert.AreEqual("action_write_required", error.Message); Assert.AreEqual(0, host.Applies);
            Assert.AreEqual("committed", transaction.Commit(ready, host.NewWrite(ready.Plan)).Code);
        }

        [Test]
        public void 已准备但提交前取消由真实Kernel拒绝且不调用Apply()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(host);
            var transaction = new KernelActionTransaction(kernel);
            string before = kernel.Capture();
            var preparation = Prepare(transaction, transaction.Begin("cancel-commit", Packet(host.World)), new Provider(Buy)).GetAwaiter().GetResult();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var write = host.NewWrite(preparation.Plan);
                var result = transaction.Commit(preparation, write, cancellation.Token);
                Assert.AreEqual("cancelled", result.KernelCode); Assert.AreEqual("cancelled", result.FailureCode);
                Assert.IsFalse(write.Applied); Assert.AreEqual(0, host.Applies); Assert.AreEqual(before, kernel.Capture());
            }
        }

        [TestCase("cancelled")]
        [TestCase("late_after_cancel")]
        [TestCase("stale_state")]
        [TestCase("stale_session")]
        [TestCase("request_conflict")]
        [TestCase("replay_mismatch")]
        public void 非适配错误码逐字保留(string code) => Assert.AreEqual(code, KernelActionTransaction.MapFailure(code));

        [Test]
        public void 取消前与提供方忽略取消后的Kernel错误码及usage旁路()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(host));
            var provider = new Provider(Buy); var request = transaction.Begin("cancel-before", Packet(host.World));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var result = Prepare(transaction, request, provider, cancelled.Token).GetAwaiter().GetResult();
                Assert.AreEqual("cancelled", result.FailureCode); Assert.AreEqual(0, provider.Calls);
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var lateProvider = new Provider(Buy) { OnCall = () => cancelled.Cancel() };
                var late = Prepare(transaction, transaction.Begin("cancel-late", Packet(host.World)), lateProvider, cancelled.Token)
                    .GetAwaiter().GetResult();
                Assert.AreEqual("late_after_cancel", late.KernelCode); Assert.AreEqual("late_after_cancel", late.FailureCode);
                Assert.AreEqual(3, late.Provider.InputTokens); Assert.IsNull(late.Evaluation);
            }
        }

        [Test]
        public void 评估快照集合不可变且变化对象为深复制()
        {
            var handler = new ActionTaskHandler();
            var plan = handler.Evaluate(DsWorldSnapshot.Capture(DsContent.NewWorld(), 0, 4).Json, Buy);
            var evaluation = plan.EvaluationSnapshot;
            Assert.Throws<NotSupportedException>(() => ((IList<string>)evaluation.Notes).Add("changed"));
            string original = evaluation.Changes[0].After;
            evaluation.Changes[0].After = "changed";
            Assert.AreEqual(original, evaluation.Changes[0].After);
            Assert.AreEqual(original, plan.EvaluationSnapshot.Changes[0].After);
        }

        [Test]
        public void 共用端口只允许有效scope读取_禁止scope写入越权嵌套与跨线程()
        {
            var snapshot = DsWorldSnapshot.Capture(DsContent.NewWorld(), 0, 4);
            var port = new DsKernelStatePort(() => snapshot);
            Assert.Throws<InvalidOperationException>(() => port.Read());
            Assert.Throws<InvalidOperationException>(() => port.Write(snapshot.Json));
            var scope = port.OpenRead();
            Assert.AreEqual(snapshot.Json, port.Read());
            Assert.Throws<InvalidOperationException>(() => port.Write(snapshot.Json));
            Assert.Throws<InvalidOperationException>(() => port.OpenRead());
            Assert.Throws<InvalidOperationException>(() => port.OpenReset());
            Task.Run(() =>
            {
                Assert.Throws<InvalidOperationException>(() => port.Read());
                Assert.Throws<InvalidOperationException>(() => port.OpenRead());
                Assert.Throws<InvalidOperationException>(() => scope.Dispose());
            }).GetAwaiter().GetResult();
            Assert.AreEqual(snapshot.Json, port.Read(), "错误线程释放不应关闭所有者 scope");
            scope.Dispose(); scope.Dispose();
            Assert.Throws<InvalidOperationException>(() => port.Read());
            using (port.OpenRead()) Assert.AreEqual(snapshot.Json, port.Read());
        }

        [Test]
        public void 共用端口捕获回调禁止重入_异常后仍可关闭并重开scope()
        {
            var snapshot = DsWorldSnapshot.Capture(DsContent.NewWorld(), 0, 4);
            DsKernelStatePort port = null;
            bool reenter = true;
            port = new DsKernelStatePort(() =>
            {
                if (reenter) port.Read();
                return snapshot;
            });
            using (port.OpenRead())
            {
                var error = Assert.Throws<InvalidOperationException>(() => port.Read());
                Assert.AreEqual("kernel_port_reentry", error.Message);
                reenter = false;
                Assert.AreEqual(snapshot.Json, port.Read());
            }
            using (port.OpenRead()) Assert.AreEqual(snapshot.Json, port.Read());
        }

        [Test]
        public void 共用端口Reset仅比较当前快照_不替换第二份世界_且scope至多写一次()
        {
            var world = DsContent.NewWorld(); var snapshot = DsWorldSnapshot.Capture(world, 0, 4);
            var originalPip = world.Actors["pip"];
            int captures = 0;
            var port = new DsKernelStatePort(() => { captures++; return snapshot; });
            using (port.OpenReset())
            {
                port.Write(snapshot.Json);
                Assert.Throws<InvalidOperationException>(() => port.Write(snapshot.Json));
            }
            Assert.AreEqual(1, captures);
            using (port.OpenReset())
            {
                var error = Assert.Throws<InvalidOperationException>(() => port.Write(snapshot.Json + " "));
                Assert.AreEqual("reset_candidate_mismatch", error.Message);
            }
            Assert.AreSame(originalPip, world.Actors["pip"]);
            Assert.AreEqual(snapshot.Json, DsWorldSnapshot.Capture(world, 0, 4).Json);
        }

        [Test]
        public void 同一真实Kernel和共用端口承载SceneDayAction_旧行动按完整状态过期()
        {
            var host = new HostPort();
            var port = new DsKernelStatePort(host.Capture);
            var kernel = new ActionKernelAdapter(port);
            var transaction = new KernelActionTransaction(kernel);
            var old = Prepare(transaction, transaction.Begin("before-scene", Packet(host.World)), new Provider(Buy))
                .GetAwaiter().GetResult();
            var sceneBefore = kernel.CaptureState();
            var sceneWorld = host.WorldClone(); sceneWorld.PlayerPlace = "workshop";
            string sceneAfter = DsWorldSnapshot.Capture(sceneWorld, host.Spent, 4).Json;
            var sceneWrite = host.NewSceneWrite(sceneWorld);
            Assert.AreEqual("committed", kernel.TransactScene("scene", sceneBefore, sceneAfter, sceneWrite).Code);
            Assert.IsTrue(sceneWrite.Applied); Assert.AreEqual("workshop", host.World.PlayerPlace);
            var stale = transaction.Commit(old, host.NewWrite(old.Plan));
            Assert.AreEqual("stale_state", stale.Code); Assert.AreEqual(0, host.Applies);
            var dayBefore = kernel.CaptureState(); var dayWorld = host.WorldClone(); dayWorld.Advance();
            string dayAfter = DsWorldSnapshot.Capture(dayWorld, 0, 4).Json;
            var dayWrite = host.NewDayWrite(dayWorld, 0);
            Assert.AreEqual("committed", kernel.TransactDay("day", dayBefore, dayAfter, dayWrite).Code);
            Assert.IsTrue(dayWrite.Applied); Assert.AreEqual(DayPhase.Noon, host.World.Phase);
            var action = Prepare(transaction, transaction.Begin("after-day", Packet(host.World)), new Provider(Unsupported))
                .GetAwaiter().GetResult();
            Assert.AreEqual("committed", transaction.Commit(action, host.NewWrite(action.Plan)).Code);
            Assert.AreEqual(1, host.Applies); Assert.AreEqual(0, host.ExternalWrites);
            Assert.AreEqual(action.Plan.AfterJson, kernel.Capture());
        }

        [Test]
        public void 共用端口SceneDay要求候选逐字匹配_拒绝时不调用能力()
        {
            var host = new HostPort(); var port = new DsKernelStatePort(host.Capture);
            string before = host.Read(); var candidate = host.WorldClone(); candidate.PlayerPlace = "workshop";
            var sceneWrite = host.NewSceneWrite(candidate);
            using (port.OpenScene(sceneWrite, "expected"))
                Assert.Throws<InvalidOperationException>(() => port.Write("different"));
            Assert.IsFalse(sceneWrite.Applied); Assert.AreEqual(before, host.Read());
            candidate.Advance(); var dayWrite = host.NewDayWrite(candidate, 0);
            using (port.OpenDay(dayWrite, "expected"))
                Assert.Throws<InvalidOperationException>(() => port.Write("different"));
            Assert.IsFalse(dayWrite.Applied); Assert.AreEqual(before, host.Read());
        }

        [Test]
        public void 共享事务每次ready记录私有ReplayFrame_失败不记录_列表不可修改()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(new DsKernelStatePort(host.Capture));
            var transaction = new KernelActionTransaction(kernel);
            var request = transaction.Begin("private-record", Packet(host.World));
            var provider = new Provider(Buy);
            var result = Prepare(transaction, request, provider).GetAwaiter().GetResult();
            Assert.AreEqual("ready", result.Code);
            Assert.AreEqual(1, transaction.PrivateRecordList.Count);
            var frame = transaction.PrivateRecordList[0];
            Assert.AreEqual(request.RequestId, frame.RequestId); Assert.AreEqual(request.Fingerprint, frame.PromptFingerprint);
            Assert.AreEqual(result.Plan.BeforeHash, frame.BeforeDigest); Assert.AreEqual(result.Plan.AfterHash, frame.AfterDigest);
            Assert.AreEqual(Buy, frame.Raw); Assert.AreEqual(DsExecutor.RulesVersion, frame.HostRulesVersion);
            Assert.AreEqual(Protocol.Version, frame.KernelVersion);
            Assert.Throws<NotSupportedException>(() => ((IList<ReplayFrame>)transaction.PrivateRecordList).Add(frame));
            var snapshot = transaction.PrivateRecordList;
            Prepare(transaction, transaction.Begin("private-failed", Packet(host.World)), new Provider("{}")).GetAwaiter().GetResult();
            Assert.AreEqual(1, transaction.PrivateRecordList.Count);
            Prepare(transaction, transaction.Begin("private-second", Packet(host.World)), new Provider(Unsupported)).GetAwaiter().GetResult();
            Assert.AreEqual(2, transaction.PrivateRecordList.Count, "拒绝但有有效候选同样是 ready");
            Assert.AreEqual(1, snapshot.Count, "取出的列表必须是独立只读快照");
            Assert.AreEqual(0, host.Applies);
        }

        [Test]
        public void Begin按实际UTF8快照字节累计保留量_Reset清账且不重写世界()
        {
            var host = new HostPort(); var kernel = new ActionKernelAdapter(new DsKernelStatePort(host.Capture));
            var transaction = new KernelActionTransaction(kernel);
            string before = host.Read(); long bytes = Encoding.UTF8.GetByteCount(before);
            Assert.Greater(bytes, before.Length, "包含中文的快照必须按 UTF-8 字节而不是字符数计量");
            Assert.AreEqual(0, kernel.RetainedRequestCount); Assert.AreEqual(0, kernel.RetainedBeforeUtf8Bytes);
            var request = transaction.Begin("retained-1", Packet(host.World));
            Assert.AreEqual(1, kernel.RetainedRequestCount); Assert.AreEqual(bytes, kernel.RetainedBeforeUtf8Bytes);
            var prepared = Prepare(transaction, request, new Provider(Buy)).GetAwaiter().GetResult();
            transaction.Commit(prepared, host.NewWrite(prepared.Plan));
            string after = kernel.Capture(); long afterBytes = Encoding.UTF8.GetByteCount(after);
            transaction.Begin("retained-2", Packet(host.World));
            Assert.AreEqual(2, kernel.RetainedRequestCount); Assert.AreEqual(bytes + afterBytes, kernel.RetainedBeforeUtf8Bytes);
            Assert.Throws<InvalidOperationException>(() => transaction.Begin("retained-2", Packet(host.World)));
            Assert.AreEqual(2, kernel.RetainedRequestCount); Assert.AreEqual(bytes + afterBytes, kernel.RetainedBeforeUtf8Bytes);
            var world = host.World; var pip = host.World.Actors["pip"];
            kernel.Reset(after);
            Assert.AreEqual(0, kernel.RetainedRequestCount); Assert.AreEqual(0, kernel.RetainedBeforeUtf8Bytes);
            Assert.AreSame(world, host.World); Assert.AreSame(pip, host.World.Actors["pip"]);
            Assert.AreEqual(after, kernel.Capture()); Assert.AreEqual(0, host.ExternalWrites);
        }

        [Test]
        public void 私有ReplayFrame通过共享DotNet适配层真实Kernel重放并提交_不再次调用提供方()
        {
            var recorded = RecordReplay();
            // 直接使用真实 Kernel 请求入口，保持 ReplayFrame 的 request/kind/payload 绑定。
            var replayHost = new HostPort(); var replayKernel = new ActionKernelAdapter(new DsKernelStatePort(replayHost.Capture));
            replayKernel.Register(KernelActionTransaction.TaskKind, new ActionTaskHandler());
            var request = replayKernel.Begin(recorded.RequestId, KernelActionTransaction.TaskKind, ActionPromptCodec.Freeze(Packet(replayHost.World)));
            var prepared = replayKernel.PrepareReplayAsync(request, recorded, recorded.Digest).GetAwaiter().GetResult();
            Assert.AreEqual("ready", prepared.Receipt.Code);
            var replayPlan = new ActionTaskHandler().Evaluate(replayHost.Read(), recorded.Raw);
            var receipt = replayKernel.Commit(prepared, replayHost.NewWrite(replayPlan));
            Assert.AreEqual("committed", receipt.Code); Assert.AreEqual(1, replayHost.Applies);
            Assert.AreEqual(recorded.AfterDigest, Protocol.Hash(replayKernel.Capture()));
        }

        [TestCase("raw")]
        [TestCase("prompt")]
        [TestCase("before")]
        [TestCase("after")]
        [TestCase("rules")]
        public void 真实KernelReplay拒绝五类篡改_重新计算记录摘要也不能绕过绑定或候选核验(string field)
        {
            var frame = RecordReplay();
            var changed = Tamper(frame, field);
            Assert.AreNotEqual(frame.Digest, changed.Digest);
            foreach (string trusted in new[] { frame.Digest, changed.Digest })
            {
                var host = new HostPort(); var kernel = new ActionKernelAdapter(new DsKernelStatePort(host.Capture));
                kernel.Register(KernelActionTransaction.TaskKind, new ActionTaskHandler());
                var request = kernel.Begin(frame.RequestId, KernelActionTransaction.TaskKind, ActionPromptCodec.Freeze(Packet(host.World)));
                string before = kernel.Capture();
                var prepared = kernel.PrepareReplayAsync(request, changed, trusted).GetAwaiter().GetResult();
                Assert.AreEqual("replay_mismatch", prepared.Receipt.Code, field);
                var receipt = kernel.Commit(prepared, null);
                Assert.AreEqual("replay_mismatch", receipt.Code); Assert.AreEqual(before, kernel.Capture());
                Assert.AreEqual(0, host.Applies); Assert.AreEqual(0, host.ExternalWrites);
            }
        }

        static ReplayFrame RecordReplay()
        {
            var host = new HostPort(); var transaction = new KernelActionTransaction(new ActionKernelAdapter(new DsKernelStatePort(host.Capture)));
            var provider = new Provider(Buy);
            Prepare(transaction, transaction.Begin("replay-action", Packet(host.World)), provider).GetAwaiter().GetResult();
            Assert.AreEqual(1, provider.Calls);
            return transaction.PrivateRecordList.Single();
        }

        [Test]
        public void 私有录制可信摘要独立篡改必须被拒绝()
        {
            var frame = RecordReplay();
            var host = new HostPort();
            var kernel = new ActionKernelAdapter(new DsKernelStatePort(host.Capture));
            kernel.Register(KernelActionTransaction.TaskKind, new ActionTaskHandler());
            var request = kernel.Begin(frame.RequestId, KernelActionTransaction.TaskKind, ActionPromptCodec.Freeze(Packet(host.World)));
            string before = kernel.Capture();
            var prepared = kernel.PrepareReplayAsync(request, frame, Protocol.Hash("untrusted-anchor")).GetAwaiter().GetResult();
            Assert.AreEqual("replay_mismatch", prepared.Receipt.Code);
            Assert.AreEqual("replay_mismatch", kernel.Commit(prepared, null).Code);
            Assert.AreEqual(before, kernel.Capture());
            Assert.AreEqual(0, host.Applies);
        }

        static ReplayFrame Tamper(ReplayFrame frame, string field)
        {
            string raw = field == "raw" ? Unsupported : frame.Raw;
            return new ReplayFrame(frame.Format, frame.KernelVersion,
                field == "rules" ? frame.HostRulesVersion + "-changed" : frame.HostRulesVersion, frame.RequestId, frame.Kind,
                field == "prompt" ? Protocol.Hash("changed-prompt") : frame.PromptFingerprint,
                field == "before" ? Protocol.Hash("changed-before") : frame.BeforeDigest,
                field == "after" ? Protocol.Hash("changed-after") : frame.AfterDigest,
                raw, Protocol.Hash(raw), frame.CandidateIdentity, frame.RequiresConfirmation);
        }

        static DsExecutionPlan Plan(IDsActionPreparation preparation) => preparation.Plan;

        static async Task<DsActionPreparation> Prepare(KernelActionTransaction transaction, IDsActionRequest request,
            IAiProvider provider, CancellationToken cancellation = default) =>
            (DsActionPreparation)await transaction.PrepareAsync(request, provider, cancellation);

        sealed class HostPort : IStatePort
        {
            readonly DragonsleepSession _session = new DragonsleepSession(new RuleProvider());
            readonly List<DsActionWrite> _writes = new List<DsActionWrite>();
            internal DsWorld World => _session.World;
            internal int Spent => _session.PhaseBudget - _session.TimeLeftInPhase;
            internal int Applies => _writes.Count(write => write.Applied);
            internal int ExternalWrites;
            internal bool ReadForbidden;
            internal DsWorldSnapshot Capture()
            {
                if (ReadForbidden) throw new InvalidOperationException("prepare_read_live");
                return _session.CaptureState();
            }
            public string Read()
            {
                return Capture().Json;
            }
            public void Write(string state)
            {
                _session.RestoreState(DsWorldSnapshot.Parse(state)); ExternalWrites++;
            }
            internal DsWorld WorldClone() => DsWorldSnapshot.Parse(Read()).Restore();
            internal DsSceneRuleWrite NewSceneWrite(DsWorld candidate) =>
                (DsSceneRuleWrite)typeof(DsSceneRuleWrite).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(DsWorld), typeof(DsWorld) }, null).Invoke(new object[] { candidate, World });
            internal DsDayPhaseWrite NewDayWrite(DsWorld candidate, int spent) =>
                (DsDayPhaseWrite)typeof(DsDayPhaseWrite).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(DragonsleepSession), typeof(DsWorld), typeof(int) }, null)
                    .Invoke(new object[] { _session, candidate, spent });
            internal DsActionWrite NewWrite(DsExecutionPlan plan)
            {
                Action<DsWorld, int> apply = (candidate, spent) =>
                {
                    typeof(DragonsleepSession).GetMethod("ApplyActionCandidate", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(_session, new object[] { candidate, spent });
                };
                var write = (DsActionWrite)typeof(DsActionWrite).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(DsExecutionPlan), typeof(Action<DsWorld, int>) }, null).Invoke(new object[] { plan, apply });
                _writes.Add(write);
                return write;
            }
        }

        sealed class Provider : IAiProvider
        {
            readonly ProviderResult _result;
            internal int Calls;
            internal PromptPacket Packet;
            internal bool Yield;
            internal Action OnCall;
            public string Name => "offline-action-test";
            public bool IsSimulated => true;
            internal Provider(string raw) : this(new ProviderResult
            { ProviderName = "offline-action-test", RawText = raw, InputTokens = 3, OutputTokens = 2, Simulated = true }) { }
            internal Provider(ProviderResult result) { _result = result; }
            public async Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken)
            {
                Calls++; Packet = packet; OnCall?.Invoke();
                if (Yield) await Task.Yield();
                return _result;
            }
        }

        // 仍由真实 Kernel 准备，只在提供方入口模拟跨边界损坏的 FrozenPayload。
        sealed class PayloadChangingKernel : IPieGameActionKernel
        {
            readonly ActionKernelAdapter _kernel;
            readonly bool _changeBinding;
            internal PayloadChangingKernel(IStatePort port, bool changeBinding)
            { _kernel = new ActionKernelAdapter(port); _changeBinding = changeBinding; }
            public void Register(string kind, ITaskHandler handler) => _kernel.Register(kind, handler);
            public Request Begin(string id, string kind, FrozenPayload payload) => _kernel.Begin(id, kind, payload);
            public Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default) =>
                _kernel.PrepareAsync(request, new PayloadChangingProvider(provider, _changeBinding), cancellation);
            public Receipt Commit(Preparation preparation, DsActionWrite write, CancellationToken cancellation = default) =>
                _kernel.Commit(preparation, write, cancellation);
        }

        sealed class PayloadChangingProvider : IProvider
        {
            readonly IProvider _provider;
            readonly bool _changeBinding;
            internal PayloadChangingProvider(IProvider provider, bool changeBinding)
            { _provider = provider; _changeBinding = changeBinding; }
            public Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
            {
                var bytes = payload.Bytes;
                if (!_changeBinding) bytes[0] ^= 0x7f;
                var changed = new FrozenPayload(_changeBinding ? payload.Binding + "-changed" : payload.Binding, bytes);
                return _provider.CompleteAsync(changed, cancellationToken);
            }
        }
    }
}
