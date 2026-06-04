namespace DiffThis.AI.OpenAI.Models;

public record OllamaEndpoint(
    string  Id,
    string  Name,
    string  BaseUrl,
    string? ApiKey     = null,
    string  IconId     = "ollama",
    string  BadgeColor = "#D68E42");
