using DiffThis.Services;

namespace DiffThis;

public partial class App : Application
{
    private readonly MainPage _mainPage;
    private readonly ISettingsService _settings;

    public App(ISettingsService settings, MainPage mainPage)
    {
        InitializeComponent();
        UserAppTheme = settings.Theme;
        _mainPage = mainPage;
        _settings = settings;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_mainPage) { Title = "DiffThis" };

        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler != null)
                HookNativeWindowActivated(window);
        };

        window.Destroying += (_, _) => SaveWindowState(window);

        return window;
    }

#if WINDOWS
    private void HookNativeWindowActivated(Window window)
    {
        var nativeWin = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWin is null) return;

        void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
        {
            nativeWin.Activated -= OnActivated;
            RestoreWindowState(window);
        }

        nativeWin.Activated += OnActivated;
    }

    private void SaveWindowState(Window window)
    {
        try
        {
            var nativeWin = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWin is null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWin);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(winId);

            _settings.WindowX      = appWindow.Position.X;
            _settings.WindowY      = appWindow.Position.Y;
            _settings.WindowWidth  = appWindow.Size.Width;
            _settings.WindowHeight = appWindow.Size.Height;

            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(winId,
                Microsoft.UI.Windowing.DisplayAreaFallback.None);
            if (display is not null)
            {
                _settings.WindowMonitorLeft   = display.WorkArea.X;
                _settings.WindowMonitorTop    = display.WorkArea.Y;
                _settings.WindowMonitorRight  = display.WorkArea.X + display.WorkArea.Width;
                _settings.WindowMonitorBottom = display.WorkArea.Y + display.WorkArea.Height;
            }
        }
        catch { /* best-effort */ }
    }

    private void RestoreWindowState(Window window)
    {
        try
        {
            if (_settings.WindowX == -1) return;

            var nativeWin = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWin is null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWin);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(winId);

            var allDisplays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            var monitorFound = false;
            for (int i = 0; i < allDisplays.Count; i++)
            {
                var d = allDisplays[i];
                if (d.WorkArea.X                     == _settings.WindowMonitorLeft  &&
                    d.WorkArea.Y                     == _settings.WindowMonitorTop   &&
                    d.WorkArea.X + d.WorkArea.Width  == _settings.WindowMonitorRight &&
                    d.WorkArea.Y + d.WorkArea.Height == _settings.WindowMonitorBottom)
                    monitorFound = true;
            }

            if (!monitorFound) return;

            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                _settings.WindowX, _settings.WindowY,
                _settings.WindowWidth, _settings.WindowHeight));
        }
        catch { /* best-effort */ }
    }
#else
    private void HookNativeWindowActivated(Window window) { }
    private void SaveWindowState(Window window) { }
    private void RestoreWindowState(Window window) { }
#endif
}
