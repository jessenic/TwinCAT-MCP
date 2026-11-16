namespace TwinCatMcp.Automation;

public sealed partial class XaeShellSession
{
    /// <summary>
    /// Reads every error-list description (errors, warnings and messages) for TcUnit parsing. TcUnit logs
    /// its results as ADS messages, mostly at "message" level, so the list's message filter is switched on
    /// first; shells that don't expose the filter properties are read as they are.
    /// </summary>
    public Task<IReadOnlyList<string>> ReadErrorListDescriptionsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<string>>(() =>
        {
            if (_dte is { } dte)
            {
                try
                {
                    const string vsWindowKindErrorList = "{D78612C7-9962-4B83-95D9-268046DAD23A}";
                    dynamic errorList = dte.Windows.Item(vsWindowKindErrorList).Object;
                    errorList.ShowErrors = true;
                    errorList.ShowWarnings = true;
                    errorList.ShowMessages = true;
                }
                catch
                {
                    // Best-effort: ReadErrorList below has its own fallbacks for locating the window.
                }
            }

            return ReadErrorList().Select(e => e.Description).ToArray();
        });
}
