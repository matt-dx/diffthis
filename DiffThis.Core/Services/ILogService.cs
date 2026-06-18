namespace DiffThis.Core.Services;

public interface ILogService
{
    bool   IsEnabled    { get; }
    string LogsDirectory { get; }
    int    LogFileCount  { get; }

    void WriteRequest (string provider, string model, string feature, string content);
    void WriteResponse(string provider, string model, string feature, string content, long elapsedMs);
    void WriteError   (string provider, string model, string feature, string error);

    void ClearLogs();
    void OpenLogsFolder();
}
