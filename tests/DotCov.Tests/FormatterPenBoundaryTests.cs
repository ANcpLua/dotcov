using DotCov.Formatters;

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
    [Test]
    public async Task MovementEpsilon_MatchesTheLiteralUsedByTheBoundaryTheory()
    {
        // InlineData needs compile-time constants, so the theory below uses literals; this
        // pin makes a change to the constant fail loudly here instead of silently defusing
        // the boundary cases.
        var epsilon = CoverageDiff.MovementEpsilon;
        await Assert.That(epsilon).IsEqualTo(0.0001);
    }

    [Test]
    [Arguments(0.0001, "\e[32m")]    // exactly +epsilon → movement → green
    [Arguments(-0.0001, "\e[31m")]   // exactly -epsilon → movement → red
    [Arguments(0.00005, "\e[2m")]    // inside the noise band → dim
    [Arguments(-0.00005, "\e[2m")]
    public async Task Delta_AtMovementEpsilonBoundary_AgreesWithDiffClassification(double delta, string expectedPrefix)
    {
        var pen = new AnsiPen(enabled: true);

        await Assert.That(pen.Delta("X", delta)).StartsWith(expectedPrefix);
    }
}