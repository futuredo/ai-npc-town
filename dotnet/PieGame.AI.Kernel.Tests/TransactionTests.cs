using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PieGame.AI.Kernel;

namespace PieGame.AI.Kernel.Tests
{
    public class TransactionTests
    {
        [Test]
        public async Task RepairCommitsQuantitiesAndContinuousMinutesExactlyOnce()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            var prepared = await kernel.PrepareAsync(request, new ScriptProvider());
            Assert.AreEqual("committed", kernel.Commit(prepared).Code);
            Assert.AreEqual("duplicate", kernel.Commit(prepared).Code);
            var state = WarehouseState.Parse(kernel.Capture());
            Assert.AreEqual(8, state.Parts);
            Assert.AreEqual(17, state.Minute);
            Assert.AreEqual(20, state.Spent);
            Assert.AreEqual(1, port.Writes);
            Assert.Throws<InvalidOperationException>(() => ToyHost.Begin(kernel));
        }

        [Test]
        public async Task DifferentCandidateForCommittedRequestIsNotADuplicate()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var request = ToyHost.Begin(kernel);
            var first = await kernel.PrepareAsync(request, new ScriptProvider());
            var second = await kernel.PrepareAsync(request, new ScriptProvider
            { Raw = "{\"parts\":1,\"minutes\":7,\"cost\":10}" });
            Assert.AreEqual("committed", kernel.Commit(first).Code);
            Assert.AreEqual("request_conflict", kernel.Commit(second).Code);
            Assert.AreEqual(1, port.Writes);
        }

        [Test]
        public async Task ExternalWriteDuringProviderWaitMakesCandidateStale()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var provider = new ScriptProvider { Wait = true };
            var waiting = kernel.PrepareAsync(ToyHost.Begin(kernel), provider);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var digest = kernel.CaptureState();
            var state = WarehouseState.Parse(kernel.Capture());
            state.Parts = 1;
            Assert.AreEqual("committed", kernel.Transact("delivery", digest, state.Json()).Code);
            provider.Release.SetResult(true);
            Assert.AreEqual("stale_state", kernel.Commit(await waiting).Code);
            Assert.AreEqual(1, port.Writes);
            Assert.AreEqual(1, WarehouseState.Parse(kernel.Capture()).Parts);
        }

        [Test]
        public async Task ExternalWriteCannotEnterBetweenComparisonAndHostWrite()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider());
            var digest = kernel.CaptureState();
            var externalState = WarehouseState.Parse(kernel.Capture());
            externalState.Parts = 1;
            using var insideWrite = new ManualResetEventSlim();
            using var releaseWrite = new ManualResetEventSlim();
            using var attempted = new ManualResetEventSlim();
            port.BeforeWrite = () =>
            {
                insideWrite.Set();
                if (!releaseWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            };
            var candidate = Task.Run(() => kernel.Commit(prepared));
            Assert.IsTrue(insideWrite.Wait(TimeSpan.FromSeconds(5)));
            var external = Task.Run(() =>
            {
                attempted.Set();
                return kernel.Transact("delivery", digest, externalState.Json());
            });
            try
            {
                Assert.IsTrue(attempted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(external.Wait(100), "The second transaction must remain outside the same lock.");
            }
            finally { releaseWrite.Set(); }
            Assert.AreEqual("committed", (await candidate).Code);
            Assert.AreEqual("stale_state", (await external).Code);
            Assert.AreEqual(1, port.Writes);
            Assert.AreEqual(8, WarehouseState.Parse(kernel.Capture()).Parts);
        }

        [Test]
        public async Task CancelBeforeCommitDoesNotWrite()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            using var cancel = new CancellationTokenSource();
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider(), cancel.Token);
            cancel.Cancel();
            Assert.AreEqual("cancelled", kernel.Commit(prepared).Code);
            Assert.AreEqual(0, port.Writes);
        }

        [TestCase(false, "cancelled")]
        [TestCase(true, "late_after_cancel")]
        public async Task CancellationDuringProviderWait(bool ignore, string expected)
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            using var cancel = new CancellationTokenSource();
            var provider = new ScriptProvider { Wait = true, IgnoreCancellation = ignore };
            var waiting = kernel.PrepareAsync(ToyHost.Begin(kernel), provider, cancel.Token);
            await provider.Entered.Task;
            cancel.Cancel();
            provider.Release.SetResult(true);
            Assert.AreEqual(expected, kernel.Commit(await waiting).Code);
            Assert.AreEqual(0, port.Writes);
        }

        [Test]
        public async Task CancellationAfterHostWriteIsStillCommitted()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            using var cancel = new CancellationTokenSource();
            port.BeforeWrite = () => cancel.Cancel();
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider(), cancel.Token);
            var receipt = kernel.Commit(prepared);
            Assert.AreEqual("committed", receipt.Code);
            Assert.IsTrue(receipt.CancellationAfterCommit);
            Assert.AreEqual(1, port.Writes);
        }

        [Test]
        public async Task CancellationObservedAfterReturnDoesNotRewriteCommitHistory()
        {
            var kernel = ToyHost.Create(new WarehousePort());
            using var cancel = new CancellationTokenSource();
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider(), cancel.Token);
            var receipt = kernel.Commit(prepared);
            Assert.IsFalse(receipt.CancellationAfterCommit);
            cancel.Cancel();
            Assert.AreEqual("committed", receipt.Code);
            Assert.IsTrue(receipt.CancellationAfterCommit);
        }

        [TestCase("not-json", "parse_failed")]
        [TestCase("{\"parts\":100,\"minutes\":1,\"cost\":0}", "host_rejected")]
        [TestCase("{\"parts\":1,\"minutes\":-1,\"cost\":0}", "host_rejected")]
        public async Task InvalidProviderOutputNeverWrites(string raw, string expected)
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider { Raw = raw });
            Assert.AreEqual(expected, kernel.Commit(prepared).Code);
            Assert.AreEqual(0, port.Writes);
        }

        [Test]
        public async Task OccupiedStationRejectsWithoutEffects()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            kernel.Reset(new WarehouseState { StationOccupied = true }.Json());
            int writes = port.Writes;
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider());
            Assert.AreEqual("host_rejected", kernel.Commit(prepared).Code);
            Assert.AreEqual(writes, port.Writes);
        }

        [Test]
        public void ExternalDuplicateConflictAndPortReentryAreExplicit()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var before = kernel.CaptureState();
            string after = new WarehouseState { Parts = 20 }.Json();
            Assert.AreEqual("committed", kernel.Transact("event", before, after).Code);
            Assert.AreEqual("duplicate", kernel.Transact("event", before, after).Code);
            Assert.AreEqual("request_conflict", kernel.Transact("event", before, after + " ").Code);
            Assert.Throws<InvalidOperationException>(() => ToyHost.Begin(kernel, "event"));
            port.BeforeWrite = () => kernel.Transact("nested", kernel.CaptureState(), after);
            Assert.Throws<InvalidOperationException>(() => kernel.Transact("outer", kernel.CaptureState(), after));
            Assert.AreEqual(1, port.Writes);
        }

        [Test]
        public async Task ResetInvalidatesPendingRequestAndMissingUsageIsUnknown()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var reply = await new ScriptProvider().CompleteAsync(ToyHost.Freeze(kernel.Capture()), default);
            Assert.IsNull(reply.EstimatedUsd);
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider());
            kernel.Reset(kernel.Capture());
            Assert.AreEqual("stale_session", kernel.Commit(prepared).Code);
        }

        [Test]
        public void ResetToIdenticalStateInvalidatesPendingAndRepeatedExternalEvents()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var old = kernel.CaptureState();
            Assert.AreEqual("committed", kernel.Transact("done", old, old.State).Code);
            kernel.Reset(old.State);
            int writes = port.Writes;
            Assert.AreEqual("stale_session", kernel.Transact("pending", old, old.State).Code);
            Assert.AreEqual("stale_session", kernel.Transact("done", old, old.State).Code);
            Assert.AreEqual(writes, port.Writes);
            Assert.AreEqual("committed", kernel.Transact("pending", kernel.CaptureState(), old.State).Code);
        }

        [Test]
        public async Task DuplicateReceiptsRetainOriginalCancellationObservation()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            using var candidateCancel = new CancellationTokenSource();
            var prepared = await kernel.PrepareAsync(ToyHost.Begin(kernel), new ScriptProvider(), candidateCancel.Token);
            kernel.Commit(prepared);
            var repeated = kernel.Commit(prepared);
            using var externalCancel = new CancellationTokenSource();
            var state = kernel.CaptureState();
            kernel.Transact("event", state, state.State, externalCancel.Token);
            var repeatedExternal = kernel.Transact("event", state, state.State);
            candidateCancel.Cancel();
            externalCancel.Cancel();
            Assert.IsTrue(repeated.CancellationAfterCommit);
            Assert.IsTrue(repeatedExternal.CancellationAfterCommit);
            Assert.AreEqual(2, port.Writes);
        }

        [Test]
        public void InvalidUnicodeIsRejectedBeforeAnyHostWrite()
        {
            var port = new WarehousePort();
            var kernel = ToyHost.Create(port);
            var before = kernel.CaptureState();
            Assert.Throws<System.Text.EncoderFallbackException>(() => Protocol.Hash("\uD800"));
            Assert.Throws<System.Text.EncoderFallbackException>(() => Protocol.Hash("\uD801"));
            Assert.Throws<System.Text.EncoderFallbackException>(() => kernel.Transact("bad", before, "\uD800"));
            Assert.Throws<System.Text.EncoderFallbackException>(() => kernel.Reset("\uD801"));
            Assert.AreEqual(0, port.Writes);
            Assert.AreEqual(before.State, kernel.Capture());
        }
    }
}
