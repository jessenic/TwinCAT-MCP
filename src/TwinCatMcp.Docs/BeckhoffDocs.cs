using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwinCatMcp.Docs;

public sealed record DocEntry(string Title, string Url, string? Parent);

public sealed record SectionIndex(string Section, DateTimeOffset CrawledUtc, bool Complete, int Fetches, IReadOnlyList<DocEntry> Entries);

public sealed record DocHit(string Title, string Url, string? Parent, string Section, int Score);

public sealed record DocPage(string Url, string? Title, string Text, bool Truncated, int TotalChars);

/// <summary>
/// Beckhoff Infosys lookup without an external search engine. Infosys page URLs are opaque numeric IDs,
/// so a section's title-to-URL index is built by walking Infosys's own navigation tree:
/// <c>content/1033/&lt;section&gt;/index.html</c> carries a <c>primaryid</c> meta tag, and
/// <c>english/menu/menu.php?content=../content/1033/&lt;section&gt;/&lt;file&gt;&amp;id=&lt;id&gt;</c> returns
/// that node's menu with its children. The crawl is bounded (fetches and depth), sequential with a polite
/// delay, and cached on disk, so only the first lookup in a section is slow. Approach after TcKit's
/// InfosysNavigator (https://github.com/georgeturneruk/tckit, MIT).
/// </summary>
public sealed partial class BeckhoffDocs
{
    public const string InfosysHost = "https://infosys.beckhoff.com";
    private const string MenuUrl = InfosysHost + "/english/menu/menu.php";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IInfosysFetcher _fetcher;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _politeDelay;
    private readonly TimeSpan _cacheLifetime;
    private readonly SemaphoreSlim _crawlLock = new(1, 1);

    public BeckhoffDocs(IInfosysFetcher fetcher, string cacheDirectory, TimeSpan? politeDelay = null, TimeSpan? cacheLifetime = null)
    {
        _fetcher = fetcher;
        _cacheDirectory = cacheDirectory;
        _politeDelay = politeDelay ?? TimeSpan.FromMilliseconds(150);
        _cacheLifetime = cacheLifetime ?? TimeSpan.FromDays(30);
    }

    public string CacheDirectory => _cacheDirectory;

    public static bool IsInfosysUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "infosys.beckhoff.com", StringComparison.OrdinalIgnoreCase);

    public bool IsCached(string section) => File.Exists(CachePath(section));

    /// <summary>Returns the cached index, crawling the section first when it isn't cached or is stale.</summary>
    public async Task<SectionIndex?> GetIndexAsync(string section, int maxFetches, int maxDepth, CancellationToken ct)
    {
        if (!InfosysSections.IsValidSlug(section))
            throw new ArgumentException($"'{section}' is not an Infosys section slug (lowercase letters, digits, '_' and '-').", nameof(section));

        if (TryLoad(section) is { } cached && DateTimeOffset.UtcNow - cached.CrawledUtc < _cacheLifetime)
            return cached;

        await _crawlLock.WaitAsync(ct);
        try
        {
            if (TryLoad(section) is { } again && DateTimeOffset.UtcNow - again.CrawledUtc < _cacheLifetime)
                return again;

            var index = await CrawlAsync(section, maxFetches, maxDepth, ct);
            if (index is not null && index.Entries.Count > 0)
                Save(index);
            return index;
        }
        finally
        {
            _crawlLock.Release();
        }
    }

    public static IReadOnlyList<DocHit> Match(SectionIndex index, string query, int maxResults)
    {
        var q = Normalize(query);
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];

        return index.Entries
            .Select(e => (Entry: e, Title: Normalize(e.Title), Context: Normalize(e.Parent ?? "")))
            .Where(x => tokens.All(t => x.Title.Contains(t, StringComparison.Ordinal) || x.Context.Contains(t, StringComparison.Ordinal)))
            .Select(x => new DocHit(x.Entry.Title, x.Entry.Url, x.Entry.Parent, index.Section,
                x.Title == q ? 4 : x.Title.StartsWith(q, StringComparison.Ordinal) ? 3 : tokens.All(t => x.Title.Contains(t, StringComparison.Ordinal)) ? 2 : 1))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title.Length)
            .Take(maxResults)
            .ToArray();
    }

    public async Task<DocPage?> GetPageAsync(string url, int maxChars, CancellationToken ct)
    {
        if (!IsInfosysUrl(url))
            throw new ArgumentException($"Only {InfosysHost} pages can be fetched.", nameof(url));

        var html = await _fetcher.GetAsync(url, ct);
        if (html is null)
            return null;

        var title = TitleTag().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value) : null;
        var text = HtmlToText(html);
        var truncated = text.Length > maxChars;
        return new DocPage(url, title, truncated ? text[..maxChars] : text, truncated, text.Length);
    }

    /// <summary>Converts an Infosys page to readable text, keeping table rows as "a | b | c" lines.</summary>
    public static string HtmlToText(string html)
    {
        var body = BodyTag().Match(html) is { Success: true } b ? b.Groups[1].Value : html;
        body = ScriptOrStyle().Replace(body, " ");
        body = CellEnd().Replace(body, " | ");
        body = BlockBreak().Replace(body, "\n");
        body = AnyTag().Replace(body, "");
        body = WebUtility.HtmlDecode(body);

        var lines = body.Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim().TrimEnd('|').Trim())
            .Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    private async Task<SectionIndex?> CrawlAsync(string section, int maxFetches, int maxDepth, CancellationToken ct)
    {
        var fetches = 1;
        var indexHtml = await _fetcher.GetAsync($"{InfosysHost}/content/1033/{section}/index.html", ct);
        if (indexHtml is null || PrimaryId().Match(indexHtml) is not { Success: true } pid)
            return null;

        var entries = new List<DocEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html" };
        var queue = new Queue<(string File, string Id, int Depth, string? Title)>();
        queue.Enqueue(("index.html", pid.Groups[1].Value, 0, null));
        var complete = true;

        while (queue.Count > 0)
        {
            if (fetches >= maxFetches)
            {
                complete = false;
                break;
            }

            var (file, id, depth, title) = queue.Dequeue();
            await Task.Delay(_politeDelay, ct);
            fetches++;
            var menu = await _fetcher.GetAsync(
                $"{MenuUrl}?content={Uri.EscapeDataString($"../content/1033/{section}/{file}")}&id={Uri.EscapeDataString(id)}", ct);
            if (menu is null)
                continue;

            foreach (var (childFile, childId, childTitle) in ParseMenu(menu, section))
            {
                if (!seen.Add(childFile))
                    continue;

                entries.Add(new DocEntry(childTitle, $"{InfosysHost}/content/1033/{section}/{childFile}", title));
                if (depth + 1 < maxDepth && childId is not null)
                    queue.Enqueue((childFile, childId, depth + 1, childTitle));
                else if (childId is not null)
                    complete = false;
            }
        }

        return new SectionIndex(section, DateTimeOffset.UtcNow, complete, fetches, entries);
    }

    /// <summary>In-section links of a menu.php reply as (file, id, title); the section's own index.html is skipped.</summary>
    public static IEnumerable<(string File, string? Id, string Title)> ParseMenu(string menuHtml, string section)
    {
        var marker = $"/content/1033/{section}/";
        foreach (Match a in Anchor().Matches(menuHtml))
        {
            var href = WebUtility.HtmlDecode(a.Groups[1].Value);
            var title = Clean(a.Groups[2].Value);
            var at = href.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (title.Length == 0 || at < 0)
                continue;

            var rest = href[(at + marker.Length)..];
            var query = rest.IndexOf('?');
            var file = query >= 0 ? rest[..query] : rest;
            if (file.Length == 0 || file.Equals("index.html", StringComparison.OrdinalIgnoreCase) || file.Contains('/'))
                continue;

            var id = IdParam().Match(rest) is { Success: true } m ? m.Groups[1].Value : null;
            yield return (file, id, title);
        }
    }

    private string CachePath(string section) => Path.Combine(_cacheDirectory, section + ".json");

    private SectionIndex? TryLoad(string section)
    {
        try
        {
            var path = CachePath(section);
            return File.Exists(path) ? JsonSerializer.Deserialize<SectionIndex>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(SectionIndex index)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(CachePath(index.Section), JsonSerializer.Serialize(index, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache write failure only costs a re-crawl next time.
        }
    }

    private static string Normalize(string text) => Spaces().Replace(text.ToLowerInvariant(), " ").Trim();

    private static string Clean(string html) => Spaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, "")), " ").Trim();

    [GeneratedRegex(@"<meta\s+name=""primaryid""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PrimaryId();

    [GeneratedRegex(@"<a\s[^>]*?href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"[?&]id=(\d+)")]
    private static partial Regex IdParam();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyTag();

    [GeneratedRegex(@"<(script|style|noscript)\b.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnd();

    [GeneratedRegex(@"<br\s*/?>|</?(p|div|tr|li|ul|ol|table|h[1-6]|pre|section|article)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();
}
