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
    public void EqualityOperator_NormalizesToOpEquality()
    {
        var members = Parse(Method("bool Calculator.operator ==(Calculator a, Calculator b)", 1));

        Assert.Equal("op_Equality", Assert.Single(members).MemberName);
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
