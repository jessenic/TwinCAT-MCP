using TwinCatMcp.Safety;

namespace TwinCatMcp.Automation;

/// <summary>
/// Applies <see cref="SafetyOptions.BlockedNetIds"/> to XAE operations that reach the XAE's selected
/// target system (configuration activation, runtime restart, boot-project generation). The target is read
/// from the open solution just before the operation. When the block list is empty nothing is read, so
/// behaviour without a block list is unchanged. When it is non-empty and the target can't be read, the
/// operation is refused: the guard fails closed.
/// </summary>
internal static class XaeTargetGuard
{
    /// <summary>Returns a denial when the operation must not run, or null when it may proceed to the normal gate.</summary>
    public static async Task<SafetyDecision?> CheckAsync(XaeShellSession session, SafetyGate safety, string operationName, CancellationToken ct)
    {
        if (safety.BlockedNetIds.Count == 0)
            return null;

        string target;
        try
        {
            target = await session.GetXaeTargetNetIdAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return safety.CheckTargetAccessUnknown(operationName, ex.Message);
        }

        var decision = safety.CheckTargetAccess(target, operationName);
        return decision.IsAllowed ? null : decision;
    }
}
