using System;
using NUnit.Framework;

namespace Town.Core.Tests
{
    public sealed class ResearchStatisticsTests
    {
        // Wilson公式独立代入z=1.959963984540054；不是Wald的 p +/- 1.96*SE。
        [TestCase(50, 100, .4038315303659957, .5961684696340044)]
        [TestCase(0, 10, 0, .2775327998628892)]
        [TestCase(10, 10, .7224672001371107, 1)]
        [TestCase(41, 48, .728328234752537, .9275175067339628)]
        [TestCase(48, 48, .9258998703338823, 1)]
        public void Wilson95MatchesKnownScoreIntervals(int passed, int total, double lower, double upper)
        {
            var interval = ResearchStatistics.Wilson95(passed, total);
            Assert.That(interval.Lower, Is.EqualTo(lower).Within(1e-12));
            Assert.That(interval.Upper, Is.EqualTo(upper).Within(1e-12));
        }

        [Test]
        public void WilsonRejectsUndefinedDenominatorAndInvalidCounts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.Wilson95(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.Wilson95(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.Wilson95(-1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.Wilson95(11, 10));
            var large = ResearchStatistics.Wilson95(int.MaxValue, int.MaxValue);
            Assert.That(large.Lower, Is.InRange(.99999999, 1));
            Assert.That(large.Upper, Is.EqualTo(1).Within(1e-15));
        }

        // 精确二项系数：b=1,c=7 => 2*(C(8,0)+C(8,1))/256=18/256。
        [TestCase(0, 0, 1)]
        [TestCase(0, 1, 1)]
        [TestCase(0, 3, .25)]
        [TestCase(1, 7, .0703125)]
        [TestCase(1, 5, .21875)]
        [TestCase(3, 3, 1)]
        [TestCase(3, 4, 1)]
        [TestCase(0, 10, .001953125)]
        public void McNemarUsesExactTwoSidedDiscordantBinomial(int b, int c, double expected)
        {
            var result = ResearchStatistics.McNemarExact(b, c);
            Assert.AreEqual(b, result.BaselineOnly);
            Assert.AreEqual(c, result.CandidateOnly);
            Assert.AreEqual((long)b + c, result.Discordant);
            Assert.That(result.TwoSidedP, Is.EqualTo(expected).Within(1e-13));
            Assert.That(ResearchStatistics.McNemarExact(c, b).TwoSidedP, Is.EqualTo(result.TwoSidedP).Within(1e-13));
        }

        [Test]
        public void McNemarDoesNotLoseModerateTailWhenTwoToNegativeNWouldUnderflow()
        {
            // Binomial(2000,.5)的中心对称性；中心概率由组合数乘积独立算出。
            double center = 1;
            for (int i = 1; i <= 1000; i++) center *= (2.0 * i - 1) / (2.0 * i);
            Assert.That(ResearchStatistics.McNemarExact(999, 1001).TwoSidedP,
                Is.EqualTo(1 - center).Within(1e-10));
            Assert.AreEqual(1, ResearchStatistics.McNemarExact(int.MaxValue, int.MaxValue).TwoSidedP);
            Assert.That(ResearchStatistics.McNemarExact(0, 2000).TwoSidedP, Is.InRange(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.McNemarExact(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.McNemarExact(0, -1));
        }

        [Test]
        public void IndependentSampleSizeDeclaresDesignAndBaseline()
        {
            // 公开常用等分两比例正态近似公式：.5 -> .6、power=.8、alpha=.05 => ceil(387.3385)=388/臂。
            var result = ResearchStatistics.IndependentProportions(.5, .6);
            Assert.AreEqual(388, result.UnitsPerArm);
            Assert.AreEqual("independent_proportions_two_sided", result.Design);
            Assert.AreEqual(.5, result.ExpectedBaseline);
            Assert.AreEqual(.6, result.ExpectedCandidate);
            Assert.IsNull(result.ExpectedDiscordance);
            Assert.AreEqual(.8, result.Power);
            Assert.AreEqual(.05, result.Alpha);
            StringAssert.Contains("非精确", result.Assumptions);
            Assert.AreEqual(result.UnitsPerArm, ResearchStatistics.IndependentProportions(.6, .5).UnitsPerArm);
        }

        [TestCase(.05, 155)]
        [TestCase(.10, 312)]
        [TestCase(.20, 626)]
        public void PairedSampleSizeRequiresDiscordanceNotOnlyMde(double q, int expected)
        {
            var result = ResearchStatistics.PairedMcNemar(.85, .90, q);
            Assert.AreEqual(expected, result.UnitsPerArm);
            Assert.AreEqual("paired_mcnemar_two_sided", result.Design);
            Assert.AreEqual(q, result.ExpectedDiscordance);
            StringAssert.Contains("最终精确检验功效须另核", result.Assumptions);
        }

        [Test]
        public void SampleSizeValidatesFeasiblePairedTableAndRejectsFalsePrecision()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.PairedMcNemar(.85, .90, .01));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.PairedMcNemar(.90, .95, .20));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.PairedMcNemar(.85, .90, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.PairedMcNemar(.85, .85 + 1e-13, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.PairedMcNemar(.85, .85 + 1e-13, 1e-15));
            Assert.AreEqual(ResearchStatistics.PairedMcNemar(.85, .90, .1).UnitsPerArm,
                ResearchStatistics.PairedMcNemar(.90, .85, .1).UnitsPerArm);
            Assert.Throws<ArgumentException>(() => ResearchStatistics.PairedMcNemar(.9, .9, .1));
            Assert.Throws<ArgumentException>(() => ResearchStatistics.IndependentProportions(.9, .9));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(-.1, .9));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, 1.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(double.NaN, .9));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, .9, .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, .9, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, .9, alpha: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, .9, alpha: .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.8, .9, alpha: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResearchStatistics.IndependentProportions(.9, .9 + 1e-12));
            Assert.Greater(ResearchStatistics.IndependentProportions(.8, .9, .9).UnitsPerArm,
                ResearchStatistics.IndependentProportions(.8, .9).UnitsPerArm);
            Assert.Greater(ResearchStatistics.PairedMcNemar(.85, .90, .10, alpha: .01).UnitsPerArm,
                ResearchStatistics.PairedMcNemar(.85, .90, .10).UnitsPerArm);
        }
    }
}
