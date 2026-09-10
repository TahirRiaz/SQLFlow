using System.Globalization;

namespace SqlFlow.Core.Calendar;

/// <summary>The four points that open the astronomical seasons, as instants in UTC.</summary>
public readonly record struct SeasonEventInstants(
    DateTime MarchEquinoxUtc,
    DateTime JuneSolsticeUtc,
    DateTime SeptemberEquinoxUtc,
    DateTime DecemberSolsticeUtc);

/// <summary>
/// Computes the equinoxes and solstices for a given year using Meeus, <i>Astronomical Algorithms</i> (2nd ed.),
/// chapter 27: a low-precision mean instant from a per-event polynomial, corrected by twenty-four periodic
/// terms. The result is good to well under a minute across the years a data warehouse can address, which is
/// several orders of magnitude more accuracy than picking the right calendar DATE requires.
///
/// The polynomials yield Dynamical Time, so the result is shifted to UT by an estimate of delta-T before it is
/// handed back; without that the instant is roughly a minute late, which only ever matters for an event landing
/// within a minute of local midnight.
/// </summary>
internal static class SeasonEvents
{
    /// <summary>Meeus table 27.C: the amplitude, phase (degrees) and frequency (degrees per Julian century) of
    /// the twenty-four periodic terms that correct the mean instant.</summary>
    private static readonly (double A, double B, double C)[] PeriodicTerms =
    [
        (485, 324.96, 1934.136), (203, 337.23, 32964.467), (199, 342.08, 20.186),
        (182, 27.85, 445267.112), (156, 73.14, 45036.886), (136, 171.52, 22518.443),
        (77, 222.54, 65928.934), (74, 296.72, 3034.906), (70, 243.58, 9037.513),
        (58, 119.81, 33718.147), (52, 297.17, 150.678), (50, 21.02, 2281.226),
        (45, 247.54, 29929.562), (44, 325.15, 31555.956), (29, 60.93, 4443.417),
        (18, 155.12, 67555.328), (17, 288.79, 4562.452), (16, 198.04, 62894.029),
        (14, 199.76, 31436.921), (12, 95.39, 14577.848), (12, 287.11, 31931.756),
        (12, 320.81, 34777.259), (9, 227.73, 1222.114), (8, 15.45, 16859.074),
    ];

    /// <summary>Meeus table 27.B: the mean-instant polynomial per event, for years 1000 through 3000.</summary>
    private static readonly double[][] MeanCoefficients =
    [
        [2451623.80984, 365242.37404, 0.05169, -0.00411, -0.00057],  // March equinox
        [2451716.56767, 365241.62603, 0.00325, 0.00888, -0.00030],   // June solstice
        [2451810.21715, 365242.01767, -0.11575, 0.00337, 0.00078],   // September equinox
        [2451900.05952, 365242.74049, -0.06223, -0.00823, 0.00032],  // December solstice
    ];

    public static SeasonEventInstants ForYear(int year)
        => new(Instant(year, 0), Instant(year, 1), Instant(year, 2), Instant(year, 3));

    private static DateTime Instant(int year, int eventIndex)
    {
        var y = (year - 2000) / 1000.0;
        var c = MeanCoefficients[eventIndex];
        var jde0 = c[0] + (c[1] * y) + (c[2] * y * y) + (c[3] * y * y * y) + (c[4] * y * y * y * y);

        var t = (jde0 - 2451545.0) / 36525.0;
        var w = Radians((35999.373 * t) - 2.47);
        var lambda = 1 + (0.0334 * Math.Cos(w)) + (0.0007 * Math.Cos(2 * w));

        var s = 0.0;
        foreach (var (a, b, cc) in PeriodicTerms)
        {
            s += a * Math.Cos(Radians(b + (cc * t)));
        }

        // Dynamical Time; delta-T converts it to Universal Time, which is what a wall clock (and therefore a
        // calendar date) follows.
        var jdeDynamical = jde0 + (0.00001 * s / lambda);
        var jdUniversal = jdeDynamical - (DeltaTSeconds(year) / 86400.0);
        return FromJulianDay(jdUniversal);
    }

    /// <summary>
    /// The difference between Terrestrial and Universal Time, in seconds, from the Espenak and Meeus polynomial
    /// series used by NASA's eclipse canon. Only the segments a warehouse calendar can reach are carried; a year
    /// outside them falls back to the nearest segment's endpoint, which is correct to the second at the boundary
    /// and never worse than the term it replaces.
    /// </summary>
    private static double DeltaTSeconds(int year)
    {
        if (year < 1900)
        {
            // 1800-1899: the series' own polynomial, evaluated at its start for anything earlier.
            var t = ((Math.Max(year, 1800) - 1860) / 1.0) switch { var v => v };
            return 7.62 + (0.5737 * t) - (0.251754 * t * t / 100) + (0.01680668 * t * t * t / 10000)
                   - (0.0004473624 * t * t * t * t / 1000000) + (t * t * t * t * t * t / 233174000);
        }

        if (year < 1920)
        {
            var t = year - 1900;
            return -2.79 + (1.494119 * t) - (0.0598939 * t * t) + (0.0061966 * t * t * t) - (0.000197 * t * t * t * t);
        }

        if (year < 1941)
        {
            var t = year - 1920;
            return 21.20 + (0.84493 * t) - (0.076100 * t * t) + (0.0020936 * t * t * t);
        }

        if (year < 1961)
        {
            var t = year - 1950;
            return 29.07 + (0.407 * t) - (t * t / 233) + (t * t * t / 2547);
        }

        if (year < 1986)
        {
            var t = year - 1975;
            return 45.45 + (1.067 * t) - (t * t / 260) - (t * t * t / 718);
        }

        if (year < 2005)
        {
            var t = year - 2000;
            return 63.86 + (0.3345 * t) - (0.060374 * t * t) + (0.0017275 * t * t * t)
                   + (0.000651814 * t * t * t * t) + (0.00002373599 * t * t * t * t * t);
        }

        if (year < 2050)
        {
            var t = year - 2000;
            return 62.92 + (0.32217 * t) + (0.005589 * t * t);
        }

        if (year < 2150)
        {
            var u = (year - 1820) / 100.0;
            return -20 + (32 * u * u) - (0.5628 * (2150 - year));
        }

        var w = (year - 1820) / 100.0;
        return -20 + (32 * w * w);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Converts a Julian Day number to the corresponding UTC instant (Meeus chapter 7).</summary>
    private static DateTime FromJulianDay(double julianDay)
    {
        var z = Math.Floor(julianDay + 0.5);
        var f = julianDay + 0.5 - z;

        double a;
        if (z < 2299161)
        {
            a = z;
        }
        else
        {
            var alpha = Math.Floor((z - 1867216.25) / 36524.25);
            a = z + 1 + alpha - Math.Floor(alpha / 4);
        }

        var b = a + 1524;
        var c = Math.Floor((b - 122.1) / 365.25);
        var d = Math.Floor(365.25 * c);
        var e = Math.Floor((b - d) / 30.6001);

        var dayWithFraction = b - d - Math.Floor(30.6001 * e) + f;
        var day = (int)Math.Floor(dayWithFraction);
        var month = e < 14 ? (int)e - 1 : (int)e - 13;
        var year = month > 2 ? (int)c - 4716 : (int)c - 4715;

        var fractionOfDay = dayWithFraction - day;
        var ticks = (long)Math.Round(fractionOfDay * TimeSpan.TicksPerDay);

        var midnight = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        return midnight.AddTicks(ticks);
    }

    /// <summary>Formats an instant for a diagnostic message without depending on the host's culture.</summary>
    public static string Describe(DateTime instantUtc)
        => instantUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
