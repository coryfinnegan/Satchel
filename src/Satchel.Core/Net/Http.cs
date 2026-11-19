using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Satchel.Core.Net;

public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One shared, pooled HttpClient for the whole app. No telemetry, no third-party hosts.</summary>
public static class Http
{
    public const string UserAgent = "Satchel-AddonManager/1.0";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly HttpClient Client = Create(allowRedirects: true);

    /// <summary>Downloads follow redirects by hand so API keys never travel to a different host.</summary>
    private static readonly HttpClient DownloadClient = Create(allowRedirects: false);

    /// <summary>Hosts that may receive a provider's key header when a download redirects.</summary>
    private static readonly string[] KeyHosts = ["curseforge.com", "forgecdn.net"];

    private static readonly string[] SensitiveHeaders = ["x-api-key", "Authorization"];

    private static HttpClient Create(bool allowRedirects)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false, // no tracking cookies from CDNs
            AllowAutoRedirect = allowRedirects,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(allowRedirects ? 60 : 120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>True for curseforge.com / forgecdn.net and their subdomains (not look-alikes such as evilforgecdn.net).</summary>
    public static bool IsCurseForgeHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        KeyHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private static bool MayCarryKey(Uri from, Uri to) =>
        string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase) || IsCurseForgeHost(to);

    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await DownloadClient.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location == null) return response;
            if (current.Method != HttpMethod.Get) return response; // never replay a POST (or its key) elsewhere
            if (hop >= 8)
            {
                response.Dispose();
                throw new ProviderException("Too many redirects while downloading.");
            }

            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current.RequestUri!, response.Headers.Location);
            response.Dispose();
            if (target.Scheme != Uri.UriSchemeHttps) throw new ProviderException("Refusing to follow a download redirect to a non-HTTPS address.");

            var next = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var header in current.Headers)
            {
                var sensitive = SensitiveHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase);
                if (sensitive && !MayCarryKey(current.RequestUri!, target)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = next;
        }
    }

    public static async Task<T> GetJsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // API calls carry keys too, so they get the same redirect rules as downloads.
        using var response = await SendFollowingRedirectsAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        return result ?? throw new ProviderException($"Empty response from {request.RequestUri?.Host}");
    }

    public static Task<T> GetJsonAsync<T>(string url, CancellationToken ct) =>
        GetJsonAsync<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    public static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var host = response.RequestMessage?.RequestUri?.Host ?? "server";
        var code = (int)response.StatusCode;
        string detail = "";
        try
        {
            detail = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (detail.Length > 200) detail = detail[..200];
        }
        catch { }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when host.Contains("curseforge") || host.Contains("forgecdn")
                => "CurseForge rejected the API key. Check it in Settings.",
            HttpStatusCode.Forbidden when host.Contains("github") => "GitHub rate limit reached. Add a GitHub token in Settings or try again later.",
            HttpStatusCode.NotFound => $"Not found on {host}.",
            (HttpStatusCode)429 => $"{host} is rate limiting requests. Try again in a few minutes.",
            _ => $"{host} returned HTTP {code}{(detail.Length > 0 ? ": " + detail : "")}",
        };
        throw new ProviderException(message);
    }

    /// <summary>Streams a download to disk, reporting progress 0..1.</summary>
    public static async Task DownloadToFileAsync(HttpRequestMessage request, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await SendFollowingRedirectsAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        var lastReport = 0d;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0 && progress != null)
            {
                var p = (double)read / total;
                if (p - lastReport >= 0.02 || p >= 1)
                {
                    lastReport = p;
                    progress.Report(p);
                }
            }
        }
    }
}
