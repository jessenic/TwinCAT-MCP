using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwinCatMcp.Docs;

/// <summary>One documentation page. <see cref="Keywords"/> holds the page's F1 keywords (packages only).</summary>
public sealed record DocEntry(string Title, string Url, string? Parent, IReadOnlyList<string>? Keywords = null);

/// <summary>
/// A section's title index. <see cref="Source"/> is "package" (built from Beckhoff's help package, dated
/// <see cref="ContentDate"/>) or "crawl" (built by walking the online Infosys menu; null in old caches).
/// </summary>
public sealed record SectionIndex(
    string Section,
    DateTimeOffset CrawledUtc,
    bool Complete,
    int Fetches,
    IReadOnlyList<DocEntry> Entries,
    string? Source = null,
    DateTimeOffset? ContentDate = null);

public sealed record DocHit(string Title, string Url, string? Parent, string Section, int Score, string? Source, DateTimeOffset? ContentDate);

public sealed record DocPage(string Url, string? Title, string Text, bool Truncated, int TotalChars, string Source, DateTimeOffset? ContentDate);

/// <summary>
/// Beckhoff Infosys lookup without an external search engine.
///
/// Preferred source: Beckhoff's Help Viewer packages on help3.twincat3.infosys.beckhoff.com, one CAB per
/// documentation section (see <see cref="BeckhoffDocs"/>'s package members). A package holds every page of
/// the section with its title, parent page and F1 keywords, so one download gives an exact, complete index
/// and the pages themselves, usable offline afterwards.
///
/// Fallback for sections without a package (EtherCAT terminals and other hardware): walk Infosys's own
/// navigation tree. <c>content/1033/&lt;section&gt;/index.html</c> carries a <c>primaryid</c> meta tag, and
/// <c>english/menu/menu.php?content=../content/1033/&lt;section&gt;/&lt;file&gt;&amp;id=&lt;id&gt;</c> returns
/// that node's menu with its children. The crawl is bounded (fetches and depth) and sequential with a
/// polite delay. Approach after TcKit's InfosysNavigator (https://github.com/georgeturneruk/tckit, MIT).
///
/// Both kinds of index are cached on disk, so only the first lookup in a section is slow.
/// </summary>
public sealed partial class BeckhoffDocs
{
    public const string InfosysHost = "https://infosys.beckhoff.com";
    public const string HelpPackageHost = "https://help3.twincat3.infosys.beckhoff.com";
    private const string MenuUrl = InfosysHost + "/english/menu/menu.php";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IInfosysFetcher _fetcher;
    private readonly IPackageExtractor _extractor;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _politeDelay;
    private readonly TimeSpan _cacheLifetime;
    private readonly SemaphoreSlim _crawlLock = new(1, 1);

    public BeckhoffDocs(IInfosysFetcher fetcher, string cacheDirectory, TimeSpan? politeDelay = null, TimeSpan? cacheLifetime = null, IPackageExtractor? extractor = null)
    {
        _fetcher = fetcher;
        _extractor = extractor ?? new ExpandExePackageExtractor();
        _cacheDirectory = cacheDirectory;
        _politeDelay = politeDelay ?? TimeSpan.FromMilliseconds(150);
        _cacheLifetime = cacheLifetime ?? TimeSpan.FromDays(30);
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>True for an https URL on Infosys or Beckhoff's help package server.</summary>
    public static bool IsInfosysUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "infosys.beckhoff.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "help3.twincat3.infosys.beckhoff.com", StringComparison.OrdinalIgnoreCase));

    public bool IsCached(string section) => File.Exists(CachePath(section));

    /// <summary>Sections with a cached index, whatever their source.</summary>
    public IEnumerable<string> CachedSections()
        => Directory.Exists(_cacheDirectory)
            ? Directory.EnumerateFiles(_cacheDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(s => s != "package-catalog" && InfosysSections.IsValidSlug(s))
                .Order(StringComparer.Ordinal)
            : [];

    /// <summary>
    /// Returns the section's index: from cache when fresh, otherwise from its help package, otherwise by
    /// crawling. A cached package index is rebuilt when the catalog lists a newer package.
    /// </summary>
    public async Task<SectionIndex?> GetIndexAsync(string section, int maxFetches, int maxDepth, CancellationToken ct)
    {
        if (!InfosysSections.IsValidSlug(section))
            throw new ArgumentException($"'{section}' is not an Infosys section slug (lowercase letters, digits, '_' and '-').", nameof(section));

        if (await TryFreshCacheAsync(section, ct) is { } cached)
            return cached;

        await _crawlLock.WaitAsync(ct);
        try
        {
            if (await TryFreshCacheAsync(section, ct) is { } again)
                return again;

            var index = await BuildFromPackageAsync(section, ct)
                ?? await CrawlAsync(section, maxFetches, maxDepth, ct);
            if (index is not null && index.Entries.Count > 0)
                Save(index);
            return index ?? TryLoad(section);
        }
        finally
        {
            _crawlLock.Release();
        }
    }

    private async Task<SectionIndex?> TryFreshCacheAsync(string section, CancellationToken ct)
    {
        if (TryLoad(section) is not { } cached || DateTimeOffset.UtcNow - cached.CrawledUtc >= _cacheLifetime)
            return null;

        var package = await FindPackageAsync(section, ct);
        if (package is null)
            return cached;

        // A crawled index is replaced once a package exists; a package index when a newer package is out.
        return cached.Source == "package" && cached.ContentDate is { } date && package.LastModified <= date ? cached : null;
    }

    public static IReadOnlyList<DocHit> Match(SectionIndex index, string query, int maxResults)
    {
        var q = Normalize(query);
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];

        return index.Entries
            .Select(e => (Entry: e, Title: Normalize(e.Title), Context: Normalize(e.Parent ?? ""),
                Keywords: (e.Keywords ?? []).Select(Normalize).ToArray()))
            .Where(x => x.Keywords.Contains(q) || tokens.All(t => x.Title.Contains(t, StringComparison.Ordinal) || x.Context.Contains(t, StringComparison.Ordinal)))
            .Select(x => new DocHit(x.Entry.Title, x.Entry.Url, x.Entry.Parent, index.Section,
                x.Keywords.Contains(q) ? 5
                : x.Title == q ? 4
                : x.Title.StartsWith(q, StringComparison.Ordinal) ? 3
                : tokens.All(t => x.Title.Contains(t, StringComparison.Ordinal)) ? 2
                : 1,
                index.Source ?? "crawl", index.ContentDate))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title.Length)
            .Take(maxResults)
            .ToArray();
    }

    /// <summary>Returns a page as text: from a downloaded help package when it holds the page, otherwise online.</summary>
    public async Task<DocPage?> GetPageAsync(string url, int maxChars, CancellationToken ct)
    {
        if (!IsInfosysUrl(url))
            throw new ArgumentException($"Only {InfosysHost} pages can be fetched.", nameof(url));

        string? html = null;
        var source = "online";
        DateTimeOffset? contentDate = null;

        if (TryParsePageUrl(url) is { } page && ReadPackagePage(page.Section, page.File) is { } local)
        {
            html = local;
            source = "package";
            contentDate = TryLoad(page.Section)?.ContentDate;
        }

        html ??= await _fetcher.GetAsync(url, ct);
        if (html is null)
            return null;

        var title = MetaTitle().Match(html) is { Success: true } m ? Clean(m.Groups[1].Value)
            : TitleTag().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value)
            : null;
        var text = HtmlToText(html);
        var truncated = text.Length > maxChars;
        return new DocPage(url, string.IsNullOrEmpty(title) ? null : title, truncated ? text[..maxChars] : text, truncated, text.Length, source, contentDate);
    }

    /// <summary>Converts an Infosys page to readable text, keeping table rows as "a | b | c" lines.</summary>
    public static string HtmlToText(string html)
    {
        var body = BodyTag().Match(html) is { Success: true } b ? b.Groups[1].Value : html;
        body = ScriptOrStyle().Replace(body, " ");
        body = CellEnd().Replace(body, " | ");
        body = SpanEnd().Replace(body, " "); // FB diagrams are adjacent spans ("Execute BOOL", "Position LREAL")
        body = BlockBreak().Replace(body, "\n");
        body = AnyTag().Replace(body, "");
        body = WebUtility.HtmlDecode(body);

        var lines = body.Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim().TrimEnd('|').Trim())
            .Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    private static (string Section, string File)? TryParsePageUrl(string url)
    {
        var m = PageUrl().Match(url);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
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

                entries.Add(new DocEntry(childTitle, PageUrlFor(section, childFile), title));
                if (depth + 1 < maxDepth && childId is not null)
                    queue.Enqueue((childFile, childId, depth + 1, childTitle));
                else if (childId is not null)
                    complete = false;
            }
        }

        return new SectionIndex(section, DateTimeOffset.UtcNow, complete, fetches, entries, Source: "crawl");
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

    private static string PageUrlFor(string section, string file) => $"{InfosysHost}/content/1033/{section}/{file}";

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
            // A cache write failure only costs a rebuild next time.
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

    [GeneratedRegex(@"^https://infosys\.beckhoff\.com/content/1033/([a-z0-9][a-z0-9_\-]*)/([A-Za-z0-9_\-]+\.html?)(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageUrl();

    [GeneratedRegex(@"<meta\s+name=""Title""\s+content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTitle();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyTag();

    [GeneratedRegex(@"<(script|style|noscript)\b.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnd();

    [GeneratedRegex(@"</span>", RegexOptions.IgnoreCase)]
    private static partial Regex SpanEnd();

    [GeneratedRegex(@"<br\s*/?>|</?(p|div|tr|li|ul|ol|table|h[1-6]|pre|section|article)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();
}
