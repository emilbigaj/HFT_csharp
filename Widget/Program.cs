//BEGIN_FILE HFT/Widget/Program.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Dialogs;
using System;
using Tools;

namespace Widget
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
                .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
                .UseManagedSystemDialogs()
                .LogToTrace();
    }


}
//END_FILE HFT/Widget/Program.cs