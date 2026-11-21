namespace RelativisticRod;

/// <summary>Immutable snapshot of the user-controlled parameters (swapped atomically between UI and render threads).</summary>
public sealed record SimParams(double VWave, double Freq, double ObsY, bool IsPlaying, bool ShowPhotons);

public readonly record struct RodPoint(double S, double X, double Y, double Angle);

public readonly record struct RetardedPoint(
    double S, double X, double Y, double Angle, double TEmit, double Delay, double Dist);

/// <summary>
/// Pure physics: a rod pivoted at the origin whose swing propagates along its length as a wave,
/// plus the retarded-time solve that gives what a distant observer actually sees.
/// Direct port of getPhysicalPos / getRetardedPos from the HTML version.
/// </summary>
public static class RodModel
{
    public const double C = 1.0;               // speed of light (normalised)
    public const double RodLength = 50.0;
    public const int Samples = 120;
    public const int PhotonCount = 14;
    public const double Amplitude = 0.45;      // radians
    public const double WaveSpeedScale = 100.0;
    public const double MaxLookback = 8.0;     // seconds searched by the bisection
    public const int BisectionIterations = 16;

    public static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    public static RodPoint Physical(double s, double t, double freq, double vWave)
    {
        double phase = 2.0 * Math.PI * freq * (t - s / (vWave * WaveSpeedScale));
        double angle = Amplitude * Math.Sin(phase) * (s / RodLength);
        return new RodPoint(s, s * Math.Sin(angle), s * Math.Cos(angle), angle);
    }

    /// <summary>Finds emission time tE with tE + |x(tE) - observer| / c == t by bisection.</summary>
    public static RetardedPoint Retarded(double s, double t, double obsX, double obsY, double freq, double vWave)
    {
        double lo = t - MaxLookback, hi = t, tE = t;

        for (int i = 0; i < BisectionIterations; i++)
        {
            tE = 0.5 * (lo + hi);
            var p = Physical(s, tE, freq, vWave);
            double dist = Hypot(p.X - obsX, p.Y - obsY);
            if (tE + dist / C < t) lo = tE; else hi = tE;
        }

        var pos = Physical(s, tE, freq, vWave);
        double d = Hypot(pos.X - obsX, pos.Y - obsY);
        return new RetardedPoint(s, pos.X, pos.Y, pos.Angle, tE, t - tE, d);
    }

    public static void SampleCurves(double t, SimParams p, RodPoint[] truth, RetardedPoint[] seen)
    {
        for (int i = 0; i <= Samples; i++)
        {
            double s = (double)i / Samples * RodLength;
            truth[i] = Physical(s, t, p.Freq, p.VWave);
            seen[i] = Retarded(s, t, 0.0, p.ObsY, p.Freq, p.VWave);
        }
    }
}
