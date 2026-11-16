namespace TwinCatMcp.Docs;

/// <summary>Fetches one Beckhoff Infosys URL; null means not found or not reachable.</summary>
public interface IInfosysFetcher
{
    Task<string?> GetAsync(string url, CancellationToken ct);
}

/// <summary>
/// HTTP fetcher for infosys.beckhoff.com. Infosys serves a stripped page to unknown user agents, so a
/// browser user agent is sent. Only the Infosys host is ever requested.
/// </summary>
public sealed class HttpInfosysFetcher : IInfosysFetcher, IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _client;

    public HttpInfosysFetcher()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        if (!BeckhoffDocs.IsInfosysUrl(url))
            throw new ArgumentException($"Only {BeckhoffDocs.InfosysHost} URLs can be fetched.", nameof(url));

        try
        {
            using var response = await _client.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
