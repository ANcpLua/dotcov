using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Verifies the env-var precedence cascade. Lives in <see cref="EnvCollection"/> to
/// serialize against other env-touching tests — Environment.SetEnvironmentVariable
/// mutates the process table.
/// </summary>
[Collection(nameof(EnvCollection))]
public sealed class AnsiTests
{
    private static readonly string[] AllVars =
    [
        "NO_COLOR", "FORCE_COLOR", "CLICOLOR_FORCE", "CLICOLOR", "TERM",
        "GITHUB_ACTIONS", "GITLAB_CI", "CIRCLECI", "BUILDKITE", "TF_BUILD",
        "DRONE", "APPVEYOR", "TRAVIS", "WOODPECKER", "FORGEJO_ACTIONS", "GITEA_ACTIONS"
    ];

    [Fact]
    public void NoColor_OverridesEverything_ReturnsFalse()
    {
        using var _ = new EnvScope([
            ("NO_COLOR", "1"),
            ("FORCE_COLOR", "1"),
            ("GITHUB_ACTIONS", "true")
        ]);

        Assert.False(Ansi.IsSupported(isOutputRedirected: false));
        Assert.False(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Fact]
    public void NoColor_EmptyString_DoesNotDisableBecauseEnvIsAbsent()
    {
        // no-color.org says any non-empty value disables — empty string means "unset" in our cascade.
        // .NET treats SetEnvironmentVariable("", "") as unsetting on some platforms; verify the
        // documented behavior holds by clearing instead.
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", "1")]);

        Assert.True(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void ForceColor_TruthyValues_ReturnsTrue(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", value)]);

        Assert.True(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ForceColor_FalsyValues_DoesNotForce(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", value)]);

        Assert.False(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Fact]
    public void CliColorForce_TruthyValue_ReturnsTrue()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("CLICOLOR_FORCE", "1")]);

        Assert.True(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Fact]
    public void TermDumb_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var term = new EnvScope([("TERM", "dumb")]);

        Assert.False(Ansi.IsSupported(isOutputRedirected: false));
    }

    [Fact]
    public void CliColorZero_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var clicolor = new EnvScope([("CLICOLOR", "0")]);

        Assert.False(Ansi.IsSupported(isOutputRedirected: false));
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("GITLAB_CI")]
    [InlineData("CIRCLECI")]
    [InlineData("BUILDKITE")]
    [InlineData("TF_BUILD")]
    [InlineData("DRONE")]
    [InlineData("APPVEYOR")]
    [InlineData("TRAVIS")]
    [InlineData("WOODPECKER")]
    [InlineData("FORGEJO_ACTIONS")]
    [InlineData("GITEA_ACTIONS")]
    public void KnownCi_EnablesColorEvenWhenOutputRedirected(string ciVar)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var ci = new EnvScope([(ciVar, "true")]);

        Assert.True(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Fact]
    public void NoEnvSignals_FallsBackToTty()
    {
        using var _ = EnvScope.Clear(AllVars);

        Assert.True(Ansi.IsSupported(isOutputRedirected: false));
        Assert.False(Ansi.IsSupported(isOutputRedirected: true));
    }

    [Fact]
    public void EnableOnWindows_DoesNotThrow_OnAnyPlatform()
    {
        // We don't assert side-effects: on non-Windows it's a no-op, on Windows it may or may not
        // succeed depending on whether a console handle is attached. The contract is "never throws".
        var ex = Record.Exception(Ansi.EnableOnWindows);
        Assert.Null(ex);
    }
}
