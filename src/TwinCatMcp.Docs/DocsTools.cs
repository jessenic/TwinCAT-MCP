using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace TwinCatMcp.Docs;

/// <summary>
/// Beckhoff Infosys lookups: find a function block, library page, NC error or I/O terminal's documentation
/// and read it as text. Uses Beckhoff's per-section help packages where they exist (exact and current,
/// offline once downloaded) and crawls Infosys for the rest (hardware). Nothing touches a PLC.
/// </summary>
[McpServerToolType]
public static class DocsTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const int MaxFetchesPerSection = 400;
    private const int MaxDepth = 6;

    [McpServerTool(Name = "search_beckhoff_docs"), Description("Searches Beckhoff Infosys page titles, e.g. a function block ('MC_MoveAbsolute', 'FB_EcGetAllSlaveStates'), " +
        "a topic ('ADS return codes') or an I/O order number ('EL3004'). Without a section, likely sections are picked from the query (MC_ -> Tc2_MC2, " +
        "order numbers -> the terminal's hardware section, ...) and every already cached section is searched too. An exact F1 keyword match (the FB name " +
        "XAE's F1 key uses) ranks first. The first search in a section downloads Beckhoff's help package for it (software sections: PLC libraries, " +
        "TwinCAT functions, NC errors; usually a few MB) or, for hardware, walks the Infosys menu (tens of seconds). Indexes are cached; a package " +
        "index is refreshed when Beckhoff publishes a newer package. Each hit says its Source and ContentDate. Then read a hit with get_beckhoff_doc_page. " +
        "Use this before writing code with an unfamiliar Beckhoff FB instead of guessing its inputs and outputs.")]
    public static async Task<string> SearchBeckhoffDocs(
        BeckhoffDocs docs,
        [Description("Words to find in page titles (all must match), an FB name, or an order number.")] string query,
        [Description("Infosys section slug to search, e.g. 'tcplclib_tc2_mc2' (see list_beckhoff_doc_sections). Omit to pick automatically.")] string? section = null,
        [Description("Maximum hits to return. Default 20.")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var sections = section is not null
            ? [section]
            : InfosysSections.Candidates(query).Take(3)
                .Concat(docs.CachedSections())
                .Distinct()
                .ToList();

        var hits = new List<DocHit>();
        var searched = new List<object>();
        foreach (var s in sections)
        {
            var index = await docs.GetIndexAsync(s, MaxFetchesPerSection, MaxDepth, cancellationToken);
            if (index is null)
            {
                searched.Add(new { Section = s, Found = false });
                continue;
            }

            searched.Add(new { Section = s, Found = true, Pages = index.Entries.Count, index.Complete, Source = index.Source ?? "crawl", index.ContentDate });
            hits.AddRange(BeckhoffDocs.Match(index, query, maxResults));
        }

        var ranked = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title.Length).Take(maxResults).ToArray();
        return Serialize(new
        {
            Query = query,
            Hits = ranked,
            Searched = searched,
            Note = ranked.Length == 0
                ? "No title matched. Try fewer words, another section (list_beckhoff_doc_sections), or pass section explicitly."
                : null,
        });
    }

    [McpServerTool(Name = "get_beckhoff_doc_page"), Description("Fetches one Beckhoff Infosys page (a URL from search_beckhoff_docs) and returns it as plain text, " +
        "with tables as 'a | b | c' lines (FB inputs/outputs, technical data). Served from the downloaded help package when it holds the page " +
        "(Source='package', works offline), otherwise fetched online. Only infosys.beckhoff.com URLs are accepted.")]
    public static async Task<string> GetBeckhoffDocPage(
        BeckhoffDocs docs,
        [Description("https://infosys.beckhoff.com/... page URL.")] string url,
        [Description("Maximum characters of text to return. Default 20000.")] int maxChars = 20000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await docs.GetPageAsync(url, Math.Max(500, maxChars), cancellationToken);
            return page is null
                ? Serialize(new { Succeeded = false, Url = url, Error = "Page not found or Infosys not reachable." })
                : Serialize(page);
        }
        catch (ArgumentException ex)
        {
            return Serialize(new { Succeeded = false, Url = url, Error = ex.Message });
        }
    }

    [McpServerTool(Name = "list_beckhoff_doc_sections"), Description("Lists the documentation sections search_beckhoff_docs can use: every section Beckhoff publishes a help " +
        "package for (software: PLC libraries, TwinCAT functions, NC errors, with package date) and the known hardware sections (EtherCAT terminals/boxes/couplers, " +
        "crawled), each with whether it is cached locally. Any other Infosys section slug can also be passed to search_beckhoff_docs.")]
    public static async Task<string> ListBeckhoffDocSections(BeckhoffDocs docs, CancellationToken cancellationToken = default)
    {
        var packages = await docs.GetPackagesAsync(cancellationToken);
        return Serialize(new
        {
            docs.CacheDirectory,
            PackageCatalog = BeckhoffDocs.PackageCatalogUrl,
            Packages = packages.Select(p => new { p.Section, p.LastModified, Cached = docs.IsCached(p.Section) }),
            Hardware = InfosysSections.Hardware.Select(s => new { Section = s, Cached = docs.IsCached(s) }),
            Note = packages.Count == 0 ? "The package catalog could not be read; sections fall back to crawling Infosys." : null,
        });
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
