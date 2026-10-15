using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Pins <see cref = "CodeMetricsReader"/> against the Microsoft.CodeAnalysis.Metrics XML shape
/// (verified upstream: dotnet/roslyn <c>src/RoslynAnalyzers/Tools/Metrics/MetricsOutputWriter.cs</c>
/// plus real generated reports): MinimallyQualifiedFormat member names, per-node Metric
/// elements, Accessors nesting, and the display-string → coverage-name normalization.
/// </summary>
[TestClass]
public sealed class CodeMetricsReaderTests
{
    private static IReadOnlyList<CodeMetricsMember> Parse(string membersXml, string ns = "MyApp", string type = "Calculator")
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CodeMetricsReport Version="1.0">
              <Targets>
                <Target Name="MyApp.csproj">
                  <Assembly Name="MyApp, Version=1.0.0.0">
                    <Metrics>
                      <Metric Name="MaintainabilityIndex" Value="90" />
                      <Metric Name="CyclomaticComplexity" Value="999" />
                    </Metrics>
                    <Namespaces>
                      <Namespace Name="{ns}">
                        <Metrics>
                          <Metric Name="CyclomaticComplexity" Value="888" />
                        </Metrics>
                        <Types>
                          <NamedType Name="{type}">
                            <Metrics>
                              <Metric Name="CyclomaticComplexity" Value="777" />
                            </Metrics>
                            <Members>
            {membersXml}
                            </Members>
                          </NamedType>
                        </Types>
                      </Namespace>
                    </Namespaces>
                  </Assembly>
                </Target>
              </Targets>
            </CodeMetricsReport>
            """;
        return CodeMetricsReader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    }

    private static string Method(string name, int complexity) => $"""
        <Method Name="{name}">
          <Metrics>
            <Metric Name="MaintainabilityIndex" Value="80" />
            <Metric Name="CyclomaticComplexity" Value="{complexity}" />
          </Metrics>
        </Method>
        """;
    [TestMethod]
    public void Method_WithReturnTypeAndParameters_NormalizesNameAndArity()
    {
        var members = Parse(Method("int Calculator.Add(int a, int b)", 2));
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual("MyApp.Calculator", m.TypeName);
        Assert.AreEqual("Add", m.MemberName);
        Assert.AreEqual(2, m.Arity);
        Assert.AreEqual(2, m.CyclomaticComplexity);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
        Assert.AreEqual("int Calculator.Add(int a, int b)", m.DisplayName);
    }

    [TestMethod]
    public void TopLevelStatements_EntryPointDisplay_NormalizesToMain()
    {
        // Roslyn names the synthesized top-level-statements entry point `<Main>$`; it must
        // normalize to `Main` — the identity MethodIdentity gives the coverage side — instead
        // of being generic-stripped down to a bare `$`.
        var members = Parse(Method("void Program.&lt;Main&gt;$(string[] args)", 4), type: "Program");
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual("Main", m.MemberName);
        Assert.AreEqual(1, m.Arity);
        Assert.AreEqual(CodeMetricsMemberKind.Method, m.Kind);
    }

    [TestMethod]
    public void TypeAndNamespaceMetrics_NeverLeakIntoMembers()
    {
        // Assembly (999), namespace (888), and type (777) CyclomaticComplexity nodes surround
        // the member — only the member's own value may be recorded.
        var members = Parse(Method("void Calculator.M()", 3));
        Assert.AreEqual(3, members.Single().CyclomaticComplexity);
    }

    [TestMethod]
    public void GenericReturnType_WithCommaInsideAngles_DoesNotInflateArity()
    {
        var members = Parse(Method("Dictionary&lt;string, int&gt; Calculator.Load(string key)", 1));
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual("Load", m.MemberName);
        Assert.AreEqual(1, m.Arity);
    }

    [TestMethod]
    public void GenericParameter_WithComma_CountsAsOneParameter()
    {
        var members = Parse(Method("void Calculator.Store(Dictionary&lt;string, int&gt; map)", 1));
        Assert.AreEqual(1, members.Single().Arity);
    }

    [TestMethod]
    public void ParameterlessMethod_ArityZero()
    {
        var members = Parse(Method("void Calculator.Reset()", 1));
        Assert.AreEqual(0, members.Single().Arity);
    }

    [TestMethod]
    public void Constructor_NoReturnType_NormalizesToDotCtor()
    {
        // Constructors display without a return type: `Calculator.Calculator(int seed)`.
        var members = Parse(Method("Calculator.Calculator(int seed)", 1));
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual(".ctor", m.MemberName);
        Assert.AreEqual(1, m.Arity);
    }

    [TestMethod]
    public void GenericMethod_TypeArgumentsStripped()
    {
        var members = Parse(Method("T Calculator.Identity&lt;T&gt;(T value)", 1));
        Assert.AreEqual("Identity", members.Single().MemberName);
    }

    [TestMethod]
    public void GenericNamedType_ParametersStrippedFromTypeName()
    {
        var members = Parse(Method("void Stack&lt;T&gt;.Push(T item)", 1), type: "Stack&lt;T&gt;");
        Assert.AreEqual("MyApp.Stack", members.Single().TypeName);
    }

    [TestMethod]
    public void NestedType_DottedNamedTypeName_KeptDotted()
    {
        // The writer prepends containing types with dots: <NamedType Name="Outer.Inner">.
        var members = Parse(Method("void Inner.M()", 1), type: "Outer.Inner");
        Assert.AreEqual("MyApp.Outer.Inner", members.Single().TypeName);
    }

    [TestMethod]
    public void PropertyWithAccessors_EmitsAggregateAndAccessorEntries()
    {
        // Real shape (verified): Property Metrics first, then <Accessors> with Method children
        // whose display ends `.get` / `.set`.
        var members = Parse("""
            <Property Name="int Calculator.Value">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
              <Accessors>
                <Method Name="int Calculator.Value.get">
                  <Metrics>
                    <Metric Name="CyclomaticComplexity" Value="1" />
                  </Metrics>
                </Method>
                <Method Name="void Calculator.Value.set">
                  <Metrics>
                    <Metric Name="CyclomaticComplexity" Value="1" />
                  </Metrics>
                </Method>
              </Accessors>
            </Property>
            """);
        Assert.AreEqual(3, members.Count);
        var getter = members.Single(m => m.MemberName == "get_Value");
        Assert.AreEqual(CodeMetricsMemberKind.Accessor, getter.Kind);
        Assert.AreEqual(1, getter.CyclomaticComplexity); // the accessor's own metric, not the aggregate
        var setter = members.Single(m => m.MemberName == "set_Value");
        Assert.AreEqual(1, setter.CyclomaticComplexity);
        var aggregate = members.Single(m => m.Kind == CodeMetricsMemberKind.Property);
        Assert.AreEqual("Value", aggregate.MemberName);
        Assert.AreEqual(2, aggregate.CyclomaticComplexity);
    }

    [TestMethod]
    public void Indexer_ThisDisplay_NormalizesToItem()
    {
        var members = Parse("""
            <Property Name="int Calculator.this[int index]">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
            </Property>
            """);
        Assert.AreEqual("Item", members.Single().MemberName);
    }

    [TestMethod]
    public void Field_NormalizesToFieldName()
    {
        var members = Parse("""
            <Field Name="int Calculator.seed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Field>
            """);
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual("seed", m.MemberName);
        Assert.AreEqual(CodeMetricsMemberKind.Field, m.Kind);
        Assert.IsNull(m.Arity);
    }

    [TestMethod]
    public void Event_NormalizesToEventName()
    {
        var members = Parse("""
            <Event Name="EventHandler Calculator.Changed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Event>
            """);
        var m = Assert.ContainsSingle(members);
        Assert.AreEqual("Changed", m.MemberName);
        Assert.AreEqual(CodeMetricsMemberKind.Event, m.Kind);
    }

    [TestMethod]
    public void InitAccessor_NormalizesToSetPrefix()
    {
        // init-only setters compile to set_ accessors — the identity coverage reports carry.
        var members = Parse("""
            <Property Name="int Calculator.Value">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
              <Accessors>
                <Method Name="void Calculator.Value.init">
                  <Metrics>
                    <Metric Name="CyclomaticComplexity" Value="1" />
                  </Metrics>
                </Method>
              </Accessors>
            </Property>
            """);
        var init = members.Single(m => m.Kind == CodeMetricsMemberKind.Accessor);
        Assert.AreEqual("set_Value", init.MemberName);
    }

    [TestMethod]
    public void EventAccessors_NormalizeToAddAndRemovePrefixes()
    {
        var members = Parse("""
            <Event Name="EventHandler Calculator.Changed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
              <Accessors>
                <Method Name="void Calculator.Changed.add">
                  <Metrics>
                    <Metric Name="CyclomaticComplexity" Value="1" />
                  </Metrics>
                </Method>
                <Method Name="void Calculator.Changed.remove">
                  <Metrics>
                    <Metric Name="CyclomaticComplexity" Value="1" />
                  </Metrics>
                </Method>
              </Accessors>
            </Event>
            """);
        Assert.Contains(m => m is { MemberName: "add_Changed", Kind: CodeMetricsMemberKind.Accessor }, members);
        Assert.Contains(m => m is { MemberName: "remove_Changed", Kind: CodeMetricsMemberKind.Accessor }, members);
    }

    [TestMethod]
    public void EqualityOperator_NormalizesToOpEquality()
    {
        var members = Parse(Method("bool Calculator.operator ==(Calculator a, Calculator b)", 1));
        Assert.AreEqual("op_Equality", members.Single().MemberName);
    }

    /// <summary>
    /// The full operator-token table, exercised through real MinimallyQualifiedFormat display
    /// strings (XML-escaped where the token contains angle brackets). Comparison and shift
    /// tokens carry unbalanced <c>&lt;</c>/<c>&gt;</c> that must not derail the depth-tracked
    /// parameter split before they reach the table.
    /// </summary>
    [TestMethod]
    [DataRow("Calculator Calculator.operator +(Calculator a)", "op_UnaryPlus")]
    [DataRow("Calculator Calculator.operator +(Calculator a, Calculator b)", "op_Addition")]
    [DataRow("Calculator Calculator.operator *(Calculator a, Calculator b)", "op_Multiply")]
    [DataRow("Calculator Calculator.operator /(Calculator a, Calculator b)", "op_Division")]
    [DataRow("Calculator Calculator.operator %(Calculator a, Calculator b)", "op_Modulus")]
    [DataRow("bool Calculator.operator !(Calculator a)", "op_LogicalNot")]
    [DataRow("Calculator Calculator.operator ~(Calculator a)", "op_OnesComplement")]
    [DataRow("Calculator Calculator.operator ++(Calculator a)", "op_Increment")]
    [DataRow("Calculator Calculator.operator --(Calculator a)", "op_Decrement")]
    [DataRow("bool Calculator.operator true(Calculator a)", "op_True")]
    [DataRow("bool Calculator.operator false(Calculator a)", "op_False")]
    [DataRow("Calculator Calculator.operator &amp;(Calculator a, Calculator b)", "op_BitwiseAnd")]
    [DataRow("Calculator Calculator.operator |(Calculator a, Calculator b)", "op_BitwiseOr")]
    [DataRow("Calculator Calculator.operator ^(Calculator a, Calculator b)", "op_ExclusiveOr")]
    [DataRow("Calculator Calculator.operator &lt;&lt;(Calculator a, int shift)", "op_LeftShift")]
    [DataRow("Calculator Calculator.operator &gt;&gt;(Calculator a, int shift)", "op_RightShift")]
    [DataRow("Calculator Calculator.operator &gt;&gt;&gt;(Calculator a, int shift)", "op_UnsignedRightShift")]
    [DataRow("bool Calculator.operator !=(Calculator a, Calculator b)", "op_Inequality")]
    [DataRow("bool Calculator.operator &lt;(Calculator a, Calculator b)", "op_LessThan")]
    [DataRow("bool Calculator.operator &gt;(Calculator a, Calculator b)", "op_GreaterThan")]
    [DataRow("bool Calculator.operator &lt;=(Calculator a, Calculator b)", "op_LessThanOrEqual")]
    [DataRow("bool Calculator.operator &gt;=(Calculator a, Calculator b)", "op_GreaterThanOrEqual")]
    public void OperatorTokens_NormalizeToClsOpNames(string display, string expected)
    {
        var members = Parse(Method(display, 1));
        Assert.AreEqual(expected, members.Single().MemberName);
    }

    [TestMethod]
    public void CheckedOperator_NormalizesToCheckedClsName()
    {
        // `operator checked -` compiles to op_CheckedSubtraction (C# 11 checked operators);
        // the raw spelling could never match the coverage method.
        var members = Parse(Method("Calculator Calculator.operator checked -(Calculator a, Calculator b)", 1));
        Assert.AreEqual("op_CheckedSubtraction", members.Single().MemberName);
    }

    [TestMethod]
    public void UnaryVersusBinaryMinus_DisambiguatedByArity()
    {
        var members = Parse(Method("Calculator Calculator.operator -(Calculator a)", 1) + Method("Calculator Calculator.operator -(Calculator a, Calculator b)", 1));
        Assert.Contains(m => m.MemberName == "op_UnaryNegation", members);
        Assert.Contains(m => m.MemberName == "op_Subtraction", members);
    }

    [TestMethod]
    public void ImplicitConversion_NormalizesToOpImplicit()
    {
        var members = Parse(Method("Calculator.implicit operator int(Calculator c)", 1));
        Assert.AreEqual("op_Implicit", members.Single().MemberName);
    }

    [TestMethod]
    public void ExplicitConversion_NormalizesToOpExplicit()
    {
        var members = Parse(Method("Calculator.explicit operator int(Calculator c)", 1));
        Assert.AreEqual("op_Explicit", members.Single().MemberName);
    }

    [TestMethod]
    public void MemberWithoutComplexityMetric_IsKeptAsNoMeasurement()
    {
        var members = Parse("""
            <Method Name="void Calculator.M()">
              <Metrics>
                <Metric Name="MaintainabilityIndex" Value="100" />
              </Metrics>
            </Method>
            """);
        var member = Assert.ContainsSingle(members);
        Assert.AreEqual(0, member.CyclomaticComplexity);
    }

    [TestMethod]
    public async Task ParseFile_MalformedXml_RethrowsWithPathPrefixed()
    {
        using var temp = TempWorkspace.Create("dotcov-metrics-");
        var path = temp.PrepareFile("bad.xml");
        await File.WriteAllTextAsync(path, "<CodeMetricsReport><unclosed>");
        var ex = Assert.ThrowsExactly<XmlException>(() => CodeMetricsReader.ParseFile(path));
        Assert.StartsWith(path, ex.Message);
    }
}
