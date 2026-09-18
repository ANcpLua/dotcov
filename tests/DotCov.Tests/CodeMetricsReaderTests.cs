using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Pins <see cref="CodeMetricsReader"/> against the Microsoft.CodeAnalysis.Metrics XML shape
/// (verified upstream: dotnet/roslyn <c>src/RoslynAnalyzers/Tools/Metrics/MetricsOutputWriter.cs</c>
/// plus real generated reports): MinimallyQualifiedFormat member names, per-node Metric
/// elements, Accessors nesting, and the display-string → coverage-name normalization.
/// </summary>
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

    [Test]
    public async Task Method_WithReturnTypeAndParameters_NormalizesNameAndArity()
    {
        var members = Parse(Method("int Calculator.Add(int a, int b)", 2));

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.TypeName).IsEqualTo("MyApp.Calculator");
        await Assert.That(m.MemberName).IsEqualTo("Add");
        await Assert.That(m.Arity).IsEqualTo(2);
        await Assert.That(m.CyclomaticComplexity).IsEqualTo(2);
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
        await Assert.That(m.DisplayName).IsEqualTo("int Calculator.Add(int a, int b)");
    }

    [Test]
    public async Task TopLevelStatements_EntryPointDisplay_NormalizesToMain()
    {
        // Roslyn names the synthesized top-level-statements entry point `<Main>$`; it must
        // normalize to `Main` — the identity MethodIdentity gives the coverage side — instead
        // of being generic-stripped down to a bare `$`.
        var members = Parse(Method("void Program.&lt;Main&gt;$(string[] args)", 4), type: "Program");

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.MemberName).IsEqualTo("Main");
        await Assert.That(m.Arity).IsEqualTo(1);
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Method);
    }

    [Test]
    public async Task TypeAndNamespaceMetrics_NeverLeakIntoMembers()
    {
        // Assembly (999), namespace (888), and type (777) CyclomaticComplexity nodes surround
        // the member — only the member's own value may be recorded.
        var members = Parse(Method("void Calculator.M()", 3));

        await Assert.That(members.Single().CyclomaticComplexity).IsEqualTo(3);
    }

    [Test]
    public async Task GenericReturnType_WithCommaInsideAngles_DoesNotInflateArity()
    {
        var members = Parse(Method("Dictionary&lt;string, int&gt; Calculator.Load(string key)", 1));

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.MemberName).IsEqualTo("Load");
        await Assert.That(m.Arity).IsEqualTo(1);
    }

    [Test]
    public async Task GenericParameter_WithComma_CountsAsOneParameter()
    {
        var members = Parse(Method("void Calculator.Store(Dictionary&lt;string, int&gt; map)", 1));

        await Assert.That(members.Single().Arity).IsEqualTo(1);
    }

    [Test]
    public async Task ParameterlessMethod_ArityZero()
    {
        var members = Parse(Method("void Calculator.Reset()", 1));

        await Assert.That(members.Single().Arity).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_NoReturnType_NormalizesToDotCtor()
    {
        // Constructors display without a return type: `Calculator.Calculator(int seed)`.
        var members = Parse(Method("Calculator.Calculator(int seed)", 1));

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.MemberName).IsEqualTo(".ctor");
        await Assert.That(m.Arity).IsEqualTo(1);
    }

    [Test]
    public async Task GenericMethod_TypeArgumentsStripped()
    {
        var members = Parse(Method("T Calculator.Identity&lt;T&gt;(T value)", 1));

        await Assert.That(members.Single().MemberName).IsEqualTo("Identity");
    }

    [Test]
    public async Task GenericNamedType_ParametersStrippedFromTypeName()
    {
        var members = Parse(Method("void Stack&lt;T&gt;.Push(T item)", 1), type: "Stack&lt;T&gt;");

        await Assert.That(members.Single().TypeName).IsEqualTo("MyApp.Stack");
    }

    [Test]
    public async Task NestedType_DottedNamedTypeName_KeptDotted()
    {
        // The writer prepends containing types with dots: <NamedType Name="Outer.Inner">.
        var members = Parse(Method("void Inner.M()", 1), type: "Outer.Inner");

        await Assert.That(members.Single().TypeName).IsEqualTo("MyApp.Outer.Inner");
    }

    [Test]
    public async Task PropertyWithAccessors_EmitsAggregateAndAccessorEntries()
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

        await Assert.That(members.Count).IsEqualTo(3);

        var getter = members.Single(m => m.MemberName == "get_Value");
        await Assert.That(getter.Kind).IsEqualTo(CodeMetricsMemberKind.Accessor);
        await Assert.That(getter.CyclomaticComplexity).IsEqualTo(1);   // the accessor's own metric, not the aggregate

        var setter = members.Single(m => m.MemberName == "set_Value");
        await Assert.That(setter.CyclomaticComplexity).IsEqualTo(1);

        var aggregate = members.Single(m => m.Kind == CodeMetricsMemberKind.Property);
        await Assert.That(aggregate.MemberName).IsEqualTo("Value");
        await Assert.That(aggregate.CyclomaticComplexity).IsEqualTo(2);
    }

    [Test]
    public async Task Indexer_ThisDisplay_NormalizesToItem()
    {
        var members = Parse("""
            <Property Name="int Calculator.this[int index]">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
            </Property>
            """);

        await Assert.That(members.Single().MemberName).IsEqualTo("Item");
    }

    [Test]
    public async Task Field_NormalizesToFieldName()
    {
        var members = Parse("""
            <Field Name="int Calculator.seed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Field>
            """);

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.MemberName).IsEqualTo("seed");
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Field);
        await Assert.That(m.Arity).IsNull();
    }

    [Test]
    public async Task Event_NormalizesToEventName()
    {
        var members = Parse("""
            <Event Name="EventHandler Calculator.Changed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Event>
            """);

        var m = await Assert.That(members).HasSingleItem();
        await Assert.That(m.MemberName).IsEqualTo("Changed");
        await Assert.That(m.Kind).IsEqualTo(CodeMetricsMemberKind.Event);
    }

    [Test]
    public async Task InitAccessor_NormalizesToSetPrefix()
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
        await Assert.That(init.MemberName).IsEqualTo("set_Value");
    }

    [Test]
    public async Task EventAccessors_NormalizeToAddAndRemovePrefixes()
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

        await Assert.That(members).Contains(m => m is { MemberName: "add_Changed", Kind: CodeMetricsMemberKind.Accessor });
        await Assert.That(members).Contains(m => m is { MemberName: "remove_Changed", Kind: CodeMetricsMemberKind.Accessor });
    }

    [Test]
    public async Task EqualityOperator_NormalizesToOpEquality()
    {
        var members = Parse(Method("bool Calculator.operator ==(Calculator a, Calculator b)", 1));

        await Assert.That(members.Single().MemberName).IsEqualTo("op_Equality");
    }

    /// <summary>
    /// The full operator-token table, exercised through real MinimallyQualifiedFormat display
    /// strings (XML-escaped where the token contains angle brackets). Comparison and shift
    /// tokens are the regression half: their unbalanced <c>&lt;</c>/<c>&gt;</c> once derailed
    /// the depth-tracked parameter split, so they never reached the table.
    /// </summary>
    [Test]
    [Arguments("Calculator Calculator.operator +(Calculator a)", "op_UnaryPlus")]
    [Arguments("Calculator Calculator.operator +(Calculator a, Calculator b)", "op_Addition")]
    [Arguments("Calculator Calculator.operator *(Calculator a, Calculator b)", "op_Multiply")]
    [Arguments("Calculator Calculator.operator /(Calculator a, Calculator b)", "op_Division")]
    [Arguments("Calculator Calculator.operator %(Calculator a, Calculator b)", "op_Modulus")]
    [Arguments("bool Calculator.operator !(Calculator a)", "op_LogicalNot")]
    [Arguments("Calculator Calculator.operator ~(Calculator a)", "op_OnesComplement")]
    [Arguments("Calculator Calculator.operator ++(Calculator a)", "op_Increment")]
    [Arguments("Calculator Calculator.operator --(Calculator a)", "op_Decrement")]
    [Arguments("bool Calculator.operator true(Calculator a)", "op_True")]
    [Arguments("bool Calculator.operator false(Calculator a)", "op_False")]
    [Arguments("Calculator Calculator.operator &amp;(Calculator a, Calculator b)", "op_BitwiseAnd")]
    [Arguments("Calculator Calculator.operator |(Calculator a, Calculator b)", "op_BitwiseOr")]
    [Arguments("Calculator Calculator.operator ^(Calculator a, Calculator b)", "op_ExclusiveOr")]
    [Arguments("Calculator Calculator.operator &lt;&lt;(Calculator a, int shift)", "op_LeftShift")]
    [Arguments("Calculator Calculator.operator &gt;&gt;(Calculator a, int shift)", "op_RightShift")]
    [Arguments("Calculator Calculator.operator &gt;&gt;&gt;(Calculator a, int shift)", "op_UnsignedRightShift")]
    [Arguments("bool Calculator.operator !=(Calculator a, Calculator b)", "op_Inequality")]
    [Arguments("bool Calculator.operator &lt;(Calculator a, Calculator b)", "op_LessThan")]
    [Arguments("bool Calculator.operator &gt;(Calculator a, Calculator b)", "op_GreaterThan")]
    [Arguments("bool Calculator.operator &lt;=(Calculator a, Calculator b)", "op_LessThanOrEqual")]
    [Arguments("bool Calculator.operator &gt;=(Calculator a, Calculator b)", "op_GreaterThanOrEqual")]
    public async Task OperatorTokens_NormalizeToClsOpNames(string display, string expected)
    {
        var members = Parse(Method(display, 1));

        await Assert.That(members.Single().MemberName).IsEqualTo(expected);
    }

    [Test]
    public async Task CheckedOperator_UnknownToken_KeepsRawSpelling()
    {
        // `operator checked -` is not in the CLS table: the raw spelling survives so the member
        // lands in the unmatched list with its display name rather than silently colliding.
        var members = Parse(Method("Calculator Calculator.operator checked -(Calculator a, Calculator b)", 1));

        await Assert.That(members.Single().MemberName).IsEqualTo("operator checked -");
    }

    [Test]
    public async Task UnaryVersusBinaryMinus_DisambiguatedByArity()
    {
        var members = Parse(
            Method("Calculator Calculator.operator -(Calculator a)", 1) +
            Method("Calculator Calculator.operator -(Calculator a, Calculator b)", 1));

        await Assert.That(members).Contains(m => m.MemberName == "op_UnaryNegation");
        await Assert.That(members).Contains(m => m.MemberName == "op_Subtraction");
    }

    [Test]
    public async Task ImplicitConversion_NormalizesToOpImplicit()
    {
        var members = Parse(Method("Calculator.implicit operator int(Calculator c)", 1));

        await Assert.That(members.Single().MemberName).IsEqualTo("op_Implicit");
    }

    [Test]
    public async Task ExplicitConversion_NormalizesToOpExplicit()
    {
        var members = Parse(Method("Calculator.explicit operator int(Calculator c)", 1));

        await Assert.That(members.Single().MemberName).IsEqualTo("op_Explicit");
    }

    [Test]
    public async Task MemberWithoutComplexityMetric_IsSkippedNotZeroed()
    {
        var members = Parse("""
            <Method Name="void Calculator.M()">
              <Metrics>
                <Metric Name="MaintainabilityIndex" Value="100" />
              </Metrics>
            </Method>
            """);

        await Assert.That(members).IsEmpty();
    }

    [Test]
    public async Task ParseFile_MalformedXml_RethrowsWithPathPrefixed()
    {
        using var temp = TempWorkspace.Create("dotcov-metrics-");
        var path = temp.PrepareFile("bad.xml");
        await File.WriteAllTextAsync(path, "<CodeMetricsReport><unclosed>");

        var ex = Assert.ThrowsExactly<XmlException>(() => CodeMetricsReader.ParseFile(path));
        await Assert.That(ex.Message).StartsWith(path);
    }
}
