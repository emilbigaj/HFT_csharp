//BEGIN_FILE HFT/Widget/Widget.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia.Controls;
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
//END_FILE HFT/Widget/Widget.cs