using System.Text;
using System.Xml.Linq;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>Branch IDs belong to IL owners; source line identity alone cannot deduplicate them.</summary>
[TestClass]
public sealed class BranchOriginTests(TestContext context)
{
    private static CoverageReport Parse(string xml) => CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "microsoft-same-line-owners.cobertura.xml");

    private static string Owner(string package, string type, string method, string signature, string detail = "50%") => $"""
        <package name="{package}"><classes><class filename="shared.cs" name="{type}">
          <methods><method name="{method}" signature="{signature}"><lines>
            <line number="10" hits="1" branch="true" condition-coverage="50% (1/2)">
              <conditions><condition number="0" coverage="{detail}"/></conditions>
            </line>
          </lines></method></methods>
          <lines><line number="10" hits="1" branch="true" condition-coverage="50% (1/2)">
            <conditions><condition number="0" coverage="{detail}"/></conditions>
          </line></lines>
        </class></classes></package>
        """;

    [TestMethod]
    public async Task MicrosoftAsyncAndLambdaBranches_SharedLineRetainsFourOfSixOutcomes()
    {
        var xml = await File.ReadAllTextAsync(Fixture);
        var sync = Parse(xml);
        var asyncReport = await CoberturaParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        foreach (var report in new[] { sync, asyncReport })
        {
            var file = Assert.ContainsSingle(report.Files);
            Assert.AreEqual((1, 1), (file.LinesHit, file.LinesTotal));
            Assert.AreEqual((4, 6), (file.BranchesHit, file.BranchesTotal));
            Assert.AreEqual((4, 6), file.BranchesByLine[107]);
            Assert.AreEqual(new BranchDetail(107, 4, 6), Assert.ContainsSingle(file.PartialBranches));
            Assert.AreEqual(LineStatus.Partial, file.GetLineStatus(107));
            Assert.AreEqual(4, file.BranchesByOrigin.Count); // two methods and their repeated summaries
            var lambda = file.BranchesByOrigin.Single(pair => pair.Key.Method == "<StartAsync>b__23_0");
            Assert.AreEqual("Architecture.Testing", lambda.Key.Package);
            Assert.AreEqual("Architecture.Testing.RunningSystem.<>c", lambda.Key.Class);
            Assert.AreEqual("(Architecture.Testing.BrickAttribute)", lambda.Key.Signature);
            Assert.AreEqual((1, 2), lambda.Value.BranchesByLine[107]);
            Assert.AreEqual(1, lambda.Value.ConditionsByLine[107][0]);
            Assert.IsFalse(file.ConditionsByLine.ContainsKey(107));
            Assert.IsEmpty(report.Warnings);
            Assert.AreEqual(GateOutcome.Fail, report.Evaluate(0, 75).Outcome);
            Assert.AreEqual(GateOutcome.Pass, report.Evaluate(0, 60).Outcome);
        }
    }

    [TestMethod]
    public async Task RealCli_SeventyFivePercentGateFailsForCapturedFourOfSixBranches()
    {
        var failed = await DotcovProcess.RunAsync(context.CancellationToken, AppContext.BaseDirectory,
            new Dictionary<string, string?>(), "check", Fixture, "--min-branch", "75");
        Assert.AreEqual(1, failed.ExitCode);
        Assert.Contains("FAIL", failed.StdOut + failed.StdErr);
        Assert.DoesNotContain("PASS", failed.StdOut + failed.StdErr);
        Assert.DoesNotContain("condition numbers", failed.StdErr);
        var passed = await DotcovProcess.RunAsync(context.CancellationToken, AppContext.BaseDirectory,
            new Dictionary<string, string?>(), "check", Fixture, "--min-branch", "60");
        Assert.AreEqual(0, passed.ExitCode);
        Assert.Contains("PASS", passed.StdOut + passed.StdErr);
    }

    [TestMethod]
    [DataRow("Other", "C", "M", "()")]
    [DataRow("P", "Other", "M", "()")]
    [DataRow("P", "C", "Other", "()")]
    [DataRow("P", "C", "M", "(int)")]
    public async Task DistinctPackageClassMethodOrSignature_AddsIndependentBranches(string package, string type, string method, string signature)
    {
        var a = Owner("P", "C", "M", "()");
        var b = Owner(package, type, method, signature);
        var together = Parse($"<coverage><packages>{a}{b}</packages></coverage>");
        var asyncTogether = await CoberturaParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes($"<coverage><packages>{a}{b}</packages></coverage>")));
        var left = Parse($"<coverage><packages>{a}</packages></coverage>");
        var right = Parse($"<coverage><packages>{b}</packages></coverage>");
        foreach (var report in new[] { together, asyncTogether, CoverageReport.Merge(left, right), CoverageReport.Merge(right, left), CoverageReport.Merge(together, together) })
        {
            var file = report.Files.Single();
            Assert.AreEqual((2, 4), file.BranchesByLine[10]);
            Assert.AreEqual(1, file.LinesTotal);
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MethodsOnSameClassLine_AddWithoutRepeatingTheClassSummary(bool hasSummary)
    {
        var doc = XDocument.Parse($"<coverage><packages>{Owner("P", "C", "A", "()")}</packages></coverage>");
        var method = doc.Descendants("method").Single();
        var second = new XElement(method);
        second.SetAttributeValue("name", "B");
        method.AddAfterSelf(second);
        if (!hasSummary) doc.Descendants("class").Single().Element("lines")!.Remove();
        var report = Parse(doc.ToString());
        Assert.AreEqual((2, 4), report.Files.Single().BranchesByLine[10]);
        Assert.AreEqual(hasSummary ? 3 : 2, report.Files.Single().BranchesByOrigin.Count);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    [DataRow("ABC")][DataRow("ACB")][DataRow("BAC")][DataRow("BCA")][DataRow("CAB")][DataRow("CBA")]
    public void SameOwnerMismatch_StaysPoisonedWithoutCollapsingAnotherOwner(string order)
    {
        var reports = new Dictionary<char, CoverageReport>
        {
            ['A'] = Cobertura.NewDoc().AddClass("shared.cs", "C", c => c.BranchWithConditions(10, "25% (1/4)", (0, "50%"), (1, "0%"))).Parse(),
            ['B'] = Cobertura.NewDoc().AddClass("shared.cs", "C", c => c.BranchWithConditions(10, "25% (1/4)", (0, "0%"), (2, "50%")))
                .AddClass("shared.cs", "Other", c => c.BranchWithConditions(10, "50% (1/2)", (0, "50%"))).Parse(),
            ['C'] = Cobertura.NewDoc().AddClass("shared.cs", "C", c => c.BranchWithConditions(10, "25% (1/4)", (0, "0%"), (1, "50%"))).Parse()
        };
        var result = order.Select(key => reports[key]).Aggregate(CoverageReport.Merge);
        Assert.AreEqual((2, 6), result.Files.Single().BranchesByLine[10]);
        Assert.Contains(static warning => warning.Kind == CoverageWarningKind.ConditionIdentityMismatch, result.Warnings);
    }

    [TestMethod]
    public void LegacyAggregate_IsAFileFloorAlongsideScopedObservations()
    {
        var parsed = Parse($"<coverage><packages>{Owner("P", "C", "M", "()")}</packages></coverage>");
        var legacy = new CoverageReport([new FileCoverage("shared.cs", 1, 1, 2, 4)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (2, 4) },
            ConditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>> { [10] = new Dictionary<int, int> { [0] = 2, [1] = 0 } }
        }]);
        foreach (var merged in new[] { CoverageReport.Merge(parsed, legacy), CoverageReport.Merge(legacy, parsed) })
        {
            Assert.AreEqual((2, 4), merged.Files.Single().BranchesByLine[10]);
            Assert.AreEqual((2, 4), (merged.Files.Single().BranchesHit, merged.Files.Single().BranchesTotal));
        }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedMethodObservations_UnionConditionsOnlyInsideTheSameOwner(bool mismatch)
    {
        var doc = XDocument.Parse($"<coverage><packages>{Owner("P", "C", "A", "()")}</packages></coverage>");
        doc.Descendants("class").Single().Element("lines")!.Remove();
        var method = doc.Descendants("method").Single();
        var firstLine = method.Descendants("line").Single();
        firstLine.SetAttributeValue("condition-coverage", "50% (2/4)");
        firstLine.Descendants("condition").Single().SetAttributeValue("coverage", "100%");
        firstLine.Element("conditions")!.Add(new XElement("condition", new XAttribute("number", "1"), new XAttribute("coverage", "0%")));
        var second = new XElement(method);
        var secondLine = second.Descendants("line").Single();
        secondLine.SetAttributeValue("hits", "3");
        secondLine.Descendants("condition").First().SetAttributeValue("coverage", "0%");
        secondLine.Descendants("condition").Last().SetAttributeValue("coverage", "100%");
        if (mismatch) secondLine.Descendants("condition").Last().SetAttributeValue("number", "2");
        method.AddAfterSelf(second, new XElement(method));
        var report = Parse(doc.ToString());
        var file = report.Files.Single();
        Assert.AreEqual(mismatch ? (2, 4) : (4, 4), file.BranchesByLine[10]);
        Assert.AreEqual(3, file.LineHits[10]);
        Assert.AreEqual(1, file.BranchesByOrigin.Count);
        if (mismatch)
        {
            Assert.IsEmpty(file.ConditionsByLine[10]);
            Assert.AreEqual(CoverageWarningKind.ConditionIdentityMismatch, Assert.ContainsSingle(report.Warnings).Kind);
        }
        else
        {
            Assert.AreEqual(2, file.ConditionsByLine[10][0]);
            Assert.AreEqual(2, file.ConditionsByLine[10][1]);
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RepeatedLineWithLargerTotal_PreservesAggregateAndValidatesConditionIdentity(bool incomingDetail)
    {
        var second = incomingDetail
            ? """<conditions><condition number="0" coverage="0%"/><condition number="1" coverage="50%"/></conditions>""" : "";
        var report = Parse($"""
            <coverage><packages><package name="P"><classes><class filename="a.cs" name="A"><lines>
              <line number="1" hits="1" branch="true" condition-coverage="50% (1/2)">
                <conditions><condition number="0" coverage="50%"/></conditions>
              </line>
              <line number="1" hits="2" branch="true" condition-coverage="25% (1/4)">{second}</line>
            </lines></class></classes></package></packages></coverage>
            """);
        Assert.AreEqual((1, 4), report.Files.Single().BranchesByLine[1]);
        Assert.AreEqual(2, report.Files.Single().LineHits[1]);
        if (incomingDetail)
        {
            Assert.IsEmpty(report.Files.Single().ConditionsByLine[1]);
            Assert.AreEqual(CoverageWarningKind.ConditionIdentityMismatch, Assert.ContainsSingle(report.Warnings).Kind);
        }
        else
        {
            Assert.IsFalse(report.Files.Single().ConditionsByLine.ContainsKey(1));
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    public void ScopedMeasurement_MergesWithAnEmptyLegacyFileInBothOrders()
    {
        var measured = Parse($"<coverage><packages>{Owner("P", "C", "M", "()")}</packages></coverage>").Files.Single();
        foreach (var file in new[] { measured.MergeWith(default).Merged, default(FileCoverage).MergeWith(measured).Merged })
        {
            Assert.AreEqual((1, 2), file.BranchesByLine[10]);
            Assert.AreEqual((1, 1), (file.LinesHit, file.LinesTotal));
        }
    }

    [TestMethod]
    public async Task PackageScope_EndsAtItsElementIncludingAnEmptyPackage()
    {
        var wrapped = XDocument.Parse($"<coverage><packages>{Owner("P", "C", "M", "()")}</packages></coverage>");
        var orphan = new XElement(wrapped.Descendants("class").Single());
        wrapped.Root!.Element("packages")!.Add(orphan);
        wrapped.Root.Element("packages")!.Add(new XElement("package", new XAttribute("name", "Empty")), new XElement(orphan));
        var xml = wrapped.ToString();
        foreach (var report in new[] { Parse(xml), await CoberturaParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml))) })
        {
            var file = report.Files.Single();
            Assert.AreEqual((2, 4), file.BranchesByLine[10]);
            Assert.AreSequenceEqual(["", "P"], file.BranchesByOrigin.Keys.Select(origin => origin.Package).Distinct(), SequenceOrder.InAnyOrder);
        }
    }

    [TestMethod]
    public void BranchOriginCollections_RejectMutationOfTheParsedMeasurement()
    {
        var file = Parse($"<coverage><packages>{Owner("P", "C", "M", "()")}</packages></coverage>").Files.Single();
        var origins = Assert.IsInstanceOfType<IDictionary<BranchOrigin, BranchCoverage>>(file.BranchesByOrigin);
        var pair = origins.First();
        Assert.ThrowsExactly<NotSupportedException>(() => origins.Remove(pair.Key));
        var branches = Assert.IsInstanceOfType<IDictionary<int, (int Covered, int Total)>>(pair.Value.BranchesByLine);
        Assert.ThrowsExactly<NotSupportedException>(() => branches[10] = (2, 2));
        Assert.AreEqual((1, 2), file.BranchesByLine[10]);
    }

}
