namespace DiffThis.Core.Models;

public enum AiProvider { Claude, Copilot, Ollama }

public record AiModel(
    string  Key,
    string  Id,
    string  DisplayName,
    AiProvider Provider,
    string  IconId     = "",
    string  BadgeColor = "");
// Key = disambiguated dropdown value: "copilot:gpt-4o" or "claude-opus-4-8"
// Id  = raw API model ID used in actual API calls
// IconId/BadgeColor are optional; empty means use provider default
