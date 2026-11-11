namespace BlackBox.Core;

/// <summary>
/// 추세 보조 계산(최소제곱 기울기). "분석"이 아니라 덤프에 함께 적는 파생 통계다 — 느려짐(A 메커니즘)은
/// 점 이벤트가 아니라 기울기라서, 엔진이 24시간치 원시 시계열을 다시 적분하지 않도록 수집기가 숫자 하나로 요약한다.
/// 순수 함수 — xUnit으로 검증한다.
/// </summary>
public static class Trend
{
    /// <summary>x(분)·y 쌍의 최소제곱 기울기. 점이 3개 미만이거나 x가 모두 같으면 null.</summary>
    public static double? Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) return null;
        double mx = 0, my = 0;
        foreach (var (x, y) in points) { mx += x; my += y; }
        mx /= points.Count; my /= points.Count;
        double sxx = 0, sxy = 0;
        foreach (var (x, y) in points) { sxx += (x - mx) * (x - mx); sxy += (x - mx) * (y - my); }
        if (sxx <= 0) return null;
        return sxy / sxx;
    }

    /// <summary>기울기와 함께 결정계수 R²(0~1)도 돌려준다. 분산이 0인 평탄 데이터는 R²=1(완전 설명)로 본다.</summary>
    public static (double Slope, double R2)? Fit(IReadOnlyList<(double X, double Y)> points)
    {
        var s = Slope(points);
        if (s is not { } slope) return null;
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double ssTot = 0, ssRes = 0;
        foreach (var (x, y) in points)
        {
            double pred = my + slope * (x - mx);
            ssTot += (y - my) * (y - my);
            ssRes += (y - pred) * (y - pred);
        }
        double r2 = ssTot <= 1e-12 ? 1.0 : Math.Clamp(1 - ssRes / ssTot, 0, 1);
        return (slope, r2);
    }
}
