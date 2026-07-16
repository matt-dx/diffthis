namespace DiffThis.Core.Models;

public enum AiProvider { Claude, OpenAI, Copilot, Ollama }

public record AiModel(
    string  Key,
    string  Id,
    string  DisplayName,
    AiProvider Provider,
    string  IconId     = "",
    string  BadgeColor = "");
// Key = disambiguated dropdown value: "openai:gpt-5" or "copilot:gpt-4o"
// Id  = raw API model ID used in actual API calls
// IconId/BadgeColor are optional; empty means use provider default
