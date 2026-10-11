using System.Text;

namespace DotCov.Tests;

/// <summary>
/// <see cref="CodeMetricsReader"/> against the display strings Roslyn really writes. Every
/// display below was produced by <c>MetricsOutputWriter</c>'s exact naming calls (namespace
/// <c>ToDisplayString()</c>, member <c>MinimallyQualifiedFormat</c>) over real C# source, so
/// the expected member names are the coverage spellings the IL carries for the same symbols.
/// Plus the malformed and unusual document shapes the streaming reader must survive.
/// </summary>
public sealed class CodeMetricsReaderEdgeTests
{
    private static async Task<CodeMetricsMember> One(IReadOnlyList<CodeMetricsMember> members) =>
        await Assert.That(members).HasSingleItem();

    private static IReadOnlyList<CodeMetricsMember> ParseMethod(string display, string type = "GlobalThing", string ns = "MyApp") =>
        CodeMetricsReportBuilder.ParseType(ns, type, m => m.Method(display, 3));

    private static IReadOnlyList<CodeMetricsMember> ParseRaw(string namespacesXml)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CodeMetricsReport Version="1.0">
              <Targets>
                <Target Name="Probe.csproj">
                  <Assembly Name="Probe, Version=1.0.0.0">
                    <Namespaces>
            {namespacesXml}
                    </Namespaces>
                  </Assembly>
                </Target>
              </Targets>
            </CodeMetricsReport>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return CodeMetricsReader.Parse(stream);
    }

    private const string Metric3 = """<Metrics><Metric Name="CyclomaticComplexity" Value="3" /></Metrics>""";

    // ── Real Roslyn shapes ────────────────────────────────────────────────────

    [Test]
    public async Task GlobalNamespace_DisplayName_ContributesNoTypePrefix()
    {
        // Namespace names come from ToDisplayString(), which spells the global namespace
        // "<global namespace>". IL (and so every coverage report) names those types bare.
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "GlobalThing",
            m => m.Method("void GlobalThing.Run()", 4));

        var m = await One(members);
        await Assert.That(m.TypeName).IsEqualTo("GlobalThing");
        await Assert.That(m.MemberName).IsEqualTo("Run");
    }

    [Test]
    public async Task TopLevelStatements_RealEntryPointDisplay_NormalizesToProgramMain()
    {
        // MinimallyQualifiedFormat displays the synthesized entry point as
        // "<top-level-statements-entry-point>" (no type prefix, no parameter list), under
        // <NamedType Name="Program"> in the global namespace. Coverage names it Program/<Main>$,
        // which MethodIdentity folds to Program.Main.
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "Program",
            m => m.Method("<top-level-statements-entry-point>", 5));

        var m = await One(members);
        await Assert.That(m.TypeName).IsEqualTo("Program");
        await Assert.That(m.MemberName).IsEqualTo("Main");
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
        await Assert.That(m.CyclomaticComplexity).IsEqualTo(5);
    }

    [Test]
    [Arguments("(int A, string B) GlobalThing.Split(string s, char c)", "Split", 2)]
    [Arguments("(int, (string, int)) GlobalThing.Nest()", "Nest", 0)]
    [Arguments("(int, int)[] GlobalThing.Pairs(int count)", "Pairs", 1)]
    [Arguments("ref (int, int) GlobalThing.Slot(int index)", "Slot", 1)]
    [Arguments("Task<(int, int)> GlobalThing.LoadAsync(string a, string b)", "LoadAsync", 2)]
    public async Task TupleReturnType_NameAndArityComeFromTheParameterList(string display, string name, int arity)
    {
        // A tuple return type opens the display with a top-level '(' that is a TYPE, not the
        // parameter list; taking it as the list would leave an empty member name.
        var m = await One(ParseMethod(display));

        await Assert.That(m.MemberName).IsEqualTo(name);
        await Assert.That(m.Arity).IsEqualTo(arity);
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
    }

    [Test]
    public async Task TupleTypedProperty_AggregateAndAccessorsKeepTheirNames()
    {
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m
            .Property("(int, int) GlobalThing.Pair", 2, a => a
                .Method("(int, int) GlobalThing.Pair.get", 1)
                .Method("void GlobalThing.Pair.set", 1)));

        await Assert.That(members.Select(m => (m.MemberName, m.Kind, m.Arity))).IsEquivalentTo(
        [
            ("get_Pair", CodeMetricsMemberKind.Accessor, (int?)null),
            ("set_Pair", CodeMetricsMemberKind.Accessor, (int?)null),
            ("Pair", CodeMetricsMemberKind.Property, (int?)null),
        ]);
    }

    [Test]
    public async Task ConversionToTuple_ArityCountsOnlyTheParameterList()
    {
        var m = await One(ParseMethod("GlobalThing.implicit operator (int, int)(GlobalThing g)"));

        await Assert.That(m.MemberName).IsEqualTo("op_Implicit");
        await Assert.That(m.Arity).IsEqualTo(1);
    }

    [Test]
    [Arguments("string GlobalThing.Join(IEnumerable<string> items, string sep = \", \")", 2)]
    [Arguments("(int A, string B) GlobalThing.Split(string s = \", \", char c = '<')", 2)]
    [Arguments("string GlobalThing.Quote(string q = \"a\\\"(,\", char c = '\\'')", 2)]
    [Arguments("void GlobalThing.Wrap(char open = '(', char close = ')', int depth = 0)", 3)]
    [Arguments("void GlobalThing.Angle(char open = '<', int depth = 0)", 2)]
    [Arguments("void GlobalThing.Close(string tail = \")(\", int depth = 0)", 2)]
    [Arguments("void GlobalThing.Path(string sep = \"\\\\\", int depth = 0)", 2)]
    public async Task DefaultValueLiterals_NeverChangeTheArity(string display, int arity)
    {
        // MinimallyQualifiedFormat includes default values; string and char literals may hold
        // commas and brackets that are text, not parameter-list structure.
        var m = await One(ParseMethod(display));

        await Assert.That(m.Arity).IsEqualTo(arity);
    }

    [Test]
    public async Task Destructor_NormalizesToFinalize()
    {
        // `~T()` compiles to a method named Finalize — the coverage spelling.
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "Stack<T>", m => m.Method("Stack<T>.~Stack()", 2));

        var m = await One(members);
        await Assert.That(m.MemberName).IsEqualTo("Finalize");
        await Assert.That(m.Arity).IsEqualTo(0);
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
    }

    [Test]
    public async Task CheckedExplicitConversion_NormalizesToOpCheckedExplicit()
    {
        // `explicit operator checked int` compiles to op_CheckedExplicit; mapping it to
        // op_Explicit would silently merge it with the unchecked conversion of the same arity.
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m
            .Method("GlobalThing.explicit operator int(GlobalThing b)", 1)
            .Method("GlobalThing.explicit operator checked int(GlobalThing b)", 4));

        await Assert.That(members.Select(m => (m.MemberName, m.CyclomaticComplexity))).IsEquivalentTo(
        [
            ("op_Explicit", 1),
            ("op_CheckedExplicit", 4),
        ]);
    }

    /// <summary>
    /// C# 11 checked operators and C# 14 compound-assignment / instance increment operators:
    /// displays as Roslyn 5 writes them, expected names as reflection reads them from the IL.
    /// </summary>
    [Test]
    [Arguments("Op Op.operator checked +(Op a, Op b)", "op_CheckedAddition")]
    [Arguments("Op Op.operator checked -(Op a, Op b)", "op_CheckedSubtraction")]
    [Arguments("Op Op.operator checked *(Op a, Op b)", "op_CheckedMultiply")]
    [Arguments("Op Op.operator checked /(Op a, Op b)", "op_CheckedDivision")]
    [Arguments("Op Op.operator checked -(Op a)", "op_CheckedUnaryNegation")]
    [Arguments("Op Op.operator checked ++(Op a)", "op_CheckedIncrement")]
    [Arguments("Op Op.operator checked --(Op a)", "op_CheckedDecrement")]
    [Arguments("Op Op.operator ++(Op a)", "op_Increment")]
    [Arguments("void Op.operator ++()", "op_IncrementAssignment")]
    [Arguments("void Op.operator --()", "op_DecrementAssignment")]
    [Arguments("void Op.operator checked ++()", "op_CheckedIncrementAssignment")]
    [Arguments("void Op.operator checked --()", "op_CheckedDecrementAssignment")]
    [Arguments("void Op.operator +=(Op o)", "op_AdditionAssignment")]
    [Arguments("void Op.operator -=(Op o)", "op_SubtractionAssignment")]
    [Arguments("void Op.operator *=(Op o)", "op_MultiplicationAssignment")]
    [Arguments("void Op.operator /=(Op o)", "op_DivisionAssignment")]
    [Arguments("void Op.operator %=(Op o)", "op_ModulusAssignment")]
    [Arguments("void Op.operator &=(Op o)", "op_BitwiseAndAssignment")]
    [Arguments("void Op.operator |=(Op o)", "op_BitwiseOrAssignment")]
    [Arguments("void Op.operator ^=(Op o)", "op_ExclusiveOrAssignment")]
    [Arguments("void Op.operator <<=(int o)", "op_LeftShiftAssignment")]
    [Arguments("void Op.operator >>=(int o)", "op_RightShiftAssignment")]
    [Arguments("void Op.operator >>>=(int o)", "op_UnsignedRightShiftAssignment")]
    [Arguments("void Op.operator checked +=(Op o)", "op_CheckedAdditionAssignment")]
    [Arguments("void Op.operator checked -=(Op o)", "op_CheckedSubtractionAssignment")]
    [Arguments("void Op.operator checked *=(Op o)", "op_CheckedMultiplicationAssignment")]
    [Arguments("void Op.operator checked /=(Op o)", "op_CheckedDivisionAssignment")]
    public async Task CheckedAndCompoundOperators_NormalizeToTheirIlNames(string display, string expected)
    {
        var m = await One(ParseMethod(display, type: "Op"));

        await Assert.That(m.MemberName).IsEqualTo(expected);
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
    }

    [Test]
    [Arguments("Op Op.operator @(Op a)", "operator @")]
    [Arguments("Op Op.operator checked @(Op a)", "operator checked @")]
    public async Task UnknownOperatorToken_KeepsItsRawSpelling(string display, string expected)
    {
        // A token outside the table (a future language version) must land in the unmatched
        // list under its own spelling, never collide with a known operator.
        await Assert.That((await One(ParseMethod(display, type: "Op"))).MemberName).IsEqualTo(expected);
    }

    [Test]
    public async Task ExtensionBlockMembers_BelongToTheEnclosingStaticClass()
    {
        // C# 14 `extension(string s) { ... }` displays as a nested `Ext.extension(string)` type,
        // but its members compile to static methods of Ext itself (Ext.Twice(string),
        // Ext.get_IsBig(string), Ext.Make()).
        var members = CodeMetricsReportBuilder.Create().Namespace("P", t => t
                .Type("Ext.extension(string)", m => m
                    .Method("int extension(string).Twice()", 2)
                    .Property("bool extension(string).IsBig", 1, a => a.Method("bool extension(string).IsBig.get", 1))
                    .Method("string extension(string).Make()", 1))
                .Type("Ext.extension<T>(List<T>)", m => m.Method("T? extension<T>(List<T>).FirstOrNone(int skip = 0)", 2))
                .Type("Ext.extension(Inner)", m => m
                    .Property("int extension(Inner).Depth", 1, a => a.Method("int extension(Inner).Depth.get", 1))))
            .Parse();

        await Assert.That(members.Select(m => (m.TypeName, m.MemberName, m.Kind))).IsEquivalentTo(
        [
            ("P.Ext", "Twice", CodeMetricsMemberKind.Method),
            ("P.Ext", "get_IsBig", CodeMetricsMemberKind.Accessor),
            ("P.Ext", "IsBig", CodeMetricsMemberKind.Property),
            ("P.Ext", "Make", CodeMetricsMemberKind.Method),
            ("P.Ext", "FirstOrNone", CodeMetricsMemberKind.Method),
            ("P.Ext", "get_Depth", CodeMetricsMemberKind.Accessor),
            ("P.Ext", "Depth", CodeMetricsMemberKind.Property),
        ]);
    }

    [Test]
    public async Task IndexerAccessors_NormalizeToItemAccessors()
    {
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m
            .Property("int GlobalThing.this[int i]", 2, a => a
                .Method("int GlobalThing.this[int i].get", 1)
                .Method("void GlobalThing.this[int i].set", 1)));

        await Assert.That(members.Select(m => (m.MemberName, m.Kind))).IsEquivalentTo(
        [
            ("get_Item", CodeMetricsMemberKind.Accessor),
            ("set_Item", CodeMetricsMemberKind.Accessor),
            ("Item", CodeMetricsMemberKind.Property),
        ]);
    }

    [Test]
    public async Task ArrayReturnOperator_BracketsBeforeTheOperatorMarkerStayBalanced()
    {
        var m = await One(ParseMethod("int[] GlobalThing.operator +(GlobalThing a, int[,] b)"));

        await Assert.That(m.MemberName).IsEqualTo("op_Addition");
        await Assert.That(m.Arity).IsEqualTo(2);
    }

    [Test]
    public async Task NestedTypeInGlobalNamespace_DottedNameKeepsContainingTypes()
    {
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "GlobalThing.Nested<T>.Deeper",
            m => m.Method("void Deeper.D()", 1));

        await Assert.That((await One(members)).TypeName).IsEqualTo("GlobalThing.Nested.Deeper");
    }

    [Test]
    public async Task VeryLongGenericTypeName_StripsLikeAShortOne()
    {
        // Names over 256 characters take the heap buffer instead of stackalloc.
        var args = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"T{i}"));
        var type = $"Wide<{args}>";
        await Assert.That(type.Length).IsGreaterThan(256);

        var members = CodeMetricsReportBuilder.ParseType("MyApp", type, m => m.Method($"void {type}.Run()", 1));

        await Assert.That((await One(members)).TypeName).IsEqualTo("MyApp.Wide");
    }

    [Test]
    public async Task UnclosedParameterList_CountsWhatIsThere()
    {
        var m = await One(ParseMethod("void GlobalThing.Cut(int a, int b"));

        await Assert.That(m.MemberName).IsEqualTo("Cut");
        await Assert.That(m.Arity).IsEqualTo(2);
    }

    [Test]
    public async Task UnterminatedDefaultLiteral_SwallowsTheRestOfTheList()
    {
        var m = await One(ParseMethod("void GlobalThing.Cut(int a, string s = \"x, int b)"));

        await Assert.That(m.MemberName).IsEqualTo("Cut");
        await Assert.That(m.Arity).IsEqualTo(2);
    }

    // ── Unusual document shapes ───────────────────────────────────────────────

    [Test]
    public async Task NamespaceWithoutName_TypeNameHasNoPrefix()
    {
        var members = ParseRaw($"""
            <Namespace><Types><NamedType Name="Calc"><Members>
              <Method Name="void Calc.M()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);

        await Assert.That((await One(members)).TypeName).IsEqualTo("Calc");
    }

    [Test]
    public async Task EmptyNamedTypeElement_DoesNotNestTheNextType()
    {
        // An empty element raises no EndElement, so it must not push a type segment.
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types>
              <NamedType Name="Empty" />
              <NamedType Name="Calc"><Members><Method Name="void Calc.M()">{Metric3}</Method></Members></NamedType>
            </Types></Namespace>
            """);

        await Assert.That((await One(members)).TypeName).IsEqualTo("MyApp.Calc");
    }

    [Test]
    public async Task NamedTypeWithoutName_MemberStillRecordedUnderAnEmptySegment()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType><Members>
              <Method Name="void Calc.M()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);

        var m = await One(members);
        await Assert.That(m.TypeName).IsEqualTo("MyApp.");
        await Assert.That(m.MemberName).IsEqualTo("M");
        await Assert.That(m.CyclomaticComplexity).IsEqualTo(3);
    }

    [Test]
    public async Task MemberOutsideAnyType_IsIgnored_AndDoesNotDisturbLaterMembers()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp">
              <Method Name="void Stray.M()">{Metric3}</Method>
              <Types><NamedType Name="Calc"><Members>
                <Method Name="void Calc.Kept()"><Metrics><Metric Name="CyclomaticComplexity" Value="7" /></Metrics></Method>
              </Members></NamedType></Types>
            </Namespace>
            """);

        var m = await One(members);
        await Assert.That(m.MemberName).IsEqualTo("Kept");
        await Assert.That(m.CyclomaticComplexity).IsEqualTo(7);
    }

    [Test]
    public async Task EmptyMemberElement_RecordsNothing_AndLeavesTheStackBalanced()
    {
        // An empty Property raises no EndElement; pushing it would make the following
        // method's metric land on the property and drop the method.
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Property Name="int Calc.Hollow" />
              <Method Name="void Calc.After()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);

        var m = await One(members);
        await Assert.That(m.MemberName).IsEqualTo("After");
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
    }

    [Test]
    public async Task MemberWithoutName_RecordedWithEmptyDisplay()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Method>{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);

        var m = await One(members);
        await Assert.That(m.DisplayName).IsEqualTo("");
        await Assert.That(m.MemberName).IsEqualTo("");
        await Assert.That(m.Arity).IsNull();
    }

    [Test]
    [Arguments("n/a")]
    [Arguments("2.5")]
    [Arguments("99999999999")]
    [Arguments("")]
    public async Task NonIntegerComplexity_MemberSkippedNotZeroed(string value)
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Method Name="void Calc.M()"><Metrics><Metric Name="CyclomaticComplexity" Value="{value}" /></Metrics></Method>
            </Members></NamedType></Types></Namespace>
            """);

        await Assert.That(members).IsEmpty();
    }

    [Test]
    public async Task Parse_LeavesTheCallersStreamOpen()
    {
        var bytes = CodeMetricsReportBuilder.Create()
            .Namespace("MyApp", t => t.Type("Calc", m => m.Method("void Calc.M()", 2)))
            .ToBytes();
        using var stream = new MemoryStream(bytes);

        var members = CodeMetricsReader.Parse(stream);

        await Assert.That(members.Count).IsEqualTo(1);
        await Assert.That(stream.CanRead).IsTrue();
    }
}
