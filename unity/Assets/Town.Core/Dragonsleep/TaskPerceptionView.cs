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
        TaskPerceptionView(AiTaskKind kind, string actor, Dictionary<string, object> view, string actionPromptVersion)
        { Kind = kind; ActorId = actor; Json = MiniJson.Write(view); ActionPromptVersion = actionPromptVersion; }

        public static TaskPerceptionView Capture(DsWorld world, AiTaskKind kind, string actor, int timeLeftInPhase = 4,
            string planContext = null, string actionPromptVersion = DsActionPrompt.Legacy)
        {
            DsActionPrompt.Validate(actionPromptVersion);
            if (kind != AiTaskKind.Adjudicate && actionPromptVersion != DsActionPrompt.Legacy)
                throw new ArgumentException("action_prompt_requires_adjudicate");
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
            return new TaskPerceptionView(kind, actor, view, actionPromptVersion);
        }
        public Dictionary<string, object> ToDictionary() => (Dictionary<string, object>)MiniJson.Parse(Json);
    }
}
