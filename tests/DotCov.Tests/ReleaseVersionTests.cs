using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCov.Tests;
/// <summary>
/// One release version across the places a release sets it: the packages' VersionPrefix, the
/// DotCov.Tool version the badge action installs, the README's action snippet, and its link to
/// the release notes. A badge action left on the previous version would silently install the
/// previous dotcov from the new tag.
/// </summary>
[TestClass]
public sealed partial class ReleaseVersionTests
{
    private static readonly string Root = FindRoot(AppContext.BaseDirectory);
    private static string VersionPrefix => XDocument.Load(Path.Combine(Root, "Directory.Build.props")).Descendants("VersionPrefix").Single().Value;

    [TestMethod]
    public async Task BadgeAction_InstallsTheReleaseVersion()
    {
        var action = await File.ReadAllTextAsync(Path.Combine(Root, ".github", "actions", "badge", "action.yml"));
        Assert.AreEqual(VersionPrefix, ActionVersionDefault().Match(action).Groups[1].Value);
    }

    [TestMethod]
    public async Task Readme_PointsAtTheReleaseVersion()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(Root, "README.md"));
        // Every match names the release, and there is at least one of each.
        Assert.AreEqual(VersionPrefix, Versions(ActionReference(), readme));
        Assert.AreEqual(VersionPrefix, Versions(ReleaseNotesLink(), readme));
        Assert.IsTrue(File.Exists(Path.Combine(Root, "docs", "releases", $"{VersionPrefix}.md")));
    }

    private static string Versions(Regex pattern, string text) => string.Join(", ", pattern.Matches(text).Select(static m => m.Groups[1].Value).Distinct());
    private static string FindRoot(string directory) => File.Exists(Path.Combine(directory, "DotCov.slnx")) ? directory : FindRoot(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("No DotCov.slnx above the test output."));
    /// <summary>The <c>default</c> of the <c>version</c> input, whatever the line endings.</summary>
    [GeneratedRegex(@"\n  version:\r?\n(?:    (?!default:).*\r?\n)*    default: '([^']*)'")]
    private static partial Regex ActionVersionDefault();
    [GeneratedRegex(@"ANcpLua/dotcov/\.github/actions/badge@v([0-9.]+)")]
    private static partial Regex ActionReference();
    [GeneratedRegex(@"\[Release notes\]\(docs/releases/([0-9.]+)\.md\)")]
    private static partial Regex ReleaseNotesLink();
}
