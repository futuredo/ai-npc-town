using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PieGame.AI.Kernel.Tests
{
    public sealed class ReplayTests
    {
        private static async Task<ReplayFrame> Record(string raw = ScriptProvider.Repair)
        {
            var kernel = ToyHost.Create(new WarehousePort());
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider { Raw = raw });
            var frame = ReplayFrame.Capture(prepared);
            if (prepared.RequiresConfirmation) kernel.Confirm(prepared, kernel.IssueConfirmation(prepared));
            else kernel.Commit(prepared);
            Assert.That(kernel.Digest, Is.EqualTo(frame.AfterDigest));
            return frame;
        }

        private static ReplayFrame Change(ReplayFrame frame, string field, string value)
        {
            string Pick(string name, string original) => field == name ? value : original;
            return new ReplayFrame(Pick("format", frame.Format), Pick("kernel", frame.KernelVersion), Pick("rules", frame.HostRulesVersion),
                Pick("request", frame.RequestId), Pick("kind", frame.Kind), Pick("prompt", frame.PromptFingerprint),
                Pick("before", frame.BeforeDigest), Pick("after", frame.AfterDigest), Pick("raw", frame.Raw), Pick("raw_digest", frame.RawDigest),
                Pick("identity", frame.CandidateIdentity), field == "confirmation" ? !frame.RequiresConfirmation : frame.RequiresConfirmation);
        }

        private sealed class CountedHandler : ITaskHandler
        {
            public int Calls;
            public Func<string, string, Candidate> PrepareCandidate = new RepairTask().Prepare;
            public Candidate Prepare(string before, string raw) { Calls++; return PrepareCandidate(before, raw); }
        }

        [Test]
        public async Task StrictReplayReproducesExactBeforeAndAfterAndOriginalText()
        {
            const string raw = "{ \"minutes\":17, \"cost\":20, \"parts\":2 }";
            var recorded = await Record(raw);
            var frame = JsonSerializer.Deserialize<ReplayFrame>(JsonSerializer.Serialize(recorded));
            Assert.That(frame.Digest, Is.EqualTo(recorded.Digest));
            var port = new WarehousePort();
            var kernel = new Kernel(port, ToyHost.Rules);
            string observed = null;
            var handler = new CountedHandler { PrepareCandidate = (before, text) => { observed = text; return new RepairTask().Prepare(before, text); } };
            kernel.Register("repair", handler);
            var prepared = await kernel.PrepareReplayAsync(ToyHost.Begin(kernel), frame, frame.Digest);
            Assert.That(prepared.Receipt.Code, Is.EqualTo("ready"));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(observed, Is.EqualTo(raw));
            var receipt = kernel.Commit(prepared);
            Assert.That(receipt.Code, Is.EqualTo("committed"));
            Assert.That(receipt.BeforeDigest, Is.EqualTo(frame.BeforeDigest));
            Assert.That(receipt.AfterDigest, Is.EqualTo(frame.AfterDigest));
            Assert.That(kernel.Digest, Is.EqualTo(frame.AfterDigest));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [TestCase("format", null)]
        [TestCase("format", "")]
        [TestCase("format", "PIEGAME-REPLAY-1")]
        [TestCase("format", "piegame-replay-1-extra")]
        [TestCase("kernel", null)]
        [TestCase("kernel", "")]
        [TestCase("kernel", "PIEGAME-KERNEL-1")]
        [TestCase("kernel", "piegame-kernel-1-extra")]
        [TestCase("rules", null)]
        [TestCase("rules", "")]
        [TestCase("rules", "WAREHOUSE-1")]
        [TestCase("rules", "warehouse-1-extra")]
        [TestCase("request", "r2")]
        [TestCase("kind", "other")]
        [TestCase("prompt", "wrong")]
        [TestCase("before", "wrong")]
        [TestCase("raw", "{ \"parts\":2,\"minutes\":17,\"cost\":20}")]
        [TestCase("raw_digest", "wrong")]
        public async Task MetadataAndRawMismatchRejectBeforeHostExecution(string field, string value)
        {
            var frame = Change(await Record(), field, value);
            var port = new WarehousePort();
            var kernel = new Kernel(port, ToyHost.Rules);
            var handler = new CountedHandler();
            kernel.Register("repair", handler);
            string before = kernel.Capture();
            var prepared = await kernel.PrepareReplayAsync(ToyHost.Begin(kernel), frame, frame.Digest);
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("replay_mismatch"));
            Assert.That(kernel.IssueConfirmation(prepared), Is.Null);
            Assert.That(handler.Calls, Is.Zero);
            Assert.That(port.Writes, Is.Zero);
            Assert.That(kernel.Capture(), Is.EqualTo(before));
        }

        [TestCase("after")]
        [TestCase("identity")]
        [TestCase("confirmation")]
        public async Task CandidateMismatchIsRejectedWithoutExposingCommittableCandidate(string field)
        {
            var frame = Change(await Record(), field, "wrong");
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            string before = kernel.Capture();
            var prepared = await kernel.PrepareReplayAsync(ToyHost.Begin(kernel), frame, frame.Digest);
            Assert.That(prepared.Receipt.Code, Is.EqualTo("replay_mismatch"));
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("replay_mismatch"));
            Assert.That(kernel.IssueConfirmation(prepared), Is.Null);
            Assert.That(kernel.Capture(), Is.EqualTo(before));
            Assert.That(port.Writes, Is.Zero);
        }

        [Test]
        public async Task IndependentTrustedDigestRejectsFullyRecomputedTamperedRecording()
        {
            var original = await Record();
            var altered = Change(original, "raw", original.Raw + " ");
            altered = Change(altered, "raw_digest", Protocol.Hash(altered.Raw));
            Assert.That(altered.Digest, Is.Not.EqualTo(original.Digest));
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await kernel.PrepareReplayAsync(ToyHost.Begin(kernel), altered, original.Digest);
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("replay_mismatch"));
            Assert.That(port.Writes, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CurrentFrozenBytesAndBindingMustMatch(bool changeBinding)
        {
            var frame = await Record();
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var payload = ToyHost.Freeze(kernel.Capture());
            var altered = new FrozenPayload(changeBinding ? "other-dispatcher" : payload.Binding,
                changeBinding ? payload.Bytes : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload.Bytes) + " "));
            var request = kernel.Begin("r1", "repair", altered);
            Assert.That(kernel.Commit(await kernel.PrepareReplayAsync(request, frame, frame.Digest)).Code, Is.EqualTo("replay_mismatch"));
            Assert.That(port.Writes, Is.Zero);
        }

        [Test]
        public async Task PrivateStateChangeAndCurrentHostVersionAreIndependentlyChecked()
        {
            var frame = await Record();
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            var stamp = kernel.CaptureState();
            var state = WarehouseState.Parse(stamp.State);
            state.KeeperSecret = "changed-private-only";
            kernel.Transact("event", stamp, state.Json());
            Assert.That(kernel.Commit(await kernel.PrepareReplayAsync(request, frame, frame.Digest)).Code, Is.EqualTo("replay_mismatch"));
            Assert.That(port.Writes, Is.EqualTo(1));
            var upgraded = new Kernel(new WarehousePort(), "warehouse-2");
            upgraded.Register("repair", new RepairTask());
            Assert.That(upgraded.Commit(await upgraded.PrepareReplayAsync(ToyHost.Begin(upgraded), frame, frame.Digest)).Code,
                Is.EqualTo("replay_mismatch"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task NormalCommitStillRejectsExternalChangeOrResetAfterReplayPreparation(bool reset)
        {
            var frame = await Record();
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await kernel.PrepareReplayAsync(ToyHost.Begin(kernel), frame, frame.Digest);
            if (reset) kernel.Reset(kernel.Capture());
            else
            {
                var stamp = kernel.CaptureState();
                var state = WarehouseState.Parse(stamp.State);
                state.Minute++;
                kernel.Transact("event", stamp, state.Json());
            }
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo(reset ? "stale_session" : "stale_state"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task ReplayRequiresFreshConfirmationNotAuthorizationFromAnotherCandidate()
        {
            var frame = await Record("{\"parts\":2,\"minutes\":17,\"cost\":80}");
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            var original = await kernel.PrepareAsync(request, new ScriptProvider { Raw = frame.Raw });
            string oldToken = kernel.IssueConfirmation(original);
            var replayed = await kernel.PrepareReplayAsync(request, frame, frame.Digest);
            Assert.That(kernel.Commit(replayed).Code, Is.EqualTo("confirmation_required"));
            Assert.That(kernel.Confirm(replayed, oldToken).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(kernel.Confirm(replayed, kernel.IssueConfirmation(replayed)).Code, Is.EqualTo("committed"));
            Assert.That(kernel.Digest, Is.EqualTo(frame.AfterDigest));
        }

        [Test]
        public async Task NoOpStillValidatesRawVersionsAndConfirmation()
        {
            var port = new WarehousePort();
            var kernel = new Kernel(port, ToyHost.Rules);
            var handler = new CountedHandler { PrepareCandidate = (before, raw) => new Candidate("noop", before) };
            kernel.Register("repair", handler);
            var request = ToyHost.Begin(kernel);
            var prepared = await kernel.PrepareAsync(request, new ScriptProvider { Raw = "private-RAW-secret" });
            var frame = ReplayFrame.Capture(prepared);
            Assert.That(frame.BeforeDigest, Is.EqualTo(frame.AfterDigest));
            foreach (string field in new[] { "raw", "kernel", "confirmation" })
            {
                var altered = Change(frame, field, "wrong");
                Assert.That(kernel.Commit(await kernel.PrepareReplayAsync(request, altered, altered.Digest)).Code, Is.EqualTo("replay_mismatch"));
            }
            var verified = await kernel.PrepareReplayAsync(request, frame, frame.Digest);
            var publicTrace = new List<Receipt>();
            kernel.Completed += publicTrace.Add;
            Assert.That(kernel.Commit(verified).Code, Is.EqualTo("committed"));
            Assert.That(JsonSerializer.Serialize(publicTrace), Does.Not.Contain(frame.Raw).And.Not.Contain("PRIVATE"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task ReplayCancellationAndMalformedOrRejectedOutputNeverWriteOrPublishPrivateData()
        {
            var frame = await Record();
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var result = await kernel.PrepareReplayAsync(request, frame, frame.Digest, cancelled.Token);
            Assert.That(kernel.Commit(result).Code, Is.EqualTo("cancelled"));
            var publicTrace = new List<Receipt>();
            kernel.Completed += publicTrace.Add;
            foreach (string raw in new[] { "bad JSON", "{\"parts\":200,\"minutes\":17,\"cost\":20}" })
            {
                var altered = Change(frame, "raw", raw);
                altered = Change(altered, "raw_digest", Protocol.Hash(raw));
                var prepared = await kernel.PrepareReplayAsync(request, altered, altered.Digest);
                Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("replay_mismatch"));
            }
            Assert.That(JsonSerializer.Serialize(publicTrace), Does.Not.Contain("PRIVATE").And.Not.Contain("parts").And.Not.Contain("raw"));
            Assert.That(port.Writes, Is.Zero);
            Assert.Throws<ArgumentException>(() => ReplayFrame.Capture(result));
        }
    }
}
