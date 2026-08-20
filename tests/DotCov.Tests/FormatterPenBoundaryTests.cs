using DotCov.Formatters;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins <see cref="AnsiPen.Delta"/>'s coloring boundary to the exact value of
/// <see cref="CoverageDiff.MovementEpsilon"/>: a delta of exactly ±epsilon is movement —
/// the same classification <see cref="CoverageDiff.Compare"/> makes (its noise test is
/// strictly "closer to zero than epsilon") — so the pen and the diff can never disagree
/// about a file sitting precisely on the boundary.
/// </summary>
public sealed class FormatterPenBoundaryTests
{
    [Fact]
    public void MovementEpsilon_MatchesTheLiteralUsedByTheBoundaryTheory()
    {
        // InlineData needs compile-time constants, so the theory below uses literals; this
        // pin makes a change to the constant fail loudly here instead of silently defusing
        // the boundary cases.
        Assert.Equal(0.0001, CoverageDiff.MovementEpsilon);
    }

    [Theory]
    [InlineData(0.0001, "\e[32m")]    // exactly +epsilon → movement → green
    [InlineData(-0.0001, "\e[31m")]   // exactly -epsilon → movement → red
    [InlineData(0.00005, "\e[2m")]    // inside the noise band → dim
    [InlineData(-0.00005, "\e[2m")]
    public void Delta_AtMovementEpsilonBoundary_AgreesWithDiffClassification(double delta, string expectedPrefix)
    {
        var pen = new AnsiPen(enabled: true);

        Assert.StartsWith(expectedPrefix, pen.Delta("X", delta));
    }
}
