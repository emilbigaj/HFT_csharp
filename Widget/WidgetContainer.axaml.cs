//BEGIN_FILE HFT/Widget/WidgetContainer.axaml.cs
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Threading;
using System.Reflection;

namespace Widget;

public enum WidgetResizeEdge
{
    None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight
}

public partial class WidgetContainer : UserControl
{
    private const double ResizeBorderThickness = 6;
    private const double TitleBarHeight = 32;
    private const double ContainerMinWidth = 160;
    private const double ContainerMinHeight = 80;

    public IWidget Widget { get; } = null!;

    private bool _isDragging;
    private bool _isResizing;
    private WidgetResizeEdge _resizeEdge;
    private Point _dragStart;
    private double _startLeft, _startTop;
    private Point _resizeStart;
    private double _startWidth, _startHeight, _startLeftResize, _startTopResize;

    public WidgetContainer()
    {
        InitializeComponent();
    }

    public WidgetContainer(IWidget widget)
    {
        Widget = widget ?? throw new ArgumentNullException(nameof(widget));
        InitializeComponent();

        PART_ContentPresenter.Content = widget;
        TitleText.Text = widget.Title;

        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        Dispatcher.UIThread.Post(() =>
        {
            // FIX: If the Width/Height has been set externally (e.g. by loading a save file),
            // do not overwrite it with defaults. Avalonia defaults are double.NaN.
            if (!double.IsNaN(Width) && !double.IsNaN(Height))
                return;

            Width = widget.DefaultWidth;
            Height = widget.DefaultHeight;
        }, DispatcherPriority.Loaded);

        RootEvents();
    }


    private void RootEvents()
    {
        TitleBar.PointerPressed += OnTitleBarPointerPressed;

        this.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Bubble, true);
        this.AddHandler(PointerMovedEvent, OnHoverCursorUpdate, RoutingStrategies.Bubble, true);

        this.PointerMoved += OnRootPointerMoved;
        this.PointerReleased += OnRootPointerReleased;
        this.PointerCaptureLost += OnRootPointerCaptureLost;

        this.AttachedToVisualTree += (_, _) => BringToFront();

        CloseButton.Click += (_, _) => Close();
        MinimizeButton.Click += (_, _) => OnMinimizeClick();
    }

    private void OnMinimizeClick()
    {
        if (PART_ContentPresenter.IsVisible)
        {
            PART_ContentPresenter.IsVisible = false;
            Height = TitleBarHeight;
        }
        else
        {
            PART_ContentPresenter.IsVisible = true;
            Height = double.NaN; // Restore to auto/previous height logic if needed, or better: keep the explicit height
            // If we are restoring, we might want to go back to the stored Height. 
            // For now, let's just make it visible. The layout engine might need a trigger or explicit height restore 
            // if we were storing 'PreMinimizeHeight'. 
            // Simple fix for now: if Bounds.Height is small, try to restore a default or just let Content drive it if NaN.
            if (Bounds.Height < ContainerMinHeight) Height = 400;
        }
    }

    private Canvas? GetParentCanvas() => this.FindAncestorOfType<Canvas>();

    private void BringToFront()
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null) return;

        int maxZ = 0;
        foreach (Control child in canvas.Children)
        {
            int z = child.ZIndex;
            if (z > maxZ) maxZ = z;
        }

        this.ZIndex = maxZ + 1;
    }

    private void OnHoverCursorUpdate(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _isResizing) return;

        Point pt = e.GetPosition(this);
        WidgetResizeEdge edge = HitTestResizeEdge(pt);

        if (edge != WidgetResizeEdge.None)
        {
            this.Cursor = edge switch
            {
                WidgetResizeEdge.Left or WidgetResizeEdge.Right => new Cursor(StandardCursorType.SizeWestEast),
                WidgetResizeEdge.Top or WidgetResizeEdge.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                WidgetResizeEdge.TopLeft or WidgetResizeEdge.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
                WidgetResizeEdge.TopRight or WidgetResizeEdge.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
                _ => new Cursor(StandardCursorType.Arrow)
            };
        }
        else
        {
            this.Cursor = Cursor.Default;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsRightButtonPressed)
        {
            var items = Widget.GetTitleBarMenuItems()?.ToList();

            if (items != null && items.Count > 0)
            {
                var menu = new ContextMenu();
                foreach (var item in items)
                {
                    menu.Items.Add(item);
                }

                TitleBar.ContextMenu = menu;
                menu.Open(TitleBar);
                e.Handled = true;
            }
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;
        Point local = e.GetPosition(this);
        if (HitTestResizeEdge(local) == WidgetResizeEdge.None)
            StartDrag(e);
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BringToFront();

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Point local = e.GetPosition(this);
        WidgetResizeEdge edge = HitTestResizeEdge(local);

        if (edge != WidgetResizeEdge.None)
        {
            StartResize(e, edge);
            e.Handled = true;
            return;
        }

        if (e.Handled) return;

        if (local.Y <= TitleBarHeight)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                StartDrag(e);
        }
        else
        {
            if (PART_ContentPresenter?.Content is IInputElement input)
                input.Focus();
        }
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null) return;

        if (_isDragging)
        {
            Point pt = e.GetPosition(canvas);
            double dx = pt.X - _dragStart.X;
            double dy = pt.Y - _dragStart.Y;
            double newLeft = _startLeft + dx;
            double newTop = _startTop + dy;
            ClampTitleBar(canvas, ref newLeft, ref newTop);
            Canvas.SetLeft(this, newLeft);
            Canvas.SetTop(this, newTop);
            return;
        }

        if (_isResizing)
        {
            Point pt = e.GetPosition(canvas);
            double dx = pt.X - _resizeStart.X;
            double dy = pt.Y - _resizeStart.Y;

            double newLeft = _startLeftResize;
            double newTop = _startTopResize;
            double newWidth = _startWidth;
            double newHeight = _startHeight;

            if (_resizeEdge is WidgetResizeEdge.Left or WidgetResizeEdge.TopLeft or WidgetResizeEdge.BottomLeft) { newLeft = _startLeftResize + dx; newWidth = _startWidth - dx; }
            if (_resizeEdge is WidgetResizeEdge.Right or WidgetResizeEdge.TopRight or WidgetResizeEdge.BottomRight) { newWidth = _startWidth + dx; }
            if (_resizeEdge is WidgetResizeEdge.Top or WidgetResizeEdge.TopLeft or WidgetResizeEdge.TopRight) { newTop = _startTopResize + dy; newHeight = _startHeight - dy; }
            if (_resizeEdge is WidgetResizeEdge.Bottom or WidgetResizeEdge.BottomLeft or WidgetResizeEdge.BottomRight) { newHeight = _startHeight + dy; }

            if (newWidth < ContainerMinWidth) { newWidth = ContainerMinWidth; if (_resizeEdge is WidgetResizeEdge.Left or WidgetResizeEdge.TopLeft or WidgetResizeEdge.BottomLeft) newLeft = _startLeftResize + (_startWidth - ContainerMinWidth); }
            if (newHeight < ContainerMinHeight) { newHeight = ContainerMinHeight; if (_resizeEdge is WidgetResizeEdge.Top or WidgetResizeEdge.TopLeft or WidgetResizeEdge.TopRight) newTop = _startTopResize + (_startHeight - ContainerMinHeight); }

            ClampTitleBar(canvas, ref newLeft, ref newTop);
            Width = newWidth;
            Height = newHeight;
            Canvas.SetLeft(this, newLeft);
            Canvas.SetTop(this, newTop);
        }
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _isDragging = false;
        _isResizing = false;
        _resizeEdge = WidgetResizeEdge.None;
        this.Cursor = Cursor.Default;
    }

    private void OnRootPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDragging = false;
        _isResizing = false;
        _resizeEdge = WidgetResizeEdge.None;
        this.Cursor = Cursor.Default;
    }

    private void StartDrag(PointerPressedEventArgs e)
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null) return;
        _isDragging = true;
        _isResizing = false;
        _dragStart = e.GetPosition(canvas);
        _startLeft = Canvas.GetLeft(this);
        _startTop = Canvas.GetTop(this);
        if (double.IsNaN(_startLeft)) _startLeft = 0;
        if (double.IsNaN(_startTop)) _startTop = 0;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void StartResize(PointerPressedEventArgs e, WidgetResizeEdge edge)
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null) return;
        _isDragging = false;
        _isResizing = true;
        _resizeEdge = edge;
        _resizeStart = e.GetPosition(canvas);
        _startLeftResize = Canvas.GetLeft(this);
        _startTopResize = Canvas.GetTop(this);
        if (double.IsNaN(_startLeftResize)) _startLeftResize = 0;
        if (double.IsNaN(_startTopResize)) _startTopResize = 0;
        _startWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        _startHeight = Bounds.Height > 0 ? Bounds.Height : Height;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private WidgetResizeEdge HitTestResizeEdge(Point p)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        bool l = p.X <= ResizeBorderThickness;
        bool r = p.X >= w - ResizeBorderThickness;
        bool t = p.Y <= ResizeBorderThickness;
        bool b = p.Y >= h - ResizeBorderThickness;

        if (l && t) return WidgetResizeEdge.TopLeft;
        if (r && t) return WidgetResizeEdge.TopRight;
        if (l && b) return WidgetResizeEdge.BottomLeft;
        if (r && b) return WidgetResizeEdge.BottomRight;
        if (l) return WidgetResizeEdge.Left;
        if (r) return WidgetResizeEdge.Right;
        if (t) return WidgetResizeEdge.Top;
        if (b) return WidgetResizeEdge.Bottom;
        return WidgetResizeEdge.None;
    }

    private void ClampTitleBar(Canvas canvas, ref double left, ref double top)
    {
        if (canvas == null) return;
        Rect bounds = canvas.Bounds;
        double minX = -Width * 0.5;
        double maxX = bounds.Width - Width * 0.5;
        double minY = 0;
        double maxY = bounds.Height - TitleBarHeight;
        left = Math.Clamp(left, minX, maxX);
        top = Math.Clamp(top, minY, maxY);
    }

    private void Close()
    {
        if (Widget is IDisposable d) d.Dispose();
        Canvas? canvas = GetParentCanvas();
        canvas?.Children.Remove(this);
    }
}

//END_FILE HFT/Widget/WidgetContainer.axaml.cs