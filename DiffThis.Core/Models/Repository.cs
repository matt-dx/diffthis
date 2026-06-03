namespace DiffThis.Core.Models;

public class Repository
{
    public string Path { get; set; } = string.Empty;
    public DateTime LastOpened { get; set; } = DateTime.Now;

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'))
        is { Length: > 0 } n ? n : Path;
}
