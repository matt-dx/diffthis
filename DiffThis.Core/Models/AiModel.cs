namespace DiffThis.Core.Models;

public enum AiProvider { Claude, Copilot }

public record AiModel(string Key, string Id, string DisplayName, AiProvider Provider);
// Key = disambiguated dropdown value: "copilot:gpt-4o" or "claude-opus-4-8"
// Id  = raw API model ID used in actual API calls
