namespace Town.Core.Dragonsleep
{
    /// <summary>本地裁决元数据。中文 Reason 只显示；本类型不进入感知、提示词、快照或旧版回放。</summary>
    public readonly struct DsReason
    {
        public string Code { get; }
        public string Entity { get; }
        public string StableCode => string.IsNullOrEmpty(Entity) ? Code : Code + ":" + Entity;

        public DsReason(string code, string entity = null)
        {
            Code = code;
            Entity = entity;
        }

        public bool Is(string code, string entity) => Code == code && Entity == entity;

        // 与原有异地预测反馈的隐私遮蔽范围完全一致，不扩大或缩小可见信息。
        public bool MasksRemoteTarget => Code == "actor_absent" || Code == "whelp_absent" ||
            Code == "rescue_target_absent" || Code == "whelp_hidden" || Code == "whelp_scared";
    }

    /// <summary>只传递已经向玩家呈现的失败，不传静态试运行探知的异地状态。</summary>
    public sealed class DsPlanLocalContext
    {
        public DsReason Reason { get; }
        public DsPlanLocalContext(DsReason reason) { Reason = reason; }
    }
}
