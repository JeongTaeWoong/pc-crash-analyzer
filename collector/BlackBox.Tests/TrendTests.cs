using BlackBox.Core;
using Xunit;

namespace BlackBox.Tests;

public class TrendTests
{
    [Fact]
    public void Linear_growth_gives_exact_slope_and_r2_one()
    {
        var pts = Enumerable.Range(0, 30).Select(i => ((double)i, 100.0 + 2.5 * i)).ToList();
        var fit = Trend.Fit(pts);
        Assert.NotNull(fit);
        Assert.Equal(2.5, fit!.Value.Slope, 6);
        Assert.Equal(1.0, fit.Value.R2, 6);
    }

    [Fact]
    public void Flat_series_has_zero_slope()
    {
        var pts = Enumerable.Range(0, 10).Select(i => ((double)i, 512.0)).ToList();
        Assert.Equal(0.0, Trend.Slope(pts)!.Value, 9);
        Assert.Equal(1.0, Trend.Fit(pts)!.Value.R2, 9); // 분산 0 → 완전 설명으로 본다
    }

    [Fact]
    public void Too_few_points_or_same_x_returns_null()
    {
        Assert.Null(Trend.Slope(new List<(double, double)> { (0, 1), (1, 2) }));
        Assert.Null(Trend.Slope(new List<(double, double)> { (3, 1), (3, 2), (3, 3) }));
    }

    [Fact]
    public void Noisy_series_has_r2_below_one_but_slope_sign_right()
    {
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 60).Select(i => ((double)i, 1000.0 + 3 * i + rnd.NextDouble() * 40 - 20)).ToList();
        var fit = Trend.Fit(pts)!.Value;
        Assert.InRange(fit.Slope, 2.0, 4.0);
        Assert.InRange(fit.R2, 0.8, 0.9999);
    }
}
