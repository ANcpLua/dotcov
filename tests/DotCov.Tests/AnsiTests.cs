using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
[ResourceLock(ProcessState.Environment)]
public sealed class AnsiTests
{
    private static readonly string[] AllVars = ["NO_COLOR", "FORCE_COLOR", "CLICOLOR_FORCE", "CLICOLOR", "TERM", "GITHUB_ACTIONS", "GITLAB_CI", "CIRCLECI", "BUILDKITE", "TF_BUILD", "DRONE", "APPVEYOR", "TRAVIS", "WOODPECKER", "FORGEJO_ACTIONS", "GITEA_ACTIONS"];
    [TestMethod]
    public void NoColor_OverridesEverything_ReturnsFalse()
    {
        using var _ = new EnvScope(("NO_COLOR", "1"), ("FORCE_COLOR", "1"), ("GITHUB_ACTIONS", "true"));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: false));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    public void NoColor_EmptyString_DoesNotDisableBecauseEnvIsAbsent()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope(("FORCE_COLOR", "1"));
        Assert.IsTrue(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("true")]
    [DataRow("yes")]
    public void ForceColor_TruthyValues_ReturnsTrue(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope(("FORCE_COLOR", value));
        Assert.IsTrue(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("false")]
    [DataRow("False")]
    [DataRow("FALSE")]
    [DataRow("FaLsE")]
    public void ForceColor_FalsyValues_DoesNotForce(string value)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope(("FORCE_COLOR", value));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    public void CliColorForce_TruthyValue_ReturnsTrue()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var force = new EnvScope(("CLICOLOR_FORCE", "1"));
        Assert.IsTrue(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    public void TermDumb_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var term = new EnvScope(("TERM", "dumb"));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: false));
    }

    [TestMethod]
    public void CliColorZero_DisablesColor()
    {
        using var _ = EnvScope.Clear(AllVars);
        using var clicolor = new EnvScope(("CLICOLOR", "0"));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: false));
    }

    [TestMethod]
    [DataRow("GITHUB_ACTIONS")]
    [DataRow("GITLAB_CI")]
    [DataRow("CIRCLECI")]
    [DataRow("BUILDKITE")]
    [DataRow("TF_BUILD")]
    [DataRow("DRONE")]
    [DataRow("APPVEYOR")]
    [DataRow("TRAVIS")]
    [DataRow("WOODPECKER")]
    [DataRow("FORGEJO_ACTIONS")]
    [DataRow("GITEA_ACTIONS")]
    public void KnownCi_EnablesColorEvenWhenOutputRedirected(string ciVar)
    {
        using var _ = EnvScope.Clear(AllVars);
        using var ci = new EnvScope((ciVar, "true"));
        Assert.IsTrue(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    public void NoEnvSignals_FallsBackToTty()
    {
        using var _ = EnvScope.Clear(AllVars);
        Assert.IsTrue(Ansi.IsSupported(isOutputRedirected: false));
        Assert.IsFalse(Ansi.IsSupported(isOutputRedirected: true));
    }

    [TestMethod]
    public void IsSupported_NoArgOverload_UsesActualConsoleRedirectionState()
    {
        using var _ = EnvScope.Clear(AllVars);
        // Expected value derived from the console state directly, not by re-invoking the
        // two-arg overload — a no-arg overload that hardcoded either redirection state
        // would fail this on the runner (where stdout is redirected under `dotnet test`).
        Assert.AreEqual(!Console.IsOutputRedirected, Ansi.IsSupported());
    }

    [TestMethod]
    public void EnableOnWindows_DoesNotThrow_OnAnyPlatform()
    {
        Ansi.EnableOnWindows();
    }
}
