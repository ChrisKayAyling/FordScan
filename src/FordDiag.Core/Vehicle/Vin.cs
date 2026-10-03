using System.Net.Http;
using System.Text.Json;

namespace FordDiag.Core.Vehicle;

public enum VinRegion { Unknown, NorthAmerica, Europe, Asia, SouthAmerica, Africa, Oceania }

/// <summary>What can be read from the VIN itself, without any database.</summary>
public sealed record VinInfo(string Vin, bool WellFormed, bool CheckDigitApplies, bool CheckDigitOk, string Wmi,
    string? Manufacturer, string? Country, VinRegion Region, IReadOnlyList<int> ModelYears, char PlantCode, string Serial)
{
    /// <summary>Most likely model year (the newest that is not in the future), or null.</summary>
    public int? ModelYear => ModelYears.Count == 0 ? null : ModelYears.Where(y => y <= DateTime.Now.Year + 1).DefaultIfEmpty(ModelYears[0]).Max();
    public bool IsFord => Manufacturer is not null;
}

/// <summary>ISO 3779 VIN parsing: world manufacturer identifier, check digit, model year (position 10) and plant (position 11).</summary>
public static class VinDecoder
{
    private const string YearChars = "ABCDEFGHJKLMNPRSTVWXY123456789";
    private static readonly int[] Weights = { 8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2 };

    // Ford Motor Company world manufacturer identifiers (common ones; the first two or three characters of the VIN).
    private static readonly Dictionary<string, (string Maker, string Country, VinRegion Region)> Wmi = new()
    {
        ["1FA"] = ("Ford (passenger car)", "United States", VinRegion.NorthAmerica),
        ["1FB"] = ("Ford (bus)", "United States", VinRegion.NorthAmerica),
        ["1FC"] = ("Ford (stripped chassis)", "United States", VinRegion.NorthAmerica),
        ["1FD"] = ("Ford (truck, incomplete vehicle)", "United States", VinRegion.NorthAmerica),
        ["1FM"] = ("Ford (multipurpose vehicle)", "United States", VinRegion.NorthAmerica),
        ["1FT"] = ("Ford (truck)", "United States", VinRegion.NorthAmerica),
        ["1ZV"] = ("Ford (AutoAlliance, Flat Rock)", "United States", VinRegion.NorthAmerica),
        ["1LN"] = ("Lincoln", "United States", VinRegion.NorthAmerica),
        ["1ME"] = ("Mercury", "United States", VinRegion.NorthAmerica),
        ["2FA"] = ("Ford (passenger car)", "Canada", VinRegion.NorthAmerica),
        ["2FM"] = ("Ford (multipurpose vehicle)", "Canada", VinRegion.NorthAmerica),
        ["2FT"] = ("Ford (truck)", "Canada", VinRegion.NorthAmerica),
        ["3FA"] = ("Ford (passenger car)", "Mexico", VinRegion.NorthAmerica),
        ["3FM"] = ("Ford (multipurpose vehicle)", "Mexico", VinRegion.NorthAmerica),
        ["3FT"] = ("Ford (truck)", "Mexico", VinRegion.NorthAmerica),
        ["3LN"] = ("Lincoln", "Mexico", VinRegion.NorthAmerica),
        ["5LM"] = ("Lincoln (multipurpose vehicle)", "United States", VinRegion.NorthAmerica),
        ["WF0"] = ("Ford of Europe", "Germany", VinRegion.Europe),
        ["WF1"] = ("Ford of Europe", "Germany", VinRegion.Europe),
        ["SFA"] = ("Ford of Britain", "United Kingdom", VinRegion.Europe),
        ["VS6"] = ("Ford of Spain", "Spain", VinRegion.Europe),
        ["NM0"] = ("Ford Otosan", "Turkey", VinRegion.Europe),
        ["X9F"] = ("Ford (Russia)", "Russia", VinRegion.Europe),
        ["6FP"] = ("Ford Australia", "Australia", VinRegion.Oceania),
        ["MAJ"] = ("Ford India", "India", VinRegion.Asia),
        ["MNB"] = ("Ford Thailand", "Thailand", VinRegion.Asia),
        ["LVS"] = ("Changan Ford", "China", VinRegion.Asia),
        ["9BF"] = ("Ford Brazil", "Brazil", VinRegion.SouthAmerica),
        ["8AF"] = ("Ford Argentina", "Argentina", VinRegion.SouthAmerica),
        ["AFA"] = ("Ford South Africa", "South Africa", VinRegion.Africa),
    };

    public static VinInfo Decode(string? vin)
    {
        var v = (vin ?? "").Trim().ToUpperInvariant();
        bool wellFormed = v.Length == 17 && v.All(c => char.IsAsciiLetterOrDigit(c) && c is not ('I' or 'O' or 'Q'));
        string wmiKey = v.Length >= 3 ? v[..3] : v;
        Wmi.TryGetValue(wmiKey, out var w);
        var region = w.Maker is not null ? w.Region : RegionFromFirstChar(v);
        bool applies = region == VinRegion.NorthAmerica;
        bool checkOk = wellFormed && CheckDigit(v) == v[8];
        var years = wellFormed ? ModelYearCandidates(v, region) : Array.Empty<int>();
        return new VinInfo(v, wellFormed, applies, checkOk, wmiKey, w.Maker, w.Country, region, years,
            v.Length > 10 ? v[10] : '?', v.Length == 17 ? v[11..] : "");
    }

    public static char CheckDigit(string vin)
    {
        int sum = 0;
        for (int i = 0; i < 17; i++) sum += Transliterate(vin[i]) * Weights[i];
        int r = sum % 11;
        return r == 10 ? 'X' : (char)('0' + r);
    }

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1, 'B' or 'K' or 'S' => 2, 'C' or 'L' or 'T' => 3, 'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5, 'F' or 'W' => 6, 'G' or 'P' or 'X' => 7, 'H' or 'Y' => 8, 'R' or 'Z' => 9,
        _ => 0,
    };

    /// <summary>
    /// Model years the 10th character can mean. The code repeats every 30 years (A = 1980 or 2010). North American VINs use
    /// position 7: a letter means 2010-2039, a digit 1980-2009. Elsewhere both candidates are returned, newest first.
    /// </summary>
    public static IReadOnlyList<int> ModelYearCandidates(string vin, VinRegion region)
    {
        int i = YearChars.IndexOf(vin[9]);
        if (i < 0) return Array.Empty<int>();       // 0 and Z are never used for the model year
        int old = 1980 + i, recent = 2010 + i;
        if (region == VinRegion.NorthAmerica) return new[] { char.IsLetter(vin[6]) ? recent : old };
        return new[] { recent, old };
    }

    private static VinRegion RegionFromFirstChar(string v) => v.Length == 0 ? VinRegion.Unknown : v[0] switch
    {
        >= '1' and <= '5' => VinRegion.NorthAmerica,
        >= 'A' and <= 'H' => VinRegion.Africa,
        >= 'J' and <= 'R' => VinRegion.Asia,
        >= 'S' and <= 'Z' => VinRegion.Europe,
        '6' or '7' => VinRegion.Oceania,
        '8' or '9' => VinRegion.SouthAmerica,
        _ => VinRegion.Unknown,
    };
}

/// <summary>Details returned by the NHTSA vPIC service (only fetched when the user asks).</summary>
public sealed record VehicleDetails(string? Make, string? Model, int? ModelYear, string? Trim, string? Series, string? BodyClass,
    string? DriveType, string? Engine, string? Plant, string? Error)
{
    public string Title => string.Join(" ", new[] { ModelYear?.ToString(), Make, Model, Trim }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>NHTSA vPIC VIN decoder. Free, no key; the VIN is sent to vpic.nhtsa.dot.gov. Covers vehicles sold in the US market.</summary>
public sealed class NhtsaVinClient
{
    private readonly HttpClient _http;
    public NhtsaVinClient(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<VehicleDetails> LookupAsync(string vin, CancellationToken ct = default)
    {
        var url = $"https://vpic.nhtsa.dot.gov/api/vehicles/DecodeVinValues/{Uri.EscapeDataString(vin)}?format=json";
        using var rsp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        rsp.EnsureSuccessStatusCode();
        return Parse(await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static VehicleDetails Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Results", out var results) || results.GetArrayLength() == 0)
            return new VehicleDetails(null, null, null, null, null, null, null, null, null, "No result");
        var r = results[0];
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        int? year = int.TryParse(S("ModelYear"), out var y) ? y : null;
        var engine = string.Join(" ", new[] { S("DisplacementL") is { } d && double.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out var l) ? $"{l:0.0}L" : null,
            S("EngineCylinders") is { } c ? $"{c}-cyl" : null, S("FuelTypePrimary"), S("EngineModel") }.Where(s => s is not null));
        var code = S("ErrorCode");
        string? err = code is null or "0" ? null : S("ErrorText") ?? "Decode error " + code;
        return new VehicleDetails(S("Make"), S("Model"), year, S("Trim"), S("Series"), S("BodyClass"), S("DriveType"),
            engine.Length == 0 ? null : engine, S("PlantCity"), err);
    }
}
