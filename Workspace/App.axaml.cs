using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Workspace
{
public partial class App : Avalonia.Application
{
public override void Initialize()
{
AvaloniaXamlLoader.Load(this);
}

    public override void OnFrameworkInitializationCompleted()
    {
        // Delegate startup logic to the runner to keep App clean
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            WorkspaceRunner.OnAppReady(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}


}
