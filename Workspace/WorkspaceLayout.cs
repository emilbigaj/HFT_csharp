using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Provider;
using Tools;
using Widget;

namespace Workspace;
/// <summary>
/// Serialized workspace state: window bounds + all widget layouts + child windows.
/// </summary>
/// 
[RegisterJson]
public sealed class WorkspaceLayout
{
    // Window Geometry
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowX { get; set; }
    public double WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    // Widgets in this window
    public List<WidgetLayout> Widgets { get; set; } = new();

    // Extended windows
    public List<WorkspaceLayout> ChildWindows { get; set; } = new();
}