using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Runtime;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Runtime.Tests;

public sealed class DiagnosticsDecodingTests
{
    [Theory]
    [InlineData(0x08, "OP")]
    [InlineData(0x18, "OP")]
    [InlineData(0x04, "SAFEOP")]
    [InlineData(0x12, "PREOP")]
    [InlineData(0x01, "INIT")]
    [InlineData(0x03, "BOOT")]
    [InlineData(0x05, "UNKNOWN(5)")]
    public void EtherCat_state_is_decoded_from_low_nibble(int raw, string expected)
        => Assert.Equal(expected, EtherCatDiagnostics.DecodeState(raw));

    [Fact]
    public void EtherCat_link_flags_are_named()
    {
        Assert.Equal("OK", EtherCatDiagnostics.DecodeLink(0));
        Assert.Equal("MISSING_LINK|PORT_B", EtherCatDiagnostics.DecodeLink(0x24));
    }

    [Fact]
    public void Axis_state_dword_flags()
    {
        var flags = AxisStateDecoder.Decode(0x8000_0007);

        Assert.Equal(["Operational", "Homed", "NotMoving", "Error"], flags);
        Assert.Empty(AxisStateDecoder.Decode(0));
    }

    [Fact]
    public async Task EtherCat_diagnostics_refuse_blocked_master_without_connecting()
    {
        var log = Path.Combine(Path.GetTempPath(), $"twincat-mcp-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var gate = new SafetyGate(
                Options.Create(new SafetyOptions { BlockedNetIds = ["5.1.2.3.4.1"] }),
                new OperationAuditLog(log, NullLogger<OperationAuditLog>.Instance));
            var diagnostics = new EtherCatDiagnostics(gate);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.ReadAsync("5.1.2.3.4.1", true, false, CancellationToken.None));

            Assert.Contains("BlockedNetIds", ex.Message);
        }
        finally
        {
            if (File.Exists(log))
                File.Delete(log);
        }
    }
}
