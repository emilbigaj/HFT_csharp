//BEGIN_FILE HFT/Widget/Widget.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Tools;

namespace Widget;

/// <summary>
/// Interface for the container window hosting widgets.
/// Allows widgets to request actions (like adding new widgets) without referencing the Window class directly.
/// </summary>
public interface IWidgetHost
{
    void AddWidget(IWidget widget);
    void ExtendWorkspace();
}

/// <summary>
/// Interface that all workspace widgets implement so hosts can manage them.
/// </summary>
public interface IWidget
{
    string TypeKey { get; }
    string Title { get; }

    // Static default dimensions for the widget type
    double DefaultWidth { get; }
    double DefaultHeight { get; }

    string? SaveStateJson();
    void LoadStateJson(string? json);

    /// <summary>
    /// Allows the widget to provide custom menu items for the container's title bar context menu.
    /// Default implementation returns empty list.
    /// </summary>
    IEnumerable<MenuItem> GetTitleBarMenuItems() => Enumerable.Empty<MenuItem>();

    /// <summary>
    /// Optional UI content to inject directly into the WidgetContainer's Title Bar.
    /// </summary>
    object? TitleBarContent => null;
}

/// <summary>
/// Serialized widget layout: type + geometry + custom JSON state.
/// </summary>
[RegisterJson]
public sealed class WidgetLayout
{
    public string TypeKey { get; set; } = "";
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? StateJson { get; set; }
}

/// Puts a widget into the Workspace window that holds a control: a widget opening another (order entry, a ladder).
public static class WidgetHost
{
    /// Adds the widget to the IWidgetHost window holding this control; logs and drops it when there is none.
    public static void AddToHost(this Control control, IWidget widget)
    {
        if (control.FindAncestorOfType<Window>() is IWidgetHost widgetHost)
            widgetHost.AddWidget(widget);
        else
            Console.WriteLine($"{control.GetType().Name}: not inside a Workspace window, {widget.TypeKey} not opened");
    }
}
//END_FILE HFT/Widget/Widget.cs