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
    public Timestamp Timestamp;   // when the value was applied: the time OnValue was given, else Clock.Now; default in a GetPoint preview

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
    public Timestamp Timestamp;   // when the value was applied: the time OnValue was given, else Clock.Now; default in a GetPoint preview

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
public struct FactorPoint(MeanPoint mean, StdDevPoint meanStdDev)
{
    /// <summary>The exponentially-weighted mean channel.</summary>
    public MeanPoint Mean = mean;

    /// <summary>Standard deviation of the mean channel.</summary>
    public StdDevPoint MeanStdDev = meanStdDev;

    // When the value was applied; both channels carry it, and this puts it at the top of a saved line for the chart
    public readonly Timestamp Timestamp => Mean.Timestamp;

    // Mean / MeanStdDev; NaN before the first value, for a non-finite input (Mean.Value NaN), and while the spread is 0
    public readonly double Score => !double.IsNaN(Mean.Value) && MeanStdDev.StdDev > 0.0 ? Mean.Mean / MeanStdDev.StdDev : double.NaN;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

/// A running statistic the Factors folder can save: a name, the current point, and the event that says the point
/// changed. Mean, StdDev and Factor add the one-value recursion (Factor<TPoint>); Correl takes two values.
/// Single writer; hot path: no allocation after construction.
public abstract class Statistic<TPoint> where TPoint : unmanaged
{
    public event Action? Value;   // raised after each update that changed Point

    private TPoint _point;        // the current state of the calculator
    public ref TPoint Point => ref _point;

    public string Name { get; }

    /// True once enough updates have accumulated for the recursion to be reliable.
    public abstract bool IsInitialized { get; }

    protected Statistic(string name)
    {
        Name = name ?? GetType().Name;
    }

    /// Raises Value: call after Point has been updated.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void OnValue()
    {
        Value?.Invoke();
    }

    /// Converts a half-life in updates to the smoothing alpha.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double AlphaFromHalfLife(int halfLife)
    {
        return 1.0 - Math.Exp(Math.Log(0.5) / halfLife);
    }

    /// How many values a statistic weights as a plain average before alpha takes over: 1 / alpha.
    protected static int NewWarmUpCount(double alpha) => (int)(1.0 / alpha);

    /// The weight of the next value: 1 / (count + 1) for the first warmUpCount values, which makes the estimate the
    /// plain average of what it has seen, then alpha (see Spec.md "The statistics a strategy saves").
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static double GetWeight(double alpha, int warmUpCount, int count) => count < warmUpCount ? 1.0 / (count + 1) : alpha;
}

/// <summary>
/// Base for the one-value exponential statistics calculators (<see cref="Mean"/>, <see cref="StdDev"/>, <see cref="Factor"/>).
/// Adds the shared OnValue/GetPoint plumbing to <see cref="Statistic{TPoint}"/>;
/// derived classes supply only the finite-value recursion via <see cref="ComputeNext"/>.
/// </summary>
public abstract class Factor<TPoint> : Statistic<TPoint> where TPoint : unmanaged
{
    public abstract double ToDouble();

    public static implicit operator double(Factor<TPoint> factor) => factor.ToDouble();

    /// statistic += value applies a value now: one clock read live, free in a simulation.
    public void operator +=(double value)
    {
        OnValue(Clock.Now, value);
    }

    /// statistic += (timestamp, value) applies a value at a time the caller already holds, such as a timer's tick: no clock read.
    public void operator +=((Timestamp Timestamp, double Value) update)
    {
        OnValue(update.Timestamp, update.Value);
    }

    protected Factor(string name) : base(name)
    {
    }

    /// Applies one value now; see OnValue(Timestamp, double).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPoint OnValue(double value) => OnValue(Clock.Now, value);

    /// <summary>
    /// Apply one tick at this time: update <see cref="Point"/> and fire <see cref="Value"/>, returning the new snapshot.
    /// If <paramref name="value"/> is non-finite, state is unchanged and a transient NaN point is returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPoint OnValue(Timestamp timestamp, double value)
    {
        TPoint point = ComputePoint(timestamp, value);
        if (double.IsFinite(value))
        {
            Point = point;
            OnValue();
        }
        return point;
    }

    /// <summary>
    /// Compute the hypothetical next point for <paramref name="value"/> WITHOUT mutating this instance's state.
    /// Its Timestamp is default: a preview has no time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPoint GetPoint(double value) => ComputePoint(default, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TPoint ComputePoint(Timestamp timestamp, double value) => double.IsFinite(value) ? ComputeNext(timestamp, value) : NaNPoint();

    /// <summary>The finite-value recursion, stamped with this time. Reads current <see cref="Point"/> state; does not mutate.</summary>
    protected abstract TPoint ComputeNext(Timestamp timestamp, double value);

    /// <summary>The transient point returned for a non-finite input: current state with NaN markers.</summary>
    protected abstract TPoint NaNPoint();
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

    private readonly int _warmUpCount;   // values weighted as a plain average before Alpha takes over

    /// <summary>Create a new <see cref="Mean"/>.</summary>
    public Mean(string name, int halfLife) : base(name)
    {
        HalfLife = halfLife > 0 ? halfLife : 1;
        Alpha = AlphaFromHalfLife(HalfLife);
        _warmUpCount = NewWarmUpCount(Alpha);
        Point = new MeanPoint(double.NaN, 0, 0.0);
    }

    /// <summary>Pure mean recursion at this weight; also used by <see cref="Factor"/> for its mean channel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeanPoint Next(in MeanPoint previous, double weight, Timestamp timestamp, double value)
    {
        double mean = weight * value + (1.0 - weight) * previous.Mean;
        return new MeanPoint(value, previous.Count + 1, mean) { Timestamp = timestamp };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override MeanPoint ComputeNext(Timestamp timestamp, double value) => Next(in Point, GetWeight(Alpha, _warmUpCount, Point.Count), timestamp, value);

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
public sealed class StdDev : Factor<StdDevPoint>
{
    /// <summary>Smoothing alpha, computed from <see cref="HalfLife"/>.</summary>
    public double Alpha { get; }

    /// <summary>Half-life in ticks for the variance recursion.</summary>
    public int HalfLife { get; }

    public override bool IsInitialized => Point.Count >= HalfLife;

    public override double ToDouble() => Point.StdDev;

    private readonly int _warmUpCount;   // values weighted as a plain average before Alpha takes over

    /// <summary>Create a new <see cref="StdDev"/>.</summary>
    public StdDev(string name, int halfLife) : base(name)
    {
        HalfLife = halfLife > 0 ? halfLife : 1;
        Alpha = AlphaFromHalfLife(HalfLife);
        _warmUpCount = NewWarmUpCount(Alpha);
        Point = new StdDevPoint(double.NaN, 0, 0.0, 0.0);
    }

    /// <summary>Pure variance recursion at this weight; also used by <see cref="Factor"/> for its stddev channels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StdDevPoint Next(in StdDevPoint previous, double weight, Timestamp timestamp, double value)
    {
        double var = weight * (value * value) + (1.0 - weight) * previous.Var;
        return new StdDevPoint(value, previous.Count + 1, var, Math.Sqrt(var)) { Timestamp = timestamp };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override StdDevPoint ComputeNext(Timestamp timestamp, double value) => Next(in Point, GetWeight(Alpha, _warmUpCount, Point.Count), timestamp, value);

    protected override StdDevPoint NaNPoint()
    {
        StdDevPoint point = Point;
        point.Value = double.NaN;
        return point;
    }
}

/// <summary>
/// Full exponential-statistics factor: composes a <see cref="Mean"/> channel with two <see cref="StdDev"/>
/// channels (of the mean and of the raw values) and produces a z-score. Use <see cref="Mean"/> or
/// <see cref="StdDev"/> directly when the full machinery is not needed.
/// </summary>
public sealed class Factor : Factor<FactorPoint>
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

    private readonly int _meanWarmUpCount;     // values the mean channel weights as a plain average before MeanAlpha takes over
    private readonly int _stdDevWarmUpCount;   // the same for the variance channel and StdDevAlpha
    private double NextMeanWeight => GetWeight(MeanAlpha, _meanWarmUpCount, Point.Mean.Count);
    private double NextStdDevWeight => GetWeight(StdDevAlpha, _stdDevWarmUpCount, Point.MeanStdDev.Count);

    /// <summary>Create a new <see cref="Factor"/>.</summary>
    public Factor(string name, int meanHalfLife, int stdDevHalfLife) : base(name)
    {
        MeanHalfLife = meanHalfLife > 0 ? meanHalfLife : 1;
        StdDevHalfLife = stdDevHalfLife > 0 ? stdDevHalfLife : 1;

        MeanAlpha = AlphaFromHalfLife(MeanHalfLife);
        StdDevAlpha = AlphaFromHalfLife(StdDevHalfLife);
        _meanWarmUpCount = NewWarmUpCount(MeanAlpha);
        _stdDevWarmUpCount = NewWarmUpCount(StdDevAlpha);

        Point = new FactorPoint(
            new MeanPoint(double.NaN, 0, 0.0),
            new StdDevPoint(double.NaN, 0, 0.0, 0.0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override FactorPoint ComputeNext(Timestamp timestamp, double value)
    {
        MeanPoint mean = Mean.Next(in Point.Mean, NextMeanWeight, timestamp, value);
        StdDevPoint meanStdDev = StdDev.Next(in Point.MeanStdDev, NextStdDevWeight, timestamp, mean.Mean);
        return new FactorPoint(mean, meanStdDev);
    }

    /// The current state with a NaN input, so its Score reads NaN.
    protected override FactorPoint NaNPoint()
    {
        FactorPoint point = Point;
        point.Mean.Value = double.NaN;
        return point;
    }

    /// The value whose GetPoint would score this: the inverse of the score recursion. Does not mutate.
    /// Returns NaN while the spread is 0 or when |score| is at or beyond its ceiling, 1 / sqrt(β), where α and β are
    /// the weights the next value gets in the mean and variance channels (NextMeanWeight, NextStdDevWeight).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetValue(double score)
    {
        // Step 1: the mean that scores this, from score² · (β · mean² + (1 − β) · var) = mean²
        double meanStdDevVar = Point.MeanStdDev.Var;
        double stdDevWeight = NextStdDevWeight;
        double scoreCeilingMargin = 1.0 - score * score * stdDevWeight;
        if (!(meanStdDevVar > 0.0) || !(scoreCeilingMargin > 0.0)) return double.NaN;
        double mean = score * Math.Sqrt((1.0 - stdDevWeight) * meanStdDevVar / scoreCeilingMargin);

        // Step 2: the value that moves the mean there, from mean = α · value + (1 − α) · previous mean
        double meanWeight = NextMeanWeight;
        return (mean - (1.0 - meanWeight) * Point.Mean.Mean) / meanWeight;
    }
}

/// Snapshot of an exponentially-weighted correlation of two series after an update. Both means are
/// taken as zero, as StdDevPoint. The persistent state of Correl.
[StructLayout(LayoutKind.Sequential)]
[RegisterJson]
public struct CorrelPoint(double x, double y, int count, double varX, double varY, double covar)
{
    public Timestamp Timestamp;             // when the pair was applied: the time OnValues was given, else Clock.Now; default in a GetPoint preview
    public double X = x;                    // the raw x supplied on this update
    public double Y = y;                    // the raw y supplied on this update
    public int Count = count;               // updates applied (both inputs finite)
    public double VarX = varX;              // exponentially-weighted mean of x²
    public double VarY = varY;              // exponentially-weighted mean of y²
    public double Covar = covar;  // exponentially-weighted mean of x · y

    // Covar / (StdDev of x · StdDev of y), −1 to 1; NaN while either variance is 0
    public readonly double Correl => VarX > 0.0 && VarY > 0.0 ? Covar / Math.Sqrt(VarX * VarY) : double.NaN;

    // How far y moves per unit of x (the regression slope through zero); NaN while x has no variance
    public readonly double Beta => VarX > 0.0 ? Covar / VarX : double.NaN;

    public override string ToString()
    {
        return Json.Serialize(this);
    }
}

/// Exponentially-weighted correlation and beta of two series, both taken as zero-mean like StdDev: three
/// moving averages (x², y², x · y) at one half-life. A sibling of Mean, StdDev and Factor: a Statistic<TPoint>
/// like them, so a strategy saves it the same way, but not a Factor<TPoint>, whose plumbing takes one value.
/// Single writer; hot path: no allocation after construction.
public sealed class Correl : Statistic<CorrelPoint>
{
    public double Alpha { get; }            // smoothing alpha, from HalfLife
    public int HalfLife { get; }            // in updates

    public override bool IsInitialized => Point.Count >= HalfLife;

    private readonly int _warmUpCount;      // pairs weighted as a plain average before Alpha takes over

    public Correl(string name, int halfLife) : base(name)
    {
        HalfLife = halfLife > 0 ? halfLife : 1;
        Alpha = AlphaFromHalfLife(HalfLife);
        _warmUpCount = NewWarmUpCount(Alpha);
        Point = new CorrelPoint(double.NaN, double.NaN, 0, 0.0, 0.0, 0.0);
    }

    /// correl += (x, y) applies one pair now: one clock read live, free in a simulation.
    public void operator +=((double X, double Y) values)
    {
        OnValues(Clock.Now, values.X, values.Y);
    }

    /// correl += (timestamp, x, y) applies one pair at a time the caller already holds: no clock read.
    public void operator +=((Timestamp Timestamp, double X, double Y) update)
    {
        OnValues(update.Timestamp, update.X, update.Y);
    }

    /// Applies one pair now; see OnValues(Timestamp, double, double).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CorrelPoint OnValues(double x, double y) => OnValues(Clock.Now, x, y);

    /// Applies one pair at this time, raises Value and returns the new point. A pair with a non-finite value leaves
    /// the state unchanged, raises nothing and returns the state with NaN inputs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CorrelPoint OnValues(Timestamp timestamp, double x, double y)
    {
        CorrelPoint point = GetPoint(x, y);
        if (double.IsFinite(x) && double.IsFinite(y))
        {
            point.Timestamp = timestamp;
            Point = point;
            OnValue();
        }
        return point;
    }

    /// The point this pair would give, without mutating; its Timestamp is default, as a preview has no time. A pair
    /// with a non-finite value returns the current state with NaN inputs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CorrelPoint GetPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return new CorrelPoint(double.NaN, double.NaN, Point.Count, Point.VarX, Point.VarY, Point.Covar);
        double weight = GetWeight(Alpha, _warmUpCount, Point.Count);
        double decay = 1.0 - weight;
        return new CorrelPoint(x, y, Point.Count + 1,
            weight * (x * x) + decay * Point.VarX,
            weight * (y * y) + decay * Point.VarY,
            weight * (x * y) + decay * Point.Covar);
    }
}

/// Rolling regression of one target on several inputs, all taken as zero-mean like Correl: the betas that best
/// explain the target as the sum of beta × input. Made of one Correl per pair of series (each input with the
/// target and with every other input), so it takes any number of inputs and a strategy saves it through those
/// Correls. The betas are solved from their covariances after every update (see Spec.md "Regression"). Single
/// writer; hot path: allocates only at construction.
public sealed class Regression
{
    private const double s_pivotTolerance = 1e-12;  // a pivot below this share of its diagonal entry means the inputs are linearly dependent

    public string Name { get; }
    public int InputCount { get; }
    public double Ridge { get; }                    // damping added to the diagonal, as a share of the inputs' mean variance; 0: plain least squares

    private readonly Correl[] _targetCorrels;       // input i (x) with the target (y)
    private readonly Correl[] _inputCorrels;        // input i with input j, i < j, row by row
    private readonly bool[] _isPairSwapped;         // per input pair: true when j's name sorts first, so j is the pair's x
    private readonly double[] _matrix;             // InputCount × InputCount scratch: the inputs' covariances, then their Cholesky factor
    private readonly double[] _betas;               // the last solution; NaN until it can be solved

    // One beta per input, in the inputs' order; NaN until they can be solved
    public ReadOnlySpan<double> Betas => _betas;

    // Every pair counts the same updates, so one count covers them all
    public bool IsInitialized => _targetCorrels[0].IsInitialized;

    /// newCorrel makes each pair's Correl from its name and half-life: a strategy's NewCorrel, which saves them, or a
    /// plain constructor. A pair is named by its two inputs' names in sorted order, and fed in that order, so the
    /// same pair has the same file however the inputs are ordered (see Spec.md "Regression").
    public Regression(string name, string[] inputNames, int halfLife, double ridge, Func<string, int, Correl> newCorrel)
    {
        Name = name;
        InputCount = inputNames.Length;
        Ridge = ridge;
        _targetCorrels = new Correl[InputCount];
        _inputCorrels = new Correl[InputCount * (InputCount - 1) / 2];
        _isPairSwapped = new bool[_inputCorrels.Length];
        _matrix = new double[InputCount * InputCount];
        _betas = new double[InputCount];
        int _pairIndex = 0;
        for (int rowIndex = 0; rowIndex < InputCount; rowIndex++)
        {
            _targetCorrels[rowIndex] = newCorrel($"{name} {inputNames[rowIndex]}-Target", halfLife);
            for (int columnIndex = rowIndex + 1; columnIndex < InputCount; columnIndex++)
            {
                bool isSwapped = string.CompareOrdinal(inputNames[rowIndex], inputNames[columnIndex]) > 0;
                string pairName = isSwapped ? $"{inputNames[columnIndex]}-{inputNames[rowIndex]}" : $"{inputNames[rowIndex]}-{inputNames[columnIndex]}";
                _isPairSwapped[_pairIndex] = isSwapped;
                _inputCorrels[_pairIndex++] = newCorrel($"{name} {pairName}", halfLife);
            }
        }
        ThrowIfCountsDiffer();
        SolveBetas();
    }

    /// Adds one observation now; see OnValues(Timestamp, ReadOnlySpan<double>, double). One clock read live, however many pairs.
    public void OnValues(ReadOnlySpan<double> inputs, double target) => OnValues(Clock.Now, inputs, target);

    /// Adds one observation to every pair at this time and re-solves the betas. An observation with a non-finite
    /// value is skipped whole, so every pair covers the same observations.
    public void OnValues(Timestamp timestamp, ReadOnlySpan<double> inputs, double target)
    {
        // Step 1: skip the observation if any value is missing
        if (!double.IsFinite(target)) return;
        foreach (double input in inputs)
        {
            if (!double.IsFinite(input)) return;
        }

        // Step 2: update every pair
        int _pairIndex = 0;
        for (int rowIndex = 0; rowIndex < InputCount; rowIndex++)
        {
            _targetCorrels[rowIndex].OnValues(timestamp, inputs[rowIndex], target);
            for (int columnIndex = rowIndex + 1; columnIndex < InputCount; columnIndex++)
            {
                bool isSwapped = _isPairSwapped[_pairIndex];
                _inputCorrels[_pairIndex++].OnValues(timestamp, inputs[isSwapped ? columnIndex : rowIndex], inputs[isSwapped ? rowIndex : columnIndex]);
            }
        }

        // Step 3: solve the betas from the new covariances
        SolveBetas();
    }

    /// Lays the pairs' covariances out as (the inputs' covariance matrix) · betas = (each input's covariance with
    /// the target) and solves it. Leaves NaN betas while it cannot be solved.
    private void SolveBetas()
    {
        int _pairIndex = 0;
        for (int rowIndex = 0; rowIndex < InputCount; rowIndex++)
        {
            ref readonly CorrelPoint targetPoint = ref _targetCorrels[rowIndex].Point;
            _matrix[rowIndex * InputCount + rowIndex] = targetPoint.VarX;
            _betas[rowIndex] = targetPoint.Covar;
            for (int columnIndex = rowIndex + 1; columnIndex < InputCount; columnIndex++)
            {
                double covar = _inputCorrels[_pairIndex++].Point.Covar;
                _matrix[rowIndex * InputCount + columnIndex] = covar;
                _matrix[columnIndex * InputCount + rowIndex] = covar;
            }
        }
        Solve(_matrix, _betas, InputCount, Ridge);
    }

    /// Solves matrix · x = betas in place for a symmetric positive matrix (Cholesky), after adding ridge × the mean
    /// of its diagonal to the diagonal. Fills betas with NaN when the matrix cannot be factored.
    private static void Solve(Span<double> matrix, Span<double> betas, int count, double ridge)
    {
        // Step 1: damp the diagonal
        double _diagonalSum = 0.0;
        for (int index = 0; index < count; index++) _diagonalSum += matrix[index * count + index];
        double damping = ridge * _diagonalSum / count;
        for (int index = 0; index < count; index++) matrix[index * count + index] += damping;

        // Step 2: factor into lower × lowerᵀ, kept in the lower triangle
        for (int columnIndex = 0; columnIndex < count; columnIndex++)
        {
            double diagonal = matrix[columnIndex * count + columnIndex];
            for (int rowIndex = columnIndex; rowIndex < count; rowIndex++)
            {
                double _sum = matrix[rowIndex * count + columnIndex];
                for (int innerIndex = 0; innerIndex < columnIndex; innerIndex++)
                    _sum -= matrix[rowIndex * count + innerIndex] * matrix[columnIndex * count + innerIndex];
                bool isPivot = rowIndex == columnIndex;
                if (isPivot && !(_sum > s_pivotTolerance * diagonal))
                {
                    betas.Fill(double.NaN);
                    return;
                }
                matrix[rowIndex * count + columnIndex] = isPivot ? Math.Sqrt(_sum) : _sum / matrix[columnIndex * count + columnIndex];
            }
        }

        // Step 3: forward substitution, then back substitution
        for (int rowIndex = 0; rowIndex < count; rowIndex++)
        {
            double _sum = betas[rowIndex];
            for (int innerIndex = 0; innerIndex < rowIndex; innerIndex++)
                _sum -= matrix[rowIndex * count + innerIndex] * betas[innerIndex];
            betas[rowIndex] = _sum / matrix[rowIndex * count + rowIndex];
        }
        for (int rowIndex = count - 1; rowIndex >= 0; rowIndex--)
        {
            double _sum = betas[rowIndex];
            for (int innerIndex = rowIndex + 1; innerIndex < count; innerIndex++)
                _sum -= matrix[innerIndex * count + rowIndex] * betas[innerIndex];
            betas[rowIndex] = _sum / matrix[rowIndex * count + rowIndex];
        }
    }

    /// Throws if the pairs do not all count the same updates, which a reload from files of different lengths gives:
    /// their covariances would be of different histories and the betas wrong (see Spec.md "Regression").
    private void ThrowIfCountsDiffer()
    {
        int count = _targetCorrels[0].Point.Count;
        foreach (Correl correl in _targetCorrels)
        {
            if (correl.Point.Count != count) ThrowCountsDiffer(correl, count);
        }
        foreach (Correl correl in _inputCorrels)
        {
            if (correl.Point.Count != count) ThrowCountsDiffer(correl, count);
        }
    }

    private void ThrowCountsDiffer(Correl correl, int count) => throw new InvalidOperationException(
        $"Regression.Regression: {correl.Name} has {correl.Point.Count} updates, must be {count} like {_targetCorrels[0].Name}; delete the .correl files of {Name} so it starts afresh");
}
