using System.Globalization;

namespace DotCov.Tests.Infrastructure;

/// <summary>
/// Sets <see cref="CultureInfo.CurrentCulture"/> for the calling thread and restores it on
/// dispose, whatever the outcome. CurrentCulture is thread state, so the scope must wrap a
/// synchronous render; it must not span an <c>await</c>.
/// </summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    private CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

    /// <summary>
    /// A comma-decimal culture cloned from the invariant culture instead of a real locale, so
    /// the test also runs under <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>.
    /// </summary>
    public static CultureScope CommaDecimal()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.PercentDecimalSeparator = ",";
        return new CultureScope(culture);
    }

    /// <summary>Render under <see cref="CommaDecimal"/> and restore the culture before returning.</summary>
    public static string RenderWithCommaDecimal(Func<string> render)
    {
        using var _ = CommaDecimal();
        return render();
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;
}
