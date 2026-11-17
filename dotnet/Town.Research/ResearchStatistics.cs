using System;

/// <summary>纯离线二元指标统计；不读文件、不创建提供方、不推断样本是否独立。</summary>
internal static class ResearchStatistics
{
    internal readonly struct Interval
    {
        internal double Lower { get; }
        internal double Upper { get; }
        internal Interval(double lower, double upper) { Lower = lower; Upper = upper; }
    }

    internal readonly struct McNemarResult
    {
        // b：仅基线通过；c：仅候选通过。正向差异为 (c-b)/全部配对数，而非 /discordant。
        internal int BaselineOnly { get; }
        internal int CandidateOnly { get; }
        internal long Discordant => (long)BaselineOnly + CandidateOnly;
        internal double TwoSidedP { get; }
        internal McNemarResult(int b, int c, double p) { BaselineOnly = b; CandidateOnly = c; TwoSidedP = p; }
    }

    internal sealed class SampleSizeApproximation
    {
        internal string Design { get; }
        internal double ExpectedBaseline { get; }
        internal double ExpectedCandidate { get; }
        internal double? ExpectedDiscordance { get; }
        internal double Power { get; }
        internal double Alpha { get; }
        // 配对设计为 N 个独立配对（每臂 N 次）；独立比例设计为每臂 N 个独立观测。
        internal int UnitsPerArm { get; }
        internal string Assumptions { get; }

        internal SampleSizeApproximation(string design, double baseline, double candidate, double? discordance,
            double power, double alpha, int units, string assumptions)
        {
            Design = design; ExpectedBaseline = baseline; ExpectedCandidate = candidate;
            ExpectedDiscordance = discordance; Power = power; Alpha = alpha; UnitsPerArm = units;
            Assumptions = assumptions;
        }
    }

    /// <summary>双侧95% Wilson score区间，无连续性校正。n=0无定义，明确拒绝。</summary>
    internal static Interval Wilson95(int passed, int total)
    {
        if (total <= 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (passed < 0 || passed > total) throw new ArgumentOutOfRangeException(nameof(passed));
        const double z = 1.959963984540054;
        double p = (double)passed / total, z2 = z * z, denominator = 1 + z2 / total;
        double center = (p + z2 / (2.0 * total)) / denominator;
        double half = z * Math.Sqrt(p * (1 - p) / total + z2 / (4 * (double)total * total)) / denominator;
        return new Interval(Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>
    /// 条件精确双侧McNemar：min(1, 2*P[Binomial(b+c, .5) &lt;= min(b,c)])，不是mid-p。
    /// b=仅基线通过，c=仅候选通过；两者都为0返回p=1（无差异证据，不证明等效）。
    /// 只适用于独立的配对二元单位；四步同局、重复seed或事后挑选同上下文不自动满足此前提。
    /// </summary>
    internal static McNemarResult McNemarExact(int baselineOnly, int candidateOnly)
    {
        if (baselineOnly < 0) throw new ArgumentOutOfRangeException(nameof(baselineOnly));
        if (candidateOnly < 0) throw new ArgumentOutOfRangeException(nameof(candidateOnly));
        long n = (long)baselineOnly + candidateOnly;
        int k = Math.Min(baselineOnly, candidateOnly);
        if (n == 0 || n - 2L * k <= 1) return new McNemarResult(baselineOnly, candidateOnly, 1);

        // 先算较小侧端点的log概率，再按相邻概率比向下累加，避免2^-n在n>1074时先下溢。
        double logProbability = -n * Math.Log(2);
        for (long i = 1; i <= k; i++) logProbability += Math.Log(n - k + i) - Math.Log(i);
        double scaledTail = 1, term = 1;
        for (long i = k; i > 0; i--)
        {
            term *= (double)i / (n - i + 1);
            double next = scaledTail + term;
            if (next == scaledTail) break;
            scaledTail = next;
        }
        double p = Math.Min(1, Math.Exp(Math.Log(2) + logProbability + Math.Log(scaledTail)));
        return new McNemarResult(baselineOnly, candidateOnly, p);
    }

    /// <summary>等分两独立比例，双侧优效正态近似；返回每臂独立观测数，不适用于同题配对。</summary>
    internal static SampleSizeApproximation IndependentProportions(double baseline, double candidate,
        double power = .8, double alpha = .05)
    {
        ValidateDesign(baseline, candidate, power, alpha);
        double mean = (baseline + candidate) / 2;
        double nullVariance = 2 * mean * (1 - mean);
        double alternativeVariance = baseline * (1 - baseline) + candidate * (1 - candidate);
        int n = ApproximateUnits(baseline, candidate, nullVariance, alternativeVariance, power, alpha);
        return new SampleSizeApproximation("independent_proportions_two_sided", baseline, candidate, null, power, alpha, n,
            "等分两臂；观测相互独立；双侧优效正态近似；无连续性校正、无聚类/多重性调整；非精确功效保证。");
    }

    /// <summary>
    /// 配对McNemar双侧优效的正态近似。q=P(仅基线通过)+P(仅候选通过)，d=candidate-baseline。
    /// n=(z_(1-alpha/2)*sqrt(q)+z_power*sqrt(q-d^2))^2/d^2；返回独立配对数，不是请求总数。
    /// 必须显式给基线和q；只给MDE不能确定配对样本量。不计算非劣效或聚类功效。
    /// </summary>
    internal static SampleSizeApproximation PairedMcNemar(double baseline, double candidate, double discordance,
        double power = .8, double alpha = .05)
    {
        ValidateDesign(baseline, candidate, power, alpha);
        ValidateProbability(discordance, nameof(discordance));
        double difference = candidate - baseline;
        // 四格概率：(q-d)/2、(q+d)/2、(baseline+candidate-q)/2、1-(baseline+candidate+q)/2。
        double minimum = Math.Abs(difference), maximum = Math.Min(baseline + candidate, 2 - baseline - candidate);
        // 只容忍二进制浮点表示的相对舍入差，不能让q=0或远小于微小效应量的非法表通过。
        if (discordance <= 0 || minimum - discordance > 1e-14 * Math.Max(minimum, discordance)
            || discordance - maximum > 1e-14 * Math.Max(maximum, discordance))
            throw new ArgumentOutOfRangeException(nameof(discordance), "discordance与两臂预期比例不能构成合法配对四格表。");
        double q = Math.Max(minimum, Math.Min(maximum, discordance));
        int n = ApproximateUnits(baseline, candidate, q,
            Math.Max(0, q - difference * difference), power, alpha);
        return new SampleSizeApproximation("paired_mcnemar_two_sided", baseline, candidate, discordance, power, alpha, n,
            "独立配对二元单位；q已预先给定；双侧优效正态近似；无连续性校正、无聚类/多重性调整；最终精确检验功效须另核。");
    }

    static int ApproximateUnits(double baseline, double candidate, double nullVariance, double alternativeVariance,
        double power, double alpha)
    {
        double numerator = StandardNormalQuantile(1 - alpha / 2) * Math.Sqrt(nullVariance)
            + StandardNormalQuantile(power) * Math.Sqrt(alternativeVariance);
        double raw = Math.Pow(numerator / Math.Abs(candidate - baseline), 2);
        if (!double.IsFinite(raw) || raw > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(candidate), "近似样本量超出Int32。");
        return Math.Max(1, (int)Math.Ceiling(raw));
    }

    static void ValidateDesign(double baseline, double candidate, double power, double alpha)
    {
        ValidateProbability(baseline, nameof(baseline));
        ValidateProbability(candidate, nameof(candidate));
        if (baseline == candidate) throw new ArgumentException("效应量不能为0。", nameof(candidate));
        if (!double.IsFinite(power) || power <= .5 || power >= 1) throw new ArgumentOutOfRangeException(nameof(power));
        if (!double.IsFinite(alpha) || alpha <= 0 || alpha >= .5) throw new ArgumentOutOfRangeException(nameof(alpha));
    }

    static void ValidateProbability(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(name);
    }

    // Acklam逆标准正态近似（绝对误差约1e-8），不需要统计包；这里只用于近似样本量。
    static double StandardNormalQuantile(double p)
    {
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p));
        if (p < .02425 || p > .97575)
        {
            double q = Math.Sqrt(-2 * Math.Log(p < .02425 ? p : 1 - p));
            double x = (((((-7.784894002430293e-3 * q - .3223964580411365) * q - 2.400758277161838) * q
                - 2.549732539343734) * q + 4.374664141464968) * q + 2.938163982698783)
                / ((((7.784695709041462e-3 * q + .3224671290700398) * q + 2.445134137142996) * q
                + 3.754408661907416) * q + 1);
            return p < .02425 ? x : -x;
        }
        double centered = p - .5, r = centered * centered;
        return (((((-39.69683028665376 * r + 220.9460984245205) * r - 275.9285104469687) * r
            + 138.3577518672690) * r - 30.66479806614716) * r + 2.506628277459239) * centered
            / (((((-54.47609879822406 * r + 161.5858368580409) * r - 155.6989798598866) * r
            + 66.80131188771972) * r - 13.28068155288572) * r + 1);
    }
}
