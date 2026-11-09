using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PieGame.AI.Kernel
{
    /// <summary>Private recording metadata and exact provider text. Never publish as a public receipt or trace.</summary>
    public sealed class ReplayFrame
    {
        public const string CurrentFormat = "piegame-replay-1";
        public string Format { get; }
        public string KernelVersion { get; }
        public string HostRulesVersion { get; }
        public string RequestId { get; }
        public string Kind { get; }
        public string PromptFingerprint { get; }
        public string BeforeDigest { get; }
        public string AfterDigest { get; }
        public string Raw { get; }
        public string RawDigest { get; }
        public string CandidateIdentity { get; }
        public bool RequiresConfirmation { get; }
        public string Digest { get; }

        public ReplayFrame(string format, string kernelVersion, string hostRulesVersion, string requestId, string kind,
            string promptFingerprint, string beforeDigest, string afterDigest, string raw, string rawDigest,
            string candidateIdentity, bool requiresConfirmation)
        {
            Format = format; KernelVersion = kernelVersion; HostRulesVersion = hostRulesVersion; RequestId = requestId; Kind = kind;
            PromptFingerprint = promptFingerprint; BeforeDigest = beforeDigest; AfterDigest = afterDigest; Raw = raw; RawDigest = rawDigest;
            CandidateIdentity = candidateIdentity; RequiresConfirmation = requiresConfirmation;
            Digest = Protocol.Hash(Pack(format, kernelVersion, hostRulesVersion, requestId, kind, promptFingerprint,
                beforeDigest, afterDigest, raw, rawDigest, candidateIdentity, requiresConfirmation ? "1" : "0"));
        }

        public static ReplayFrame Capture(Preparation preparation)
        {
            if (preparation == null || preparation.Candidate == null || preparation.Reply == null || preparation.Receipt.Code != "ready")
                throw new ArgumentException(nameof(preparation));
            var request = preparation.Request;
            var candidate = preparation.Candidate;
            return new ReplayFrame(CurrentFormat, Protocol.Version, request.HostRulesVersion, request.Id, request.Kind,
                request.Payload.Fingerprint, request.BeforeDigest, candidate.AfterDigest, preparation.Reply.Raw,
                Protocol.Hash(preparation.Reply.Raw), candidate.Identity, candidate.RequiresConfirmation);
        }

        internal bool Matches(Request request, string trustedDigest) =>
            Format == CurrentFormat && KernelVersion == Protocol.Version && HostRulesVersion == request.HostRulesVersion &&
            RequestId == request.Id && Kind == request.Kind && PromptFingerprint == request.Payload.Fingerprint &&
            BeforeDigest == request.BeforeDigest && Raw != null && RawDigest == Protocol.Hash(Raw) && Digest == trustedDigest;

        internal bool Matches(Preparation preparation) => preparation.Candidate != null &&
            preparation.Reply?.Raw == Raw && preparation.Candidate.AfterDigest == AfterDigest &&
            preparation.Candidate.Identity == CandidateIdentity && preparation.RequiresConfirmation == RequiresConfirmation;

        private static string Pack(params string[] fields)
        {
            var builder = new StringBuilder();
            foreach (string field in fields)
                builder.Append((field?.Length ?? -1).ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
            return builder.ToString();
        }
    }

    /// <summary>No fallback or network path. Only the exact recorded text is returned.</summary>
    internal sealed class RecordedProvider : IProvider
    {
        private readonly ReplayFrame _frame;
        internal RecordedProvider(ReplayFrame frame) { _frame = frame; }
        public Task<ProviderReply> CompleteAsync(FrozenPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Fingerprint != _frame.PromptFingerprint) throw new InvalidOperationException("replay_mismatch");
            return Task.FromResult(new ProviderReply(_frame.Raw));
        }
    }
}
