using FordDiag.Comms.Diagnostics;

namespace FordDiag.Core;

/// <summary>ISO 14229 DTC status byte decoded into the flags a technician cares about.</summary>
public readonly record struct DtcStatus(byte Raw)
{
    public bool TestFailed => (Raw & 0x01) != 0;
    public bool Pending => (Raw & 0x04) != 0;
    public bool Confirmed => (Raw & 0x08) != 0;
    public bool WarningIndicator => (Raw & 0x80) != 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Confirmed) parts.Add("Confirmed");
            if (Pending) parts.Add("Pending");
            if (TestFailed) parts.Add("Active");
            if (WarningIndicator) parts.Add("MIL/Warning");
            return parts.Count == 0 ? "History" : string.Join(", ", parts);
        }
    }
}

public static class DtcDescriptions
{
    // SAE J2012 generic powertrain codes only; manufacturer-specific codes (P1xxx, B/C/U) need Ford's service data.
    private static readonly Dictionary<string, string> Generic = new()
    {
        ["P0016"] = "Crankshaft/camshaft position correlation (bank 1 sensor A)",
        ["P0087"] = "Fuel rail/system pressure too low",
        ["P0101"] = "Mass air flow circuit range/performance",
        ["P0102"] = "Mass air flow circuit low input",
        ["P0113"] = "Intake air temperature circuit high",
        ["P0116"] = "Engine coolant temperature range/performance",
        ["P0128"] = "Coolant thermostat below regulating temperature",
        ["P0171"] = "System too lean (bank 1)",
        ["P0172"] = "System too rich (bank 1)",
        ["P0174"] = "System too lean (bank 2)",
        ["P0299"] = "Turbo/supercharger underboost",
        ["P0300"] = "Random/multiple cylinder misfire detected",
        ["P0301"] = "Cylinder 1 misfire detected",
        ["P0302"] = "Cylinder 2 misfire detected",
        ["P0303"] = "Cylinder 3 misfire detected",
        ["P0304"] = "Cylinder 4 misfire detected",
        ["P0335"] = "Crankshaft position sensor A circuit",
        ["P0420"] = "Catalyst system efficiency below threshold (bank 1)",
        ["P0442"] = "Evaporative emission system small leak",
        ["P0455"] = "Evaporative emission system large leak",
        ["P0500"] = "Vehicle speed sensor malfunction",
        ["P0562"] = "System voltage low",
        ["P0606"] = "Control module processor",
        ["P0700"] = "Transmission control system malfunction",
        ["U0100"] = "Lost communication with ECM/PCM",
        ["U0121"] = "Lost communication with ABS control module",
        ["U0140"] = "Lost communication with body control module",
    };

    /// <summary>"P0171" -> description, or null when unknown. A failure-type suffix ("-00") is ignored.</summary>
    public static string? Describe(string code) => Generic.GetValueOrDefault(code.Split('-')[0]);

    public static string Describe(Dtc dtc) =>
        Describe(dtc.Text) ?? (dtc.Text[0] == 'P' && dtc.Text[1] == '0' ? "Generic code - no description in the built-in table"
            : "Manufacturer-specific code - needs Ford service information");
}
