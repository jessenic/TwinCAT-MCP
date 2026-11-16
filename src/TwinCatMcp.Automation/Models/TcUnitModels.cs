namespace TwinCatMcp.Automation.Models;

/// <summary>One TcUnit test as reported in TcUnit's extended results.</summary>
public sealed record TcUnitTest(
    string Name,
    string? ClassName,
    string? Status,
    string? Duration,
    string? AssertMessage,
    string? AssertType);

/// <summary>One TcUnit test suite (a function block extending <c>TcUnit.FB_TestSuite</c>).</summary>
public sealed record TcUnitSuite(
    int? Id,
    string? Name,
    int? TestCount,
    int? FailedTestCount,
    string? Duration,
    IReadOnlyList<TcUnitTest> Tests);

/// <summary>
/// TcUnit results parsed from the XAE Shell's error list. <see cref="Finished"/> is false when no
/// "TESTS FINISHED RUNNING" marker was found, in which case the counts are null and only the
/// failure messages seen so far are returned.
/// </summary>
public sealed record TcUnitRunResult(
    bool Finished,
    bool? Passed,
    int? TestSuiteCount,
    int? TestCount,
    int? SuccessfulTestCount,
    int? FailedTestCount,
    string? Duration,
    IReadOnlyList<TcUnitSuite> Suites,
    IReadOnlyList<string> FailureMessages,
    int FinishedMarkerCount,
    string? Note);
