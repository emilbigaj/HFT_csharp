//BEGIN_FILE Chart/Series.cs
using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Data;
using Tools;

namespace Chart;

[RegisterJson]
public struct SeriesRenderContext
{
    public DrawingContext DrawingContext;
    public Rect PlotRect;
    public double XMin, XMax, XScale, XOffset, YMin, YMax, YScale;
}

public interface ISeries
{
    FileType FileType { get; }
    string Name { get; }
    bool IsVisible { get; set; }
    bool IsTick { get; set; }
    YAxisSide AxisSide { get; }
    IBrush Stroke { get; }
    double StrokeThickness { get; }
    int Count { get; }

    bool TryGetDomain(out double minX, out double maxX);
    void Render(ref SeriesRenderContext context);
    bool TryGetLastVisible(double xMin, double xMax, out double x, out double y);
    bool TryGetNearest(double x, double xMin, double xMax, out double nearestX, out double nearestY);

    // Updated to support dynamic label coloring
    bool TryGetLastRenderedPoint(double xMin, double xMax, out double x, out double y, out IBrush? overrideBrush);

    bool TryGetRange(double xMin, double xMax, out double yMin, out double yMax);

    // Explicit System.Action usage to avoid ambiguity
    event Action<ISeries, double>? DataAppended;
}
//END_FILE Chart/Series.cs