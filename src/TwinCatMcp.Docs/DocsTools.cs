using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace TwinCatMcp.Docs;

/// <summary>
/// Beckhoff Infosys lookups: find a function block, library page, NC error or I/O terminal's documentation
/// and read it as text. Needs internet access to infosys.beckhoff.com; nothing touches a PLC.
/// </summary>
[McpServerToolType]
public static class DocsTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const int MaxFetchesPerSection = 400;
    private const int MaxDepth = 6;

    [McpServerTool(Name = "search_beckhoff_docs"), Description("Searches Beckhoff Infosys page titles, e.g. a function block ('MC_MoveAbsolute', 'FB_EcGetAllSlaveStates'), " +
        "a topic ('ADS return codes') or an I/O order number ('EL3004'). Without a section, likely sections are picked from the query (MC_ -> Tc2_MC2, " +
        "order numbers -> the terminal's hardware section, ...) and every already cached section is searched too. The first search in a section " +
        "walks its Infosys menu (can take tens of seconds) and caches the index for 30 days. Then read a hit with get_beckhoff_doc_page. " +
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
                .Concat(InfosysSections.Libraries.Concat(InfosysSections.Hardware).Where(docs.IsCached))
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

            searched.Add(new { Section = s, Found = true, Pages = index.Entries.Count, index.Complete });
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
        "with tables as 'a | b | c' lines (FB inputs/outputs, technical data). Only infosys.beckhoff.com URLs are accepted.")]
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

    [McpServerTool(Name = "list_beckhoff_doc_sections"), Description("Lists the Infosys sections search_beckhoff_docs knows (PLC libraries, TwinCAT functions, NC errors, " +
        "EtherCAT terminals/boxes/couplers) and which are already cached locally. Any other Infosys section slug can also be passed to search_beckhoff_docs.")]
    public static string ListBeckhoffDocSections(BeckhoffDocs docs)
        => Serialize(new
        {
            docs.CacheDirectory,
            Libraries = InfosysSections.Libraries.Select(s => new { Section = s, Cached = docs.IsCached(s) }),
            Hardware = InfosysSections.Hardware.Select(s => new { Section = s, Cached = docs.IsCached(s) }),
        });

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
