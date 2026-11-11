namespace Town.Core.Dragonsleep
{
    /// <summary>
    /// 本机会话签发的只读造物图样句柄。描述在生成候选时冻结，不暴露 trace、候选世界或取消令牌。
    /// 只能把原实例交回签发会话确认；它不是可持久化的世界状态或 JSON 标识。
    /// </summary>
    public sealed class DsCraftProposal
    {
        public string Description { get; }

        internal DsCraftProposal(string description) { Description = description; }
    }
}
