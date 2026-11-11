using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using KnightWinNotes.Services;
using KnightWinNotes.Views;

namespace KnightWinNotes;

public partial class App : Application
{
    public static List<string> StartupFiles { get; } = new();
    private static bool _popupHookRegistered;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        if (!_popupHookRegistered)
        {
            _popupHookRegistered = true;
            Control.LoadedEvent.AddClassHandler<PopupRoot>((popupRoot, _) =>
            {
                ApplyPopupWin11Acrylic(popupRoot);
                popupRoot.Opened -= OnPopupRootOpened;
                popupRoot.Opened += OnPopupRootOpened;
            });
        }
    }

    private void OnPopupRootOpened(object? sender, EventArgs e)
    {
        if (sender is PopupRoot popupRoot)
            ApplyPopupWin11Acrylic(popupRoot);
    }

    private void ApplyPopupWin11Acrylic(PopupRoot popupRoot)
    {
        IntPtr hwnd = popupRoot.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            bool isDark = ActualThemeVariant == ThemeVariant.Dark;
            bool fallbackAcrylic = OperatingSystem.IsWindows()
                && popupRoot.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur;
            WindowsNativeInterop.ApplyPopupAcrylicChrome(hwnd, isDark, fallbackAcrylic);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    StartupFiles.Add(arg);
            }

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
