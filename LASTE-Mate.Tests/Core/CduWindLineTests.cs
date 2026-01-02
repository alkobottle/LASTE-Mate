using LASTE_Mate.Core;
using Xunit;

namespace LASTE_Mate.Tests.Core;

public class CduWindLineTests
{
    [Fact]
    public void AltText_FormatsCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, 20);

        // Act & Assert
        Assert.Equal("00", line.AltText);
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(1, "01")]
    [InlineData(2, "02")]
    [InlineData(7, "07")]
    [InlineData(26, "26")]
    public void AltText_WithVariousAltitudes_FormatsCorrectly(int altKft, string expected)
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(altKft, 270, 15, 20);

        // Act & Assert
        Assert.Equal(expected, line.AltText);
    }

    [Fact]
    public void BrgText_FormatsCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, 20);

        // Act & Assert
        Assert.Equal("270", line.BrgText);
    }

    [Theory]
    [InlineData(0, "000")]
    [InlineData(45, "045")]
    [InlineData(90, "090")]
    [InlineData(180, "180")]
    [InlineData(270, "270")]
    [InlineData(359, "359")]
    public void BrgText_WithVariousBearings_FormatsCorrectly(int brgDeg, string expected)
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, brgDeg, 15, 20);

        // Act & Assert
        Assert.Equal(expected, line.BrgText);
    }

    [Fact]
    public void SpdText_FormatsCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, 20);

        // Act & Assert
        Assert.Equal("15", line.SpdText);
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(5, "05")]
    [InlineData(15, "15")]
    [InlineData(99, "99")]
    [InlineData(100, "100")] // 3 digits if >= 100
    public void SpdText_WithVariousSpeeds_FormatsCorrectly(int spdKt, string expected)
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, spdKt, 20);

        // Act & Assert
        Assert.Equal(expected, line.SpdText);
    }

    [Fact]
    public void BrgPlusSpd_CombinesCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, 20);

        // Act & Assert
        Assert.Equal("27015", line.BrgPlusSpd);
    }

    [Theory]
    [InlineData(0, 5, "00005")]
    [InlineData(45, 10, "04510")]
    [InlineData(270, 15, "27015")]
    [InlineData(359, 99, "35999")]
    public void BrgPlusSpd_WithVariousValues_CombinesCorrectly(int brgDeg, int spdKt, string expected)
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, brgDeg, spdKt, 20);

        // Act & Assert
        Assert.Equal(expected, line.BrgPlusSpd);
    }

    [Fact]
    public void TmpText_WithPositiveTemperature_FormatsCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, 20);

        // Act & Assert
        Assert.Equal("+20", line.TmpText);
    }

    [Fact]
    public void TmpText_WithNegativeTemperature_FormatsCorrectly()
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, -10);

        // Act & Assert
        Assert.Equal("-10", line.TmpText);
    }

    [Theory]
    [InlineData(0, "+00")]
    [InlineData(5, "+05")]
    [InlineData(20, "+20")]
    [InlineData(-5, "-05")]
    [InlineData(-10, "-10")]
    [InlineData(-32, "-32")]
    public void TmpText_WithVariousTemperatures_FormatsCorrectly(int tmpC, string expected)
    {
        // Arrange
        var line = new WindRecalculator.CduWindLine(0, 270, 15, tmpC);

        // Act & Assert
        Assert.Equal(expected, line.TmpText);
    }
}

