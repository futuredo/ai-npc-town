using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>一条自足的本机回放帧。冻结世界保存在本地；回放提供方完全不联网。</summary>
    public sealed class DsReplayFrame
    {
        public const string Format = "dragonsleep-replay-v1";
        public string RulesVersion = DsExecutor.RulesVersion;
        public DsWorldSnapshot Before;
        public DsWorldSnapshot After;
        public AiTaskKind Kind;
        public string ActorId, Input, PromptHash, Presented, Failure, CraftDecision, PlanContext;
        public VerdictKind Verdict;
        public ProviderResult Response;

        public static DsReplayFrame Capture(DsWorldSnapshot before, DsWorldSnapshot after, AiTrace trace) => new DsReplayFrame
        {
            Before = before, After = after, Kind = trace.Kind, ActorId = trace.ActorId, Input = trace.PlayerInput,
            PromptHash = Fingerprint(trace.Prompt), Presented = trace.Presented, Failure = trace.FailureCode, Verdict = trace.Verdict.Kind,
            CraftDecision = trace.CraftDecision, PlanContext = trace.PlanContext,
            Response = trace.Provider == null ? null : new ProviderResult { ProviderName = trace.Provider.ProviderName,
                RawText = trace.Provider.RawText, Error = trace.Provider.Error, InputTokens = trace.Provider.InputTokens,
                OutputTokens = trace.Provider.OutputTokens, EstimatedUsd = trace.Provider.EstimatedUsd, Simulated = trace.Provider.Simulated }
        };

        public static string Fingerprint(PromptPacket p) => p == null ? null : DsWorldSnapshot.Digest(MiniJson.Write(new Dictionary<string, object>
        {
            ["kind"] = p.Kind.ToString(), ["actor"] = p.ActorId, ["schema"] = p.SchemaName,
            ["instructions"] = p.Instructions, ["sections"] = p.Sections.Select(s => (object)new Dictionary<string, object>
            { ["name"] = s.Name, ["text"] = s.Text, ["untrusted"] = s.Untrusted }).ToList()
        }));

        public Dictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["format"] = Format, ["rules_version"] = RulesVersion, ["before"] = MiniJson.Parse(Before.Json),
            ["after"] = MiniJson.Parse(After.Json), ["kind"] = Kind.ToString(), ["actor"] = ActorId, ["input"] = Input,
            ["prompt_hash"] = PromptHash, ["presented"] = Presented, ["failure"] = Failure,
            ["verdict"] = Verdict.ToString(), ["craft_decision"] = CraftDecision,
            ["plan_context"] = PlanContext,
            ["response"] = Response == null ? null : new Dictionary<string, object>
            { ["provider"] = Response.ProviderName, ["raw"] = Response.RawText, ["error"] = Response.Error,
                ["input_tokens"] = Response.InputTokens, ["output_tokens"] = Response.OutputTokens,
                ["estimated_usd"] = Response.EstimatedUsd, ["simulated"] = Response.Simulated }
        };

        public static DsReplayFrame Parse(string json)
        {
            var d = (Dictionary<string, object>)MiniJson.Parse(json);
            if (d.Str("format") != Format) throw new FormatException("unsupported_replay_format");
            var r = d.Obj("response");
            return new DsReplayFrame
            {
                RulesVersion = d.Str("rules_version"), Before = DsWorldSnapshot.Parse(MiniJson.Write(d.Obj("before"))),
                After = DsWorldSnapshot.Parse(MiniJson.Write(d.Obj("after"))), Kind = (AiTaskKind)Enum.Parse(typeof(AiTaskKind), d.Str("kind")),
                ActorId = d.Str("actor"), Input = d.Str("input"), PromptHash = d.Str("prompt_hash"), Presented = d.Str("presented"),
                Failure = d.Str("failure"), Verdict = (VerdictKind)Enum.Parse(typeof(VerdictKind), d.Str("verdict")),
                CraftDecision = d.Str("craft_decision"), PlanContext = d.Str("plan_context"),
                Response = r == null ? null : new ProviderResult { ProviderName = r.Str("provider"), RawText = r.Str("raw"), Error = r.Str("error"),
                    InputTokens = (int?)r.Num("input_tokens"), OutputTokens = (int?)r.Num("output_tokens"),
                    EstimatedUsd = (decimal?)r.Num("estimated_usd"), Simulated = (bool)r["simulated"] }
            };
        }

        /// <summary>严格回归：提示构建、世界输入和输出均须一致。</summary>
        public Task<bool> VerifyAsync(CancellationToken ct = default) => VerifyInternal(true, ct);

        /// <summary>执行回放：复用录制的模型输出，允许提示词已迭代；仍要求同一规则版本和最终世界哈希。</summary>
        public Task<bool> VerifyExecutionAsync(CancellationToken ct = default) => VerifyInternal(false, ct);

        async Task<bool> VerifyInternal(bool requireMatchingPrompt, CancellationToken ct)
        {
            if (RulesVersion != DsExecutor.RulesVersion) return false;
            var s = new DragonsleepSession(new RecordedProvider(this, requireMatchingPrompt));
            s.RestoreState(Before);
            var t = await s.RunAsync(Kind, ActorId, Input, ct, PlanContext).ConfigureAwait(false);
            if (s.HasPendingCraft && (CraftDecision == "confirmed" || CraftDecision == "declined")) t = s.ConfirmCraft(CraftDecision == "confirmed");
            return s.CaptureState().Hash == After.Hash && t.Presented == Presented && t.FailureCode == Failure && t.Verdict.Kind == Verdict;
        }

        sealed class RecordedProvider : IAiProvider
        {
            readonly DsReplayFrame _frame;
            readonly bool _requireMatchingPrompt;
            public string Name => "recorded-replay";
            public bool IsSimulated => true;
            public RecordedProvider(DsReplayFrame frame, bool requireMatchingPrompt) { _frame = frame; _requireMatchingPrompt = requireMatchingPrompt; }
            public Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var r = _frame.Response;
                return Task.FromResult((_requireMatchingPrompt && Fingerprint(packet) != _frame.PromptHash) || r == null
                    ? new ProviderResult { ProviderName = Name, Simulated = true, Error = "replay_context_mismatch", EstimatedUsd = 0 }
                    : new ProviderResult { ProviderName = Name, RawText = r.RawText, Error = r.Error, Simulated = true,
                        InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, EstimatedUsd = 0 });
            }
        }
    }
}
