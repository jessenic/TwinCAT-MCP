using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace TwinCatMcp.Runtime;

/// <summary>
/// Read-only hardware diagnostics over ADS: EtherCAT master and slave state, and NC axis state through the
/// PLC's <c>AXIS_REF</c> instances. Nothing here writes to the target.
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool(Name = "get_ethercat_status"), Description("Reads EtherCAT diagnostics from an EtherCAT master over ADS: master state, slave count, and per slave its fixed address, " +
        "EtherCAT state (INIT/PREOP/BOOT/SAFEOP/OP), error flags and link state, optionally identity (vendor/product/revision/serial) and CRC counters. " +
        "masterNetId is the EtherCAT device's own AMS Net ID (XAE: I/O > Devices > EtherCAT device > EtherCAT tab, or get_xae_tree_xml on the device), " +
        "usually the PLC's Net ID with a different fifth byte (for example 5.1.2.3.4.1, not 5.1.2.3.1.1). Read-only; refused for a Net ID on Safety:BlockedNetIds.")]
    public static async Task<string> GetEtherCatStatus(
        EtherCatDiagnostics diagnostics,
        [Description("AMS Net ID of the EtherCAT master device (not the PLC runtime's).")] string masterNetId,
        [Description("Also read each slave's identity (one extra ADS read per slave). Default true.")] bool includeIdentity = true,
        [Description("Also read CRC error counters per port (best-effort). Default false.")] bool includeCrc = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Serialize(await diagnostics.ReadAsync(masterNetId, includeIdentity, includeCrc, cancellationToken));
        }
        catch (Exception ex) when (ex is AdsErrorException or InvalidOperationException or ArgumentException)
        {
            return Serialize(new { Succeeded = false, MasterNetId = masterNetId, Error = ex.Message });
        }
    }

    [McpServerTool(Name = "list_axes"), Description("Lists NC axis references (AXIS_REF instances, Tc2_MC2) found in the connected PLC's symbols, searching maxDepth levels below each " +
        "top-level symbol (GVLs and programs). Use get_axis_state on a returned path.")]
    public static async Task<string> ListAxes(
        SymbolBrowser browser,
        [Description("How many levels to search below each top-level symbol. Default 3.")] int maxDepth = 3,
        [Description("Maximum number of axes to return. Default 100.")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        var axes = await browser.FindByTypeAsync(IsAxisRef, maxDepth, maxResults, cancellationToken);
        return Serialize(new { Count = axes.Count, Axes = axes.Select(a => new { a.InstancePath, a.TypeName, a.Comment }) });
    }

    [McpServerTool(Name = "get_axis_state"), Description("Reads an NC axis's live state from its AXIS_REF in the PLC (the NcToPlc process image): position, velocity, " +
        "error code, axis state and the StateDWord decoded into flags (Operational, Homed, NotMoving, InTargetPosition, Error, ...). Read-only. " +
        "A non-zero ErrorCode is an NC error number; look it up with search_beckhoff_docs.")]
    public static async Task<string> GetAxisState(
        SymbolBrowser browser,
        [Description("Instance path of the AXIS_REF, e.g. 'GVL_Axes.axX' or 'MAIN.fbAxis.Axis' (see list_axes).")] string axis,
        CancellationToken cancellationToken = default)
    {
        var path = axis.EndsWith(".NcToPlc", StringComparison.OrdinalIgnoreCase) ? axis : axis + ".NcToPlc";
        if (await browser.FindSymbolAsync(path, cancellationToken) is not IValueSymbol ncToPlc)
            return Serialize(new { Succeeded = false, Axis = axis, Error = $"'{path}' not found. Pass the AXIS_REF instance path from list_axes." });

        var result = await ncToPlc.ReadValueAsync(cancellationToken);
        if ((AdsErrorCode)result.ErrorCode != AdsErrorCode.NoError)
            return Serialize(new { Succeeded = false, Axis = axis, Error = ((AdsErrorCode)result.ErrorCode).ToString() });

        var values = JsonSerializer.SerializeToElement(result.Value, Json);
        uint? stateDWord = TryGetUInt(values, "StateDWord");
        uint? errorCode = TryGetUInt(values, "ErrorCode");

        return Serialize(new
        {
            Succeeded = true,
            Axis = axis,
            ErrorCode = errorCode,
            ErrorCodeHex = errorCode is { } e ? $"0x{e:X4}" : null,
            StateDWordHex = stateDWord is { } s ? $"0x{s:X8}" : null,
            StateFlags = stateDWord is { } sd ? AxisStateDecoder.Decode(sd) : null,
            NcToPlc = values,
        });
    }

    private static bool IsAxisRef(string typeName)
        => typeName.Equals("AXIS_REF", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith(".AXIS_REF", StringComparison.OrdinalIgnoreCase);

    private static uint? TryGetUInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt64(out var n))
                return unchecked((uint)n);
        }

        return null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
