using System.Reflection;
using System.Runtime.InteropServices;

namespace DiffThis.WinUI;

public partial class App : MauiWinUIApplication
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public App()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--version"))
        {
            AttachConsole(-1); // attach to parent console (e.g. terminal that launched the exe)
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
            Console.WriteLine(version);
            Environment.Exit(0);
        }

        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
