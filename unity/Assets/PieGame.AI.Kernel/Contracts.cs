using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PieGame.AI.Kernel
{
    public static class Protocol
    {
        public const string Version = "piegame-kernel-1";

        public static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// All reads and writes must use the owning kernel. Write must replace state atomically or throw without effects.
    /// Port callbacks must not reenter the kernel. Direct host mutation is outside the transaction guarantee.
    /// Snapshots must be detached, deterministic values; neither providers nor public receipts receive them.
    /// </summary>
    public interface IStatePort
    {
        string Read();
        void Write(string state);
    }

    public sealed class StateStamp
    {
        internal object Owner { get; }
        public long Generation { get; }
        public string State { get; }
        public string Digest { get; }
        internal StateStamp(object owner, long generation, string state)
        { Owner = owner; Generation = generation; State = state; Digest = Protocol.Hash(state); }
    }

    public sealed class FrozenPayload
    {
        private readonly byte[] _bytes;
        public string Binding { get; }
        public byte[] Bytes => (byte[])_bytes.Clone();
        public string Fingerprint { get; }

        public FrozenPayload(string binding, byte[] bytes)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _bytes = (byte[])(bytes ?? throw new ArgumentNullException(nameof(bytes))).Clone();
            Fingerprint = Protocol.Hash(binding.Length + ":" + binding + Convert.ToBase64String(_bytes));
        }
    }

    public sealed class Usage
    {
        public int Input { get; }
        public int Output { get; }
        public decimal? EstimatedUsd { get; }
        public Usage(int input, int output, decimal? estimatedUsd = null)
        { Input = input; Output = output; EstimatedUsd = estimatedUsd; }
    }

    public sealed class ProviderReply
    {
        public string Raw { get; }
        public Usage Usage { get; }
        public decimal? EstimatedUsd => Usage?.EstimatedUsd;
        public ProviderReply(string raw, Usage usage = null)
        { Raw = raw ?? throw new ArgumentNullException(nameof(raw)); Usage = usage; }
    }

    public interface IProvider
    {
        Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken);
    }

    /// <summary>Host parsing, validation and isolated execution. Must not mutate the live port.</summary>
    public interface ITaskHandler
    {
        Candidate Prepare(string before, string raw);
    }

    public sealed class HostRejection : Exception
    {
        public HostRejection() : base("host_rejected") { }
    }

    public sealed class Candidate
    {
        internal string State { get; }
        public string Identity { get; }
        public string AfterDigest { get; }
        public bool RequiresConfirmation { get; }
        public Candidate(string proposalIdentity, string state, bool requiresConfirmation = false)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            AfterDigest = Protocol.Hash(state);
            Identity = Protocol.Hash((proposalIdentity ?? throw new ArgumentNullException(nameof(proposalIdentity))) + ":" + AfterDigest);
            RequiresConfirmation = requiresConfirmation;
        }
    }

    public sealed class Request
    {
        internal object Owner { get; }
        internal string Before { get; }
        internal ITaskHandler Handler { get; }
        public string Id { get; }
        public long Generation { get; }
        public string Kind { get; }
        public string HostRulesVersion { get; }
        public FrozenPayload Payload { get; }
        public string BeforeDigest { get; }
        internal Request(object owner, string id, long generation, string kind, string hostRulesVersion,
            FrozenPayload payload, string before, ITaskHandler handler)
        {
            Owner = owner; Id = id; Generation = generation; Kind = kind; Payload = payload; Before = before; Handler = handler;
            HostRulesVersion = hostRulesVersion;
            BeforeDigest = Protocol.Hash(before);
        }
    }

    /// <summary>Safe public metadata only. Payload, snapshots and provider output belong in a separate private recording.</summary>
    public sealed class Receipt
    {
        public string RequestId { get; }
        public string Code { get; }
        public string BeforeDigest { get; }
        public string AfterDigest { get; }
        private readonly CancellationToken _first;
        private readonly CancellationToken _second;
        private readonly bool _cancelled;
        private readonly Receipt _prior;
        public bool CancellationAfterCommit => (Code == "committed" || Code == "duplicate") &&
            (_cancelled || _first.IsCancellationRequested || _second.IsCancellationRequested || _prior?.CancellationAfterCommit == true);
        internal Receipt(string id, string code, string before, string after, bool cancellationAfterCommit = false,
            CancellationToken first = default, CancellationToken second = default, Receipt prior = null)
        {
            RequestId = id; Code = code; BeforeDigest = before; AfterDigest = after;
            _cancelled = cancellationAfterCommit; _first = first; _second = second; _prior = prior;
        }
    }

    public sealed class Preparation
    {
        internal Request Request { get; }
        internal Candidate Candidate { get; }
        internal CancellationToken Cancellation { get; }
        internal ProviderReply Reply { get; }
        public Receipt Receipt { get; }
        public bool RequiresConfirmation => Candidate?.RequiresConfirmation == true;
        internal Preparation(Request request, Candidate candidate, ProviderReply reply, Receipt receipt, CancellationToken cancellation)
        { Request = request; Candidate = candidate; Reply = reply; Receipt = receipt; Cancellation = cancellation; }
    }
}
