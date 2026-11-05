using System;
using Town.Core.Brain;

namespace Town.OpenAI
{
    public static class OpenAiPolicy
    {
        // 2026-11-04 官方 Standard 短上下文价格；换模型/价格时必须一起复核预算。
        public const string Model = "gpt-6-luna";
        public const int MaxRequestsPerRun = 3;
        public const int MaxRequestsPerUtcDay = 20;
        public const int MaxOutputTokens = 384;
        public const int MaxRequestBytes = 12288;
        public const int TimeoutSeconds = 30;
        public const decimal InputUsdPerMillion = 0.10m;
        public const decimal CachedInputUsdPerMillion = 0.01m;
        public const decimal OutputUsdPerMillion = 0.50m;
        public const decimal ReservationUsdPerRequest = 0.002m;
        public const decimal MaxRunReservationUsd = 0.01m;
        public const decimal MaxDayReservationUsd = 0.05m;

        public static decimal? EstimateUsd(TokenUsage usage)
        {
            if (usage == null) return null;
            int cached = Math.Max(0, Math.Min(usage.InputTokens, usage.CachedInputTokens));
            return ((usage.InputTokens - cached) * InputUsdPerMillion +
                cached * CachedInputUsdPerMillion + usage.OutputTokens * OutputUsdPerMillion) / 1000000m;
        }
    }
}
