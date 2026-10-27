using System;
using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Phone;

/// The Android application: builds the Avalonia App once for the process (Avalonia 12 puts the builder here, not in the activity).
[Application]
public class PhoneApplication(IntPtr handle, JniHandleOwnership ownership) : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}

/// The app's one activity: Avalonia's. Follows the phone's rotation, kept across rotation and size changes so the Mirror thread lives on.
[Activity(Label = "HFT", Theme = "@style/HftTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
