using CommunityToolkit.Maui;
using DiffThis.Services;
using Microsoft.Extensions.Logging;

namespace DiffThis;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();

        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper
            .AppendToMapping("DevTools", (handler, _) =>
            {
                if (handler.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
                    wv2.CoreWebView2Initialized += (wv, _) =>
                    {
                        wv.CoreWebView2.Settings.AreDevToolsEnabled = true;
                        wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                    };
            });
#endif

        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IExportService, ExportService>();
        builder.Services.AddSingleton<DiffSessionService>();
        builder.Services.AddSingleton<IClaudeAuthService, ClaudeAuthService>();
        builder.Services.AddSingleton<IClaudeModelService, ClaudeModelService>();
        builder.Services.AddSingleton<IClaudeService, ClaudeService>();
        builder.Services.AddSingleton<AiCacheService>();
        builder.Services.AddSingleton<IAnalysisLinkService, AnalysisLinkService>();

        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
