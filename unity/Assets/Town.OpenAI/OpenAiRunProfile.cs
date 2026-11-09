namespace Town.OpenAI
{
    /// <summary>必须由显式验证入口选择。普通 client、Play 与 CI 不自动启用研究额度。</summary>
    public sealed class OpenAiRunProfile
    {
        public static readonly OpenAiRunProfile Dialogue = new OpenAiRunProfile(false, 3, 20, 384, 0.01m, 0.05m);
        // 用户于 2026-11-08 明确取消本项研究的费用上限。仍记账，实验入口按有限案例/夜晚数结束。
        // 普通 Play 与 Dialogue 配额不变；此配置不代表启动自动或后台持续调用。
        public static readonly OpenAiRunProfile SixMechanismResearch = new OpenAiRunProfile(true, int.MaxValue, int.MaxValue, 768, decimal.MaxValue, decimal.MaxValue);
        public bool AllMechanisms { get; }
        public int MaxRequests { get; }
        public int MaxDailyRequests { get; }
        public int MaxOutputTokens { get; }
        public decimal MaxReservedUsd { get; }
        public decimal MaxDailyReservedUsd { get; }
        OpenAiRunProfile(bool all, int requests, int daily, int output, decimal reserved, decimal dailyReserved)
        { AllMechanisms = all; MaxRequests = requests; MaxDailyRequests = daily; MaxOutputTokens = output; MaxReservedUsd = reserved; MaxDailyReservedUsd = dailyReserved; }
    }
}
