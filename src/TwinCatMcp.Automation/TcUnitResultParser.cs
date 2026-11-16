using System.Globalization;
using System.Text.RegularExpressions;
using TwinCatMcp.Automation.Models;

namespace TwinCatMcp.Automation;

/// <summary>
/// Parses the ADS log messages TcUnit writes when a test run finishes, as they appear in the XAE Shell's
/// error list. TcUnit prints a summary after the "==========TESTS FINISHED RUNNING==========" marker and,
/// with extended results enabled (the default since TcUnit 1.2), one "key=value" line per suite and test
/// field. Failed assertions are logged as "FAILED TEST '...', EXP: ..., ACT: ..., MSG: ...".
///
/// The error list prefixes each ADS message with a timestamp and source, so every pattern looks for the
/// "| key" part anywhere in the description instead of anchoring at the start. Only the most recent run
/// (the messages after the previous finished marker, up to and including the summary after the last one)
/// is parsed, so results from earlier runs still in the list are ignored.
/// </summary>
public static partial class TcUnitResultParser
{
    public const string FinishedMarker = "TESTS FINISHED RUNNING";

    [GeneratedRegex(@"\|\s*(Test suites|Tests|Successful tests|Failed tests|Duration)\s*:\s*(.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"\|\s*(Test suite ID|ID|Test suite name|Test suite tests|Test suite failed tests|Test suite duration|Test name|Test class name|Test duration|Test status|Test assert message|Test assert type)\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedLine();

    [GeneratedRegex(@"FAILED TEST\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailedAssertion();

    [GeneratedRegex(@"^(\d+)\s*(?:'(.*)')?")]
    private static partial Regex SuiteIdAndName();

    /// <summary>Counts the finished markers in the given messages; a run is new once this count grows.</summary>
    public static int CountFinishedMarkers(IEnumerable<string> descriptions)
        => descriptions.Count(d => d.Contains(FinishedMarker, StringComparison.OrdinalIgnoreCase));

    public static TcUnitRunResult Parse(IReadOnlyList<string> descriptions)
    {
        var markerIndexes = descriptions
            .Select((d, i) => (d, i))
            .Where(x => x.d.Contains(FinishedMarker, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .ToArray();

        if (markerIndexes.Length == 0)
        {
            var pending = descriptions.Select(MatchFailure).OfType<string>().Distinct().ToArray();
            return new TcUnitRunResult(false, pending.Length > 0 ? false : null, null, null, null, null, null, [], pending, 0,
                descriptions.Count == 0
                    ? "The error list is empty. Run the tests (run_tcunit_tests) or check that the XAE Shell shows ADS messages."
                    : "No TcUnit 'TESTS FINISHED RUNNING' marker found yet. Tests may still be running or TcUnit isn't in the PLC project.");
        }

        var last = markerIndexes[^1];
        var runStart = markerIndexes.Length > 1 ? markerIndexes[^2] + 1 : 0;

        int? suiteCount = null, testCount = null, successCount = null, failCount = null;
        string? duration = null;
        var suites = new List<SuiteBuilder>();
        SuiteBuilder? suite = null;
        TestBuilder? test = null;

        for (var i = last + 1; i < descriptions.Count; i++)
        {
            var line = descriptions[i];
            if (SummaryLine().Match(line) is { Success: true } s)
            {
                var value = s.Groups[2].Value;
                switch (s.Groups[1].Value.ToLowerInvariant())
                {
                    case "test suites": suiteCount = ParseInt(value); break;
                    case "tests": testCount = ParseInt(value); break;
                    case "successful tests": successCount = ParseInt(value); break;
                    case "failed tests": failCount = ParseInt(value); break;
                    case "duration": duration = value; break;
                }
                continue;
            }

            if (ExtendedLine().Match(line) is not { Success: true } e)
                continue;

            var key = e.Groups[1].Value.ToLowerInvariant();
            var text = e.Groups[2].Value;
            switch (key)
            {
                case "test suite id":
                {
                    var m = SuiteIdAndName().Match(text);
                    suite = new SuiteBuilder { Id = m.Success ? ParseInt(m.Groups[1].Value) : null, Name = m.Success && m.Groups[2].Success ? m.Groups[2].Value : null };
                    suites.Add(suite);
                    test = null;
                    break;
                }
                case "id":
                {
                    var id = ParseInt(text);
                    if (suite is null || (suite.Id is not null && suite.Id != id) || suite.Tests.Count > 0)
                    {
                        suite = new SuiteBuilder();
                        suites.Add(suite);
                        test = null;
                    }
                    suite.Id = id;
                    break;
                }
                case "test suite name": Ensure(ref suite, suites).Name = text; break;
                case "test suite tests": Ensure(ref suite, suites).TestCount = ParseInt(text); break;
                case "test suite failed tests": Ensure(ref suite, suites).FailedTestCount = ParseInt(text); break;
                case "test suite duration": Ensure(ref suite, suites).Duration = text; break;
                case "test name":
                    test = new TestBuilder { Name = text };
                    Ensure(ref suite, suites).Tests.Add(test);
                    break;
                case "test class name": if (test is not null) test.ClassName = text; break;
                case "test duration": if (test is not null) test.Duration = text; break;
                case "test status": if (test is not null) test.Status = text; break;
                case "test assert message": if (test is not null) test.AssertMessage = text; break;
                case "test assert type": if (test is not null) test.AssertType = text; break;
            }
        }

        var failures = descriptions
            .Skip(runStart)
            .Take(last - runStart)
            .Select(MatchFailure)
            .OfType<string>()
            .Distinct()
            .ToArray();

        bool? passed = failCount is { } f ? f == 0 && failures.Length == 0 : failures.Length > 0 ? false : null;

        return new TcUnitRunResult(
            Finished: true,
            Passed: passed,
            TestSuiteCount: suiteCount,
            TestCount: testCount,
            SuccessfulTestCount: successCount,
            FailedTestCount: failCount,
            Duration: duration,
            Suites: suites.Select(b => b.Build()).ToArray(),
            FailureMessages: failures,
            FinishedMarkerCount: markerIndexes.Length,
            Note: suites.Count == 0
                ? "Summary only. Per-test detail needs TcUnit extended results (GVL_Param_TcUnit.LogExtendedResults, TRUE by default since TcUnit 1.2)."
                : null);
    }

    private static string? MatchFailure(string description)
        => FailedAssertion().Match(description) is { Success: true } m ? m.Value.Trim() : null;

    private static SuiteBuilder Ensure(ref SuiteBuilder? suite, List<SuiteBuilder> suites)
    {
        if (suite is null)
        {
            suite = new SuiteBuilder();
            suites.Add(suite);
        }
        return suite;
    }

    private static int? ParseInt(string value)
        => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private sealed class SuiteBuilder
    {
        public int? Id;
        public string? Name;
        public int? TestCount;
        public int? FailedTestCount;
        public string? Duration;
        public List<TestBuilder> Tests { get; } = [];

        public TcUnitSuite Build() => new(Id, Name, TestCount, FailedTestCount, Duration, Tests.Select(t => t.Build()).ToArray());
    }

    private sealed class TestBuilder
    {
        public string Name = "";
        public string? ClassName;
        public string? Status;
        public string? Duration;
        public string? AssertMessage;
        public string? AssertType;

        public TcUnitTest Build() => new(Name, ClassName, Status, Duration, AssertMessage, AssertType);
    }
}
