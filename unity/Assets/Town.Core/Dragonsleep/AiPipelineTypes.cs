using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Town.Core.Dragonsleep
{
    /// <summary>论文六类 AI 原生机制在本作中的任务类型。</summary>
    public enum AiTaskKind
    {
        Dialogue,     // N1 认知交互 + N2 社会影响（同一次对话里可同时发生）
        Adjudicate,   // N4 语义判定
        AgentPlan,    // N5 多智能体：居民自主行动
        Direct,       // N3 AI GM：龙母之心导演
        Craft,        // N6 生成式构造：织言机造物
        Plan,         // 多步计划：把玩家目标拆成现有动词组成的步骤，只提案、不改世界
    }

    public enum AiStage { Context, Prompt, Provider, Parse, Validate, Adjudicate, Commit }

    public enum StageStatus { Pending, Ok, Warn, Fail, Skipped }

    public enum VerdictKind { Accepted, Partial, Rejected }

    public sealed class StageRecord
    {
        public AiStage Stage;
        public StageStatus Status = StageStatus.Pending;
        public string Detail = "";
        public double Millis;
    }

    /// <summary>一个提示词分段。分段记录长度，便于研究“哪段上下文起作用、花了多少 token”。</summary>
    public sealed class PromptSection
    {
        public string Name;
        public string Text;
        /// <summary>是否为不可信数据（玩家输入、他人台词）。提示词中会显式标注为数据，不得当成指令。</summary>
        public bool Untrusted;
    }

    public sealed class PromptPacket
    {
        public NpcPerceptionView DialogueSnapshot { get; internal set; }
        public TaskPerceptionView TaskSnapshot { get; internal set; }
        public string OriginalPlayerInput { get; internal set; }
        public string ActionPromptVersion => TaskSnapshot?.ActionPromptVersion ?? DsActionPrompt.Legacy;
        // 本地离线规划元数据；不属于模型协议，不参与 TotalChars、提示词指纹或回放记录。
        internal DsPlanLocalContext LocalPlanContext { get; set; }
        public AiTaskKind Kind;
        public string ActorId;
        public string SchemaName;
        public string Instructions;
        public readonly List<PromptSection> Sections = new List<PromptSection>();
        public PromptPacket FreezeCopy()
        {
            var copy = new PromptPacket
            {
                Kind = Kind, ActorId = ActorId, SchemaName = SchemaName, Instructions = Instructions,
                DialogueSnapshot = DialogueSnapshot, TaskSnapshot = TaskSnapshot,
                OriginalPlayerInput = OriginalPlayerInput, LocalPlanContext = LocalPlanContext
            };
            foreach (var section in Sections)
                copy.Sections.Add(new PromptSection { Name = section.Name, Text = section.Text, Untrusted = section.Untrusted });
            return copy;
        }
        public int TotalChars
        {
            get
            {
                int n = Instructions?.Length ?? 0;
                foreach (var s in Sections) n += (s.Text?.Length ?? 0) + s.Name.Length;
                return n;
            }
        }
    }

    public sealed class ProviderResult
    {
        public string ProviderName;
        public string RawText;
        public string Error;
        public string ResponseStatus;
        public string IncompleteReason;
        public int? InputTokens;
        public int? OutputTokens;
        /// <summary>估算费用（美元）。null 表示未知，不能当成 0。</summary>
        public decimal? EstimatedUsd;
        public bool Simulated;
    }

    /// <summary>模型提供方。离线规则提供方、录制回放、真实模型都实现它。</summary>
    public interface IAiProvider
    {
        string Name { get; }
        bool IsSimulated { get; }
        Task<ProviderResult> CompleteAsync(PromptPacket packet, CancellationToken cancellationToken);
    }

    public sealed class WorldChange
    {
        public string Path;
        public string Before;
        public string After;
        public string Note;
        public override string ToString() => $"{Path}: {Before} → {After}" + (string.IsNullOrEmpty(Note) ? "" : $"（{Note}）");
    }

    public sealed class Verdict
    {
        public VerdictKind Kind = VerdictKind.Rejected;
        public string Reason = "";
        public DsReason ReasonCode;
        public double? Roll;
        public double? Difficulty;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>一次 AI 调用的完整链路记录，是“织言镜”和研究导出的唯一数据源。</summary>
    public sealed class AiTrace
    {
        public int Sequence;
        public string RequestId;
        public AiTaskKind Kind;
        public string ActorId;
        public string ActorName;
        public string PlayerInput;
        public string Clock;
        public int SessionGen;
        public int WorldVersionBefore;
        public int WorldVersionAfter;

        public readonly List<StageRecord> Stages = new List<StageRecord>();
        public Dictionary<string, object> Perception;
        public PromptPacket Prompt;
        public ProviderResult Provider;
        public Dictionary<string, object> Proposal;
        public readonly List<string> ValidationIssues = new List<string>();
        public Verdict Verdict = new Verdict();
        public readonly List<WorldChange> Changes = new List<WorldChange>();
        /// <summary>最终呈现给玩家的文字（角色台词或判定叙述）。</summary>
        public string Presented = "";
        public DsExecutionReceipt Receipt;
        public string CraftDecision;
        /// <summary>计划任务的整体校验报告（结构、引用、试运行可行性）。</summary>
        public DsPlanReport PlanReport;
        /// <summary>重规划时由系统生成的上下文（失败类别、原因、旧计划摘要），属于冻结感知的一部分。</summary>
        public string PlanContext;
        public string FailureCode;
        /// <summary>仅显式玩家行动内核路径记录原码；不改变默认/headless 的记录字段。</summary>
        public string KernelCode;
        public double TotalMillis;

        public StageRecord Stage(AiStage stage)
        {
            foreach (var s in Stages) if (s.Stage == stage) return s;
            var r = new StageRecord { Stage = stage };
            Stages.Add(r);
            return r;
        }

        public bool Succeeded => FailureCode == null && Verdict.Kind != VerdictKind.Rejected;

        public static string MechanismLabel(AiTaskKind kind)
        {
            switch (kind)
            {
                case AiTaskKind.Dialogue: return "N1/N2 认知·说服";
                case AiTaskKind.Adjudicate: return "N4 语义判定";
                case AiTaskKind.AgentPlan: return "N5 多智能体";
                case AiTaskKind.Direct: return "N3 AI GM";
                case AiTaskKind.Plan: return "多步计划";
                default: return "N6 生成式构造";
            }
        }

        public static string StageLabel(AiStage stage)
        {
            switch (stage)
            {
                case AiStage.Context: return "感知分区";
                case AiStage.Prompt: return "提示组装";
                case AiStage.Provider: return "模型调用";
                case AiStage.Parse: return "结构解析";
                case AiStage.Validate: return "校验";
                case AiStage.Adjudicate: return "规则裁决";
                default: return "世界提交";
            }
        }

        /// <summary>研究导出用的一行 JSON。</summary>
        public Dictionary<string, object> ToRecord()
        {
            var stages = new List<object>();
            foreach (var s in Stages)
                stages.Add(new Dictionary<string, object>
                {
                    ["stage"] = s.Stage.ToString(), ["status"] = s.Status.ToString(),
                    ["ms"] = s.Millis, ["detail"] = s.Detail
                });
            var sections = new List<object>();
            if (Prompt != null)
                foreach (var s in Prompt.Sections)
                    sections.Add(new Dictionary<string, object> { ["name"] = s.Name, ["chars"] = s.Text?.Length ?? 0, ["untrusted"] = s.Untrusted });
            var changes = new List<object>();
            foreach (var c in Changes) changes.Add(c.ToString());
            var record = new Dictionary<string, object>
            {
                ["seq"] = Sequence, ["request_id"] = RequestId, ["kind"] = Kind.ToString(),
                ["actor"] = ActorId, ["clock"] = Clock, ["session"] = SessionGen,
                ["input"] = PlayerInput, ["provider"] = Provider?.ProviderName, ["simulated"] = Provider?.Simulated,
                ["input_tokens"] = Provider?.InputTokens, ["output_tokens"] = Provider?.OutputTokens,
                ["estimated_usd"] = Provider?.EstimatedUsd, ["prompt_sections"] = sections,
                ["raw"] = Provider?.RawText, ["issues"] = new List<object>(ValidationIssues),
                ["response_status"] = Provider?.ResponseStatus, ["incomplete_reason"] = Provider?.IncompleteReason,
                ["verdict"] = Verdict.Kind.ToString(), ["reason"] = Verdict.Reason, ["roll"] = Verdict.Roll,
                ["difficulty"] = Verdict.Difficulty, ["changes"] = changes, ["presented"] = Presented,
                ["receipt"] = Receipt?.ToRecord(), ["craft_decision"] = CraftDecision, ["rules_version"] = DsExecutor.RulesVersion,
                ["plan"] = PlanReport?.ToRecord(), ["plan_context"] = PlanContext,
                ["failure"] = FailureCode, ["stages"] = stages, ["total_ms"] = TotalMillis,
                ["version_before"] = WorldVersionBefore, ["version_after"] = WorldVersionAfter
            };
            if (KernelCode != null) record["kernel_code"] = KernelCode;
            if (Kind == AiTaskKind.Adjudicate && Prompt?.ActionPromptVersion == DsActionPrompt.Current)
                record["action_prompt_version"] = Prompt.ActionPromptVersion;
            return record;
        }
    }
}
