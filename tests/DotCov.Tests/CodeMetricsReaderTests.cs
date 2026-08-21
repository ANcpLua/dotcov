using System.Text;
using System.Xml;
using Xunit;

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

    [Fact]
    public void Method_WithReturnTypeAndParameters_NormalizesNameAndArity()
    {
        var members = Parse(Method("int Calculator.Add(int a, int b)", 2));

        var m = Assert.Single(members);
        Assert.Equal("MyApp.Calculator", m.TypeName);
        Assert.Equal("Add", m.MemberName);
        Assert.Equal(2, m.Arity);
        Assert.Equal(2, m.CyclomaticComplexity);
        Assert.Equal(CodeMetricsMemberKind.Method, m.Kind);
        Assert.Equal("int Calculator.Add(int a, int b)", m.DisplayName);
    }

    [Fact]
    public void TopLevelStatements_EntryPointDisplay_NormalizesToMain()
    {
        // Roslyn names the synthesized top-level-statements entry point `<Main>$`; it must
        // normalize to `Main` — the identity MethodIdentity gives the coverage side — instead
        // of being generic-stripped down to a bare `$`.
        var members = Parse(Method("void Program.&lt;Main&gt;$(string[] args)", 4), type: "Program");

        var m = Assert.Single(members);
        Assert.Equal("Main", m.MemberName);
        Assert.Equal(1, m.Arity);
        Assert.Equal(CodeMetricsMemberKind.Method, m.Kind);
    }

    [Fact]
    public void TypeAndNamespaceMetrics_NeverLeakIntoMembers()
    {
        // Assembly (999), namespace (888), and type (777) CyclomaticComplexity nodes surround
        // the member — only the member's own value may be recorded.
        var members = Parse(Method("void Calculator.M()", 3));

        Assert.Equal(3, Assert.Single(members).CyclomaticComplexity);
    }

    [Fact]
    public void GenericReturnType_WithCommaInsideAngles_DoesNotInflateArity()
    {
        var members = Parse(Method("Dictionary&lt;string, int&gt; Calculator.Load(string key)", 1));

        var m = Assert.Single(members);
        Assert.Equal("Load", m.MemberName);
        Assert.Equal(1, m.Arity);
    }

    [Fact]
    public void GenericParameter_WithComma_CountsAsOneParameter()
    {
        var members = Parse(Method("void Calculator.Store(Dictionary&lt;string, int&gt; map)", 1));

        Assert.Equal(1, Assert.Single(members).Arity);
    }

    [Fact]
    public void ParameterlessMethod_ArityZero()
    {
        var members = Parse(Method("void Calculator.Reset()", 1));

        Assert.Equal(0, Assert.Single(members).Arity);
    }

    [Fact]
    public void Constructor_NoReturnType_NormalizesToDotCtor()
    {
        // Constructors display without a return type: `Calculator.Calculator(int seed)`.
        var members = Parse(Method("Calculator.Calculator(int seed)", 1));

        var m = Assert.Single(members);
        Assert.Equal(".ctor", m.MemberName);
        Assert.Equal(1, m.Arity);
    }

    [Fact]
    public void GenericMethod_TypeArgumentsStripped()
    {
        var members = Parse(Method("T Calculator.Identity&lt;T&gt;(T value)", 1));

        Assert.Equal("Identity", Assert.Single(members).MemberName);
    }

    [Fact]
    public void GenericNamedType_ParametersStrippedFromTypeName()
    {
        var members = Parse(Method("void Stack&lt;T&gt;.Push(T item)", 1), type: "Stack&lt;T&gt;");

        Assert.Equal("MyApp.Stack", Assert.Single(members).TypeName);
    }

    [Fact]
    public void NestedType_DottedNamedTypeName_KeptDotted()
    {
        // The writer prepends containing types with dots: <NamedType Name="Outer.Inner">.
        var members = Parse(Method("void Inner.M()", 1), type: "Outer.Inner");

        Assert.Equal("MyApp.Outer.Inner", Assert.Single(members).TypeName);
    }

    [Fact]
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

        Assert.Equal(3, members.Count);

        var getter = members.Single(m => m.MemberName == "get_Value");
        Assert.Equal(CodeMetricsMemberKind.Accessor, getter.Kind);
        Assert.Equal(1, getter.CyclomaticComplexity);   // the accessor's own metric, not the aggregate

        var setter = members.Single(m => m.MemberName == "set_Value");
        Assert.Equal(1, setter.CyclomaticComplexity);

        var aggregate = members.Single(m => m.Kind == CodeMetricsMemberKind.Property);
        Assert.Equal("Value", aggregate.MemberName);
        Assert.Equal(2, aggregate.CyclomaticComplexity);
    }

    [Fact]
    public void Indexer_ThisDisplay_NormalizesToItem()
    {
        var members = Parse("""
            <Property Name="int Calculator.this[int index]">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="2" />
              </Metrics>
            </Property>
            """);

        Assert.Equal("Item", Assert.Single(members).MemberName);
    }

    [Fact]
    public void Field_NormalizesToFieldName()
    {
        var members = Parse("""
            <Field Name="int Calculator.seed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Field>
            """);

        var m = Assert.Single(members);
        Assert.Equal("seed", m.MemberName);
        Assert.Equal(CodeMetricsMemberKind.Field, m.Kind);
        Assert.Null(m.Arity);
    }

    [Fact]
    public void Event_NormalizesToEventName()
    {
        var members = Parse("""
            <Event Name="EventHandler Calculator.Changed">
              <Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics>
            </Event>
            """);

        var m = Assert.Single(members);
        Assert.Equal("Changed", m.MemberName);
        Assert.Equal(CodeMetricsMemberKind.Event, m.Kind);
    }

    [Fact]
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
        Assert.Equal("set_Value", init.MemberName);
    }

    [Fact]
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

        Assert.Contains(members, m => m is { MemberName: "add_Changed", Kind: CodeMetricsMemberKind.Accessor });
        Assert.Contains(members, m => m is { MemberName: "remove_Changed", Kind: CodeMetricsMemberKind.Accessor });
    }

    [Fact]
    public void EqualityOperator_NormalizesToOpEquality()
    {
        var members = Parse(Method("bool Calculator.operator ==(Calculator a, Calculator b)", 1));

        Assert.Equal("op_Equality", Assert.Single(members).MemberName);
    }

    /// <summary>
    /// The full operator-token table, exercised through real MinimallyQualifiedFormat display
    /// strings (XML-escaped where the token contains angle brackets). Comparison and shift
    /// tokens are the regression half: their unbalanced <c>&lt;</c>/<c>&gt;</c> once derailed
    /// the depth-tracked parameter split, so they never reached the table.
    /// </summary>
    [Theory]
    [InlineData("Calculator Calculator.operator +(Calculator a)", "op_UnaryPlus")]
    [InlineData("Calculator Calculator.operator +(Calculator a, Calculator b)", "op_Addition")]
    [InlineData("Calculator Calculator.operator *(Calculator a, Calculator b)", "op_Multiply")]
    [InlineData("Calculator Calculator.operator /(Calculator a, Calculator b)", "op_Division")]
    [InlineData("Calculator Calculator.operator %(Calculator a, Calculator b)", "op_Modulus")]
    [InlineData("bool Calculator.operator !(Calculator a)", "op_LogicalNot")]
    [InlineData("Calculator Calculator.operator ~(Calculator a)", "op_OnesComplement")]
    [InlineData("Calculator Calculator.operator ++(Calculator a)", "op_Increment")]
    [InlineData("Calculator Calculator.operator --(Calculator a)", "op_Decrement")]
    [InlineData("bool Calculator.operator true(Calculator a)", "op_True")]
    [InlineData("bool Calculator.operator false(Calculator a)", "op_False")]
    [InlineData("Calculator Calculator.operator &amp;(Calculator a, Calculator b)", "op_BitwiseAnd")]
    [InlineData("Calculator Calculator.operator |(Calculator a, Calculator b)", "op_BitwiseOr")]
    [InlineData("Calculator Calculator.operator ^(Calculator a, Calculator b)", "op_ExclusiveOr")]
    [InlineData("Calculator Calculator.operator &lt;&lt;(Calculator a, int shift)", "op_LeftShift")]
    [InlineData("Calculator Calculator.operator &gt;&gt;(Calculator a, int shift)", "op_RightShift")]
    [InlineData("Calculator Calculator.operator &gt;&gt;&gt;(Calculator a, int shift)", "op_UnsignedRightShift")]
    [InlineData("bool Calculator.operator !=(Calculator a, Calculator b)", "op_Inequality")]
    [InlineData("bool Calculator.operator &lt;(Calculator a, Calculator b)", "op_LessThan")]
    [InlineData("bool Calculator.operator &gt;(Calculator a, Calculator b)", "op_GreaterThan")]
    [InlineData("bool Calculator.operator &lt;=(Calculator a, Calculator b)", "op_LessThanOrEqual")]
    [InlineData("bool Calculator.operator &gt;=(Calculator a, Calculator b)", "op_GreaterThanOrEqual")]
    public void OperatorTokens_NormalizeToClsOpNames(string display, string expected)
    {
        var members = Parse(Method(display, 1));

        Assert.Equal(expected, Assert.Single(members).MemberName);
    }

    [Fact]
    public void CheckedOperator_UnknownToken_KeepsRawSpelling()
    {
        // `operator checked -` is not in the CLS table: the raw spelling survives so the member
        // lands in the unmatched list with its display name rather than silently colliding.
        var members = Parse(Method("Calculator Calculator.operator checked -(Calculator a, Calculator b)", 1));

        Assert.Equal("operator checked -", Assert.Single(members).MemberName);
    }

    [Fact]
    public void UnaryVersusBinaryMinus_DisambiguatedByArity()
    {
        var members = Parse(
            Method("Calculator Calculator.operator -(Calculator a)", 1) +
            Method("Calculator Calculator.operator -(Calculator a, Calculator b)", 1));

        Assert.Contains(members, m => m.MemberName == "op_UnaryNegation");
        Assert.Contains(members, m => m.MemberName == "op_Subtraction");
    }

    [Fact]
    public void ImplicitConversion_NormalizesToOpImplicit()
    {
        var members = Parse(Method("Calculator.implicit operator int(Calculator c)", 1));

        Assert.Equal("op_Implicit", Assert.Single(members).MemberName);
    }

    [Fact]
    public void ExplicitConversion_NormalizesToOpExplicit()
    {
        var members = Parse(Method("Calculator.explicit operator int(Calculator c)", 1));

        Assert.Equal("op_Explicit", Assert.Single(members).MemberName);
    }

    [Fact]
    public void MemberWithoutComplexityMetric_IsSkippedNotZeroed()
    {
        var members = Parse("""
            <Method Name="void Calculator.M()">
              <Metrics>
                <Metric Name="MaintainabilityIndex" Value="100" />
              </Metrics>
            </Method>
            """);

        Assert.Empty(members);
    }

    [Fact]
    public void ParseFile_MalformedXml_RethrowsWithPathPrefixed()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("dotcov-metrics-").FullName, "bad.xml");
        try
        {
            File.WriteAllText(path, "<CodeMetricsReport><unclosed>");

            var ex = Assert.Throws<XmlException>(() => CodeMetricsReader.ParseFile(path));
            Assert.StartsWith(path, ex.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
