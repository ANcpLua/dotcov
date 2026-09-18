namespace DotCov.Tests.Infrastructure;

public static class CoberturaSamples
{
    // 1999/2500 = 79.96%: rounding to one decimal would hide a failed 80% gate.
    public static Cobertura JustBelowEightyPercent() => Cobertura.NewDoc()
        .AddClass("src/F.cs", c =>
        {
            for (var i = 1; i <= 1999; i++) c.Line(i, hits: 1);
            for (var i = 2000; i <= 2500; i++) c.Line(i, hits: 0);
        });
}
