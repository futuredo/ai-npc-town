using System;
using System.Collections.Generic;

namespace Town.Core.Dragonsleep
{
    /// <summary>按任务角色裁切的冻结快照。没有接受任意字典或世界 JSON 的公开构造入口。</summary>
    public sealed class TaskPerceptionView
    {
        public AiTaskKind Kind { get; }
        public string ActorId { get; }
        public string Json { get; }
        public string ActionPromptVersion { get; }
        public string ActionPerceptionVersion { get; }
        TaskPerceptionView(AiTaskKind kind, string actor, Dictionary<string, object> view, string actionPromptVersion,
            string actionPerceptionVersion)
        {
            Kind = kind; ActorId = actor; Json = MiniJson.Write(view); ActionPromptVersion = actionPromptVersion;
            ActionPerceptionVersion = actionPerceptionVersion;
        }

        public static TaskPerceptionView Capture(DsWorld world, AiTaskKind kind, string actor, int timeLeftInPhase = 4,
            string planContext = null, string actionPromptVersion = DsActionPrompt.Legacy,
            string actionPerceptionVersion = DsActionPerception.Legacy) =>
            CaptureCore(world, kind, actor, timeLeftInPhase, planContext, actionPromptVersion, actionPerceptionVersion, null);

        internal static TaskPerceptionView CaptureAction(DsWorld world, int timeLeft, string promptVersion,
            string perceptionVersion, DsPlayerActionMemory memory) =>
            CaptureCore(world, AiTaskKind.Adjudicate, "player", timeLeft, null, promptVersion, perceptionVersion, memory);

        static TaskPerceptionView CaptureCore(DsWorld world, AiTaskKind kind, string actor, int timeLeftInPhase,
            string planContext, string actionPromptVersion, string actionPerceptionVersion, DsPlayerActionMemory memory)
        {
            DsActionPrompt.Validate(actionPromptVersion);
            DsActionPerception.Validate(actionPerceptionVersion);
            if (kind != AiTaskKind.Adjudicate && actionPromptVersion != DsActionPrompt.Legacy)
                throw new ArgumentException("action_prompt_requires_adjudicate");
            if (kind != AiTaskKind.Adjudicate && actionPerceptionVersion != DsActionPerception.Legacy)
                throw new ArgumentException("action_perception_requires_adjudicate");
            if (actionPerceptionVersion == DsActionPerception.Current && actionPromptVersion != DsActionPrompt.Current)
                throw new ArgumentException("action_memory_requires_current_prompt");
            Dictionary<string, object> view;
            switch (kind)
            {
                case AiTaskKind.Dialogue:
                    view = ContextBuilder.DialoguePerception(world, actor); break;
                case AiTaskKind.AgentPlan:
                    view = ContextBuilder.AgentPerception(world, actor); break;
                case AiTaskKind.Adjudicate:
                    if (actor != "player") throw new ArgumentException("player_role_required");
                    view = ContextBuilder.AdjudicatePerception(world); break;
                case AiTaskKind.Craft:
                    if (actor != "player") throw new ArgumentException("player_role_required");
                    view = ContextBuilder.CraftPerception(world); break;
                case AiTaskKind.Direct:
                    if (actor != "gm") throw new ArgumentException("director_role_required");
                    view = ContextBuilder.DirectorPerception(world); break;
                case AiTaskKind.Plan:
                    if (actor != "player") throw new ArgumentException("player_role_required");
                    view = ContextBuilder.PlanPerception(world, timeLeftInPhase, planContext); break;
                default: throw new ArgumentException("unsupported_task");
            }
            if (actionPerceptionVersion == DsActionPerception.Current)
            {
                view["recent_player_actions"] = memory?.Capture() ?? new List<object>();
                view["memory_scope"] = "玩家亲历的过去裁决；不是当前持有、可用性或异地实时状态。";
            }
            return new TaskPerceptionView(kind, actor, view, actionPromptVersion, actionPerceptionVersion);
        }
        public Dictionary<string, object> ToDictionary() => (Dictionary<string, object>)MiniJson.Parse(Json);
    }
}
