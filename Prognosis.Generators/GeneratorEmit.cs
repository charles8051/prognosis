using System.Text;

namespace Prognosis.Generators;

/// <summary>
/// Emission helpers shared by the source generators.
/// </summary>
internal static class GeneratorEmit
{
    /// <summary>
    /// Appends <paramref name="text"/> followed by a bare <c>\n</c>. Use instead of
    /// <see cref="StringBuilder.AppendLine()"/>, which appends <c>Environment.NewLine</c> and
    /// makes generated source differ by build host (the CRLF defect).
    /// </summary>
    internal static StringBuilder AppendLineLf(this StringBuilder sb, string text = "")
        => sb.Append(text).Append('\n');
}
