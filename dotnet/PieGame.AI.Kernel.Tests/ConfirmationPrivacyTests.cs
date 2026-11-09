using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PieGame.AI.Kernel.Tests
{
    public sealed class ConfirmationPrivacyTests
    {
        private const string Expensive = "{\"parts\":2,\"minutes\":17,\"cost\":80}";

        private static Task<Preparation> ExpensivePreparation(Kernel kernel, string id = "r1") =>
            kernel.PrepareAsync(ToyHost.Begin(kernel, id), new ScriptProvider { Raw = Expensive });

        [Test]
        public async Task ConfirmationIsRequiredAndSingleUse()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await ExpensivePreparation(kernel);
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("confirmation_required"));
            Assert.That(port.Writes, Is.Zero);
            string token = kernel.IssueConfirmation(prepared);
            Assert.That(token, Is.Not.Null.And.Not.Empty);
            Assert.That(kernel.Confirm(prepared, token).Code, Is.EqualTo("committed"));
            Assert.That(kernel.Confirm(prepared, token).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("duplicate"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task OldCandidateCannotAuthorizeReplacementEvenUnderSameRequest()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            var first = await kernel.PrepareAsync(request, new ScriptProvider { Raw = Expensive });
            string token = kernel.IssueConfirmation(first);
            var replacement = await kernel.PrepareAsync(request, new ScriptProvider { Raw = Expensive.Replace("17", "18") });
            Assert.That(kernel.Confirm(replacement, token).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(port.Writes, Is.Zero);
            Assert.That(kernel.Confirm(first, token).Code, Is.EqualTo("committed"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task BindingCoversBothProposalIdentityAndAfterDigest(bool changeProposal)
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var handler = new SelectedCandidate { Value = new Candidate("p1", kernel.Capture(), true) };
            kernel.Register("binding", handler);
            var request = kernel.Begin("binding", "binding", ToyHost.Freeze(kernel.Capture()));
            var first = await kernel.PrepareAsync(request, new ScriptProvider());
            string token = kernel.IssueConfirmation(first);
            var state = WarehouseState.Parse(kernel.Capture());
            state.Minute++;
            handler.Value = changeProposal ? new Candidate("p2", kernel.Capture(), true) : new Candidate("p1", state.Json(), true);
            var replacement = await kernel.PrepareAsync(request, new ScriptProvider());
            Assert.That(kernel.Confirm(replacement, token).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(kernel.Confirm(first, token).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        private sealed class SelectedCandidate : ITaskHandler
        {
            public Candidate Value;
            public Candidate Prepare(string before, string raw) => Value;
        }

        [Test]
        public async Task TokenCannotCrossRequestOrKernelOrBeForged()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var first = await ExpensivePreparation(kernel);
            var second = await ExpensivePreparation(kernel, "r2");
            string token = kernel.IssueConfirmation(first);
            Assert.That(kernel.Confirm(second, token).Code, Is.EqualTo("confirmation_invalid"));
            foreach (string fake in new[] { null, "", "forged", Guid.NewGuid().ToString("N"), token.ToUpperInvariant(), " " + token, token + " " })
                Assert.That(kernel.Confirm(first, fake).Code, Is.EqualTo("confirmation_invalid"));
            var other = ToyHost.Create(new WarehousePort());
            var foreign = await ExpensivePreparation(other);
            Assert.That(other.Confirm(foreign, token).Code, Is.EqualTo("confirmation_invalid"));
            Assert.Throws<ArgumentException>(() => other.Confirm(first, token));
            Assert.That(port.Writes, Is.Zero);
        }

        [Test]
        public async Task ResetAndStateChangesInvalidateTokensIncludingChangeBack()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var first = await ExpensivePreparation(kernel);
            string token = kernel.IssueConfirmation(first);
            string original = kernel.Capture();
            var stamp = kernel.CaptureState();
            var changed = WarehouseState.Parse(stamp.State);
            changed.Minute++;
            Assert.That(kernel.Transact("event1", stamp, changed.Json()).Code, Is.EqualTo("committed"));
            Assert.That(kernel.Transact("event2", kernel.CaptureState(), original).Code, Is.EqualTo("committed"));
            Assert.That(kernel.Confirm(first, token).Code, Is.EqualTo("confirmation_invalid"));
            string newToken = kernel.IssueConfirmation(first);
            Assert.That(newToken, Is.Not.Null);
            kernel.Reset(original);
            Assert.That(kernel.Confirm(first, newToken).Code, Is.EqualTo("confirmation_invalid"));
            Assert.That(kernel.IssueConfirmation(first), Is.Null);
            Assert.That(port.Writes, Is.EqualTo(3));
        }

        [Test]
        public async Task CancellationBeforeConfirmationDoesNotWriteButAfterWriteRemainsCommitted()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await ExpensivePreparation(kernel);
            string token = kernel.IssueConfirmation(prepared);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.That(kernel.Confirm(prepared, token, cancelled.Token).Code, Is.EqualTo("cancelled"));
            Assert.That(port.Writes, Is.Zero);
            using var late = new CancellationTokenSource();
            port.BeforeWrite = late.Cancel;
            var receipt = kernel.Confirm(prepared, token, late.Token);
            Assert.That(receipt.Code, Is.EqualTo("committed"));
            Assert.That(receipt.CancellationAfterCommit, Is.True);
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task AllTokensForCandidateAreRevokedByFirstCommitAndConcurrentConfirmationWritesOnce()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await ExpensivePreparation(kernel);
            string first = kernel.IssueConfirmation(prepared);
            string second = kernel.IssueConfirmation(prepared);
            var receipts = await Task.WhenAll(Task.Run(() => kernel.Confirm(prepared, first)), Task.Run(() => kernel.Confirm(prepared, second)));
            Assert.That(Array.ConvertAll(receipts, r => r.Code), Is.EquivalentTo(new[] { "committed", "confirmation_invalid" }));
            Assert.That(kernel.IssueConfirmation(prepared), Is.Null);
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task AtomicPortFailureDoesNotConsumeToken()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await ExpensivePreparation(kernel);
            string token = kernel.IssueConfirmation(prepared);
            string before = kernel.Capture();
            port.BeforeWrite = () => throw new InvalidOperationException("port_failed");
            Assert.Throws<InvalidOperationException>(() => kernel.Confirm(prepared, token));
            Assert.That(kernel.Capture(), Is.EqualTo(before));
            Assert.That(port.Writes, Is.Zero);
            port.BeforeWrite = null;
            Assert.That(kernel.Confirm(prepared, token).Code, Is.EqualTo("committed"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task ConfirmationAndExternalTransactionUseSameAtomicLock()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await ExpensivePreparation(kernel);
            string token = kernel.IssueConfirmation(prepared);
            var stamp = kernel.CaptureState();
            using var insideWrite = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var eventStarted = new ManualResetEventSlim();
            port.BeforeWrite = () => { insideWrite.Set(); Assert.That(release.Wait(5000), Is.True); };
            var confirmation = Task.Run(() => kernel.Confirm(prepared, token));
            Assert.That(insideWrite.Wait(5000), Is.True);
            var external = Task.Run(() => { eventStarted.Set(); return kernel.Transact("event", stamp, stamp.State); });
            try
            {
                Assert.That(eventStarted.Wait(5000), Is.True);
                Assert.That(external.Wait(100), Is.False);
            }
            finally { release.Set(); }
            Assert.That((await confirmation).Code, Is.EqualTo("committed"));
            Assert.That((await external).Code, Is.EqualTo("stale_state"));
            Assert.That(port.Writes, Is.EqualTo(1));
        }

        [Test]
        public async Task PayloadIsCopiedOnInputAndOutputAndRemainsFrozenDuringWorldChanges()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var source = ToyHost.Freeze(kernel.Capture());
            byte[] bytes = source.Bytes;
            byte[] expected = (byte[])bytes.Clone();
            var frozen = new FrozenPayload(source.Binding, bytes);
            string fingerprint = frozen.Fingerprint;
            Array.Fill(bytes, (byte)'X');
            Array.Fill(frozen.Bytes, (byte)'Y');
            var request = kernel.Begin("r1", "repair", frozen);
            var stamp = kernel.CaptureState();
            var state = WarehouseState.Parse(stamp.State);
            state.Parts = 6;
            state.KeeperSecret = "other-PRIVATE-value";
            kernel.Transact("event", stamp, state.Json());
            var provider = new ScriptProvider();
            var prepared = await kernel.PrepareAsync(request, provider);
            Assert.That(provider.Sent, Is.EqualTo(expected));
            Assert.That(frozen.Fingerprint, Is.EqualTo(fingerprint));
            Assert.That(kernel.Commit(prepared).Code, Is.EqualTo("stale_state"));
        }

        [Test]
        public async Task PrivateKnowledgeNeverAppearsInSentPayloadOrPublicReceiptsAndTrace()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            string secret = WarehouseState.Parse(kernel.Capture()).KeeperSecret;
            var publicTrace = new List<Receipt>();
            kernel.Completed += publicTrace.Add;
            var provider = new ScriptProvider();
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), provider);
            var committed = kernel.Commit(prepared);
            var rejected = await kernel.PrepareAsync(ToyHost.Begin(kernel, "bad"),
                new ScriptProvider { Raw = "{\"leaked\":\"" + secret + "\"}" });
            kernel.Commit(rejected);
            string sent = Encoding.UTF8.GetString(provider.Sent);
            string publicJson = JsonSerializer.Serialize(new { committed, preparation = prepared.Receipt, trace = publicTrace });
            Assert.That(sent, Does.Not.Contain(secret).And.Not.Contain("KeeperSecret"));
            Assert.That(publicJson, Does.Not.Contain(secret).And.Not.Contain("KeeperSecret").And.Not.Contain("leaked"));
            Assert.That(WarehouseState.Parse(kernel.Capture()).KeeperSecret, Is.EqualTo(secret));
        }
    }
}
