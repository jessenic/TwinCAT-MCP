using Microsoft.Extensions.Options;

namespace TwinCatMcp.Safety;

/// <summary>
/// The single chokepoint every mutating operation — PLC source edits, live symbol writes, runtime state
/// changes, and XAE build/automation operations — passes through.
///
/// Two things happen here, in one place:
/// 1. The three toggles from <see cref="SafetyOptions"/> are enforced for the operations they cover —
///    live ADS symbol writes (<see cref="AllowAdsWrites"/>), configuration downloads (<see cref="AllowDownloads"/>),
///    and runtime state changes (<see cref="AllowRuntimeStateChanges"/>).
/// 2. Every check — allowed *or* blocked — is recorded to the <see cref="OperationAuditLog"/>, so nothing
///    mutating slips through unrecorded.
///
/// Operations not covered by a toggle (source file edits, build/clean, PLC object create/delete/import/export,
/// save) are not gated by design — the gate still audits them. Read/browse/search tools never call this type.
/// </summary>
public sealed class SafetyGate
{
    private readonly SafetyOptions _options;
    private readonly OperationAuditLog _audit;
    private readonly IReadOnlyList<string> _blockedNetIds;

    public SafetyGate(IOptions<SafetyOptions> options, OperationAuditLog audit)
    {
        _options = options.Value;
        _audit = audit;
        _blockedNetIds = (_options.BlockedNetIds ?? [])
            .SelectMany(entry => (entry ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(id => TryNormalizeNetId(id) ?? throw new ArgumentException(
                $"Safety:BlockedNetIds contains '{id}', which is not an AMS Net ID (expected six dot-separated bytes such as '5.1.2.3.1.1')."))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The configured, normalized list of AMS Net IDs the server refuses to touch. Read-only by design.</summary>
    public IReadOnlyList<string> BlockedNetIds => _blockedNetIds;

    /// <summary>True when <paramref name="amsNetId"/> is on the configured block list.</summary>
    public bool IsBlocked(string? amsNetId)
        => TryNormalizeNetId(amsNetId) is { } normalized && _blockedNetIds.Contains(normalized, StringComparer.Ordinal);

    /// <summary>
    /// Gate for any operation that reaches a specific target (ADS connect, making it the session default,
    /// or an XAE download/restart while it is the XAE target). Denied when the target is on
    /// <see cref="SafetyOptions.BlockedNetIds"/>; denials are audited, allowed checks are not (reads would
    /// otherwise flood the log).
    /// </summary>
    public SafetyDecision CheckTargetAccess(string? amsNetId, string operationName)
    {
        if (!IsBlocked(amsNetId))
            return SafetyDecision.Allow("Target is not on Safety:BlockedNetIds.");

        var decision = SafetyDecision.Deny(
            $"Target '{TryNormalizeNetId(amsNetId)}' is on Safety:BlockedNetIds — {operationName} is refused. The block list is configuration-only and cannot be changed from a tool.");
        _audit.Record(operationName, $"target {TryNormalizeNetId(amsNetId)}", decision);
        return decision;
    }

    /// <summary>
    /// Fail-closed denial for an operation whose target couldn't be determined while a block list is
    /// configured. Audited.
    /// </summary>
    public SafetyDecision CheckTargetAccessUnknown(string operationName, string why)
    {
        var decision = SafetyDecision.Deny(
            $"{operationName} is refused: Safety:BlockedNetIds is configured but the target Net ID could not be determined ({why}).");
        _audit.Record(operationName, "target unknown", decision);
        return decision;
    }

    /// <summary>
    /// Returns the canonical "a.b.c.d.e.f" form of an AMS Net ID (each part a byte, no leading zeros), or
    /// null when <paramref name="amsNetId"/> isn't one.
    /// </summary>
    public static string? TryNormalizeNetId(string? amsNetId)
    {
        if (string.IsNullOrWhiteSpace(amsNetId))
            return null;

        var parts = amsNetId.Trim().Split('.');
        if (parts.Length != 6)
            return null;

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }

        return string.Join('.', bytes);
    }

    /// <summary>Current toggle state — always a snapshot copy, so callers can't mutate the live policy by accident.</summary>
    public SafetyToggleState Snapshot() => new(
        AllowAdsWrites: _options.AllowAdsWrites,
        AllowDownloads: _options.AllowDownloads,
        AllowRuntimeStateChanges: _options.AllowRuntimeStateChanges);

    /// <summary>
    /// Updates toggles live for the current session; each <c>null</c> argument leaves that toggle unchanged.
    /// The change is itself recorded to the audit log (every policy change is traceable).
    /// </summary>
    public SafetyToggleState SetToggles(bool? allowAdsWrites, bool? allowDownloads, bool? allowRuntimeStateChanges)
    {
        var changes = new List<string>(3);
        if (allowAdsWrites is { } a) { _options.AllowAdsWrites = a; changes.Add($"AllowAdsWrites={a}"); }
        if (allowDownloads is { } d) { _options.AllowDownloads = d; changes.Add($"AllowDownloads={d}"); }
        if (allowRuntimeStateChanges is { } s) { _options.AllowRuntimeStateChanges = s; changes.Add($"AllowRuntimeStateChanges={s}"); }

        var decision = SafetyDecision.Allow("Safety toggles updated for this session; reset to configured defaults on server restart.");
        _audit.Record("PolicyChanged", changes.Count > 0 ? string.Join(", ", changes) : "no changes requested", decision);
        return Snapshot();
    }

    /// <summary>Gate for writing a live PLC symbol value via ADS.</summary>
    public SafetyDecision CheckSymbolWrite(string symbolName)
    {
        var decision = _options.AllowAdsWrites
            ? SafetyDecision.Allow("Permitted — Safety:AllowAdsWrites is true.")
            : SafetyDecision.Deny($"Symbol write to '{symbolName}' is blocked — Safety:AllowAdsWrites is false. Enable it with set_safety_toggles.");

        _audit.Record("WriteSymbol", symbolName, decision);
        return decision;
    }

    /// <summary>
    /// Audit-only pass-through for file-based PLC source edits (write_pou_declaration / write_pou_implementation):
    /// not gated by the toggles, but recorded to the audit log.
    /// </summary>
    public SafetyDecision CheckSourceEdit(string relativePath)
    {
        var decision = SafetyDecision.Allow("Source edits are not gated by the toggles — permitted; recorded to the audit log.");
        _audit.Record("EditSource", relativePath, decision);
        return decision;
    }

    /// <summary>Gate for changing PLC runtime state (Run/Stop/Reset/Config) via ADS.</summary>
    public SafetyDecision CheckStateTransition(string fromState, string toState)
    {
        var transition = $"{fromState}->{toState}";
        var decision = _options.AllowRuntimeStateChanges
            ? SafetyDecision.Allow("Permitted — Safety:AllowRuntimeStateChanges is true.")
            : SafetyDecision.Deny($"Runtime state change '{transition}' is blocked — Safety:AllowRuntimeStateChanges is false. Enable it with set_safety_toggles.");

        _audit.Record("SetPlcState", transition, decision);
        return decision;
    }

    /// <summary>
    /// Gate for an XAE Shell automation operation. "ActivateConfiguration" and "SetPlcBootProject"
    /// (downloads) are covered by <see cref="AllowDownloads"/> and "RestartTwinCat" (cold
    /// restart) by <see cref="AllowRuntimeStateChanges"/>. Everything else (build, clean, save, PLC object
    /// create/delete/import/export/write) is audit-only — not gated, just recorded.
    /// </summary>
    public SafetyDecision CheckAutomationOperation(string operationName, string? subject = null)
    {
        var decision = operationName switch
        {
            "ActivateConfiguration" or "SetPlcBootProject" when !_options.AllowDownloads =>
                SafetyDecision.Deny($"Configuration/boot-project download ({operationName}) is blocked — Safety:AllowDownloads is false. Enable it with set_safety_toggles."),
            "ActivateConfiguration" or "SetPlcBootProject" =>
                SafetyDecision.Allow("Permitted — Safety:AllowDownloads is true."),
            "RestartTwinCat" when !_options.AllowRuntimeStateChanges =>
                SafetyDecision.Deny("Runtime restart (RestartTwinCat) is blocked — Safety:AllowRuntimeStateChanges is false. Enable it with set_safety_toggles."),
            "RestartTwinCat" =>
                SafetyDecision.Allow("Permitted — Safety:AllowRuntimeStateChanges is true."),
            _ =>
                SafetyDecision.Allow("Operation is not gated by the toggles — permitted; recorded to the audit log."),
        };

        _audit.Record("Automation", subject is null ? operationName : $"{operationName}: {subject}", decision);
        return decision;
    }
}
