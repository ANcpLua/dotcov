using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[NotInParallel(ProcessState.Environment)]
public sealed class AnsiTests
{
    private static readonly string[] AllVars =
    [
        "NO_COLOR", "FORCE_COLOR", "CLICOLOR_FORCE", "CLICOLOR", "TERM",
        "GITHUB_ACTIONS", "GITLAB_CI", "CIRCLECI", "BUILDKITE", "TF_BUILD",
        "DRONE", "APPVEYOR", "TRAVIS", "WOODPECKER", "FORGEJO_ACTIONS", "GITEA_ACTIONS"
    ];

    [Test]
    public async Task NoColor_OverridesEverything_ReturnsFalse()
    {
        using var _ = new EnvScope([
            ("NO_COLOR", "1"),
            ("FORCE_COLOR", "1"),
            ("GITHUB_ACTIONS", "true")
        ]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: false)).IsFalse();
        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsFalse();
    }

    [Test]
    public async Task NoColor_EmptyString_DoesNotDisableBecauseEnvIsAbsent()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", "1")]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsTrue();
    }

    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("yes")]
    public async Task ForceColor_TruthyValues_ReturnsTrue(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", value)]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsTrue();
    }

    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("False")]
    [Arguments("FALSE")]
    [Arguments("FaLsE")]
    public async Task ForceColor_FalsyValues_DoesNotForce(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("FORCE_COLOR", value)]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsFalse();
    }

    [Test]
    public async Task CliColorForce_TruthyValue_ReturnsTrue()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope([("CLICOLOR_FORCE", "1")]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsTrue();
    }

    [Test]
    public async Task TermDumb_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var term = new EnvScope([("TERM", "dumb")]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: false)).IsFalse();
    }

    [Test]
    public async Task CliColorZero_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var clicolor = new EnvScope([("CLICOLOR", "0")]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: false)).IsFalse();
    }

    [Test]
    [Arguments("GITHUB_ACTIONS")]
    [Arguments("GITLAB_CI")]
    [Arguments("CIRCLECI")]
    [Arguments("BUILDKITE")]
    [Arguments("TF_BUILD")]
    [Arguments("DRONE")]
    [Arguments("APPVEYOR")]
    [Arguments("TRAVIS")]
    [Arguments("WOODPECKER")]
    [Arguments("FORGEJO_ACTIONS")]
    [Arguments("GITEA_ACTIONS")]
    public async Task KnownCi_EnablesColorEvenWhenOutputRedirected(string ciVar)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var ci = new EnvScope([(ciVar, "true")]);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsTrue();
    }

    [Test]
    public async Task NoEnvSignals_FallsBackToTty()
    {
        using var _ = EnvScope.Clear(AllVars);

        await Assert.That(Ansi.IsSupported(isOutputRedirected: false)).IsTrue();
        await Assert.That(Ansi.IsSupported(isOutputRedirected: true)).IsFalse();
    }

    [Test]
    public async Task IsSupported_NoArgOverload_UsesActualConsoleRedirectionState()
    {
        using var _ = EnvScope.Clear(AllVars);

        // Expected value derived from the console state directly, not by re-invoking the
        // two-arg overload — a no-arg overload that hardcoded either redirection state
        // would fail this on the runner (where stdout is redirected under `dotnet test`).
        await Assert.That(Ansi.IsSupported()).IsEqualTo(!Console.IsOutputRedirected);
    }

    [Test]
    public async Task EnableOnWindows_DoesNotThrow_OnAnyPlatform()
    {
        await Assert.That(Ansi.EnableOnWindows).ThrowsNothing();
    }
}