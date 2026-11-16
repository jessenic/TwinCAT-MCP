using System.IO.Compression;
using System.Text;
using TwinCatMcp.Docs;
using Xunit;

namespace TwinCatMcp.Docs.Tests;

public sealed class HelpPackageTests : IDisposable
{
    private const string Infosys = "https://infosys.beckhoff.com";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"infosys-pkg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    private static string Catalog(string modified) =>
        $"""
        <div class="package-list">The following packages are available:
        <div class="package"><span class="name">tcplclib_tc2_mc2_vs_100_en-us</span><span class="deployed">true</span><span class="last-modified">{modified}</span><span class="package-etag">e</span><a class="current-link" href="packages/tcplclib_tc2_mc2_vs_100_en-us(e).cab">tcplclib_tc2_mc2_vs_100_en-us</a></div>
        <div class="package"><span class="name">tc3ncerrcode_vs_100_en-us</span><span class="deployed">true</span><span class="last-modified">{modified}</span><span class="package-etag">e</span><a class="current-link" href="packages/tc3ncerrcode_vs_100_en-us(e).cab">tc3ncerrcode_vs_100_en-us</a></div>
        <div class="package"><span class="name">something_else_vs_100_de-de</span><span class="deployed">true</span><span class="last-modified">{modified}</span><a class="current-link" href="packages/x.cab">x</a></div>
        </div>
        """;

    private static string Page(string title, string id, string? parent, params string[] f1) =>
        $"""
        <html><head><meta name="Title" content="{title}" />
        <meta name="Microsoft.Help.Id" content="{id}" />
        {(parent is null ? "" : $"<meta name=\"Microsoft.Help.TOCParent\" content=\"{parent}\" />")}
        {string.Concat(f1.Select(k => $"<meta name=\"Microsoft.Help.F1\" content=\"{k}\" />"))}
        <title>ignored</title></head><body><h1>{title}</h1><table><tr><td>Execute</td><td>BOOL</td></tr></table></body></html>
        """;

    /// <summary>Serves the catalog and a dummy CAB; counts every request.</summary>
    private sealed class FakeServer(string catalog) : IInfosysFetcher
    {
        public string CatalogHtml { get; set; } = catalog;
        public List<string> Requests { get; } = [];

        public Task<string?> GetAsync(string url, CancellationToken ct)
        {
            Requests.Add(url);
            return Task.FromResult(url == BeckhoffDocs.PackageCatalogUrl ? CatalogHtml : null);
        }

        public Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
        {
            Requests.Add(url);
            return Task.FromResult<byte[]?>(url.Contains("tcplclib_tc2_mc2_vs_100_en-us") ? [1, 2, 3] : null);
        }
    }

    /// <summary>Stands in for expand.exe: writes an .mshc (a ZIP of HTML pages) into the destination.</summary>
    private sealed class FakeExtractor : IPackageExtractor
    {
        public int Calls { get; private set; }

        public Task<string?> ExtractMshcAsync(string cabPath, string destinationDirectory, CancellationToken ct)
        {
            Calls++;
            var path = Path.Combine(destinationDirectory, "tcplclib_tc2_mc2_vs_100_en-us.mshc");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, "100.html", Page("Motion Function Blocks", "id-root", null));
                Add(zip, "70094731.html", Page("MC_MoveAbsolute", "id-abs", "id-root", "MC_MoveAbsolute", "BeckhoffSearchNamespace.MC_MoveAbsolute"));
                Add(zip, "70094732.html", Page("MC_MoveAbsolute command (NC)", "id-cmd", "id-root"));
                Add(zip, "images/x.png", "png");
            }
            return Task.FromResult<string?>(path);
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    [Fact]
    public void Catalog_lists_english_section_packages()
    {
        var packages = BeckhoffDocs.ParseCatalog(Catalog("2026-10-29T20:41:54.5670672+01:00"));

        Assert.Equal(["tcplclib_tc2_mc2", "tc3ncerrcode"], packages.Select(p => p.Section));
        Assert.Equal("packages/tcplclib_tc2_mc2_vs_100_en-us(e).cab", packages[0].Href);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 20, 41, 54, TimeSpan.FromHours(1)).AddTicks(5670672), packages[0].LastModified);
    }

    [Fact]
    public async Task Package_index_has_titles_parents_and_f1_keywords()
    {
        var server = new FakeServer(Catalog("2026-10-29T20:41:54+01:00"));
        var docs = new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: new FakeExtractor());

        var index = await docs.GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);

        Assert.NotNull(index);
        Assert.Equal("package", index.Source);
        Assert.True(index.Complete);
        Assert.Equal(3, index.Entries.Count);
        var abs = Assert.Single(index.Entries, e => e.Title == "MC_MoveAbsolute");
        Assert.Equal($"{Infosys}/content/1033/tcplclib_tc2_mc2/70094731.html", abs.Url);
        Assert.Equal("Motion Function Blocks", abs.Parent);
        Assert.Equal(["MC_MoveAbsolute"], abs.Keywords);

        var hits = BeckhoffDocs.Match(index, "MC_MoveAbsolute", 10);
        Assert.Equal(5, hits[0].Score);
        Assert.Equal(abs.Url, hits[0].Url);
        Assert.Equal("package", hits[0].Source);
        Assert.DoesNotContain(server.Requests, r => r.Contains("menu.php"));
    }

    [Fact]
    public async Task Pages_are_served_from_the_package_without_network()
    {
        var server = new FakeServer(Catalog("2026-10-29T20:41:54+01:00"));
        var docs = new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: new FakeExtractor());
        await docs.GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);
        server.Requests.Clear();

        var page = await docs.GetPageAsync($"{Infosys}/content/1033/tcplclib_tc2_mc2/70094731.html", 10_000, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("package", page.Source);
        Assert.Equal("MC_MoveAbsolute", page.Title);
        Assert.Contains("Execute | BOOL", page.Text);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Newer_package_replaces_the_cached_index()
    {
        var server = new FakeServer(Catalog("2026-10-29T20:41:54+01:00"));
        var extractor = new FakeExtractor();
        await new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: extractor).GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);

        // Same package date: the cache is used (a fresh instance re-reads the cached catalog, not the server).
        var docs = new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: extractor);
        await docs.GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);
        Assert.Equal(1, extractor.Calls);

        // Newer package published; force the catalog to be re-read by deleting its cache.
        File.Delete(Path.Combine(_cache, "package-catalog.json"));
        server.CatalogHtml = Catalog("2027-01-15T10:00:00+01:00");
        var refreshed = await new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: extractor).GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);

        Assert.Equal(2, extractor.Calls);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 10, 0, 0, TimeSpan.FromHours(1)), refreshed!.ContentDate);
    }

    [Fact]
    public async Task Section_without_package_falls_back_to_crawling()
    {
        var server = new FakeServer(Catalog("2026-10-29T20:41:54+01:00"));
        var docs = new BeckhoffDocs(server, _cache, TimeSpan.Zero, extractor: new FakeExtractor());

        var index = await docs.GetIndexAsync("el30xx", 50, 6, CancellationToken.None);

        Assert.Null(index); // the fake has no Infosys pages, but the crawl was attempted
        Assert.Contains($"{Infosys}/content/1033/el30xx/index.html", server.Requests);
    }

    [Fact]
    public void Help_package_host_is_allowed_other_hosts_are_not()
    {
        Assert.True(BeckhoffDocs.IsInfosysUrl("https://help3.twincat3.infosys.beckhoff.com/1033/tcinfosys3.html"));
        Assert.False(BeckhoffDocs.IsInfosysUrl("https://evil.example/help3.twincat3.infosys.beckhoff.com"));
    }
}
