using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwinCatMcp.Docs;

/// <summary>One section package listed in the help package catalog.</summary>
public sealed record HelpPackage(string Section, string Name, DateTimeOffset LastModified, string Href);

/// <summary>Extracts the .mshc file from a help package CAB; returns its path, or null when there is none.</summary>
public interface IPackageExtractor
{
    Task<string?> ExtractMshcAsync(string cabPath, string destinationDirectory, CancellationToken ct);
}

/// <summary>Uses Windows' built-in expand.exe, extracting only *.mshc into an empty directory.</summary>
public sealed class ExpandExePackageExtractor : IPackageExtractor
{
    public async Task<string?> ExtractMshcAsync(string cabPath, string destinationDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cabPath);
        start.ArgumentList.Add("-F:*.mshc");
        start.ArgumentList.Add(destinationDirectory);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start expand.exe.");
        await Task.WhenAll(process.StandardOutput.ReadToEndAsync(ct), process.StandardError.ReadToEndAsync(ct));
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0
            ? Directory.EnumerateFiles(destinationDirectory, "*.mshc").FirstOrDefault()
            : null;
    }
}

public sealed partial class BeckhoffDocs
{
    /// <summary>The English (1033) book page listing every section package with its date and CAB link.</summary>
    public const string PackageCatalogUrl = HelpPackageHost + "/1033/tcinfosys3.html";
    private const string PackageSuffix = "_vs_100_en-us";
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromDays(1);

    private sealed record CatalogCache(DateTimeOffset FetchedUtc, IReadOnlyList<HelpPackage> Packages);

    private CatalogCache? _catalog;
    private DateTimeOffset _catalogRetryAfter = DateTimeOffset.MinValue;

    /// <summary>Section packages offered by Beckhoff, refreshed daily; a stale or empty list when the server can't be reached.</summary>
    public async Task<IReadOnlyList<HelpPackage>> GetPackagesAsync(CancellationToken ct)
    {
        _catalog ??= TryLoadCatalog();
        if (_catalog is { } fresh && DateTimeOffset.UtcNow - fresh.FetchedUtc < CatalogLifetime)
            return fresh.Packages;

        if (DateTimeOffset.UtcNow < _catalogRetryAfter)
            return _catalog?.Packages ?? [];

        var html = await _fetcher.GetAsync(PackageCatalogUrl, ct);
        var packages = html is null ? [] : ParseCatalog(html);
        if (packages.Count == 0)
        {
            // Offline or changed format: don't ask again on every lookup.
            _catalogRetryAfter = DateTimeOffset.UtcNow.AddMinutes(10);
            return _catalog?.Packages ?? [];
        }

        _catalog = new CatalogCache(DateTimeOffset.UtcNow, packages);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(_catalog, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return packages;
    }

    /// <summary>Parses the book page's package list ("&lt;section&gt;_vs_100_en-us" entries).</summary>
    public static IReadOnlyList<HelpPackage> ParseCatalog(string html)
    {
        var packages = new List<HelpPackage>();
        foreach (Match m in CatalogPackage().Matches(html))
        {
            var name = m.Groups["name"].Value.Trim();
            if (!name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase)
                || !DateTimeOffset.TryParse(m.Groups["modified"].Value, out var modified))
                continue;

            var section = name[..^PackageSuffix.Length].ToLowerInvariant();
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            if (InfosysSections.IsValidSlug(section) && href.StartsWith("packages/", StringComparison.OrdinalIgnoreCase))
                packages.Add(new HelpPackage(section, name, modified, href));
        }

        return packages;
    }

    private async Task<HelpPackage?> FindPackageAsync(string section, CancellationToken ct)
        => (await GetPackagesAsync(ct)).FirstOrDefault(p => p.Section == section);

    private string PackageDirectory => Path.Combine(_cacheDirectory, "packages");

    private string PackagePath(string section) => Path.Combine(PackageDirectory, section + ".mshc");

    /// <summary>Downloads the section's help package and indexes every page in it, or null when there is no package.</summary>
    private async Task<SectionIndex?> BuildFromPackageAsync(string section, CancellationToken ct)
    {
        if (await FindPackageAsync(section, ct) is not { } package)
            return null;

        var cab = await _fetcher.GetBytesAsync($"{HelpPackageHost}/1033/{package.Href}", ct);
        if (cab is null)
            return null;

        var work = Path.Combine(PackageDirectory, $"{section}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(work);
            var cabPath = Path.Combine(work, "package.cab");
            await File.WriteAllBytesAsync(cabPath, cab, ct);

            var extracted = Path.Combine(work, "out");
            Directory.CreateDirectory(extracted);
            if (await _extractor.ExtractMshcAsync(cabPath, extracted, ct) is not { } mshc)
                return null;

            var entries = IndexPackage(mshc, section);
            if (entries.Count == 0)
                return null;

            File.Move(mshc, PackagePath(section), overwrite: true);
            return new SectionIndex(section, DateTimeOffset.UtcNow, Complete: true, Fetches: 1, entries, Source: "package", ContentDate: package.LastModified);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads every page's Title, Help Id, TOC parent and F1 keywords from an .mshc (a ZIP of HTML pages).</summary>
    public static IReadOnlyList<DocEntry> IndexPackage(string mshcPath, string section)
    {
        var pages = new List<(string File, string Title, string? Id, string? ParentId, string[] Keywords)>();
        using (var zip = ZipFile.OpenRead(mshcPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
                    continue;

                using var reader = new StreamReader(entry.Open());
                var html = reader.ReadToEnd();
                if (MetaTitle().Match(html) is not { Success: true } title || title.Groups[1].Value.Trim().Length == 0)
                    continue;

                pages.Add((
                    entry.Name,
                    WebUtility.HtmlDecode(title.Groups[1].Value).Trim(),
                    HelpMeta().Matches(html).FirstOrDefault(m => m.Groups[1].Value == "Id")?.Groups[2].Value,
                    HelpMeta().Matches(html).FirstOrDefault(m => m.Groups[1].Value == "TOCParent")?.Groups[2].Value,
                    HelpMeta().Matches(html).Where(m => m.Groups[1].Value == "F1")
                        .Select(m => WebUtility.HtmlDecode(m.Groups[2].Value).Trim())
                        .Where(k => k.Length > 0 && !k.StartsWith("BeckhoffSearchNamespace.", StringComparison.Ordinal))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
            }
        }

        var titlesById = pages.Where(p => p.Id is not null).GroupBy(p => p.Id!).ToDictionary(g => g.Key, g => g.First().Title);
        return pages
            .Select(p => new DocEntry(
                p.Title,
                PageUrlFor(section, p.File),
                p.ParentId is not null && titlesById.TryGetValue(p.ParentId, out var parent) ? parent : null,
                p.Keywords.Length > 0 ? p.Keywords : null))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>A page's HTML from the section's downloaded package, or null when it isn't there.</summary>
    private string? ReadPackagePage(string section, string file)
    {
        var path = PackagePath(section);
        if (!File.Exists(path))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry(file) is not { } entry)
                return null;

            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string CatalogPath => Path.Combine(_cacheDirectory, "package-catalog.json");

    private CatalogCache? TryLoadCatalog()
    {
        try
        {
            return File.Exists(CatalogPath) ? JsonSerializer.Deserialize<CatalogCache>(File.ReadAllText(CatalogPath), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<div class=""package""><span class=""name"">(?<name>[^<]+)</span>.*?<span class=""last-modified"">(?<modified>[^<]+)</span>.*?<a class=""current-link"" href=""(?<href>[^""]+)""", RegexOptions.Singleline)]
    private static partial Regex CatalogPackage();

    [GeneratedRegex(@"<meta\s+name=""Microsoft\.Help\.(Id|TOCParent|F1)""\s+content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex HelpMeta();
}
