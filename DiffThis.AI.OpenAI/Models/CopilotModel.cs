namespace DiffThis.AI.OpenAI.Models;

public class CopilotModel
{
    public string   Id               { get; set; } = string.Empty;
    public string   DisplayName      { get; set; } = string.Empty;
    public bool     IsCustomName     { get; set; }
    public bool     IsHidden         { get; set; }
    /// Effort levels reported by the API (e.g. ["low","medium","high"]). Empty on the built-in fallback list.
    public string[] ReasoningEfforts { get; set; } = [];
    /// User's chosen reasoning effort for this model. Null means use the service default.
    public string?  ReasoningEffort  { get; set; }
}
