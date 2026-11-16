using TwinCatMcp.Automation;
using Xunit;

namespace TwinCatMcp.Automation.IntegrationTests;

public sealed class TcUnitResultParserTests
{
    // Error-list descriptions carry a timestamp/source prefix before TcUnit's "| ..." text.
    private const string P = "Message 16.11.2026 10:00:00 123 ms | 'Port_851': ";

    private static readonly string[] OneRun =
    [
        "Build succeeded",
        P + "FAILED TEST 'PRG_TEST.fbDivTest@Test_DivByZero', EXP: 0, ACT: 1, MSG: Division by zero not caught",
        P + "| ==========TESTS FINISHED RUNNING==========",
        P + "| Test suites: 1",
        P + "| Tests: 2",
        P + "| Successful tests: 1",
        P + "| Failed tests: 1",
        P + "| Duration: 0.002",
        P + "| ==========EXTENDED TEST INFORMATION==========",
        P + "| Test suite ID=0 'PRG_TEST.fbDivTest'",
        P + "| ID=0",
        P + "| Test suite name=PRG_TEST.fbDivTest",
        P + "| Test suite tests=2",
        P + "| Test suite failed tests=1",
        P + "| Test suite duration=0.002",
        P + "| Test name=Test_Divide",
        P + "| Test class name=PRG_TEST.fbDivTest",
        P + "| Test duration=0.001",
        P + "| Test status=SUCCESS",
        P + "| Test name=Test_DivByZero",
        P + "| Test class name=PRG_TEST.fbDivTest",
        P + "| Test duration=0.001",
        P + "| Test status=FAIL",
        P + "| Test assert message=Division by zero not caught",
        P + "| Test assert type=DINT",
    ];

    [Fact]
    public void Parses_summary_suites_tests_and_failures()
    {
        var result = TcUnitResultParser.Parse(OneRun);

        Assert.True(result.Finished);
        Assert.False(result.Passed);
        Assert.Equal(1, result.TestSuiteCount);
        Assert.Equal(2, result.TestCount);
        Assert.Equal(1, result.SuccessfulTestCount);
        Assert.Equal(1, result.FailedTestCount);
        Assert.Equal("0.002", result.Duration);

        var suite = Assert.Single(result.Suites);
        Assert.Equal(0, suite.Id);
        Assert.Equal("PRG_TEST.fbDivTest", suite.Name);
        Assert.Equal(2, suite.TestCount);
        Assert.Equal(1, suite.FailedTestCount);
        Assert.Collection(suite.Tests,
            t => { Assert.Equal("Test_Divide", t.Name); Assert.Equal("SUCCESS", t.Status); },
            t =>
            {
                Assert.Equal("Test_DivByZero", t.Name);
                Assert.Equal("FAIL", t.Status);
                Assert.Equal("Division by zero not caught", t.AssertMessage);
                Assert.Equal("DINT", t.AssertType);
            });

        var failure = Assert.Single(result.FailureMessages);
        Assert.StartsWith("FAILED TEST 'PRG_TEST.fbDivTest@Test_DivByZero'", failure);
    }

    [Fact]
    public void Only_the_latest_run_is_reported()
    {
        string[] secondRun =
        [
            P + "| ==========TESTS FINISHED RUNNING==========",
            P + "| Test suites: 1",
            P + "| Tests: 2",
            P + "| Successful tests: 2",
            P + "| Failed tests: 0",
        ];

        var result = TcUnitResultParser.Parse([.. OneRun, .. secondRun]);

        Assert.True(result.Passed);
        Assert.Equal(0, result.FailedTestCount);
        Assert.Empty(result.Suites);
        Assert.Empty(result.FailureMessages);
        Assert.NotNull(result.Note);
        Assert.Equal(2, result.FinishedMarkerCount);
    }

    [Fact]
    public void Unfinished_run_reports_failures_seen_so_far()
    {
        var result = TcUnitResultParser.Parse([P + "FAILED TEST 'A@B', EXP: TRUE, ACT: FALSE, MSG: x"]);

        Assert.False(result.Finished);
        Assert.False(result.Passed);
        Assert.Null(result.TestCount);
        Assert.Single(result.FailureMessages);
    }

    [Fact]
    public void Empty_list_is_not_finished()
    {
        var result = TcUnitResultParser.Parse([]);

        Assert.False(result.Finished);
        Assert.Null(result.Passed);
        Assert.Equal(0, TcUnitResultParser.CountFinishedMarkers([]));
    }
}
