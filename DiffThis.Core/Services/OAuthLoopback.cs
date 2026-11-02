using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DiffThis.Core.Services;

/// <summary>
/// Shared plumbing for native-app OAuth 2.0 Authorization Code sign-in: generates a PKCE
/// verifier/challenge pair, opens the system browser to the provider's authorize page, and runs
/// a one-shot local HTTP listener on the loopback interface to catch its redirect — the standard
/// "sign in with a browser" pattern for desktop apps (RFC 8252), needing no client secret.
/// </summary>
internal static class OAuthLoopback
{
    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        var verifier  = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    public static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    /// Starts an <see cref="HttpListener"/> on the first of <paramref name="candidatePorts"/>
    /// that's free, listening at <c>http://localhost:{port}{path}</c>.
    public static (HttpListener Listener, int Port) StartListener(IReadOnlyList<int> candidatePorts, string path)
    {
        Exception? last = null;
        foreach (var port in candidatePorts)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}{path}");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex)
            {
                last = ex;
                listener.Close();
            }
        }
        throw new InvalidOperationException($"Could not open a local port for sign-in: {last?.Message}");
    }

    /// 2000 random ephemeral ports to try, so concurrent sign-ins or a port already in use by
    /// something else don't make <see cref="StartListener"/> fail outright.
    public static IReadOnlyList<int> RandomEphemeralPorts(int count = 20)
    {
        var rng = Random.Shared;
        return Enumerable.Range(0, count).Select(_ => rng.Next(49152, 65535)).Distinct().ToList();
    }

    /// Waits for exactly one redirect on <paramref name="listener"/>, replies with a small HTML
    /// page, and returns the <c>code</c>/<c>state</c> query parameters — or throws with the
    /// provider's <c>error</c>/<c>error_description</c> if the user denied consent.
    public static async Task<(string Code, string? State)> WaitForRedirectAsync(
        HttpListener listener, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var contextTask = listener.GetContextAsync();
            _ = contextTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            var completed = await Task.WhenAny(contextTask, Task.Delay(timeout, ct));
            if (completed != contextTask)
                throw new OperationCanceledException("Sign-in timed out waiting for the browser.");

            var context = await contextTask;
            var query = context.Request.QueryString;
            var error = query["error"];
            var code  = query["code"];
            var state = query["state"];

            var (title, message) = string.IsNullOrEmpty(error)
                ? ("Signed in", "You can close this window and return to DiffThis.")
                : ("Sign-in failed", query["error_description"] ?? error);
            await WriteHtmlResponseAsync(context, title, message);

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException(query["error_description"] ?? error);
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("No authorization code was returned.");

            return (code, state);
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { /* best effort */ }
        }
    }

    private static async Task WriteHtmlResponseAsync(HttpListenerContext context, string title, string message)
    {
        var safeTitle   = WebUtility.HtmlEncode(title);
        var safeMessage = WebUtility.HtmlEncode(message);
        var html = $@"
            <!doctype html><html><head><meta charset=""utf-8""><title>{safeTitle}</title>
            <style>body{{font-family:system-ui,sans-serif;background:#1e1e1e;color:#eee;display:flex;
            align-items:center;justify-content:center;height:100vh;margin:0}}
            .card{{text-align:center;padding:32px}}</style></head>
            <body><div class=""card""><h2>{safeTitle}</h2><p>{safeMessage}</p></div></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.OutputStream.Close();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
