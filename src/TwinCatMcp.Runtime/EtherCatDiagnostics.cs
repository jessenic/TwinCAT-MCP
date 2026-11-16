using System.Buffers.Binary;
using TwinCAT.Ads;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Runtime;

public sealed record EtherCatSlaveState(
    int Position,
    int Address,
    string State,
    bool Error,
    bool InvalidVendorProductRevision,
    bool InitCommandError,
    bool Disabled,
    string Link,
    int RawState,
    int RawLinkState,
    EtherCatSlaveIdentity? Identity,
    EtherCatCrcCounters? Crc);

public sealed record EtherCatSlaveIdentity(uint VendorId, uint ProductCode, uint RevisionNo, uint SerialNo)
{
    public string VendorIdHex => $"0x{VendorId:X8}";
    public string ProductCodeHex => $"0x{ProductCode:X8}";
    public string RevisionNoHex => $"0x{RevisionNo:X8}";
}

public sealed record EtherCatCrcCounters(uint PortA, uint PortB, uint PortC, uint PortD);

public sealed record EtherCatMasterStatus(
    string MasterNetId,
    string? MasterAdsState,
    int? MasterDeviceState,
    int SlaveCount,
    int SlavesNotInOp,
    int SlavesWithError,
    IReadOnlyList<EtherCatSlaveState> Slaves,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads EtherCAT diagnostics from a master's ADS interface: the master is its own ADS device (with its
/// own AMS Net ID, shown on the EtherCAT device's "EtherCAT" tab in XAE) on port 0xFFFF. The index groups
/// are the ones the Tc2_EtherCAT function blocks use (FB_EcGetSlaveCount, FB_EcGetAllSlaveAddr,
/// FB_EcGetAllSlaveStates, FB_EcGetSlaveIdentity, FB_EcGetAllSlaveCrcErrors).
///
/// A short-lived client is used so the session's main ADS connection and its subscriptions stay
/// untouched. Read-only, but still refused for a Net ID on Safety:BlockedNetIds.
/// </summary>
public sealed class EtherCatDiagnostics
{
    public const int MasterPort = 0xFFFF;
    private const uint IgSlaveCount = 0x06;
    private const uint IgSlaveAddresses = 0x07;
    private const uint IgSlaveStates = 0x09;
    private const uint IgSlaveIdentity = 0x11;
    private const uint IgSlaveCrc = 0x12;

    private readonly SafetyGate _safety;

    public EtherCatDiagnostics(SafetyGate safety) => _safety = safety;

    public async Task<EtherCatMasterStatus> ReadAsync(string masterNetId, bool includeIdentity, bool includeCrc, CancellationToken ct)
    {
        if (!AmsNetId.TryParse(masterNetId, out var netId))
            throw new ArgumentException($"Invalid AMS Net ID '{masterNetId}' — expected the dotted form like '5.1.2.3.4.1'.", nameof(masterNetId));

        var access = _safety.CheckTargetAccess(netId.ToString(), "EtherCatDiagnostics");
        if (!access.IsAllowed)
            throw new InvalidOperationException(access.Reason);

        using var client = new AdsClient();
        await client.ConnectAsync(new AmsAddress(netId, MasterPort), ct);

        var warnings = new List<string>();
        string? adsState = null;
        int? deviceState = null;
        try
        {
            var state = await client.ReadStateAsync(ct);
            adsState = state.State.AdsState.ToString();
            deviceState = state.State.DeviceState;
        }
        catch (AdsErrorException ex)
        {
            warnings.Add($"Master state: {ex.Message}");
        }

        var countBytes = await ReadBytesAsync(client, IgSlaveCount, 0, 2, ct)
            ?? throw new InvalidOperationException($"Could not read the slave count from {netId}:{MasterPort}. Is this the EtherCAT master's Net ID (not the PLC's)?");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);

        var addresses = new int[count];
        if (count > 0 && await ReadBytesAsync(client, IgSlaveAddresses, 0, 2 * count, ct) is { } addressBytes)
        {
            for (var i = 0; i < count; i++)
                addresses[i] = BinaryPrimitives.ReadUInt16LittleEndian(addressBytes.AsSpan(2 * i));
        }
        else if (count > 0)
        {
            warnings.Add("Slave addresses could not be read.");
        }

        var states = count > 0 ? await ReadBytesAsync(client, IgSlaveStates, 0, 2 * count, ct) : [];
        if (states is null)
            warnings.Add("Slave states could not be read.");

        // CRC counters: 4 UDINT (ports A..D) per slave. Only trusted when the reply has exactly that size.
        byte[]? crc = null;
        if (includeCrc && count > 0)
        {
            crc = await ReadBytesAsync(client, IgSlaveCrc, 0, 16 * count, ct);
            if (crc is null)
                warnings.Add("CRC counters could not be read (best-effort).");
        }

        var slaves = new List<EtherCatSlaveState>(count);
        for (var i = 0; i < count; i++)
        {
            int raw = states is { Length: > 0 } ? states[2 * i] : 0;
            int link = states is { Length: > 0 } ? states[2 * i + 1] : 0;

            EtherCatSlaveIdentity? identity = null;
            if (includeIdentity && addresses[i] != 0 && await ReadBytesAsync(client, IgSlaveIdentity, (uint)addresses[i], 16, ct) is { } id)
            {
                identity = new EtherCatSlaveIdentity(
                    BinaryPrimitives.ReadUInt32LittleEndian(id),
                    BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(12)));
            }

            EtherCatCrcCounters? counters = crc is null ? null : new(
                BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan(16 * i)),
                BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan(16 * i + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan(16 * i + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan(16 * i + 12)));

            slaves.Add(new EtherCatSlaveState(
                Position: i,
                Address: addresses[i],
                State: DecodeState(raw),
                Error: (raw & 0x10) != 0,
                InvalidVendorProductRevision: (raw & 0x20) != 0,
                InitCommandError: (raw & 0x40) != 0,
                Disabled: (raw & 0x80) != 0,
                Link: DecodeLink(link),
                RawState: raw,
                RawLinkState: link,
                Identity: identity,
                Crc: counters));
        }

        return new EtherCatMasterStatus(
            MasterNetId: netId.ToString(),
            MasterAdsState: adsState,
            MasterDeviceState: deviceState,
            SlaveCount: count,
            SlavesNotInOp: slaves.Count(s => s.State != "OP"),
            SlavesWithError: slaves.Count(s => s.Error || s.InvalidVendorProductRevision || s.InitCommandError || s.Link != "OK"),
            Slaves: slaves,
            Warnings: warnings);
    }

    /// <summary>EtherCAT state machine value in the low nibble (EC_DEVICE_STATE_*).</summary>
    public static string DecodeState(int raw) => (raw & 0x0F) switch
    {
        0 => "NONE",
        1 => "INIT",
        2 => "PREOP",
        3 => "BOOT",
        4 => "SAFEOP",
        8 => "OP",
        var other => $"UNKNOWN({other})",
    };

    /// <summary>EC_LINK_STATE_* flags: 0 is OK; the high nibble names the affected port.</summary>
    public static string DecodeLink(int link)
    {
        if (link == 0)
            return "OK";

        var parts = new List<string>();
        if ((link & 0x01) != 0) parts.Add("NOT_PRESENT");
        if ((link & 0x02) != 0) parts.Add("LINK_WITHOUT_COMM");
        if ((link & 0x04) != 0) parts.Add("MISSING_LINK");
        if ((link & 0x08) != 0) parts.Add("ADDITIONAL_LINK");
        if ((link & 0x10) != 0) parts.Add("PORT_A");
        if ((link & 0x20) != 0) parts.Add("PORT_B");
        if ((link & 0x40) != 0) parts.Add("PORT_C");
        if ((link & 0x80) != 0) parts.Add("PORT_D");
        return string.Join('|', parts);
    }

    private static async Task<byte[]?> ReadBytesAsync(AdsClient client, uint indexGroup, uint indexOffset, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        try
        {
            var result = await client.ReadAsync(indexGroup, indexOffset, buffer.AsMemory(), ct);
            return result.ErrorCode == AdsErrorCode.NoError && result.ReadBytes == length ? buffer : null;
        }
        catch (AdsErrorException)
        {
            return null;
        }
    }
}
