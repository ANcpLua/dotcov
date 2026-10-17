using System.Text;
using System.Xml;

namespace DotCov.Tests;
/// <summary>
/// <see cref = "CodeMetricsReader"/> against the display strings Roslyn really writes. Every
/// display below was produced by <c>MetricsOutputWriter</c>'s exact naming calls (namespace
/// <c>ToDisplayString()</c>, member <c>MinimallyQualifiedFormat</c>) over real C# source, so
/// the expected member names are the coverage spellings the IL carries for the same symbols.
/// Plus the malformed and unusual document shapes the streaming reader must survive.
/// </summary>
[TestClass]
public sealed class CodeMetricsReaderEdgeTests
{
    private static CodeMetricsMember One(IReadOnlyList<CodeMetricsMember> members) => Assert.ContainsSingle(members);
    private static IReadOnlyList<CodeMetricsMember> ParseMethod(string display, string type = "GlobalThing", string ns = "MyApp") => CodeMetricsReportBuilder.ParseType(ns, type, m => m.Method(display, 3));
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
    [TestMethod]
    public void GlobalNamespace_DisplayName_ContributesNoTypePrefix()
    {
        // Namespace names come from ToDisplayString(), which spells the global namespace
        // "<global namespace>". IL (and so every coverage report) names those types bare.
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "GlobalThing", m => m.Method("void GlobalThing.Run()", 4));
        var m = One(members);
        Assert.AreEqual("GlobalThing", m.TypeName);
        Assert.AreEqual("Run", m.MemberName);
    }

    [TestMethod]
    public void TopLevelStatements_RealEntryPointDisplay_NormalizesToProgramMain()
    {
        // MinimallyQualifiedFormat displays the synthesized entry point as
        // "<top-level-statements-entry-point>" (no type prefix, no parameter list), under
        // <NamedType Name="Program"> in the global namespace. Coverage names it Program/<Main>$,
        // which MethodIdentity folds to Program.Main.
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "Program", m => m.Method("<top-level-statements-entry-point>", 5));
        var m = One(members);
        Assert.AreEqual("Program", m.TypeName);
        Assert.AreEqual("Main", m.MemberName);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
        Assert.AreEqual(5, m.CyclomaticComplexity);
    }

    [TestMethod]
    [DataRow("(int A, string B) GlobalThing.Split(string s, char c)", "Split", 2)]
    [DataRow("(int, (string, int)) GlobalThing.Nest()", "Nest", 0)]
    [DataRow("(int, int)[] GlobalThing.Pairs(int count)", "Pairs", 1)]
    [DataRow("ref (int, int) GlobalThing.Slot(int index)", "Slot", 1)]
    [DataRow("Task<(int, int)> GlobalThing.LoadAsync(string a, string b)", "LoadAsync", 2)]
    public void TupleReturnType_NameAndArityComeFromTheParameterList(string display, string name, int arity)
    {
        // A tuple return type opens the display with a top-level '(' that is a TYPE, not the
        // parameter list; taking it as the list would leave an empty member name.
        var m = One(ParseMethod(display));
        Assert.AreEqual(name, m.MemberName);
        Assert.AreEqual(arity, m.Arity);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
    }

    [TestMethod]
    public void TupleTypedProperty_AggregateAndAccessorsKeepTheirNames()
    {
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m.Property("(int, int) GlobalThing.Pair", 2, a => a.Method("(int, int) GlobalThing.Pair.get", 1).Method("void GlobalThing.Pair.set", 1)));
        Assert.AreSequenceEqual([("get_Pair", CodeMetricsMemberKind.Accessor, null), ("set_Pair", CodeMetricsMemberKind.Accessor, null), ("Pair", CodeMetricsMemberKind.Property, (int? )null), ], members.Select(m => (m.MemberName, m.Kind, m.Arity)), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ConversionToTuple_ArityCountsOnlyTheParameterList()
    {
        var m = One(ParseMethod("GlobalThing.implicit operator (int, int)(GlobalThing g)"));
        Assert.AreEqual("op_Implicit", m.MemberName);
        Assert.AreEqual(1, m.Arity);
    }

    [TestMethod]
    [DataRow("string GlobalThing.Join(IEnumerable<string> items, string sep = \", \")", 2)]
    [DataRow("(int A, string B) GlobalThing.Split(string s = \", \", char c = '<')", 2)]
    [DataRow("string GlobalThing.Quote(string q = \"a\\\"(,\", char c = '\\'')", 2)]
    [DataRow("void GlobalThing.Wrap(char open = '(', char close = ')', int depth = 0)", 3)]
    [DataRow("void GlobalThing.Angle(char open = '<', int depth = 0)", 2)]
    [DataRow("void GlobalThing.Close(string tail = \")(\", int depth = 0)", 2)]
    [DataRow("void GlobalThing.Path(string sep = \"\\\\\", int depth = 0)", 2)]
    public void DefaultValueLiterals_NeverChangeTheArity(string display, int arity)
    {
        // MinimallyQualifiedFormat includes default values; string and char literals may hold
        // commas and brackets that are text, not parameter-list structure.
        var m = One(ParseMethod(display));
        Assert.AreEqual(arity, m.Arity);
    }

    [TestMethod]
    public void Destructor_NormalizesToFinalize()
    {
        // `~T()` compiles to a method named Finalize — the coverage spelling.
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "Stack<T>", m => m.Method("Stack<T>.~Stack()", 2));
        var m = One(members);
        Assert.AreEqual("Finalize", m.MemberName);
        Assert.AreEqual(0, m.Arity);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
    }

    [TestMethod]
    public void CheckedExplicitConversion_NormalizesToOpCheckedExplicit()
    {
        // `explicit operator checked int` compiles to op_CheckedExplicit; mapping it to
        // op_Explicit would silently merge it with the unchecked conversion of the same arity.
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m.Method("GlobalThing.explicit operator int(GlobalThing b)", 1).Method("GlobalThing.explicit operator checked int(GlobalThing b)", 4));
        Assert.AreSequenceEqual([("op_Explicit", 1), ("op_CheckedExplicit", 4), ], members.Select(m => (m.MemberName, m.CyclomaticComplexity)), SequenceOrder.InAnyOrder);
    }

    /// <summary>
    /// C# 11 checked operators and C# 14 compound-assignment / instance increment operators:
    /// displays as Roslyn 5 writes them, expected names as reflection reads them from the IL.
    /// </summary>
    [TestMethod]
    [DataRow("Op Op.operator checked +(Op a, Op b)", "op_CheckedAddition")]
    [DataRow("Op Op.operator checked -(Op a, Op b)", "op_CheckedSubtraction")]
    [DataRow("Op Op.operator checked *(Op a, Op b)", "op_CheckedMultiply")]
    [DataRow("Op Op.operator checked /(Op a, Op b)", "op_CheckedDivision")]
    [DataRow("Op Op.operator checked -(Op a)", "op_CheckedUnaryNegation")]
    [DataRow("Op Op.operator checked ++(Op a)", "op_CheckedIncrement")]
    [DataRow("Op Op.operator checked --(Op a)", "op_CheckedDecrement")]
    [DataRow("Op Op.operator ++(Op a)", "op_Increment")]
    [DataRow("void Op.operator ++()", "op_IncrementAssignment")]
    [DataRow("void Op.operator --()", "op_DecrementAssignment")]
    [DataRow("void Op.operator checked ++()", "op_CheckedIncrementAssignment")]
    [DataRow("void Op.operator checked --()", "op_CheckedDecrementAssignment")]
    [DataRow("void Op.operator +=(Op o)", "op_AdditionAssignment")]
    [DataRow("void Op.operator -=(Op o)", "op_SubtractionAssignment")]
    [DataRow("void Op.operator *=(Op o)", "op_MultiplicationAssignment")]
    [DataRow("void Op.operator /=(Op o)", "op_DivisionAssignment")]
    [DataRow("void Op.operator %=(Op o)", "op_ModulusAssignment")]
    [DataRow("void Op.operator &=(Op o)", "op_BitwiseAndAssignment")]
    [DataRow("void Op.operator |=(Op o)", "op_BitwiseOrAssignment")]
    [DataRow("void Op.operator ^=(Op o)", "op_ExclusiveOrAssignment")]
    [DataRow("void Op.operator <<=(int o)", "op_LeftShiftAssignment")]
    [DataRow("void Op.operator >>=(int o)", "op_RightShiftAssignment")]
    [DataRow("void Op.operator >>>=(int o)", "op_UnsignedRightShiftAssignment")]
    [DataRow("void Op.operator checked +=(Op o)", "op_CheckedAdditionAssignment")]
    [DataRow("void Op.operator checked -=(Op o)", "op_CheckedSubtractionAssignment")]
    [DataRow("void Op.operator checked *=(Op o)", "op_CheckedMultiplicationAssignment")]
    [DataRow("void Op.operator checked /=(Op o)", "op_CheckedDivisionAssignment")]
    public void CheckedAndCompoundOperators_NormalizeToTheirIlNames(string display, string expected)
    {
        var m = One(ParseMethod(display, type: "Op"));
        Assert.AreEqual(expected, m.MemberName);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
    }

    [TestMethod]
    [DataRow("Op Op.operator @(Op a)", "operator @")]
    [DataRow("Op Op.operator checked @(Op a)", "operator checked @")]
    public void UnknownOperatorToken_KeepsItsRawSpelling(string display, string expected)
    {
        // A token outside the table (a future language version) must land in the unmatched
        // list under its own spelling, never collide with a known operator.
        Assert.AreEqual(expected, (One(ParseMethod(display, type: "Op"))).MemberName);
    }

    [TestMethod]
    public void ExtensionBlockMembers_BelongToTheEnclosingStaticClass()
    {
        // C# 14 `extension(string s) { ... }` displays as a nested `Ext.extension(string)` type,
        // but its members compile to static methods of Ext itself (Ext.Twice(string),
        // Ext.get_IsBig(string), Ext.Make()).
        var members = CodeMetricsReportBuilder.Create().Namespace("P", t => t.Type("Ext.extension(string)", m => m.Method("int extension(string).Twice()", 2).Property("bool extension(string).IsBig", 1, a => a.Method("bool extension(string).IsBig.get", 1)).Method("string extension(string).Make()", 1)).Type("Ext.extension<T>(List<T>)", m => m.Method("T? extension<T>(List<T>).FirstOrNone(int skip = 0)", 2)).Type("Ext.extension(Inner)", m => m.Property("int extension(Inner).Depth", 1, a => a.Method("int extension(Inner).Depth.get", 1)))).Parse();
        Assert.AreSequenceEqual([("P.Ext", "Twice", CodeMetricsMemberKind.Method), ("P.Ext", "get_IsBig", CodeMetricsMemberKind.Accessor), ("P.Ext", "IsBig", CodeMetricsMemberKind.Property), ("P.Ext", "Make", CodeMetricsMemberKind.Method), ("P.Ext", "FirstOrNone", CodeMetricsMemberKind.Method), ("P.Ext", "get_Depth", CodeMetricsMemberKind.Accessor), ("P.Ext", "Depth", CodeMetricsMemberKind.Property), ], members.Select(m => (m.TypeName, m.MemberName, m.Kind)), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void IndexerAccessors_NormalizeToItemAccessors()
    {
        var members = CodeMetricsReportBuilder.ParseType("MyApp", "GlobalThing", m => m.Property("int GlobalThing.this[int i]", 2, a => a.Method("int GlobalThing.this[int i].get", 1).Method("void GlobalThing.this[int i].set", 1)));
        Assert.AreSequenceEqual([("get_Item", CodeMetricsMemberKind.Accessor), ("set_Item", CodeMetricsMemberKind.Accessor), ("Item", CodeMetricsMemberKind.Property), ], members.Select(m => (m.MemberName, m.Kind)), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DataRow("int A.this[int i].get", 1)]
    [DataRow("void A.this[int i].set", 2)]
    [DataRow("void A.this[int i].init", 2)]
    [DataRow("int A.this[int i, int j].get", 2)]
    [DataRow("void A.this[Dictionary<string, int> map, (int, int) pair, int[,] grid].set", 4)]
    [DataRow("int A.this[int i]invalid.get", null)]
    public void IndexerAccessorArity_IncludesIndicesAndSetterValue(string display, int? expected)
    {
        var member = One(ParseMethod(display, type: "A"));
        Assert.AreEqual(CodeMetricsMemberKind.Accessor, member.Kind);
        Assert.AreEqual(expected, member.Arity);
    }

    [TestMethod]
    public void ArrayReturnOperator_BracketsBeforeTheOperatorMarkerStayBalanced()
    {
        var m = One(ParseMethod("int[] GlobalThing.operator +(GlobalThing a, int[,] b)"));
        Assert.AreEqual("op_Addition", m.MemberName);
        Assert.AreEqual(2, m.Arity);
    }

    [TestMethod]
    public void NestedTypeInGlobalNamespace_DottedNameKeepsContainingTypes()
    {
        var members = CodeMetricsReportBuilder.ParseType("<global namespace>", "GlobalThing.Nested<T>.Deeper", m => m.Method("void Deeper.D()", 1));
        Assert.AreEqual("GlobalThing.Nested.Deeper", (One(members)).TypeName);
    }

    [TestMethod]
    public void VeryLongGenericTypeName_StripsLikeAShortOne()
    {
        // Names over 256 characters take the heap buffer instead of stackalloc.
        var args = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"T{i}"));
        var type = $"Wide<{args}>";
        Assert.IsGreaterThan(256, type.Length);
        var members = CodeMetricsReportBuilder.ParseType("MyApp", type, m => m.Method($"void {type}.Run()", 1));
        Assert.AreEqual("MyApp.Wide", (One(members)).TypeName);
    }

    [TestMethod]
    public void UnclosedParameterList_CountsWhatIsThere()
    {
        var m = One(ParseMethod("void GlobalThing.Cut(int a, int b"));
        Assert.AreEqual("Cut", m.MemberName);
        Assert.AreEqual(2, m.Arity);
    }

    [TestMethod]
    public void UnterminatedDefaultLiteral_SwallowsTheRestOfTheList()
    {
        var m = One(ParseMethod("void GlobalThing.Cut(int a, string s = \"x, int b)"));
        Assert.AreEqual("Cut", m.MemberName);
        Assert.AreEqual(2, m.Arity);
    }

    // ── Unusual document shapes ───────────────────────────────────────────────
    [TestMethod]
    public void NamespaceWithoutName_TypeNameHasNoPrefix()
    {
        var members = ParseRaw($"""
            <Namespace><Types><NamedType Name="Calc"><Members>
              <Method Name="void Calc.M()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);
        Assert.AreEqual("Calc", (One(members)).TypeName);
    }

    [TestMethod]
    public void EmptyNamedTypeElement_DoesNotNestTheNextType()
    {
        // An empty element raises no EndElement, so it must not push a type segment.
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types>
              <NamedType Name="Empty" />
              <NamedType Name="Calc"><Members><Method Name="void Calc.M()">{Metric3}</Method></Members></NamedType>
            </Types></Namespace>
            """);
        Assert.AreEqual("MyApp.Calc", (One(members)).TypeName);
    }

    [TestMethod]
    public void NamedTypeWithoutName_MemberStillRecordedUnderAnEmptySegment()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType><Members>
              <Method Name="void Calc.M()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);
        var m = One(members);
        Assert.AreEqual("MyApp.", m.TypeName);
        Assert.AreEqual("M", m.MemberName);
        Assert.AreEqual(3, m.CyclomaticComplexity);
    }

    [TestMethod]
    public void MemberOutsideAnyType_IsIgnored_AndDoesNotDisturbLaterMembers()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp">
              <Method Name="void Stray.M()">{Metric3}</Method>
              <Types><NamedType Name="Calc"><Members>
                <Method Name="void Calc.Kept()"><Metrics><Metric Name="CyclomaticComplexity" Value="7" /></Metrics></Method>
              </Members></NamedType></Types>
            </Namespace>
            """);
        var m = One(members);
        Assert.AreEqual("Kept", m.MemberName);
        Assert.AreEqual(7, m.CyclomaticComplexity);
    }

    [TestMethod]
    public void EmptyMemberElement_RecordsNothing_AndLeavesTheStackBalanced()
    {
        // An empty Property raises no EndElement; pushing it would make the following
        // method's metric land on the property and drop the method.
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Property Name="int Calc.Hollow" />
              <Method Name="void Calc.After()">{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);
        var m = One(members);
        Assert.AreEqual("After", m.MemberName);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
    }

    [TestMethod]
    public void MemberWithoutName_RecordedWithEmptyDisplay()
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Method>{Metric3}</Method>
            </Members></NamedType></Types></Namespace>
            """);
        var m = One(members);
        Assert.AreEqual("", m.DisplayName);
        Assert.AreEqual("", m.MemberName);
        Assert.IsNull(m.Arity);
    }

    [TestMethod]
    [DataRow("n/a")]
    [DataRow("2.5")]
    [DataRow("99999999999")]
    [DataRow("")]
    public void NonIntegerComplexity_IsKeptAsNoMeasurement(string value)
    {
        var members = ParseRaw($"""
            <Namespace Name="MyApp"><Types><NamedType Name="Calc"><Members>
              <Method Name="void Calc.M()"><Metrics><Metric Name="CyclomaticComplexity" Value="{value}" /></Metrics></Method>
            </Members></NamedType></Types></Namespace>
            """);
        var member = Assert.ContainsSingle(members);
        Assert.AreEqual(0, member.CyclomaticComplexity);
    }

    [TestMethod]
    public void Parse_LeavesTheCallersStreamOpen()
    {
        var bytes = CodeMetricsReportBuilder.Create().Namespace("MyApp", t => t.Type("Calc", m => m.Method("void Calc.M()", 2))).ToBytes();
        using var stream = new MemoryStream(bytes);
        var members = CodeMetricsReader.Parse(stream);
        Assert.HasCount(1, members);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    public void DeepTypeScope_WithManyMembers_PreservesNamesWhenReturningToParent()
    {
        const int depth = 256;
        const int count = 1000;
        var xml = new StringBuilder("<Namespace Name=\"App\">");
        for (var i = 0; i < depth; i++)
            xml.Append("<NamedType Name=\"A&lt;T&gt;\">");
        for (var i = 0; i < count; i++)
            xml.Append($"<Method Name=\"void A.M{i}()\">{Metric3}</Method>");
        for (var i = 1; i < depth; i++)
            xml.Append("</NamedType>");
        xml.Append($"<Method Name=\"void A.Parent()\">{Metric3}</Method></NamedType></Namespace>");
        var members = ParseRaw(xml.ToString());
        Assert.HasCount(count + 1, members);
        Assert.AreSequenceEqual(["App." + string.Join(".", Enumerable.Repeat("A", depth))], members.Take(count).Select(m => m.TypeName).Distinct(), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["App." + string.Join(".", Enumerable.Repeat("A`1", depth))], members.Take(count).Select(m => m.TypeIdentity!).Distinct(), SequenceOrder.InAnyOrder);
        Assert.AreEqual("App.A`1", members[^1].TypeIdentity);
        Assert.AreEqual("Parent", members[^1].MemberName);
    }

    [TestMethod]
    public void ExcessiveNamedTypeNesting_IsRejectedBeforeIdentityExpansion()
    {
        var xml = string.Concat(Enumerable.Repeat("<NamedType Name=\"A\">", 257)) + string.Concat(Enumerable.Repeat("</NamedType>", 257));
        var error = Assert.ThrowsExactly<XmlException>(() => ParseRaw(xml));
        Assert.Contains("256 levels", error.Message);
    }
}
