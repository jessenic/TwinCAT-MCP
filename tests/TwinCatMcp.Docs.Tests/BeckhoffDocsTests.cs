using TwinCatMcp.Docs;
using Xunit;

namespace TwinCatMcp.Docs.Tests;

public sealed class BeckhoffDocsTests : IDisposable
{
    private const string Host = "https://infosys.beckhoff.com";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"infosys-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    /// <summary>Serves a tiny two-level "tcplclib_tc2_mc2" tree shaped like Infosys's menu.php replies.</summary>
    private sealed class FakeInfosys : IInfosysFetcher
    {
        public List<string> Requests { get; } = [];

        public Task<string?> GetAsync(string url, CancellationToken ct)
        {
            Requests.Add(url);
            string? reply = url switch
            {
                _ when url == $"{Host}/content/1033/tcplclib_tc2_mc2/index.html" =>
                    "<html><head><meta name=\"primaryid\" content=\"100\"></head><body>MC2</body></html>",
                _ when url.Contains("index.html") && url.Contains("id=100") =>
                    """
                    <a href="menu.php?id=1&amp;noHl=1">toggle</a>
                    <a href="https://infosys.beckhoff.com/content/1033/tcinfosys3/index.html?id=1">TwinCAT 3</a>
                    <a href="https://infosys.beckhoff.com/content/1033/tcplclib_tc2_mc2/index.html?id=100">Tc2_MC2</a>
                    <a href="https://infosys.beckhoff.com/content/1033/tcplclib_tc2_mc2/200.html?id=200">Motion Function Blocks</a>
                    """,
                _ when url.Contains("200.html") && url.Contains("id=200") =>
                    """
                    <a href="https://infosys.beckhoff.com/content/1033/tcplclib_tc2_mc2/200.html?id=200">Motion Function Blocks</a>
                    <a href="https://infosys.beckhoff.com/content/1033/tcplclib_tc2_mc2/300.html?id=300"><span>MC_MoveAbsolute</span></a>
                    <a href="https://infosys.beckhoff.com/content/1033/tcplclib_tc2_mc2/301.html?id=301">MC_MoveRelative</a>
                    """,
                _ when url.Contains("menu.php") => "<p>no children</p>",
                _ when url == $"{Host}/content/1033/tcplclib_tc2_mc2/300.html" =>
                    """
                    <html><head><title>MC_MoveAbsolute</title><style>.x{}</style></head><body>
                    <h1>MC_MoveAbsolute</h1><p>Moves an axis to an &quot;absolute&quot; position.</p>
                    <table><tr><th>Name</th><th>Type</th></tr><tr><td>Execute</td><td>BOOL</td></tr></table>
                    <script>alert(1)</script></body></html>
                    """,
                _ => null,
            };
            return Task.FromResult(reply);
        }
    }

    private BeckhoffDocs Create(FakeInfosys fake) => new(fake, _cache, politeDelay: TimeSpan.Zero);

    [Fact]
    public async Task Crawls_menu_tree_and_finds_fb_by_title()
    {
        var fake = new FakeInfosys();
        var docs = Create(fake);

        var index = await docs.GetIndexAsync("tcplclib_tc2_mc2", maxFetches: 50, maxDepth: 6, CancellationToken.None);

        Assert.NotNull(index);
        Assert.True(index.Complete);
        Assert.Equal(["Motion Function Blocks", "MC_MoveAbsolute", "MC_MoveRelative"], index.Entries.Select(e => e.Title));
        Assert.DoesNotContain(index.Entries, e => e.Url.Contains("tcinfosys3"));

        var hit = Assert.Single(BeckhoffDocs.Match(index, "mc_moveabsolute", 10));
        Assert.Equal($"{Host}/content/1033/tcplclib_tc2_mc2/300.html", hit.Url);
        Assert.Equal("Motion Function Blocks", hit.Parent);
        Assert.Equal(4, hit.Score);
    }

    [Fact]
    public async Task Second_lookup_uses_the_disk_cache()
    {
        var fake = new FakeInfosys();
        await Create(fake).GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);
        var firstRequests = fake.Requests.Count;

        var again = new FakeInfosys();
        var docs = Create(again);
        var index = await docs.GetIndexAsync("tcplclib_tc2_mc2", 50, 6, CancellationToken.None);

        Assert.True(firstRequests > 0);
        Assert.Empty(again.Requests);
        Assert.True(docs.IsCached("tcplclib_tc2_mc2"));
        Assert.Equal(3, index!.Entries.Count);
    }

    [Fact]
    public async Task Fetch_budget_marks_index_incomplete()
    {
        var index = await Create(new FakeInfosys()).GetIndexAsync("tcplclib_tc2_mc2", maxFetches: 2, maxDepth: 6, CancellationToken.None);

        Assert.NotNull(index);
        Assert.False(index.Complete);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task Page_is_returned_as_text_with_tables()
    {
        var page = await Create(new FakeInfosys()).GetPageAsync($"{Host}/content/1033/tcplclib_tc2_mc2/300.html", 10_000, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("MC_MoveAbsolute", page.Title);
        Assert.Contains("Moves an axis to an \"absolute\" position.", page.Text);
        Assert.Contains("Execute | BOOL", page.Text);
        Assert.DoesNotContain("alert", page.Text);
        Assert.DoesNotContain(".x{}", page.Text);
    }

    [Fact]
    public async Task Only_infosys_urls_and_valid_slugs_are_accepted()
    {
        var docs = Create(new FakeInfosys());

        await Assert.ThrowsAsync<ArgumentException>(() => docs.GetPageAsync("https://example.com/x.html", 100, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => docs.GetPageAsync("http://infosys.beckhoff.com/x.html", 100, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => docs.GetIndexAsync("../../etc", 10, 2, CancellationToken.None));
    }

    [Theory]
    [InlineData("el30xx", "EL3004", true)]
    [InlineData("el30xx", "EL3104", false)]
    [InlineData("el10xx_el11xx", "EL1104", true)]
    [InlineData("epp7342-0002", "EPP7342-0002", true)]
    [InlineData("el34x3", "EL3443", true)]
    public void Hardware_sections_cover_order_numbers(string slug, string order, bool expected)
        => Assert.Equal(expected, InfosysSections.SectionCoversOrder(slug, order));

    [Fact]
    public void Candidates_follow_the_query_shape()
    {
        Assert.Equal("tcplclib_tc2_mc2", InfosysSections.Candidates("MC_MoveAbsolute")[0]);
        Assert.Contains("el30xx", InfosysSections.Candidates("EL3004"));
        Assert.Equal("tcplclib_tc2_ethercat", InfosysSections.Candidates("FB_EcGetAllSlaveStates")[0]);
    }
}
