using System.Text;
using System.Text.RegularExpressions;

namespace TwinCatMcp.Docs;

/// <summary>
/// Infosys documentation sections (the slug in <c>https://infosys.beckhoff.com/content/1033/&lt;slug&gt;/</c>).
/// The lists are taken from TcKit (https://github.com/georgeturneruk/tckit, MIT, Copyright (c) George
/// Turner), which enumerated them from Infosys's own menu tree. Any other slug can still be passed to the
/// tools directly.
/// </summary>
public static partial class InfosysSections
{
    public static readonly IReadOnlyList<string> Libraries =
    [
        "tcplclib_tc2_standard", "tcplclib_tc2_utilities", "tcplclib_tc2_math", "tcplclib_tc2_system",
        "tcplclib_tc2_iofunctions", "tcplclib_tc2_sups", "tcplclib_tc2_systemcx", "tcplclib_tc2_systemc69xx",
        "tcplclib_tc2_ethercat", "tcplclib_tc3_ethercatdiag", "tcplclib_tc2_dataexchange", "tcplclib_tc2_coupler",
        "tcplclib_tc2_mdp", "tcplclib_tc2_mpbus", "tcplclib_tc2_mbus", "tcplclib_tc2_genibus", "tcplclib_tc2_profinetdiag",
        "tcplclib_tc3_iolink",
        "tcplclib_tc3_eventlogger", "tcplclib_tc3_jsonxml", "tcplclib_tc3_dynamicmemory", "tcplclib_tc3_module", "tcplclib_tc3_ipcdiag",
        "tcplclib_tc2_mc2", "tcplclib_tc2_mc2_drive", "tcplclib_tc2_drive", "tcplclib_tc2_ncdrive", "tcplclib_tc3_drivemotioncontrol",
        "tcplclib_tc3_mc2_advancedhoming", "tc3plclib_tc3_physics", "tc3ncerrcode",
        "tf50x0_tc3_nc_ptp", "tf5050_tc3_nc_camming", "tf5055_tc3_nc_flying_saw", "tf5060_tc3_nc_fifo_axes",
        "tf5065_tc3_plc_motion_control_xfc_xfc_nci", "tf5100_tc3_nc_i", "tf5110-tf5113_tc3_kinematic_transformation", "tf5120",
        "tf5130_tc3_unival_plc", "tf5400_tc3_advanced_motion_pack", "tf5410_tc3_collision_avoidance",
        "tf5420_tc3_advanced_pick_and_place", "tf5430_tc3_xplanar", "tf5810_tc3_hydraulic_positioning", "tf5890_tc3_xplanar",
        "tcplclib_tc3_packml", "tcplclib_tc3_packml_v2", "tcplclib_tc3_packml_v3",
        "tf6000_tc3_ads_commlib", "tf6010_tc3_ads_monitor", "tf6020_tc3_json_data_interface",
        "tf6100_tc3_opcua_server", "tf6100_tc3_opcua_client", "tf6100_tc3_opcua_configurator", "tf6100_tc3_opcua_gateway",
        "tf6105_tc3_opc_ua_pub_sub", "tf6120_tc3_opcda", "tf6225_tc3_ethercat_external_sync", "tf6230_tc3_parallel_redundancy_protocol",
        "tf6250_tc3_modbus_tcp", "tf6255_tc3_modbus_rtu", "tf6270_tc3_profinet_rt_device", "tf6271_tc3_profinet_rt_controller",
        "tf6280_tc3_ethernetipslave", "tf6281_tc3_ethernetipscanner", "tf6300_tc3_ftp", "tf6310_tc3_tcpip", "tf6311_tc3_tcpudp",
    ];

    public static readonly IReadOnlyList<string> Hardware =
    [
        "ethercatsystem",
        "ed336x-0x00", "ed407x", "el1052_el1054", "el10xx_el11xx", "el1202_el1252", "el125x_el2258",
        "el126x", "el1382", "el1409", "el1417", "el1429", "el1489", "el15xx", "el17x2-00xx", "el18xx",
        "el2044", "el2068", "el20xx_el2124", "el2202_el2252", "el2212", "el2262", "el2407", "el2409",
        "el2489", "el2502", "el2502-0005", "el2502-0010", "el252x", "el2535", "el2564", "el2574",
        "el2595", "el2596", "el26xx", "el27x2", "el27xx", "el2838", "el2869", "el28xx",
        "el3008-00xx", "el30xx", "el318x", "el31xx", "el3204-0162", "el3255", "el32xx", "el331x",
        "el3351", "el3356", "el3403", "el3475", "el34x3", "el34xx", "el3632", "el3681", "el3692",
        "el36xx", "el3751", "el3773", "el3783", "el37x2",
        "el407x", "el40xx", "el41xx", "el4374", "el47xx",
        "el500x", "el5021", "el5031-0011", "el5032", "el5042", "el5072", "el5101", "el5102", "el5112",
        "el5122", "el5131", "el515x",
        "el600x_el602x", "el6070", "el6080", "el6090", "el6184", "el6201", "el6224", "el6233",
        "el6601_el6614", "el6631-0010", "el6631_el6632", "el6633", "el6633-0010", "el6652", "el6685",
        "el6688", "el6692", "el6695", "el6711-0010", "el6720", "el6731", "el6740-0010", "el6751",
        "el6752", "el6761", "el6821", "el6851", "el6861",
        "el7031-0030", "el7062", "el70x1", "el70x7", "el72x1", "el72x1-001x", "el72x1-901x", "el73x2",
        "el7411", "el8601-8411", "el922x",
        "el9501_el9561", "el9562", "el9576", "el95xx", "el9xxx",
        "elm2xxx", "elm3xxx", "elm72xx", "em2042", "em37xx", "em7004",
        "ek1000", "ek110x_ek15xx", "ek1110", "ek1110-004x", "ek112x_ek15xx", "ek1300", "ek1310",
        "ek1322", "ek18xx", "ek3100", "ek9000", "ek9160", "ek9300", "ek9320", "ek9500", "ekm1101",
        "ep1111-0000", "ep1122", "ep1258", "ep1312-0001", "ep1518", "ep1xxx", "ep20xx_ep28xx",
        "ep23xx", "ep2534-0002", "ep2624-0002", "ep3048-0002", "ep31xx", "ep3204_ep3314", "ep3356",
        "ep3632", "ep3744", "ep3751-0160", "ep3751-0260", "ep3752", "ep4174", "ep4374", "ep4378",
        "ep43x4-1002", "ep5xxx", "ep6001", "ep6002", "ep6070-0060", "ep6080-0000", "ep6090-0000",
        "ep6224_ep6228", "ep6601-0002", "ep7041", "ep7041-4032", "ep7047-1032", "ep7211",
        "ep7342-0002", "ep7402", "ep7412-x071", "ep7414-x071", "ep8309-1022", "ep8601-0022", "ep9128",
        "ep9208-1035", "ep9224-0037", "ep9224-2037", "ep92x4-002x", "ep9300", "ep9521", "ep9576-1032",
        "erxxxx", "eqxxxx",
        "epp1111", "epp1321-0060", "epp1322", "epp1332_epp1342", "epp1518-0002", "epp1xxx", "epp2xxx",
        "epp31xx", "epp3204", "epp3314-0002", "epp3356-0022", "epp3504", "epp3632", "epp3744",
        "epp3752-0000", "epp4174-0002", "epp4314-1002", "epp4374", "epp5xxx", "epp6001", "epp6002",
        "epp6090-0000", "epp622x", "epp7041-x002", "epp7342-0002", "epp9001", "epp9022-0060",
        "epp9022-9060", "eppxxxx-x7xx",
        "ej1008", "ej110x-00xx", "ej1122", "ej1128", "ej1254", "ej18xx", "ej2008", "ej2128", "ej2262",
        "ej2502", "ej252x", "ej2564", "ej28x9", "ej30xx", "ej3114-0010", "ej3124-0090", "ej31xx",
        "ej3255", "ej32xx", "ej3314-0090", "ej3318", "ej40xx", "ej41xx", "ej500x", "ej5021", "ej5042",
        "ej5101", "ej5112", "ej515x", "ej600x", "ej6070", "ej6080", "ej6224", "ej70x1", "ej70x7",
        "ej72x1-001x", "ej7334-0008", "ej73x2", "ej7411", "ej8906-0005", "ej8xxx", "ej9001", "ej9400",
        "ej9404", "ej9505", "ej9576",
        "epi1xxx", "epi2xxx", "epi3xxx", "epi4xxx_eri4xxx",
        "cu112x", "cu20xx_cu22xx", "cu2508", "cu2508-0022", "cu2608",
    ];

    [GeneratedRegex(@"^(ELM|EPP|EPI|ERI|EKM|EL|EK|EP|EJ|EM|ED|ER|EQ|CU)\d{3,4}(-\d{4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderNumber();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_\-]{1,100}$")]
    private static partial Regex Slug();

    public static bool IsValidSlug(string section) => Slug().IsMatch(section);

    /// <summary>True for a Beckhoff I/O order number such as EL3004 or EPP7342-0002.</summary>
    public static bool IsOrderNumber(string query) => OrderNumber().IsMatch(query.Trim());

    /// <summary>
    /// True when a hardware slug covers an order number: each underscore part becomes a pattern with
    /// 'x' as a digit wildcard, and variant suffixes (after '-') are ignored on both sides.
    /// </summary>
    public static bool SectionCoversOrder(string slug, string order)
    {
        var bare = order.Trim().ToUpperInvariant().Split('-')[0];
        foreach (var rawPart in slug.Split('_'))
        {
            var part = rawPart.Split('-')[0];
            if (part.Length == 0)
                continue;

            var pattern = new StringBuilder("^");
            foreach (var ch in part)
                pattern.Append(ch is 'x' or 'X' ? @"\d" : Regex.Escape(char.ToUpperInvariant(ch).ToString()));
            pattern.Append('$');

            if (Regex.IsMatch(bare, pattern.ToString()))
                return true;
        }

        return false;
    }

    /// <summary>Sections likely to hold <paramref name="query"/>, most specific first.</summary>
    public static IReadOnlyList<string> Candidates(string query)
    {
        var q = query.Trim();
        if (IsOrderNumber(q))
            return Hardware.Where(s => SectionCoversOrder(s, q)).OrderByDescending(s => s.Contains('-')).ToArray();

        if (q.StartsWith("MC_", StringComparison.OrdinalIgnoreCase))
            return ["tcplclib_tc2_mc2", "tcplclib_tc3_mc2_advancedhoming", "tcplclib_tc2_mc2_drive"];
        if (q.StartsWith("FB_Ec", StringComparison.OrdinalIgnoreCase) || q.StartsWith("F_Ec", StringComparison.OrdinalIgnoreCase))
            return ["tcplclib_tc2_ethercat", "tcplclib_tc3_ethercatdiag"];
        if (q.Contains("NC error", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(q, @"^(0x)?[0-9A-F]{4,5}$", RegexOptions.IgnoreCase))
            return ["tc3ncerrcode"];
        if (q.StartsWith("Tc3_EventLogger", StringComparison.OrdinalIgnoreCase) || q.StartsWith("FB_TcEvent", StringComparison.OrdinalIgnoreCase))
            return ["tcplclib_tc3_eventlogger"];
        if (q.StartsWith("FB_Json", StringComparison.OrdinalIgnoreCase) || q.StartsWith("FB_Xml", StringComparison.OrdinalIgnoreCase))
            return ["tcplclib_tc3_jsonxml"];
        if (q.StartsWith("FB_Socket", StringComparison.OrdinalIgnoreCase) || q.StartsWith("F_CreateServerHnd", StringComparison.OrdinalIgnoreCase))
            return ["tf6310_tc3_tcpip"];
        if (q.StartsWith("FB_File", StringComparison.OrdinalIgnoreCase) || q.StartsWith("NT_", StringComparison.OrdinalIgnoreCase))
            return ["tcplclib_tc2_system", "tcplclib_tc2_utilities"];

        return ["tcplclib_tc2_standard", "tcplclib_tc2_utilities", "tcplclib_tc2_system"];
    }
}
