using System;
using System.Collections.Generic;
using System.Globalization;

namespace LASTE_Mate.Core;

public static class WindRecalculator
{
    private const double MpsToKt = 1.94;      // Excel uses 1.94 (not the more precise 1.943844...)
    private const double TempLapse_C_per_kft = 2.0; // Excel uses: TMP = GroundTemp - (2 * ALT[kft])

    // Excel sheet produces 5 CDU WNDEDIT lines at these altitudes (kft):
    // H3=0, H4="01", H5="02", H6="07", H7="26"
    private static readonly int[] AltitudesKft = { 0, 1, 2, 7, 26 };

    public sealed record WindLayer(double SpeedMps, double MeteoDirectionDeg);
    public sealed record BriefingInput(
        WindLayer Ground,
        WindLayer At2000m,
        WindLayer At8000m,
        int GroundTempC,
        string MapName
    );

    public sealed record CduWindLine(
        int AltKft,
        int BrgDegMag,
        int SpdKt,
        int TmpC
    )
    {
        // Convenience formatting for CDU entry
        public string AltText => AltKft.ToString("00", CultureInfo.InvariantCulture);     // "00","01","02","07","26"
        public string BrgText => BrgDegMag.ToString("000", CultureInfo.InvariantCulture); // "000".."359"
        public string SpdText => SpdKt.ToString("00", CultureInfo.InvariantCulture);      // "00".."99" (will show 3 digits if >=100)
        public string BrgPlusSpd => $"{BrgText}{SpdText}";                                // what you type into BRG+SPD
        public string TmpText => TmpC < 0 ? $"-{Math.Abs(TmpC):00}" : $"+{TmpC:00}";       // common CDU-style; adjust if you prefer no '+'
    }

    /// <summary>
    /// Mirrors the Excel logic:
    /// - MAGVAR = VLOOKUP(map)
    /// - BRG = METEO - MAGVAR
    /// - SPD = m/s * 1.94 (except for ALT 01 and 02 where Excel uses *1.94*2)
    /// - TMP = GroundTemp - (2 * ALT[kft])
    /// </summary>
    public static IReadOnlyList<CduWindLine> Compute(BriefingInput input)
    {
        var magVar = GetMagVarDeg(input.MapName); // degrees, East positive (matches the workbook's subtraction)
        var result = new List<CduWindLine>(AltitudesKft.Length);

        foreach (var altKft in AltitudesKft)
        {
            var layer = SelectLayer(input, altKft);

            // Excel BRG formulas:
            // I3=E3-D8
            // I4=E3-D8
            // I5=E3-D8
            // I6=E4-D8
            // I7=E5-D8
            var brgRaw = layer.MeteoDirectionDeg - magVar;
            var brgDegMag = Normalize0To359((int)Math.Round(brgRaw, MidpointRounding.AwayFromZero));

            // Excel SPD formulas:
            // J3 = D3*1.94
            // J4 = D3*1.94*2
            // J5 = D3*1.94*2
            // J6 = D4*1.94
            // J7 = D5*1.94
            var speedFactor = (altKft == 1 || altKft == 2) ? 2.0 : 1.0; // mirrors the sheet (even if it feels odd)
            var spdKt = (int)Math.Round(layer.SpeedMps * MpsToKt * speedFactor, MidpointRounding.AwayFromZero);

            // Excel TMP formulas:
            // K3=F3
            // K4=F3-(2*H4)
            // K5=F3-(2*H5)
            // K6=F3-(2*H6)
            // K7=F3-(2*H7)
            var tmpC = (int)Math.Round(input.GroundTempC - (TempLapse_C_per_kft * altKft),
                                       MidpointRounding.AwayFromZero);

            result.Add(new CduWindLine(altKft, brgDegMag, spdKt, tmpC));
        }

        return result;
    }

    private static WindLayer SelectLayer(BriefingInput input, int altKft)
        => altKft switch
        {
            0 or 1 or 2 => input.Ground,
            7 => input.At2000m,
            26 => input.At8000m,
            _ => throw new ArgumentOutOfRangeException(nameof(altKft), altKft, "Unexpected altitude (this calculator only uses 0,1,2,7,26).")
        };

    private static int Normalize0To359(int deg)
    {
        // Excel does not wrap, but CDU inputs should be 0..359
        var m = deg % 360;
        return m < 0 ? m + 360 : m;
    }

    /// <summary>
    /// Workbook MagVar sheet values (2024) + your missing maps.
    /// Stored as degrees; East positive, West negative.
    /// </summary>
    public static double GetMagVarDeg(string mapName)
    {
        // Names should match your dropdown names in the UI.
        // Existing workbook entries:
        return mapName.Trim() switch
        {
            "Caucasus" => 7.3,
            "Marianas" => -0.5,
            "Nevada" => 11.5,
            "Normandy" => 1.2,
            "Persian Gulf" => 2.6,
            "Sinai" => 5.0,
            "Syria" => 5.5,
            "The channel" => 1.3,

            // Missing maps you mentioned (add the names you'll use in your app):
            "Afghanistan" => 3.5,
            "Cold War Germany" => 3.0,

            _ => throw new KeyNotFoundException($"No MagVar configured for map '{mapName}'.")
        };
    }

    /// <summary>
    /// Get all available map names for UI dropdown
    /// </summary>
    public static IReadOnlyList<string> GetAvailableMaps()
    {
        return new[]
        {
            "Caucasus",
            "Marianas",
            "Nevada",
            "Normandy",
            "Persian Gulf",
            "Sinai",
            "Syria",
            "The channel",
            "Afghanistan",
            "Cold War Germany"
        };
    }

    /// <summary>
    /// Maps DCS mission theatre names to calculator map names.
    /// Returns null if no match is found or the map is not supported.
    /// </summary>
    public static string? MapTheatreToMapName(string? dcsTheatre)
    {
        if (string.IsNullOrWhiteSpace(dcsTheatre))
        {
            return null;
        }

        // Normalize: trim whitespace
        var normalized = dcsTheatre.Trim();

        // Map DCS theatre names to calculator map names
        return normalized switch
        {
            "Afghanistan" => "Afghanistan",
            "Caucasus" => "Caucasus",
            "GermanyCW" => "Cold War Germany",
            "MarianaIslands" => "Marianas",
            "Nevada" => "Nevada",
            "Normandy" => "Normandy",
            "PersianGulf" => "Persian Gulf",
            "SinaiMap" => "Sinai",
            "Syria" => "Syria",
            "TheChannel" => "The channel",
            
            // Maps not yet supported in calculator (return null to indicate missing)
            "Iraq" => null,
            "Kola" => null,
            "SouthAtlantic" => null,
            
            // Fallback: try case-insensitive matching
            _ => TryMatchByPartialName(normalized)
        };
    }

    private static string? TryMatchByPartialName(string dcsTheatre)
    {
        // Try case-insensitive partial matching for variations
        var lower = dcsTheatre.ToLowerInvariant();
        
        if (lower.Contains("caucasus"))
        {
            return "Caucasus";
        }
        if (lower.Contains("mariana"))
        {
            return "Marianas";
        }
        if (lower.Contains("afghan"))
        {
            return "Afghanistan";
        }
        if (lower.Contains("germany") || lower.Contains("germancw"))
        {
            return "Cold War Germany";
        }
        if (lower.Contains("syria"))
        {
            return "Syria";
        }
        if (lower.Contains("nevada") || lower.Contains("nttr"))
        {
            return "Nevada";
        }
        if (lower.Contains("normandy"))
        {
            return "Normandy";
        }
        if (lower.Contains("persian") && lower.Contains("gulf"))
        {
            return "Persian Gulf";
        }
        if (lower.Contains("sinai"))
        {
            return "Sinai";
        }
        if (lower.Contains("channel"))
        {
            return "The channel";
        }
        
        return null; // Unknown theatre
    }
}

