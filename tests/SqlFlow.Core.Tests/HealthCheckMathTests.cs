using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The numeric foundation of the detector, pinned against published values: Student-t quantiles
/// (the ESD critical-value ingredient), the incomplete beta, log-gamma, the inverse normal, and the robust
/// median/MAD statistics.</summary>
public sealed class HealthCheckMathTests
{
    [Theory]
    [InlineData(0.975, 1, 12.7062)]
    [InlineData(0.975, 10, 2.2281)]
    [InlineData(0.975, 30, 2.0423)]
    [InlineData(0.995, 100, 2.6259)]
    [InlineData(0.95, 5, 2.0150)]
    public void StudentT_InverseCdf_MatchesPublishedTables(double p, double df, double expected)
    {
        Assert.Equal(expected, StudentT.InverseCdf(p, df), 3);
    }

    [Theory]
    [InlineData(0.5, 7)]
    [InlineData(0.9, 3)]
    [InlineData(0.025, 15)]
    [InlineData(0.999, 2)]
    public void StudentT_CdfAndInverse_RoundTrip(double p, double df)
    {
        Assert.Equal(p, StudentT.Cdf(StudentT.InverseCdf(p, df), df), 9);
    }

    [Fact]
    public void StudentT_Cdf_IsSymmetricAndBounded()
    {
        Assert.Equal(0.5, StudentT.Cdf(0, 7), 12);
        Assert.Equal(1.0, StudentT.Cdf(3, 7) + StudentT.Cdf(-3, 7), 12);
        Assert.Equal(1.0, StudentT.Cdf(double.PositiveInfinity, 2), 12);
        Assert.Equal(0.0, StudentT.Cdf(double.NegativeInfinity, 2), 12);
    }

    [Fact]
    public void LogGamma_MatchesKnownValues()
    {
        Assert.Equal(Math.Log(Math.Sqrt(Math.PI)), StudentT.LogGamma(0.5), 10);   // Gamma(1/2) = sqrt(pi)
        Assert.Equal(0, StudentT.LogGamma(1), 10);                                 // Gamma(1) = 1
        Assert.Equal(Math.Log(24), StudentT.LogGamma(5), 10);                      // Gamma(5) = 4!
    }

    [Fact]
    public void InverseNormalCdf_MatchesKnownQuantiles()
    {
        Assert.Equal(1.6449, StudentT.InverseNormalCdf(0.95), 3);
        Assert.Equal(1.9600, StudentT.InverseNormalCdf(0.975), 3);
        Assert.Equal(-2.3263, StudentT.InverseNormalCdf(0.01), 3);
        Assert.Equal(0, StudentT.InverseNormalCdf(0.5), 6);
    }

    [Fact]
    public void Median_OddAndEven()
    {
        Assert.Equal(3, RobustStatistics.Median([5, 1, 3]));
        Assert.Equal(2.5, RobustStatistics.Median([1, 2, 3, 4]));
        Assert.Equal(7, RobustStatistics.Median([7]));
    }

    [Fact]
    public void Scale_IsStandardDeviationConsistent_AndOutlierResistant()
    {
        // MAD of {..centered..} times 1.4826 approximates sigma for normal-ish data; one absurd outlier
        // barely moves it (the whole point).
        var clean = new List<double> { 8, 9, 10, 10, 10, 11, 12 };
        var contaminated = new List<double>(clean) { 100000 };

        var cleanScale = RobustStatistics.Scale(clean);
        var dirtyScale = RobustStatistics.Scale(contaminated);

        Assert.InRange(cleanScale, 1.0, 2.5);
        Assert.InRange(dirtyScale, 1.0, 3.5);
    }

    [Fact]
    public void Scale_MadCollapse_FallsBackToMeanAbsoluteDeviation()
    {
        // More than half the values identical: MAD is 0, yet spread exists. The fallback still sees it.
        var values = new List<double> { 10, 10, 10, 10, 10, 13, 16 };

        Assert.True(RobustStatistics.Scale(values) > 0);
    }

    [Fact]
    public void Scale_TrulyConstant_IsZero_AndZHandlesIt()
    {
        var values = new List<double> { 5, 5, 5, 5 };

        Assert.Equal(0, RobustStatistics.Scale(values));
        Assert.Equal(0, RobustStatistics.Z(5, 5, 0));
        Assert.True(double.IsPositiveInfinity(RobustStatistics.Z(6, 5, 0)));
    }
}
