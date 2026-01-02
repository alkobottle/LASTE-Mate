using LASTE_Mate.Core;
using Xunit;

namespace LASTE_Mate.Tests.Core;

public class WindRecalculatorTests
{
    [Fact]
    public void Compute_WithValidInput_ReturnsFiveWindLines()
    {
        // Arrange
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 270.0), // Ground: 5 m/s, 270° (west)
            new WindRecalculator.WindLayer(10.0, 280.0), // 2000m: 10 m/s, 280°
            new WindRecalculator.WindLayer(15.0, 290.0), // 8000m: 15 m/s, 290°
            20, // Ground temp 20°C
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Count);
        Assert.All(result, line => Assert.NotNull(line));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 3)]
    [InlineData(26, 4)]
    public void Compute_ReturnsCorrectAltitudeLevels(int expectedAltKft, int index)
    {
        // Arrange
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 270.0),
            new WindRecalculator.WindLayer(10.0, 280.0),
            new WindRecalculator.WindLayer(15.0, 290.0),
            20,
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        Assert.Equal(expectedAltKft, result[index].AltKft);
    }

    [Fact]
    public void Compute_WithCaucasusMap_AppliesCorrectMagneticVariation()
    {
        // Arrange
        // Caucasus has 7.3° East variation
        // Wind from 270° (west) should become ~262.7° magnetic (270 - 7.3)
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 270.0),
            new WindRecalculator.WindLayer(10.0, 270.0),
            new WindRecalculator.WindLayer(15.0, 270.0),
            20,
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        // Ground level (0 kft) should have bearing ~263° (270 - 7.3, rounded)
        var groundLine = result[0];
        Assert.Equal(263, groundLine.BrgDegMag);
    }

    [Fact]
    public void Compute_WithMarianasMap_AppliesCorrectMagneticVariation()
    {
        // Arrange
        // Marianas has -0.5° variation (West)
        // Wind from 270° should become ~270.5° magnetic (270 - (-0.5))
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 270.0),
            new WindRecalculator.WindLayer(10.0, 270.0),
            new WindRecalculator.WindLayer(15.0, 270.0),
            20,
            "Marianas"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        var groundLine = result[0];
        // 270 - (-0.5) = 270.5, rounded away from zero = 271
        Assert.Equal(271, groundLine.BrgDegMag);
    }

    [Fact]
    public void Compute_WithGroundTemperature_CalculatesCorrectTemperatureLapse()
    {
        // Arrange
        // Temperature should decrease by 2°C per 1000ft
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 270.0),
            new WindRecalculator.WindLayer(10.0, 280.0),
            new WindRecalculator.WindLayer(15.0, 290.0),
            20, // Ground temp 20°C
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        Assert.Equal(20, result[0].TmpC); // 0 kft: 20 - (2 * 0) = 20
        Assert.Equal(18, result[1].TmpC); // 1 kft: 20 - (2 * 1) = 18
        Assert.Equal(16, result[2].TmpC); // 2 kft: 20 - (2 * 2) = 16
        Assert.Equal(6, result[3].TmpC);  // 7 kft: 20 - (2 * 7) = 6
        Assert.Equal(-32, result[4].TmpC); // 26 kft: 20 - (2 * 26) = -32
    }

    [Fact]
    public void Compute_WithSpeedConversion_AppliesCorrectFactors()
    {
        // Arrange
        // Excel uses 1.94 m/s to knots, but doubles for 01 and 02 kft
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(10.0, 270.0), // 10 m/s
            new WindRecalculator.WindLayer(10.0, 280.0),
            new WindRecalculator.WindLayer(10.0, 290.0),
            20,
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        // 0 kft: 10 * 1.94 = 19.4 → 19 kt
        Assert.Equal(19, result[0].SpdKt);
        
        // 1 kft: 10 * 1.94 * 2 = 38.8 → 39 kt
        Assert.Equal(39, result[1].SpdKt);
        
        // 2 kft: 10 * 1.94 * 2 = 38.8 → 39 kt
        Assert.Equal(39, result[2].SpdKt);
        
        // 7 kft: 10 * 1.94 = 19.4 → 19 kt
        Assert.Equal(19, result[3].SpdKt);
        
        // 26 kft: 10 * 1.94 = 19.4 → 19 kt
        Assert.Equal(19, result[4].SpdKt);
    }

    [Fact]
    public void Compute_WithNegativeBearing_NormalizesTo0To359()
    {
        // Arrange
        // If bearing calculation results in negative, should normalize to 0-359 range
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 5.0), // Small direction
            new WindRecalculator.WindLayer(10.0, 5.0),
            new WindRecalculator.WindLayer(15.0, 5.0),
            20,
            "Caucasus" // 7.3° variation, so 5 - 7.3 = -2.3 → should normalize to ~358
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        var groundLine = result[0];
        Assert.InRange(groundLine.BrgDegMag, 0, 359);
    }

    [Fact]
    public void Compute_WithBearingOver360_NormalizesTo0To359()
    {
        // Arrange
        var input = new WindRecalculator.BriefingInput(
            new WindRecalculator.WindLayer(5.0, 350.0),
            new WindRecalculator.WindLayer(10.0, 350.0),
            new WindRecalculator.WindLayer(15.0, 350.0),
            20,
            "Caucasus"
        );

        // Act
        var result = WindRecalculator.Compute(input);

        // Assert
        var groundLine = result[0];
        Assert.InRange(groundLine.BrgDegMag, 0, 359);
    }

    [Theory]
    [InlineData("Caucasus", 7.3)]
    [InlineData("Marianas", -0.5)]
    [InlineData("Nevada", 11.5)]
    [InlineData("Normandy", 1.2)]
    [InlineData("Persian Gulf", 2.6)]
    [InlineData("Sinai", 5.0)]
    [InlineData("Syria", 5.5)]
    [InlineData("The channel", 1.3)]
    [InlineData("Afghanistan", 3.5)]
    [InlineData("Cold War Germany", 3.0)]
    public void GetMagVarDeg_WithValidMapName_ReturnsCorrectVariation(string mapName, double expectedVariation)
    {
        // Act
        var result = WindRecalculator.GetMagVarDeg(mapName);

        // Assert
        Assert.Equal(expectedVariation, result);
    }

    [Fact]
    public void GetMagVarDeg_WithInvalidMapName_ThrowsKeyNotFoundException()
    {
        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => WindRecalculator.GetMagVarDeg("Invalid Map"));
    }

    [Fact]
    public void GetMagVarDeg_WithWhitespace_TrimsAndReturnsCorrectValue()
    {
        // Act
        var result = WindRecalculator.GetMagVarDeg("  Caucasus  ");

        // Assert
        Assert.Equal(7.3, result);
    }

    [Fact]
    public void GetAvailableMaps_ReturnsAllSupportedMaps()
    {
        // Act
        var result = WindRecalculator.GetAvailableMaps();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(10, result.Count);
        Assert.Contains("Caucasus", result);
        Assert.Contains("Marianas", result);
        Assert.Contains("Nevada", result);
        Assert.Contains("Normandy", result);
        Assert.Contains("Persian Gulf", result);
        Assert.Contains("Sinai", result);
        Assert.Contains("Syria", result);
        Assert.Contains("The channel", result);
        Assert.Contains("Afghanistan", result);
        Assert.Contains("Cold War Germany", result);
    }

    [Theory]
    [InlineData("Afghanistan", "Afghanistan")]
    [InlineData("Caucasus", "Caucasus")]
    [InlineData("GermanyCW", "Cold War Germany")]
    [InlineData("MarianaIslands", "Marianas")]
    [InlineData("Nevada", "Nevada")]
    [InlineData("Normandy", "Normandy")]
    [InlineData("PersianGulf", "Persian Gulf")]
    [InlineData("SinaiMap", "Sinai")]
    [InlineData("Syria", "Syria")]
    [InlineData("TheChannel", "The channel")]
    public void MapTheatreToMapName_WithValidTheatre_ReturnsCorrectMapName(string dcsTheatre, string expectedMapName)
    {
        // Act
        var result = WindRecalculator.MapTheatreToMapName(dcsTheatre);

        // Assert
        Assert.Equal(expectedMapName, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MapTheatreToMapName_WithNullOrWhitespace_ReturnsNull(string? dcsTheatre)
    {
        // Act
        var result = WindRecalculator.MapTheatreToMapName(dcsTheatre);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Iraq")]
    [InlineData("Kola")]
    [InlineData("SouthAtlantic")]
    public void MapTheatreToMapName_WithUnsupportedTheatre_ReturnsNull(string dcsTheatre)
    {
        // Act
        var result = WindRecalculator.MapTheatreToMapName(dcsTheatre);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("caucasus", "Caucasus")]
    [InlineData("MARIANA", "Marianas")]
    [InlineData("afghan", "Afghanistan")]
    [InlineData("germany", "Cold War Germany")]
    [InlineData("syria", "Syria")]
    public void MapTheatreToMapName_WithCaseVariations_UsesPartialMatching(string dcsTheatre, string expectedMapName)
    {
        // Act
        var result = WindRecalculator.MapTheatreToMapName(dcsTheatre);

        // Assert
        Assert.Equal(expectedMapName, result);
    }
}

