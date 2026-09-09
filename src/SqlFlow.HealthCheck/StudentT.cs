namespace SqlFlow.HealthCheck;

/// <summary>
/// The Student-t distribution pieces the generalized ESD test needs: the CDF via the regularized incomplete
/// beta function and its inverse via Newton iteration seeded from the normal quantile. Self-contained and
/// deterministic (Lanczos log-gamma, the standard continued-fraction incomplete beta, Acklam's inverse
/// normal), so the detector carries no numeric dependency and its critical values are reproducible to 1e-8.
/// </summary>
public static class StudentT
{
    /// <summary>P(T &lt;= t) for <paramref name="df"/> degrees of freedom.</summary>
    public static double Cdf(double t, double df)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(df, 1);
        if (double.IsNaN(t))
        {
            throw new ArgumentException("t must be a number.", nameof(t));
        }

        if (double.IsPositiveInfinity(t))
        {
            return 1;
        }

        if (double.IsNegativeInfinity(t))
        {
            return 0;
        }

        var x = df / (df + t * t);
        var tail = 0.5 * RegularizedIncompleteBeta(df / 2.0, 0.5, x);
        return t >= 0 ? 1 - tail : tail;
    }

    /// <summary>The quantile t with P(T &lt;= t) = <paramref name="p"/> for <paramref name="df"/> degrees of
    /// freedom (the ESD critical-value ingredient).</summary>
    public static double InverseCdf(double p, double df)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(df, 1);
        if (p <= 0 || p >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p must be strictly between 0 and 1.");
        }

        // Newton iteration on the CDF, seeded from the normal quantile (exact as df grows); the t pdf is the
        // derivative, positive everywhere, so the iteration is well-behaved. Bisection guards the tails.
        var t = InverseNormalCdf(p);
        if (df < 30)
        {
            // Heavier tails than normal: widen the seed so the first steps move in the right region.
            t *= 1 + 2.0 / df;
        }

        double lo = -1e10, hi = 1e10;
        for (var i = 0; i < 100; i++)
        {
            var error = Cdf(t, df) - p;
            if (Math.Abs(error) < 1e-12)
            {
                break;
            }

            if (error > 0)
            {
                hi = Math.Min(hi, t);
            }
            else
            {
                lo = Math.Max(lo, t);
            }

            var step = error / Math.Max(Pdf(t, df), 1e-300);
            var next = t - step;
            t = next > lo && next < hi ? next : (lo + hi) / 2;
        }

        return t;
    }

    private static double Pdf(double t, double df)
        => Math.Exp(
            LogGamma((df + 1) / 2.0) - LogGamma(df / 2.0)
            - 0.5 * Math.Log(df * Math.PI)
            - (df + 1) / 2.0 * Math.Log(1 + t * t / df));

    /// <summary>I_x(a, b), the regularized incomplete beta function (continued-fraction form).</summary>
    public static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(a, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(b, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(x, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, 1);

        if (x == 0 || x == 1)
        {
            return x;
        }

        var front = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + a * Math.Log(x) + b * Math.Log(1 - x));

        // The continued fraction converges fastest for x < (a+1)/(a+b+2); use the symmetry otherwise.
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1 - Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + b * Math.Log(1 - x) + a * Math.Log(x))
                * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const double tiny = 1e-30;
        double c = 1, d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < tiny)
        {
            d = tiny;
        }

        d = 1 / d;
        var result = d;

        for (var m = 1; m <= 300; m++)
        {
            var m2 = 2 * m;

            // Even step.
            var numerator = m * (b - m) * x / ((a + m2 - 1) * (a + m2));
            d = 1 + numerator * d;
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1 + numerator / c;
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1 / d;
            result *= d * c;

            // Odd step.
            numerator = -(a + m) * (a + b + m) * x / ((a + m2) * (a + m2 + 1));
            d = 1 + numerator * d;
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1 + numerator / c;
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1 / d;
            var delta = d * c;
            result *= delta;

            if (Math.Abs(delta - 1) < 1e-14)
            {
                return result;
            }
        }

        // 300 iterations bound every (a, b, x) the t CDF produces; reaching here means the inputs were
        // degenerate beyond statistical use.
        throw new InvalidOperationException($"The incomplete beta continued fraction did not converge (a={a}, b={b}, x={x}).");
    }

    /// <summary>Lanczos approximation of ln Γ(x), accurate to ~1e-13 for x &gt; 0.</summary>
    public static double LogGamma(double x)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(x, 0);

        ReadOnlySpan<double> g =
        [
            676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
        ];

        if (x < 0.5)
        {
            // Reflection for the small-argument range.
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        }

        x -= 1;
        var sum = 0.99999999999980993;
        for (var i = 0; i < g.Length; i++)
        {
            sum += g[i] / (x + i + 1);
        }

        var t = x + g.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }

    /// <summary>Acklam's rational approximation of the standard normal quantile (~1.15e-9 relative error),
    /// the Newton seed.</summary>
    public static double InverseNormalCdf(double p)
    {
        if (p <= 0 || p >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p must be strictly between 0 and 1.");
        }

        ReadOnlySpan<double> a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        ReadOnlySpan<double> b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        ReadOnlySpan<double> c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        ReadOnlySpan<double> d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

        const double pLow = 0.02425;
        double q, r;

        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                 / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (p <= 1 - pLow)
        {
            q = p - 0.5;
            r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q
                 / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }

        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
              / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }
}
