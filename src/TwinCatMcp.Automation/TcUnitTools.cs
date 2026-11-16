using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Automation;

/// <summary>
/// TcUnit support: run the unit tests in the open PLC project and return structured results.
///
/// TcUnit (https://tcunit.org) runs inside the PLC and reports through the ADS log, which the XAE Shell
/// shows in its error list. Running the tests therefore means getting the PLC into Run with the test
/// program active: build, activate the configuration and restart TwinCAT (the same sequence TcUnit-Runner
/// uses), then wait for TcUnit's "TESTS FINISHED RUNNING" summary to appear.
/// </summary>
[McpServerToolType]
public static class TcUnitTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool(Name = "get_tcunit_results"), Description("Parses the TcUnit results currently in the XAE Shell error list (the most recent run only): " +
        "totals, per-suite and per-test status, and FAILED TEST assertion messages. Read-only. Use after run_tcunit_tests, or after running the tests yourself. " +
        "Finished=false means no TcUnit summary is in the list yet.")]
    public static async Task<string> GetTcUnitResults(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(TcUnitResultParser.Parse(await session.ReadErrorListDescriptionsAsync(cancellationToken)));

    [McpServerTool(Name = "run_tcunit_tests"), Description("Runs the TcUnit tests in the open TwinCAT project on the XAE project's target and returns structured results. " +
        "Steps: optionally enable boot-project autostart for plcName, build (stops on errors), activate the configuration, restart TwinCAT into Run, " +
        "then wait up to timeoutSeconds for TcUnit's summary in the error list. Use a test/simulation target, not production: activation and restart interrupt the target. " +
        "Requires AllowDownloads and AllowRuntimeStateChanges, and refuses a target on Safety:BlockedNetIds. The PLC must start on its own after the restart, " +
        "so pass plcName unless boot-project autostart is already on. dryRun=true reports the safety decisions only.")]
    public static async Task<string> RunTcUnitTests(
        XaeShellSession session,
        SafetyGate safety,
        [Description("PLC project name (as in add_plc_project / the tree) whose boot project gets autostart enabled before activation. Omit if autostart is already on.")] string? plcName = null,
        [Description("How long to wait for TcUnit to finish after the restart, in seconds. Default 120.")] int timeoutSeconds = 120,
        [Description("If true, report the safety decisions without building, activating or restarting anything. Default false.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<object>();

        var blocked = await XaeTargetGuard.CheckAsync(session, safety, "RunTcUnitTests", cancellationToken);
        if (blocked is not null)
            return Serialize(new { Applied = false, TimedOut = false, Reason = blocked.Reason, Steps = steps, Result = (object?)null });

        var decisions = new List<SafetyDecision>();
        if (plcName is not null)
            decisions.Add(safety.CheckAutomationOperation("SetPlcBootProject", $"run_tcunit_tests: {plcName}"));
        decisions.Add(safety.CheckAutomationOperation("ActivateConfiguration", "run_tcunit_tests"));
        decisions.Add(safety.CheckAutomationOperation("RestartTwinCat", "run_tcunit_tests"));

        var denied = decisions.FirstOrDefault(d => !d.IsAllowed);
        if (denied is not null || dryRun)
        {
            return Serialize(new
            {
                Applied = false,
                TimedOut = false,
                Reason = denied?.Reason ?? "Dry run: all required safety checks would allow this run.",
                Decisions = decisions.Select(d => new { d.Verdict, d.Reason }),
                Steps = steps,
                Result = (object?)null,
            });
        }

        if (plcName is not null)
        {
            try
            {
                steps.Add(new { Step = "SetPlcBootProject", Succeeded = true, Detail = await session.SetPlcBootProjectAsync(plcName, autostart: true, cancellationToken) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                steps.Add(new { Step = "SetPlcBootProject", Succeeded = false, Detail = ex.Message });
                return Stop("Could not enable boot-project autostart.");
            }
        }

        var build = await session.BuildAsync(configuration: null, cancellationToken);
        steps.Add(new { Step = "Build", build.Succeeded, Detail = $"{build.ErrorCount} error(s), {build.WarningCount} warning(s)" });
        if (!build.Succeeded)
            return Serialize(new { Applied = false, TimedOut = false, Reason = "Build failed; tests were not run.", Steps = steps, Build = build, Result = (object?)null });

        var baseline = TcUnitResultParser.CountFinishedMarkers(await session.ReadErrorListDescriptionsAsync(cancellationToken));

        var activation = await session.ActivateConfigurationAsync(decisions[^2].Reason, cancellationToken);
        steps.Add(new { Step = "ActivateConfiguration", activation.Succeeded, Detail = activation.Error ?? activation.Detail });
        if (!activation.Succeeded)
            return Stop("Activation failed; tests were not run.");

        var restart = await session.RestartTwinCatAsync(decisions[^1].Reason, cancellationToken);
        steps.Add(new { Step = "RestartTwinCat", restart.Succeeded, Detail = restart.Error ?? restart.Detail });
        if (!restart.Succeeded)
            return Stop("TwinCAT restart failed; tests were not run.");

        // Wait for a new "TESTS FINISHED RUNNING" marker. The error list can be cleared by the implicit
        // build during activation, so the baseline follows the count down before looking for growth.
        var timer = Stopwatch.StartNew();
        IReadOnlyList<string> descriptions = [];
        string? lastReadError = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            try
            {
                descriptions = await session.ReadErrorListDescriptionsAsync(cancellationToken);
                lastReadError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The shell often rejects calls for a while after StartRestartTwinCAT; keep waiting.
                lastReadError = ex.Message;
                continue;
            }

            var markers = TcUnitResultParser.CountFinishedMarkers(descriptions);
            baseline = Math.Min(baseline, markers);
            if (markers > baseline)
            {
                steps.Add(new { Step = "WaitForResults", Succeeded = true, Detail = $"TcUnit finished after {timer.Elapsed.TotalSeconds:F0} s." });
                return Serialize(new { Applied = true, TimedOut = false, Reason = "Tests ran.", Steps = steps, Result = TcUnitResultParser.Parse(descriptions) });
            }
        }

        steps.Add(new { Step = "WaitForResults", Succeeded = false, Detail = lastReadError ?? "No new TcUnit summary appeared before the timeout." });
        return Serialize(new
        {
            Applied = true,
            TimedOut = true,
            Reason = "Timed out waiting for TcUnit. Check that the PLC is in Run (boot-project autostart, license) and that TCUNIT_RUN() is called; " +
                "get_tcunit_results reads the list again later.",
            Steps = steps,
            Result = TcUnitResultParser.Parse(descriptions),
        });

        string Stop(string reason) => Serialize(new { Applied = true, TimedOut = false, Reason = reason, Steps = steps, Result = (object?)null });
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
