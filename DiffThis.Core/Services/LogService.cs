using System.Diagnostics;

namespace DiffThis.Core.Services;

public class LogService : ILogService
{
    private readonly ISettingsService _settings;

    public LogService(ISettingsService settings)
    {
        _settings     = settings;
        LogsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffThis", "logs");
    }

    public bool   IsEnabled    => _settings.AiLoggingEnabled;
    public string LogsDirectory { get; }

    public int LogFileCount =>
        Directory.Exists(LogsDirectory)
            ? Directory.GetFiles(LogsDirectory, "diffthis-*.log").Length
            : 0;

    public void WriteRequest(string provider, string model, string feature, string content)
        => Append(provider, model, feature, "REQUEST", content, null);

    public void WriteResponse(string provider, string model, string feature, string content, long elapsedMs)
        => Append(provider, model, feature, "RESPONSE", content, elapsedMs);

    public void WriteError(string provider, string model, string feature, string error)
        => Append(provider, model, feature, "ERROR", error, null);

    public void ClearLogs()
    {
        if (!Directory.Exists(LogsDirectory)) return;
        foreach (var f in Directory.GetFiles(LogsDirectory, "diffthis-*.log"))
            try { File.Delete(f); } catch { }
    }

    public void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void Append(string provider, string model, string feature, string label, string content, long? elapsedMs)
    {
        if (!IsEnabled) return;
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var now  = DateTime.UtcNow;
            var path = Path.Combine(LogsDirectory, $"diffthis-{now:yyyy-MM-dd}.log");
            var time = now.ToString("yyyy-MM-dd HH:mm:ss");
            var duration = elapsedMs.HasValue ? $" {elapsedMs}ms" : "";
            var header = $"[{time}] {label}{duration} {provider}/{model} ({feature})";
            File.AppendAllText(path, $"{header}\n{content}\n\n");
        }
        catch { /* never throw from logging */ }
    }
}
