//BEGIN_FILE HFT/Widget/WidgetContainer.axaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Widget;

public enum WidgetResizeEdge
{
    None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight
}

/// A widget's floating frame on the Workspace canvas: title bar, minimise and close buttons, dragging by the title bar and
/// resizing by the edges. Its creator sets its size and position. UI thread, cold path.
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
    private Point _pointerGestureStartPoint;   // where a drag or resize started, on the canvas
    private Rect _pointerGestureStartRect;     // the container's layout rect when it started (GetLayoutRect)

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

        RootEvents();
    }

    /// Where the container sits on its canvas (an unset position as 0) and its size: as laid out, else as set.
    public Rect GetLayoutRect()
    {
        double left = Canvas.GetLeft(this);
        double top = Canvas.GetTop(this);
        return new Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height);
    }

    private void RootEvents()
    {
        TitleBar.PointerPressed += OnTitleBarPointerPressed;

        this.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Bubble, true);
        this.AddHandler(PointerMovedEvent, OnHoverCursorUpdate, RoutingStrategies.Bubble, true);

        this.PointerMoved += OnRootPointerMoved;
        this.PointerReleased += OnRootPointerReleased;
        this.PointerCaptureLost += (_, _) => EndPointerGesture();

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

    /// Right button: the widget's title-bar menu, when it has one. Left button off the resize edges: a drag.
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint pointerPoint = e.GetCurrentPoint(this);

        if (pointerPoint.Properties.IsRightButtonPressed)
        {
            List<MenuItem> titleBarMenuItems = Widget.GetTitleBarMenuItems().ToList();
            TitleBar.OpenMenu(titleBarMenuItems);
            e.Handled = titleBarMenuItems.Count > 0;
            return;
        }

        if (!pointerPoint.Properties.IsLeftButtonPressed) return;
        if (HitTestResizeEdge(e.GetPosition(this)) == WidgetResizeEdge.None)
            StartDrag(e);
    }

    /// Any press brings the container to the front; a left press on an edge resizes, on the title bar drags, in the content focuses it.
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BringToFront();

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Point containerPoint = e.GetPosition(this);
        WidgetResizeEdge resizeEdge = HitTestResizeEdge(containerPoint);

        if (resizeEdge != WidgetResizeEdge.None)
        {
            StartResize(e, resizeEdge);
            e.Handled = true;
            return;
        }

        if (e.Handled) return;

        if (containerPoint.Y <= TitleBarHeight)
            StartDrag(e);
        else if (PART_ContentPresenter.Content is IInputElement inputElement)
            inputElement.Focus();
    }

    /// Moves the container (a drag) or moves and resizes it (a resize) by how far the pointer has gone since the gesture started.
    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null || !(_isDragging || _isResizing)) return;

        // Step 1: how far the pointer has moved, and where the container started
        Point canvasPoint = e.GetPosition(canvas);
        double deltaX = canvasPoint.X - _pointerGestureStartPoint.X;
        double deltaY = canvasPoint.Y - _pointerGestureStartPoint.Y;
        Rect startRect = _pointerGestureStartRect;

        // Step 2: a drag moves the whole container
        if (_isDragging)
        {
            double draggedLeft = startRect.X + deltaX;
            double draggedTop = startRect.Y + deltaY;
            ClampTitleBar(canvas, ref draggedLeft, ref draggedTop);
            Canvas.SetLeft(this, draggedLeft);
            Canvas.SetTop(this, draggedTop);
            return;
        }

        // Step 3: a resize moves the grabbed edges, a left or top edge stopping where the minimum size puts it
        bool isLeftEdge = _resizeEdge is WidgetResizeEdge.Left or WidgetResizeEdge.TopLeft or WidgetResizeEdge.BottomLeft;
        bool isRightEdge = _resizeEdge is WidgetResizeEdge.Right or WidgetResizeEdge.TopRight or WidgetResizeEdge.BottomRight;
        bool isTopEdge = _resizeEdge is WidgetResizeEdge.Top or WidgetResizeEdge.TopLeft or WidgetResizeEdge.TopRight;
        bool isBottomEdge = _resizeEdge is WidgetResizeEdge.Bottom or WidgetResizeEdge.BottomLeft or WidgetResizeEdge.BottomRight;

        double newLeft = startRect.X;
        double newTop = startRect.Y;
        double newWidth = startRect.Width;
        double newHeight = startRect.Height;

        if (isLeftEdge) { newLeft = startRect.X + deltaX; newWidth = startRect.Width - deltaX; }
        if (isRightEdge) { newWidth = startRect.Width + deltaX; }
        if (isTopEdge) { newTop = startRect.Y + deltaY; newHeight = startRect.Height - deltaY; }
        if (isBottomEdge) { newHeight = startRect.Height + deltaY; }

        if (newWidth < ContainerMinWidth) { newWidth = ContainerMinWidth; if (isLeftEdge) newLeft = startRect.X + (startRect.Width - ContainerMinWidth); }
        if (newHeight < ContainerMinHeight) { newHeight = ContainerMinHeight; if (isTopEdge) newTop = startRect.Y + (startRect.Height - ContainerMinHeight); }

        // Step 4: the new rect, its title bar kept on the canvas
        ClampTitleBar(canvas, ref newLeft, ref newTop);
        Width = newWidth;
        Height = newHeight;
        Canvas.SetLeft(this, newLeft);
        Canvas.SetTop(this, newTop);
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndPointerGesture();
    }

    /// Ends a drag or resize: the pointer released, or its capture lost.
    private void EndPointerGesture()
    {
        _isDragging = false;
        _isResizing = false;
        _resizeEdge = WidgetResizeEdge.None;
        this.Cursor = Cursor.Default;
    }

    private void StartDrag(PointerPressedEventArgs e) => StartPointerGesture(e, isResizing: false, WidgetResizeEdge.None);

    private void StartResize(PointerPressedEventArgs e, WidgetResizeEdge resizeEdge) => StartPointerGesture(e, isResizing: true, resizeEdge);

    /// Starts a drag or a resize from where the pointer and the container are now, capturing the pointer; nothing off a canvas.
    private void StartPointerGesture(PointerPressedEventArgs e, bool isResizing, WidgetResizeEdge resizeEdge)
    {
        Canvas? canvas = GetParentCanvas();
        if (canvas == null) return;
        _isDragging = !isResizing;
        _isResizing = isResizing;
        _resizeEdge = resizeEdge;
        _pointerGestureStartPoint = e.GetPosition(canvas);
        _pointerGestureStartRect = GetLayoutRect();
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