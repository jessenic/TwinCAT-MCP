namespace TwinCatMcp.Docs;

/// <summary>Fetches Beckhoff documentation URLs; null means not found or not reachable.</summary>
public interface IInfosysFetcher
{
    Task<string?> GetAsync(string url, CancellationToken ct);

    Task<byte[]?> GetBytesAsync(string url, CancellationToken ct);
}

/// <summary>
/// HTTP fetcher for infosys.beckhoff.com and Beckhoff's help package server. Infosys serves a stripped page
/// to unknown user agents, so a browser user agent is sent. No other host is ever requested.
/// </summary>
public sealed class HttpInfosysFetcher : IInfosysFetcher, IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _client;

    public HttpInfosysFetcher()
    {
        // Packages are up to a few hundred MB for the largest sections; most are a few MB.
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        // Pages and the catalog are small: fail fast (e.g. offline) instead of waiting for the package timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendAsync(url, timeout.Token);
            return response is { IsSuccessStatusCode: true } ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        return response is { IsSuccessStatusCode: true } ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }

    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        if (!BeckhoffDocs.IsInfosysUrl(url))
            throw new ArgumentException($"Only {BeckhoffDocs.InfosysHost} and {BeckhoffDocs.HelpPackageHost} URLs can be fetched.", nameof(url));

        try
        {
            return await _client.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
