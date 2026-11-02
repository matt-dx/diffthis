using System.Text.Json;
using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public class GitHostService : IGitHostService
{
    private readonly ISettingsService _settings;
    private List<GitHostConfig> _store = [];

    public event Action? Changed;

    public IReadOnlyList<GitHostConfig> Hosts => _store;

    public GitHostService(ISettingsService settings)
    {
        _settings = settings;
        Load();
    }

    public void AddHost(string name, string host, GitProviderKind kind)
    {
        _store.Add(new GitHostConfig { Id = Guid.NewGuid().ToString("N")[..8], Name = name, Host = host, Kind = kind });
        Save();
        Changed?.Invoke();
    }

    public void UpdateHost(GitHostConfig host)
    {
        var existing = _store.FirstOrDefault(h => h.Id == host.Id);
        if (existing is null) return;
        existing.Name = host.Name;
        existing.Host = host.Host;
        existing.Kind = host.Kind;
        Save();
        Changed?.Invoke();
    }

    public void RemoveHost(string hostId)
    {
        if (_store.RemoveAll(h => h.Id == hostId) == 0) return;
        try { SecureStorage.Default.Remove(TokenKey(hostId)); } catch { /* best effort */ }
        Save();
        Changed?.Invoke();
    }

    public GitHostConfig? FindByHost(string host) =>
        _store.FirstOrDefault(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> GetTokenAsync(string hostId)
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(TokenKey(hostId));
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SetTokenAsync(string hostId, string token)
    {
        try { await SecureStorage.Default.SetAsync(TokenKey(hostId), token); return true; }
        catch { return false; }
    }

    private static string TokenKey(string hostId) => $"githost_token_{hostId}";

    private void Load()
    {
        try { _store = JsonSerializer.Deserialize<List<GitHostConfig>>(_settings.GitHostsJson) ?? []; }
        catch { _store = []; }
    }

    private void Save() => _settings.GitHostsJson = JsonSerializer.Serialize(_store);
}
