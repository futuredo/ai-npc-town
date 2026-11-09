using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PieGame.AI.Kernel
{
    public sealed class Kernel
    {
        private readonly object _gate = new object();
        private readonly IStatePort _port;
        private readonly Dictionary<string, ITaskHandler> _tasks = new Dictionary<string, ITaskHandler>(StringComparer.Ordinal);
        private readonly Dictionary<string, Request> _requests = new Dictionary<string, Request>(StringComparer.Ordinal);
        private readonly Dictionary<string, Receipt> _committed = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _externalIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _candidateIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Preparation> _confirmations = new Dictionary<string, Preparation>(StringComparer.Ordinal);
        private bool _inPort;
        private long _generation;
        public event Action<Receipt> Completed;

        public Kernel(IStatePort port) { _port = port ?? throw new ArgumentNullException(nameof(port)); }

        public void Register(string kind, ITaskHandler handler)
        {
            lock (_gate)
            {
                CheckReentry();
                if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException(nameof(kind));
                _tasks.Add(kind, handler ?? throw new ArgumentNullException(nameof(handler)));
            }
        }

        public Request Begin(string id, string kind, FrozenPayload payload)
        {
            lock (_gate)
            {
                CheckReentry();
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(nameof(id));
                if (_requests.ContainsKey(id) || _committed.ContainsKey(id)) throw new InvalidOperationException("request_conflict");
                if (!_tasks.TryGetValue(kind, out var handler)) throw new InvalidOperationException("task_unknown");
                var request = new Request(this, id, _generation, kind, payload ?? throw new ArgumentNullException(nameof(payload)), Read(), handler);
                _requests.Add(id, request);
                return request;
            }
        }

        public string Digest
        {
            get { lock (_gate) { CheckReentry(); return Protocol.Hash(Read()); } }
        }

        public string Capture()
        {
            lock (_gate) { CheckReentry(); return Read(); }
        }

        public StateStamp CaptureState()
        {
            lock (_gate) { CheckReentry(); return new StateStamp(this, _generation, Read()); }
        }

        public void Reset(string state)
        {
            lock (_gate)
            {
                CheckReentry();
                Protocol.Hash(state ?? throw new ArgumentNullException(nameof(state)));
                Write(state);
                _generation++;
                _requests.Clear();
                _committed.Clear();
                _externalIdentities.Clear();
                _candidateIdentities.Clear();
                _confirmations.Clear();
            }
        }

        public async Task<Preparation> PrepareAsync(Request request, IProvider provider, CancellationToken cancellation = default)
        {
            if (request == null || !ReferenceEquals(request.Owner, this)) throw new ArgumentException(nameof(request));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (cancellation.IsCancellationRequested) return Failed(request, "cancelled", cancellation);
            ProviderReply reply;
            try { reply = await provider.CompleteAsync(request.Payload, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Failed(request, "cancelled", cancellation); }
            catch (Exception) { return Failed(request, "provider_error", cancellation); }
            if (cancellation.IsCancellationRequested) return Failed(request, "late_after_cancel", cancellation);
            try
            {
                if (reply == null) return Failed(request, "provider_error", cancellation);
                var candidate = request.Handler.Prepare(request.Before, reply.Raw);
                if (candidate == null) return Failed(request, "host_rejected", cancellation);
                return new Preparation(request, candidate, reply, Make(request, "ready", request.BeforeDigest), cancellation);
            }
            catch (FormatException) { return Failed(request, "parse_failed", cancellation); }
            catch (HostRejection) { return Failed(request, "host_rejected", cancellation); }
        }

        /// <summary>Returns an opaque capability for this exact preparation, or null if it is no longer eligible.</summary>
        public string IssueConfirmation(Preparation preparation)
        {
            if (preparation == null) throw new ArgumentNullException(nameof(preparation));
            lock (_gate)
            {
                CheckReentry();
                var request = preparation.Request;
                if (!ReferenceEquals(request.Owner, this)) throw new ArgumentException(nameof(preparation));
                if (!preparation.RequiresConfirmation || preparation.Cancellation.IsCancellationRequested ||
                    request.Generation != _generation || _committed.ContainsKey(request.Id) || Protocol.Hash(Read()) != request.BeforeDigest)
                    return null;
                string token = Guid.NewGuid().ToString("N");
                _confirmations.Add(token, preparation);
                return token;
            }
        }

        public Receipt Commit(Preparation preparation, CancellationToken cancellation = default) =>
            CommitCore(preparation, null, false, cancellation);

        public Receipt Confirm(Preparation preparation, string token, CancellationToken cancellation = default) =>
            CommitCore(preparation, token, true, cancellation);

        private Receipt CommitCore(Preparation preparation, string token, bool confirming, CancellationToken cancellation)
        {
            if (preparation == null) throw new ArgumentNullException(nameof(preparation));
            Receipt receipt;
            lock (_gate)
            {
                // Cancellation is the first check under the commit lock. A completed write is never reported as cancelled.
                bool cancelled = cancellation.IsCancellationRequested || preparation.Cancellation.IsCancellationRequested;
                CheckReentry();
                var request = preparation.Request;
                if (!ReferenceEquals(request.Owner, this)) throw new ArgumentException(nameof(preparation));
                if (confirming && cancelled) receipt = Make(request, "cancelled", request.BeforeDigest);
                else if (confirming && (token == null || !_confirmations.TryGetValue(token, out var granted) ||
                    !ReferenceEquals(granted, preparation)))
                    receipt = Make(request, "confirmation_invalid", request.BeforeDigest);
                else if (_committed.TryGetValue(request.Id, out var prior) && request.Generation == _generation)
                    receipt = new Receipt(request.Id, preparation.Candidate != null &&
                        _candidateIdentities[request.Id] == preparation.Candidate.Identity ? "duplicate" : "request_conflict",
                        prior.BeforeDigest, prior.AfterDigest, first: cancellation, second: preparation.Cancellation, prior: prior);
                else if (cancelled)
                    receipt = Make(request, preparation.Receipt.Code == "late_after_cancel" ? "late_after_cancel" : "cancelled", request.BeforeDigest);
                else if (preparation.Candidate == null) receipt = preparation.Receipt;
                else if (request.Generation != _generation) receipt = Make(request, "stale_session", request.BeforeDigest);
                else if (Protocol.Hash(Read()) != request.BeforeDigest) receipt = Make(request, "stale_state", request.BeforeDigest);
                else if (preparation.RequiresConfirmation && !confirming) receipt = Make(request, "confirmation_required", request.BeforeDigest);
                else
                {
                    Write(preparation.Candidate.State);
                    _confirmations.Clear();
                    receipt = new Receipt(request.Id, "committed", request.BeforeDigest, preparation.Candidate.AfterDigest,
                        first: cancellation, second: preparation.Cancellation);
                    _committed.Add(request.Id, receipt);
                    _candidateIdentities.Add(request.Id, preparation.Candidate.Identity);
                }
            }
            Completed?.Invoke(receipt);
            return receipt;
        }

        /// <summary>External events use the same compare/write/deduplicate lock as provider candidates.</summary>
        public Receipt Transact(string id, StateStamp expected, string after, CancellationToken cancellation = default)
        {
            Receipt receipt;
            lock (_gate)
            {
                bool cancelled = cancellation.IsCancellationRequested;
                CheckReentry();
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(nameof(id));
                if (expected == null || !ReferenceEquals(expected.Owner, this)) throw new ArgumentException(nameof(expected));
                string expectedDigest = expected.Digest;
                if (cancelled) receipt = new Receipt(id, "cancelled", expectedDigest, expectedDigest);
                else if (expected.Generation != _generation) receipt = new Receipt(id, "stale_session", expectedDigest, expectedDigest);
                else if (_committed.TryGetValue(id, out var prior))
                    receipt = new Receipt(id, _externalIdentities.TryGetValue(id, out var identity) &&
                        identity == Protocol.Hash(expectedDigest + ":" + after) ? "duplicate" : "request_conflict",
                        prior.BeforeDigest, prior.AfterDigest, first: cancellation, prior: prior);
                else if (_requests.ContainsKey(id)) receipt = new Receipt(id, "request_conflict", expectedDigest, expectedDigest);
                else if (Protocol.Hash(Read()) != expectedDigest) receipt = new Receipt(id, "stale_state", expectedDigest, expectedDigest);
                else
                {
                    string afterDigest = Protocol.Hash(after ?? throw new ArgumentNullException(nameof(after)));
                    string externalIdentity = Protocol.Hash(expectedDigest + ":" + after);
                    Write(after);
                    _confirmations.Clear();
                    receipt = new Receipt(id, "committed", expectedDigest, afterDigest, first: cancellation);
                    _committed.Add(id, receipt);
                    _externalIdentities.Add(id, externalIdentity);
                }
            }
            Completed?.Invoke(receipt);
            return receipt;
        }

        private static Receipt Make(Request request, string code, string after) => new Receipt(request.Id, code, request.BeforeDigest, after);
        private static Preparation Failed(Request request, string code, CancellationToken cancellation) =>
            new Preparation(request, null, null, Make(request, code, request.BeforeDigest), cancellation);

        private void CheckReentry() { if (_inPort) throw new InvalidOperationException("port_reentry"); }
        private string Read()
        {
            _inPort = true;
            try { return _port.Read() ?? throw new InvalidOperationException("state_null"); }
            finally { _inPort = false; }
        }
        private void Write(string state)
        {
            _inPort = true;
            try { _port.Write(state); }
            finally { _inPort = false; }
        }
    }
}
