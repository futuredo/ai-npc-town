using System;
using System.Runtime.Serialization;

namespace Town.Core.Moonlight
{
    public enum MoonDecision { Clarify, Decline, Propose }
    public enum LightMethod { FollowBeacon, SplitBeam }
    public enum MoonTaskState { None, AwaitingConfirmation, Travelling, Completed, Failed, Cancelled }

    /// <summary>仅供离线候选执行实验；不是获准发送到在线模型的新视图。</summary>
    public sealed class MoonRequest
    {
        public string Id { get; }
        public int Generation { get; }
        public int Version { get; }
        public string PlayerInput { get; }
        public string ActorId => "finn";
        public string[] EvidenceIds => new[] { "finn:beacon_seen", "finn:split_runes_seen" };
        internal MoonRequest(string id, int generation, int version, string input)
        { Id = id; Generation = generation; Version = version; PlayerInput = input; }
    }

    /// <summary>模型/人工录制只给候选，不能自行标记移动完成或拥有别人的资源。</summary>
    [DataContract]
    public sealed class MoonCandidate
    {
        [DataMember] public MoonDecision Decision;
        [DataMember] public LightMethod Method;
        [DataMember] public string Summary;
        [DataMember] public string[] Evidence = Array.Empty<string>();
        [DataMember] public bool ActorConsents;
        [DataMember] public bool RequiresContinuousLight;
        [DataMember] public int Energy;
        public MoonCandidate Copy() => new MoonCandidate { Decision = Decision, Method = Method,
            Summary = Summary, Evidence = (string[])Evidence?.Clone(), ActorConsents = ActorConsents,
            RequiresContinuousLight = RequiresContinuousLight, Energy = Energy };
    }

    public interface IMoonCandidateSource
    {
        string Mode { get; }
        MoonCandidate Read(MoonRequest request);
    }

    /// <summary>显式选择的人工样本，不按关键词猜测任意文本，不冒充真实 AI 理解。</summary>
    public sealed class RecordedMoonFixture : IMoonCandidateSource
    {
        private readonly MoonCandidate _candidate;
        public string Mode => "offline_fixture";
        public RecordedMoonFixture(MoonCandidate candidate) => _candidate = candidate?.Copy() ?? throw new ArgumentNullException(nameof(candidate));
        public MoonCandidate Read(MoonRequest request) => _candidate.Copy();
        public static RecordedMoonFixture Example(string id)
        {
            if (id == "clarify") return new RecordedMoonFixture(new MoonCandidate { Decision = MoonDecision.Clarify, Summary = "你希望我跟着灯走，还是先点亮上路的两枚符文？" });
            if (id == "decline") return new RecordedMoonFixture(new MoonCandidate { Decision = MoonDecision.Decline, Summary = "让我闭眼跳过断鳞不安全，我不愿意。" });
            bool split = id == "split";
            if (!split && id != "follow") throw new ArgumentException("未知人工样本。", nameof(id));
            return new RecordedMoonFixture(new MoonCandidate { Decision = MoonDecision.Propose,
                Method = split ? LightMethod.SplitBeam : LightMethod.FollowBeacon,
                Summary = split ? "两束月光持续照住符文，我愿从上路过去；光灭就停。" : "让小月亮沿下路引路，我会跟着；失去光就停。",
                Evidence = new[] { split ? "finn:split_runes_seen" : "finn:beacon_seen" },
                ActorConsents = true, RequiresContinuousLight = true, Energy = 2 });
        }
    }

    public readonly struct MoonResult
    {
        public bool Success { get; }
        public string Code { get; }
        public string Message { get; }
        internal MoonResult(bool success, string code, string message) { Success = success; Code = code; Message = message; }
    }
}
