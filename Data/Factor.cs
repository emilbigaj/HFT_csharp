using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tools;

namespace Data;

/// <summary>
/// Immutable snapshot of an exponentially-weighted mean after an update.
/// Serves as the persistent state container for <see cref="Mean"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[RegisterJson]
public struct MeanPoint(double value, int count, double mean)
{
    /// <summary>The raw input value supplied on this tick.</summary>
    public double Value = value;

    /// <summary>Number of successful updates (finite inputs) applied.</summary>
    public int Count = count;

    /// <summary>The exponentially-weighted mean.</summary>
    public double Mean = mean;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

/// <summary>
/// Immutable snapshot of an exponentially-weighted variance/standard deviation after an update.
/// Always assumes the mean is zero. Serves as the persistent state container for <see cref="StdDev"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[RegisterJson]
public struct StdDevPoint(double value, int count, double var, double stdDev)
{
    /// <summary>The raw input value supplied on this tick.</summary>
    public double Value = value;

    /// <summary>Number of successful updates (finite inputs) applied.</summary>
    public int Count = count;

    /// <summary>Exponentially-weighted variance (EWMA of squares, zero-mean assumption).</summary>
    public double Var = var;

    /// <summary>Standard deviation derived from <see cref="Var"/>.</summary>
    public double StdDev = stdDev;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

/// <summary>
/// Immutable snapshot of the full factor state after an update:
/// a mean channel, a stddev of the mean, a stddev of the raw values, and the resulting z-score.
/// Serves as the persistent state container for <see cref="Factor"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[RegisterJson]
public struct FactorPoint(MeanPoint mean, StdDevPoint meanStdDev, double score)
{
    /// <summary>The exponentially-weighted mean channel.</summary>
    public MeanPoint Mean = mean;

    /// <summary>Standard deviation of the mean channel.</summary>
    public StdDevPoint MeanStdDev = meanStdDev;

    /// <summary>The z-score using the mean channel: <c>Score = Mean / MeanStdDev</c>.</summary>
    public double Score = score;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

/// <summary>
/// Base for exponential statistics calculators (<see cref="Mean"/>, <see cref="StdDev"/>, <see cref="Factor"/>).
/// Holds the state snapshot, the <see cref="Value"/> event, and the shared OnValue/GetPoint plumbing;
/// derived classes supply only the finite-value recursion via <see cref="ComputeNext"/>.
/// </summary>
public abstract class Factor<TPoint> where TPoint : unmanaged
{
    /// <summary>Invoke when a new value is applied.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void OnValue()
    {
        Value?.Invoke();
    }
    public event Action? Value;

    /// <summary>
    /// The current state of the calculator.
    /// </summary>
    private TPoint _point;
    public ref TPoint Point => ref _point;

    /// <summary>Logical name for diagnostics.</summary>
    public string Name { get; }

    /// <summary>True once enough updates have accumulated for the recursion to be reliable.</summary>
    public abstract bool IsInitialized { get; }

    public abstract double ToDouble();

    public static implicit operator double(Factor<TPoint> factor) => factor.ToDouble();

    public void operator +=(double value)
    {
        OnValue(value);
    }


    protected Factor(string name)
    {
        Name = name ?? GetType().Name;
    }

    /// <summary>
    /// Apply one tick: update <see cref="Point"/> and fire <see cref="Value"/>, returning the new snapshot.
    /// If <paramref name="value"/> is non-finite, state is unchanged and a transient NaN point is returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPoint OnValue(double value)
    {
        TPoint point = ComputePoint(value);
        if (double.IsFinite(value))
        {
            _point = point;
            OnValue();
        }
        return point;
    }

    /// <summary>
    /// Compute the hypothetical next point for <paramref name="value"/> WITHOUT mutating this instance's state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPoint GetPoint(double value) => ComputePoint(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TPoint ComputePoint(double value) => double.IsFinite(value) ? ComputeNext(value) : NaNPoint();

    /// <summary>The finite-value recursion. Reads current <see cref="Point"/> state; does not mutate.</summary>
    protected abstract TPoint ComputeNext(double value);

    /// <summary>The transient point returned for a non-finite input: current state with NaN markers.</summary>
    protected abstract TPoint NaNPoint();

    /// <summary>Convert half-life (in ticks) to EWMA alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double AlphaFromHalfLife(int halfLife)
    {
        return 1.0 - Math.Exp(Math.Log(0.5) / halfLife);
    }
}

/// <summary>
/// Exponentially-weighted mean. Use when only an EWMA is needed and the variance machinery of
/// <see cref="Factor"/> would be wasted work.
/// </summary>
public sealed class Mean : Factor<MeanPoint>
{
    /// <summary>Smoothing alpha, computed from <see cref="HalfLife"/>.</summary>
    public double Alpha { get; }

    /// <summary>Half-life in ticks for the mean recursion.</summary>
    public int HalfLife { get; }

    public override bool IsInitialized => Point.Count >= HalfLife;

    public override double ToDouble() => Point.Mean;


    /// <summary>Create a new <see cref="Mean"/>.</summary>
    public Mean(string name, int halfLife) : base(name)
    {
        HalfLife = halfLife > 0 ? halfLife : 1;
        Alpha = AlphaFromHalfLife(HalfLife);
        Point = new MeanPoint(double.NaN, 0, 0.0);
    }

    /// <summary>Pure mean recursion; also used by <see cref="Factor"/> for its mean channel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeanPoint Next(in MeanPoint previous, double alpha, double value)
    {
        double mean = alpha * value + (1.0 - alpha) * previous.Mean;
        return new MeanPoint(value, previous.Count + 1, mean);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override MeanPoint ComputeNext(double value) => Next(in Point, Alpha, value);

    protected override MeanPoint NaNPoint()
    {
        MeanPoint point = Point;
        point.Value = double.NaN;
        return point;
    }
}

/// <summary>
/// Exponentially-weighted standard deviation, always assuming the mean is zero (EWMA of squares).
/// Use when only a stddev is needed and the mean/score machinery of <see cref="Factor"/> would be wasted work.
/// </summary>
public class StdDev : Factor<StdDevPoint>
{
    /// <summary>Smoothing alpha, computed from <see cref="HalfLife"/>.</summary>
    public double Alpha { get; }

    /// <summary>Half-life in ticks for the variance recursion.</summary>
    public int HalfLife { get; }

    public override bool IsInitialized => Point.Count >= HalfLife;

    public override double ToDouble() => Point.StdDev;


    /// <summary>Create a new <see cref="StdDev"/>.</summary>
    public StdDev(string name, int halfLife) : base(name)
    {
        HalfLife = halfLife > 0 ? halfLife : 1;
        Alpha = AlphaFromHalfLife(HalfLife);
        Point = new StdDevPoint(double.NaN, 0, 0.0, 0.0);
    }

    /// <summary>Pure variance recursion; also used by <see cref="Factor"/> for its stddev channels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StdDevPoint Next(in StdDevPoint previous, double alpha, double value)
    {
        double var = alpha * (value * value) + (1.0 - alpha) * previous.Var;
        return new StdDevPoint(value, previous.Count + 1, var, Math.Sqrt(var));
    }

    /// <summary>Variance recursion using the approximate reciprocal-sqrt estimate; used by the fast variants.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StdDevPoint NextEstimate(in StdDevPoint previous, double alpha, double value)
    {
        double var = alpha * (value * value) + (1.0 - alpha) * previous.Var;
        return new StdDevPoint(value, previous.Count + 1, var, Tools.Tools.SqrtEstimate(var));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override StdDevPoint ComputeNext(double value) => Next(in Point, Alpha, value);

    protected override StdDevPoint NaNPoint()
    {
        StdDevPoint point = Point;
        point.Value = double.NaN;
        return point;
    }
}

/// <summary>
/// Fast variant of <see cref="StdDev"/> using the approximate sqrt estimate.
/// </summary>
public sealed class FastStdDev : StdDev
{
    /// <summary>Create a new <see cref="FastStdDev"/>.</summary>
    public FastStdDev(string name, int halfLife) : base(name, halfLife) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected sealed override StdDevPoint ComputeNext(double value) => NextEstimate(in Point, Alpha, value);
}

/// <summary>
/// Full exponential-statistics factor: composes a <see cref="Mean"/> channel with two <see cref="StdDev"/>
/// channels (of the mean and of the raw values) and produces a z-score. Use <see cref="Mean"/> or
/// <see cref="StdDev"/> directly when the full machinery is not needed.
/// </summary>
public class Factor : Factor<FactorPoint>
{
    /// <summary>Smoothing alpha for the mean channel, computed from <see cref="MeanHalfLife"/>.</summary>
    public double MeanAlpha { get; }

    /// <summary>Smoothing alpha for both variance channels, computed from <see cref="StdDevHalfLife"/>.</summary>
    public double StdDevAlpha { get; }

    /// <summary>Half-life in ticks for the mean recursion.</summary>
    public int MeanHalfLife { get; }

    /// <summary>Half-life in ticks for the variance recursions.</summary>
    public int StdDevHalfLife { get; }

    /// <summary>True once enough updates have accumulated for the variance recursion to be reliable (Count >= StdDevHalfLife).</summary>
    public override bool IsInitialized => Point.Mean.Count >= StdDevHalfLife;

    public override double ToDouble() => Point.Score;


    /// <summary>Create a new <see cref="Factor"/>.</summary>
    public Factor(string name, int meanHalfLife, int stdDevHalfLife) : base(name)
    {
        MeanHalfLife = meanHalfLife > 0 ? meanHalfLife : 1;
        StdDevHalfLife = stdDevHalfLife > 0 ? stdDevHalfLife : 1;

        MeanAlpha = AlphaFromHalfLife(MeanHalfLife);
        StdDevAlpha = AlphaFromHalfLife(StdDevHalfLife);

        Point = new FactorPoint(
            new MeanPoint(double.NaN, 0, 0.0),
            new StdDevPoint(double.NaN, 0, 0.0, 0.0),
            double.NaN);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override FactorPoint ComputeNext(double value)
    {
        MeanPoint mean = Mean.Next(in Point.Mean, MeanAlpha, value);
        StdDevPoint meanStdDev = StdDev.Next(in Point.MeanStdDev, StdDevAlpha, mean.Mean);
        double score = meanStdDev.StdDev > 0.0 ? mean.Mean / meanStdDev.StdDev : double.NaN;

        return new FactorPoint(mean, meanStdDev, score);
    }

    protected override FactorPoint NaNPoint()
    {
        FactorPoint point = Point;
        point.Mean.Value = double.NaN;
        point.Score = double.NaN;
        return point;
    }
}

/// <summary>
/// Fast variant of <see cref="Factor"/> that uses approximate sqrt / reciprocal operations.
/// </summary>
public sealed class FastFactor : Factor
{
    /// <summary>Create a new <see cref="FastFactor"/>.</summary>
    public FastFactor(string name, int meanHalfLife, int stdDevHalfLife) : base(name, meanHalfLife, stdDevHalfLife) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected sealed override FactorPoint ComputeNext(double value)
    {
        MeanPoint mean = Mean.Next(in Point.Mean, MeanAlpha, value);
        StdDevPoint meanStdDev = StdDev.NextEstimate(in Point.MeanStdDev, StdDevAlpha, mean.Mean);

        // Score = Mean / MeanStdDev using reciprocal estimate
        double score = mean.Mean * Math.ReciprocalEstimate(meanStdDev.StdDev);
        if (!double.IsFinite(score)) score = double.NaN;

        return new FactorPoint(mean, meanStdDev, score);
    }
}
