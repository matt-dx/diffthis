using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

/// <summary>
/// Lists pull requests from whichever provider a repository's "origin" remote points to:
/// GitHub / GitHub Enterprise, GitLab / GitLab self-managed, Bitbucket Cloud / Server, Azure
/// DevOps, or Gitea. The provider is detected from the remote's hostname — well-known cloud
/// hosts are recognized automatically; anything else must be registered under Settings → Git
/// Hosts, which also supplies the Personal Access Token.
/// </summary>
public class PullRequestService(
    IGitHubAuthService github,
    IGitLabAuthService gitlab,
    IBitbucketAuthService bitbucket,
    IAzureDevOpsAuthService azureDevOps,
    IGitHostService gitHosts) : IPullRequestService
{
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http = new();

    public async Task<List<PullRequestInfo>> GetPullRequestsAsync(string repositoryPath, PullRequestState state, CancellationToken ct = default)
    {
        var remoteUrl = await GetRemoteUrlAsync(repositoryPath, ct)
            ?? throw new InvalidOperationException("This repository has no 'origin' remote.");

        var host = ExtractHost(remoteUrl)
            ?? throw new InvalidOperationException("Could not parse this repository's remote URL.");

        switch (ResolveWellKnownHost(host))
        {
            case GitProviderKind.GitHub:
                return await GetGitHubAsync("https://api.github.com", remoteUrl, state, ct);
            case GitProviderKind.GitLab:
                return await GetGitLabAsync("https://gitlab.com/api/v4", remoteUrl, state, ct);
            case GitProviderKind.BitbucketCloud:
                return await GetBitbucketCloudAsync("https://api.bitbucket.org/2.0", remoteUrl, state, ct);
            case GitProviderKind.AzureDevOps:
                return await GetAzureDevOpsAsync(remoteUrl, state, ct);
        }

        var selfHosted = gitHosts.FindByHost(host)
            ?? throw new InvalidOperationException(
                $"'{host}' isn't a recognized git host. Add it under Settings → Git Hosts, choosing the matching provider type.");

        var token = await gitHosts.GetTokenAsync(selfHosted.Id)
            ?? throw new InvalidOperationException($"No token saved for '{host}'. Add one under Settings → Git Hosts.");

        return selfHosted.Kind switch
        {
            GitProviderKind.GitHubEnterprise  => await GetGitHubAsync($"https://{host}/api/v3", remoteUrl, state, ct, token),
            GitProviderKind.GitLabSelfManaged => await GetGitLabAsync($"https://{host}/api/v4", remoteUrl, state, ct, token),
            GitProviderKind.BitbucketServer   => await GetBitbucketServerAsync($"https://{host}", remoteUrl, state, ct, token),
            GitProviderKind.Gitea             => await GetGiteaAsync($"https://{host}/api/v1", remoteUrl, state, ct, token),
            _ => throw new InvalidOperationException($"Unsupported provider type for host '{host}'."),
        };
    }

    /// Resolves which provider a repository's remote belongs to, without making any API call —
    /// used to decide whether an inline sign-in prompt is possible before attempting a fetch.
    /// Returns null when the host isn't a recognized cloud host and has no matching entry under
    /// Settings → Git Hosts (meaning the user must add one there first).
    public async Task<PullRequestProviderInfo?> DetectProviderAsync(string repositoryPath, CancellationToken ct = default)
    {
        var remoteUrl = await GetRemoteUrlAsync(repositoryPath, ct);
        if (string.IsNullOrEmpty(remoteUrl)) return null;

        var host = ExtractHost(remoteUrl);
        if (string.IsNullOrEmpty(host)) return null;

        var wellKnown = ResolveWellKnownHost(host);
        if (wellKnown is not null) return new PullRequestProviderInfo(wellKnown.Value, host, null);

        var selfHosted = gitHosts.FindByHost(host);
        return selfHosted is not null ? new PullRequestProviderInfo(selfHosted.Kind, host, selfHosted.Id) : null;
    }

    private static GitProviderKind? ResolveWellKnownHost(string host)
    {
        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)) return GitProviderKind.GitHub;
        if (string.Equals(host, "gitlab.com", StringComparison.OrdinalIgnoreCase)) return GitProviderKind.GitLab;
        if (string.Equals(host, "bitbucket.org", StringComparison.OrdinalIgnoreCase)) return GitProviderKind.BitbucketCloud;
        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)) return GitProviderKind.AzureDevOps;
        return null;
    }

    // ── GitHub / GitHub Enterprise / Gitea (near-identical REST shape) ──────

    private async Task<List<PullRequestInfo>> GetGitHubAsync(
        string apiBase, string remoteUrl, PullRequestState state, CancellationToken ct, string? overrideToken = null)
    {
        var token = overrideToken ?? await github.GetTokenAsync()
            ?? throw new InvalidOperationException("Not signed in to GitHub. Open Settings to connect your GitHub account.");

        var (owner, repo) = ParseOwnerRepo(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the owner/repository from this repository's remote.");

        var ghState = state == PullRequestState.Open ? "open" : "closed";
        var url = $"{apiBase}/repos/{owner}/{repo}/pulls?state={ghState}&per_page=100&sort=updated&direction=desc";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"token {token}");
        req.Headers.Add("User-Agent",    "DiffThis/1.0");
        req.Headers.Add("Accept",        "application/vnd.github+json");

        var resp = await SendAsync(req, "GitHub", ct);
        var dtos = await resp.Content.ReadFromJsonAsync<List<GitHubStylePrDto>>(SnakeCase, ct) ?? [];
        var repoFullName = $"{owner}/{repo}";

        return dtos
            .Select(d => new PullRequestInfo
            {
                Provider          = GitProviderKind.GitHub,
                Number            = d.Number,
                Title             = d.Title,
                Author            = d.User?.Login ?? string.Empty,
                BaseRefName       = d.Base?.Ref ?? string.Empty,
                HeadRefName       = d.Head?.Ref ?? string.Empty,
                SourceRemoteRef   = $"refs/pull/{d.Number}/head",
                IsCrossRepository = !string.Equals(d.Head?.Repo?.FullName, repoFullName, StringComparison.OrdinalIgnoreCase),
                IsDraft           = d.Draft,
                IsMerged          = d.MergedAt is not null,
                UpdatedAt         = d.UpdatedAt,
            })
            .ToList();
    }

    private async Task<List<PullRequestInfo>> GetGiteaAsync(
        string apiBase, string remoteUrl, PullRequestState state, CancellationToken ct, string token)
    {
        var (owner, repo) = ParseOwnerRepo(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the owner/repository from this repository's remote.");

        var giteaState = state == PullRequestState.Open ? "open" : "closed";
        var url = $"{apiBase}/repos/{owner}/{repo}/pulls?state={giteaState}&limit=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"token {token}");
        req.Headers.Add("Accept",        "application/json");

        var resp = await SendAsync(req, "Gitea", ct);
        var dtos = await resp.Content.ReadFromJsonAsync<List<GitHubStylePrDto>>(SnakeCase, ct) ?? [];
        var repoFullName = $"{owner}/{repo}";

        return dtos
            .Select(d => new PullRequestInfo
            {
                Provider          = GitProviderKind.Gitea,
                Number            = d.Number,
                Title             = d.Title,
                Author            = d.User?.Login ?? string.Empty,
                BaseRefName       = d.Base?.Ref ?? string.Empty,
                HeadRefName       = d.Head?.Ref ?? string.Empty,
                SourceRemoteRef   = $"refs/pull/{d.Number}/head",
                IsCrossRepository = !string.Equals(d.Head?.Repo?.FullName, repoFullName, StringComparison.OrdinalIgnoreCase),
                IsDraft           = d.Draft,
                IsMerged          = d.MergedAt is not null,
                UpdatedAt         = d.UpdatedAt,
            })
            .ToList();
    }

    // ── GitLab / GitLab self-managed ─────────────────────────────────────────

    private async Task<List<PullRequestInfo>> GetGitLabAsync(
        string apiBase, string remoteUrl, PullRequestState state, CancellationToken ct, string? overrideToken = null)
    {
        var token = overrideToken ?? await gitlab.GetTokenAsync()
            ?? throw new InvalidOperationException("Not signed in to GitLab. Open Settings to connect your GitLab account.");

        // Unlike GitHub/Bitbucket/Gitea, GitLab allows arbitrarily nested subgroups, so the
        // project path can be more than two segments — the full path is the project ID.
        var fullPath = ParseFullPath(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the project path from this repository's remote.");
        var projectId = Uri.EscapeDataString(fullPath);

        // GitLab's "closed" state excludes merged MRs — fetch both and combine for our Closed tab.
        var states = state == PullRequestState.Open ? ["opened"] : new[] { "closed", "merged" };

        var all = new List<GitLabMrDto>();
        foreach (var s in states)
        {
            var url = $"{apiBase}/projects/{projectId}/merge_requests?state={s}&per_page=100&order_by=updated_at&sort=desc";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await SendAsync(req, "GitLab", ct);
            all.AddRange(await resp.Content.ReadFromJsonAsync<List<GitLabMrDto>>(SnakeCase, ct) ?? []);
        }

        return all
            .Select(d => new PullRequestInfo
            {
                Provider          = GitProviderKind.GitLab,
                Number            = d.Iid,
                Title             = d.Title,
                Author            = d.Author?.Username ?? string.Empty,
                BaseRefName       = d.TargetBranch,
                HeadRefName       = d.SourceBranch,
                SourceRemoteRef   = $"refs/merge-requests/{d.Iid}/head",
                IsCrossRepository = d.SourceProjectId.HasValue && d.ProjectId.HasValue && d.SourceProjectId != d.ProjectId,
                IsDraft           = d.Draft,
                IsMerged          = string.Equals(d.State, "merged", StringComparison.OrdinalIgnoreCase),
                UpdatedAt         = d.UpdatedAt,
            })
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    // ── Bitbucket Cloud ──────────────────────────────────────────────────────

    private async Task<List<PullRequestInfo>> GetBitbucketCloudAsync(
        string apiBase, string remoteUrl, PullRequestState state, CancellationToken ct)
    {
        var authHeader = await bitbucket.GetAuthHeaderAsync()
            ?? throw new InvalidOperationException("Not signed in to Bitbucket. Open Settings to connect your Bitbucket account.");

        var (workspace, repo) = ParseOwnerRepo(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the workspace/repository from this repository's remote.");

        var states = state == PullRequestState.Open ? ["OPEN"] : new[] { "MERGED", "DECLINED" };
        var stateQuery = string.Join("&", states.Select(s => $"state={s}"));
        var url = $"{apiBase}/repositories/{workspace}/{repo}/pullrequests?{stateQuery}&pagelen=50";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = authHeader;
        req.Headers.Add("Accept", "application/json");

        var resp = await SendAsync(req, "Bitbucket", ct);
        var page = await resp.Content.ReadFromJsonAsync<BitbucketPageDto>(SnakeCase, ct);
        var repoFullName = $"{workspace}/{repo}";

        return (page?.Values ?? [])
            .Select(d =>
            {
                var isCrossRepo = !string.Equals(d.Source?.Repository?.FullName, repoFullName, StringComparison.OrdinalIgnoreCase);
                return new PullRequestInfo
                {
                    Provider          = GitProviderKind.BitbucketCloud,
                    Number            = d.Id,
                    Title             = d.Title,
                    Author            = d.Author?.DisplayName ?? string.Empty,
                    BaseRefName       = d.Destination?.Branch?.Name ?? string.Empty,
                    HeadRefName       = d.Source?.Branch?.Name ?? string.Empty,
                    // Unlike Bitbucket Server, Bitbucket Cloud has no "refs/pull-requests/N/from"
                    // synthetic ref. While open, the source branch itself is still fetchable by name;
                    // once merged it may be deleted, so fall back to the exact source commit SHA
                    // instead (GitService falls back further still, to the merge commit, and then to
                    // checking whether the source commit came along for free with the destination
                    // branch's own history).
                    SourceRemoteRef   = string.Equals(d.State, "MERGED", StringComparison.OrdinalIgnoreCase)
                        && d.Source?.Commit?.Hash is { Length: > 0 } sha
                            ? sha
                            : d.Source?.Branch?.Name ?? string.Empty,
                    // Recorded regardless of state — for a MERGED PR the fetch above already uses
                    // it directly; for a DECLINED one it's the exact content the PR had, pinning
                    // the diff against a branch that may have moved on or been reused since.
                    SourcePinnedCommit = d.Source?.Commit?.Hash,
                    MergeCommitSha    = d.MergeCommit?.Hash,
                    SourceAvailability = string.Equals(d.State, "OPEN", StringComparison.OrdinalIgnoreCase)
                        ? PullRequestSourceAvailability.Available
                        : d.MergeCommit?.Hash is { Length: > 0 }
                            ? PullRequestSourceAvailability.ReconstructableFromMergeCommit
                            : PullRequestSourceAvailability.Unknown,
                    SourceRemoteUrl   = isCrossRepo ? BuildCloneUrl(remoteUrl, d.Source?.Repository?.FullName) : null,
                    IsCrossRepository = isCrossRepo,
                    IsDraft           = false, // Bitbucket Cloud has no draft-PR concept
                    IsMerged          = string.Equals(d.State, "MERGED", StringComparison.OrdinalIgnoreCase),
                    UpdatedAt         = d.UpdatedOn,
                };
            })
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    // ── Bitbucket Server / Data Center ──────────────────────────────────────

    private async Task<List<PullRequestInfo>> GetBitbucketServerAsync(
        string apiBase, string remoteUrl, PullRequestState state, CancellationToken ct, string token)
    {
        var (project, repo) = ParseOwnerRepo(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the project/repository from this repository's remote.");

        // Filtering state=ALL client-side after limit=100 can bury an older PR of the requested
        // state behind up to 100 newer PRs of another state. Query each relevant state server-side
        // instead — the API only accepts one state per request.
        var states = state == PullRequestState.Open ? ["OPEN"] : new[] { "MERGED", "DECLINED" };

        var dtos = new List<BitbucketServerPrDto>();
        foreach (var s in states)
        {
            var url = $"{apiBase}/rest/api/1.0/projects/{project}/repos/{repo}/pull-requests?state={s}&limit=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await SendAsync(req, "Bitbucket Server", ct);
            var page = await resp.Content.ReadFromJsonAsync<BitbucketServerPageDto>(CamelCase, ct);
            dtos.AddRange(page?.Values ?? []);
        }

        return dtos
            .Select(d => new PullRequestInfo
            {
                Provider        = GitProviderKind.BitbucketServer,
                Number          = d.Id,
                Title           = d.Title,
                Author          = d.Author?.User?.DisplayName ?? string.Empty,
                BaseRefName     = d.ToRef?.DisplayId ?? string.Empty,
                HeadRefName     = d.FromRef?.DisplayId ?? string.Empty,
                SourceRemoteRef = $"refs/pull-requests/{d.Id}/from",
                IsDraft         = false, // Bitbucket Server has no draft-PR concept
                IsMerged        = string.Equals(d.State, "MERGED", StringComparison.OrdinalIgnoreCase),
                UpdatedAt       = DateTimeOffset.FromUnixTimeMilliseconds(d.UpdatedDate),
            })
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    // ── Azure DevOps ─────────────────────────────────────────────────────────

    private async Task<List<PullRequestInfo>> GetAzureDevOpsAsync(string remoteUrl, PullRequestState state, CancellationToken ct)
    {
        var authHeader = await azureDevOps.GetAuthHeaderAsync()
            ?? throw new InvalidOperationException("Not signed in to Azure DevOps. Open Settings to connect your Azure DevOps account.");

        var (org, project, repo) = ParseAzureDevOpsRemote(remoteUrl)
            ?? throw new InvalidOperationException("Could not determine the organization/project/repository from this repository's remote.");

        // Filtering searchCriteria.status=all client-side after $top=100 can bury an older PR of
        // the requested status behind up to 100 newer PRs of another status. Query each relevant
        // status server-side instead.
        var statuses = state == PullRequestState.Open ? ["active"] : new[] { "completed", "abandoned" };

        var dtos = new List<AdoPrDto>();
        foreach (var s in statuses)
        {
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullrequests?searchCriteria.status={s}&api-version=7.1&$top=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = authHeader;

            var resp = await SendAsync(req, "Azure DevOps", ct);
            var page = await resp.Content.ReadFromJsonAsync<AdoPageDto>(CamelCase, ct);
            dtos.AddRange(page?.Value ?? []);
        }

        return dtos
            .Select(d => new PullRequestInfo
            {
                Provider        = GitProviderKind.AzureDevOps,
                Number          = d.PullRequestId,
                Title           = d.Title,
                Author          = d.CreatedBy?.DisplayName ?? string.Empty,
                BaseRefName     = StripRefsHeads(d.TargetRefName),
                HeadRefName     = StripRefsHeads(d.SourceRefName),
                // Azure DevOps PRs have no fork ref. While active, the source branch itself is
                // still fetchable by name (Azure DevOps won't fetch an arbitrary commit SHA
                // directly even when it IS the live branch tip); once completed the branch is
                // commonly deleted, so fall back to the exact source commit SHA instead
                // (GitService falls back further still, to checking whether that commit came
                // along for free while fetching the now-containing target branch).
                SourceRemoteRef = string.Equals(d.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && d.LastMergeSourceCommit?.CommitId is { Length: > 0 } sha
                        ? sha
                        : d.SourceRefName,
                // Recorded regardless of status — for a completed PR the fetch above already uses
                // it directly; for an abandoned one it's the exact content the PR had, pinning the
                // diff against a branch that may have moved on or been reused since.
                SourcePinnedCommit = d.LastMergeSourceCommit?.CommitId,
                MergeCommitSha  = d.LastMergeCommit?.CommitId,
                SourceAvailability = string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase)
                    ? PullRequestSourceAvailability.Available
                    : d.LastMergeCommit?.CommitId is { Length: > 0 }
                        ? PullRequestSourceAvailability.ReconstructableFromMergeCommit
                        : PullRequestSourceAvailability.Unknown,
                SourceRemoteUrl = d.ForkSource?.Repository?.RemoteUrl,
                IsCrossRepository = d.ForkSource is not null,
                IsDraft         = d.IsDraft,
                IsMerged        = string.Equals(d.Status, "completed", StringComparison.OrdinalIgnoreCase),
                UpdatedAt       = d.CreationDate,
            })
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    /// Builds a fork's clone URL by substituting its full path into origin's own host and scheme
    /// (SSH or HTTPS) — a fork lives on the same host, and matching origin's scheme means the
    /// same locally-configured git credentials (SSH key or credential helper) apply to it too.
    private static string? BuildCloneUrl(string originUrl, string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;

        var scp = Regex.Match(originUrl, @"^(?<user>[\w.-]+)@(?<host>[^:/]+):");
        if (scp.Success) return $"{scp.Groups["user"].Value}@{scp.Groups["host"].Value}:{fullName}.git";

        if (!Uri.TryCreate(originUrl, UriKind.Absolute, out var uri)) return null;
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? "" : $"{uri.UserInfo}@";
        var port     = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{userInfo}{uri.Host}{port}/{fullName}.git";
    }

    private static string StripRefsHeads(string? refName) =>
        refName?.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) == true
            ? refName["refs/heads/".Length..]
            : refName ?? string.Empty;

    // ── Shared HTTP / remote-parsing helpers ────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string providerName, CancellationToken ct)
    {
        var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return resp;

        if ((int)resp.StatusCode is 401 or 403)
            throw new InvalidOperationException($"{providerName} rejected the stored credentials. Sign in again from Settings.");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{providerName} repository was not found, or the signed-in account can't access it.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"{providerName} API request failed: HTTP {(int)resp.StatusCode} {body}".Trim());
    }

    // See GitService.RunGitAsync for why CreateNoWindow is required when spawning console
    // subsystem binaries (like git) from a GUI process such as MAUI.
    private static async Task<string?> GetRemoteUrlAsync(string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            WorkingDirectory       = workingDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("remote");
        psi.ArgumentList.Add("get-url");
        psi.ArgumentList.Add("origin");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var output = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
        await proc.WaitForExitAsync(ct);

        return proc.ExitCode == 0 && output.Length > 0 ? output : null;
    }

    private static string? ExtractHost(string url)
    {
        var scp = Regex.Match(url, @"^[\w.-]+@(?<host>[^:/]+)[:/]");
        if (scp.Success) return scp.Groups["host"].Value;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// Parses the last two path segments of a remote URL as (owner, repo) — the shape shared by
    /// GitHub, GitLab, Bitbucket Cloud/Server, and Gitea remotes.
    private static (string Owner, string Repo)? ParseOwnerRepo(string url)
    {
        string path;

        var scp = Regex.Match(url, @"^[\w.-]+@[^:/]+:(?<path>.+)$");
        if (scp.Success)
        {
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath.TrimStart('/');
        }
        else return null;

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[^2], parts[^1]) : null;
    }

    /// Parses a remote URL's full path (e.g. "group/subgroup/project"), used for GitLab, which
    /// allows arbitrarily nested subgroups unlike the strictly two-segment "owner/repo" providers.
    private static string? ParseFullPath(string url)
    {
        string path;

        var scp = Regex.Match(url, @"^[\w.-]+@[^:/]+:(?<path>.+)$");
        if (scp.Success)
        {
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath.TrimStart('/');
        }
        else return null;

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        return path.Length > 0 ? path : null;
    }

    private static (string Org, string Project, string Repo)? ParseAzureDevOpsRemote(string url)
    {
        // SSH: git@ssh.dev.azure.com:v3/org/project/repo
        var ssh = Regex.Match(url, @"^[\w.-]+@ssh\.dev\.azure\.com:v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+)$");
        if (ssh.Success)
            return (ssh.Groups["org"].Value, Uri.UnescapeDataString(ssh.Groups["project"].Value), Uri.UnescapeDataString(ssh.Groups["repo"].Value));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gitIdx = Array.IndexOf(segments, "_git");
        if (gitIdx < 1 || gitIdx + 1 >= segments.Length) return null;

        if (string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // /{org}/{project}/_git/{repo}
            if (gitIdx < 2) return null;
            return (segments[0], Uri.UnescapeDataString(segments[gitIdx - 1]), Uri.UnescapeDataString(segments[gitIdx + 1]));
        }

        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            // {org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}
            var org = uri.Host.Split('.')[0];
            return (org, Uri.UnescapeDataString(segments[gitIdx - 1]), Uri.UnescapeDataString(segments[gitIdx + 1]));
        }

        return null;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed class GitHubStylePrDto
    {
        public int Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public GitHubUserDto? User { get; set; }
        public GitHubRefDto? Base { get; set; }
        public GitHubRefDto? Head { get; set; }
        public bool Draft { get; set; }
        public DateTimeOffset? MergedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class GitHubUserDto { public string Login { get; set; } = string.Empty; }

    private sealed class GitHubRefDto
    {
        public string Ref { get; set; } = string.Empty;
        public GitHubRepoDto? Repo { get; set; }
    }

    private sealed class GitHubRepoDto { public string FullName { get; set; } = string.Empty; }

    private sealed class GitLabMrDto
    {
        public int Iid { get; set; }
        public string Title { get; set; } = string.Empty;
        public GitLabUserDto? Author { get; set; }
        public string SourceBranch { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public bool Draft { get; set; }
        public string State { get; set; } = string.Empty;
        public int? ProjectId { get; set; }
        public int? SourceProjectId { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class GitLabUserDto { public string Username { get; set; } = string.Empty; }

    private sealed class BitbucketPageDto { public List<BitbucketPrDto> Values { get; set; } = []; }

    private sealed class BitbucketPrDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public BitbucketUserDto? Author { get; set; }
        public BitbucketEndpointDto? Source { get; set; }
        public BitbucketEndpointDto? Destination { get; set; }
        public BitbucketCommitDto? MergeCommit { get; set; }
        public string State { get; set; } = string.Empty;
        public DateTimeOffset UpdatedOn { get; set; }
    }

    private sealed class BitbucketUserDto { public string DisplayName { get; set; } = string.Empty; }

    private sealed class BitbucketEndpointDto
    {
        public BitbucketBranchDto? Branch { get; set; }
        public BitbucketCommitDto? Commit { get; set; }
        public BitbucketRepoDto? Repository { get; set; }
    }

    private sealed class BitbucketBranchDto { public string Name { get; set; } = string.Empty; }
    private sealed class BitbucketCommitDto { public string Hash { get; set; } = string.Empty; }
    private sealed class BitbucketRepoDto { public string FullName { get; set; } = string.Empty; }

    private sealed class BitbucketServerPageDto { public List<BitbucketServerPrDto> Values { get; set; } = []; }

    private sealed class BitbucketServerPrDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public BitbucketServerAuthorDto? Author { get; set; }
        public BitbucketServerRefDto? FromRef { get; set; }
        public BitbucketServerRefDto? ToRef { get; set; }
        public string State { get; set; } = string.Empty;
        public long UpdatedDate { get; set; }
    }

    private sealed class BitbucketServerAuthorDto { public BitbucketServerUserDto? User { get; set; } }
    private sealed class BitbucketServerUserDto { public string DisplayName { get; set; } = string.Empty; }
    private sealed class BitbucketServerRefDto { public string DisplayId { get; set; } = string.Empty; }

    private sealed class AdoPageDto { public List<AdoPrDto> Value { get; set; } = []; }

    private sealed class AdoPrDto
    {
        public int PullRequestId { get; set; }
        public string Title { get; set; } = string.Empty;
        public AdoUserDto? CreatedBy { get; set; }
        public string SourceRefName { get; set; } = string.Empty;
        public string TargetRefName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsDraft { get; set; }
        public DateTimeOffset CreationDate { get; set; }
        public AdoCommitRefDto? LastMergeSourceCommit { get; set; }
        public AdoCommitRefDto? LastMergeCommit { get; set; }
        public AdoForkSourceDto? ForkSource { get; set; }
    }

    private sealed class AdoCommitRefDto { public string CommitId { get; set; } = string.Empty; }

    private sealed class AdoUserDto { public string DisplayName { get; set; } = string.Empty; }

    private sealed class AdoForkSourceDto { public AdoRepoDto? Repository { get; set; } }

    private sealed class AdoRepoDto { public string? RemoteUrl { get; set; } }
}
