namespace DiffThis.AI.OpenAI.Models;

public class CopilotModel
{
    public string Id           { get; set; } = string.Empty;
    public string DisplayName  { get; set; } = string.Empty;
    public bool   IsCustomName { get; set; }
    public bool   IsHidden     { get; set; }
}
