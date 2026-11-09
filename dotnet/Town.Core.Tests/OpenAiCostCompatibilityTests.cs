using NUnit.Framework;
using Town.Core.Brain;
using Town.OpenAI;

namespace Town.Core.Tests
{
    public class OpenAiCostCompatibilityTests
    {
        [TestCase(1000, 200, 100, "0.000132")]
        [TestCase(900, 0, 80, "0.00013")]
        [TestCase(1000, 2000, 100, "0.00006")]
        [TestCase(1000, -10, 100, "0.00015")]
        [TestCase(0, 0, 0, "0")]
        public void 整数重载保持原费用公式(int input, int cached, int output, string expected)
        {
            decimal value = decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(value, OpenAiPolicy.EstimateUsd(input, cached, output));
            Assert.AreEqual(value, OpenAiPolicy.EstimateUsd(new TokenUsage
            { InputTokens = input, CachedInputTokens = cached, OutputTokens = output }));
        }

        [Test]
        public void 缺少用量仍为未知费用() => Assert.IsNull(OpenAiPolicy.EstimateUsd(null));
    }
}
