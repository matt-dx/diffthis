namespace DiffThis.AI.OpenAI.Models;

public class OllamaModel
{
    public string Id          { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool    IsCustomName { get; set; }
    public bool    IsHidden    { get; set; }
    public bool    IsPulling   { get; set; }
    public string? PullError   { get; set; }
}
