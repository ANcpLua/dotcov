using System.Text;

namespace DotCov.Tests;

/// <summary>
/// Run-4 mutation-gate kills: each test here kills a Stryker mutant that survived the full
/// 542-test suite at commit 2b2d203. Both targets are boundary gaps in the run-3 identity
/// code (<c>PathIdentity.NormalizeRoot</c>, <c>CoverageDiff.EvidencesSameFile</c>) — the
/// existing tests exercised those predicates only comfortably past their thresholds, so the
/// exact-boundary mutants (<c>&gt;=</c> → <c>&gt;</c>) passed unnoticed.
/// </summary>
public sealed class MutationKills4
{
    private static CoverageReport ParseXml(string xml) =>
        CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static CoverageReport Make(params FileCoverage[] files) => new(files);

    [Test]
    public async Task Parse_BareDriveRootRespellings_DedupToOneRootWithoutAmbiguityWarning()
    {
        // Kills PathIdentity.cs:33 (`root.Length >= 3` -> `> 3`). The drive-letter uppercase
        // rule needs exactly three characters for a BARE drive root ("c:/"), the shortest
        // spelling that carries one. The mutant skips uppercasing at that exact length, so
        // "c:/" normalizes to "c:" while "C:\" normalizes to "C:" — two spellings of one
        // root stop deduplicating, and a single-root report suddenly reports two roots plus
        // a guaranteed FileIdentityAmbiguous warning that the real parser never emits here.
        var report = ParseXml(
            """
            <?xml version="1.0"?>
            <coverage><sources><source>c:/</source><source>C:\</source></sources>
            <packages><package><classes>
              <class name="X" filename="app\main.py"><lines><line number="1" hits="1" branch="false" /></lines></class>
            </classes></package></packages></coverage>
            """);

        await Assert.That(report.SourceRoots.Single()).IsEqualTo("C:");
        await Assert.That(report.Warnings).IsEmpty();
        await Assert.That(report.Files.Single().Path).IsEqualTo("C:/app/main.py");
    }

    [Test]
    public async Task Compare_ExactlyTwoSegmentSuffixAgreement_IsAlreadyPairingEvidence()
    {
        // Kills CoverageDiff.cs:303 (`agree >= 2` -> `agree > 2`). Two whole trailing
        // segments are the documented minimum for the equal-roots pairing fallback: the
        // existing prefix-migration test agrees on THREE segments (src/MyApp/Calculator.cs
        // vs /_/src/MyApp/Calculator.cs), so the boundary mutant survived it. Here only
        // src/App.cs agrees (old vs new differ), which must still pair as one Modified
        // file — under the mutant the pair falls apart into Removed + Added.
        var before = Make(new FileCoverage("old/src/App.cs", 1, 2, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 }
        });
        var after = Make(new FileCoverage("new/src/App.cs", 2, 2, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 }
        });

        var result = CoverageDiff.Compare(before, after);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Path).IsEqualTo("new/src/App.cs");
        await Assert.That(result.Added).IsEmpty();
        await Assert.That(result.Removed).IsEmpty();
    }
}