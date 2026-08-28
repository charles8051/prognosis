using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Prognosis.Tests.Fuzzing;

/// <summary>
/// The property-test driver: generate <c>N</c> topologies, run an assertion over each,
/// and on the first failure shrink the counterexample and report it as a reproducible,
/// human-readable case.
/// <para>
/// <b>Deterministic by default.</b> The seed is a constant, not a clock, so CI does not
/// discover a new bug on an unrelated PR and block it. Override with the
/// <c>PROGNOSIS_FUZZ_SEED</c> environment variable to explore, and
/// <c>PROGNOSIS_FUZZ_CASES</c> to soak:
/// </para>
/// <code>
/// PROGNOSIS_FUZZ_SEED=$RANDOM PROGNOSIS_FUZZ_CASES=20000 dotnet test Prognosis.Tests
/// </code>
/// <para>
/// A failure prints the shrunk spec's <see cref="TopologySpec.ToLiteral"/> encoding;
/// paste it into <c>TopologyFuzzTests.Corpus</c> to pin it as a permanent regression
/// case that runs on every build regardless of seed.
/// </para>
/// </summary>
public static class Fuzz
{
    /// <summary>The default seed. Constant so every run and every machine agrees.</summary>
    public const int DefaultSeed = 20260822;

    /// <summary>Cases per property. Small enough that the whole suite stays fast.</summary>
    public const int DefaultCases = 250;

    public static int Seed { get; } = ReadEnv("PROGNOSIS_FUZZ_SEED", DefaultSeed);

    public static int Cases { get; } = ReadEnv("PROGNOSIS_FUZZ_CASES", DefaultCases);

    /// <summary>
    /// Runs <paramref name="assert"/> over generated topologies.
    /// </summary>
    /// <param name="property">Name of the property, for failure output.</param>
    /// <param name="assert">Throws to reject a topology.</param>
    /// <param name="shapes">Shape pool; defaults to every shape.</param>
    /// <param name="mode">Where non-healthy intrinsic statuses may sit.</param>
    /// <param name="precondition">
    /// Optional filter. A rejected case is skipped, not failed — and the filter is
    /// re-applied during shrinking, so the shrinker cannot escape into a spec the
    /// property never claimed to cover.
    /// </param>
    /// <param name="output">Optional xunit output, for the per-run coverage summary.</param>
    public static void Check(
        string property,
        Action<TopologySpec> assert,
        IReadOnlyList<string>? shapes = null,
        IntrinsicMode mode = IntrinsicMode.Anywhere,
        Func<TopologySpec, bool>? precondition = null,
        ITestOutputHelper? output = null)
    {
        shapes ??= TopologyGenerator.AllShapes;
        precondition ??= _ => true;

        var checkedCount = 0;
        var skipped = 0;
        var cyclic = 0;
        var biggest = 0;

        for (var i = 0; i < Cases; i++)
        {
            // Shape by index and seed by (seed, index): a failing case is fully
            // reproducible from the two numbers printed in the failure message.
            var shape = shapes[i % shapes.Count];
            var spec = TopologyGenerator.Generate(shape, new Random(CaseSeed(Seed, i)), mode);

            if (!precondition(spec))
            {
                skipped++;
                continue;
            }

            checkedCount++;
            if (spec.HasCycle())
                cyclic++;
            biggest = Math.Max(biggest, spec.Count);

            try
            {
                assert(spec);
            }
            catch (Exception failure)
            {
                throw Report(property, i, spec, failure, assert, precondition);
            }
        }

        Assert.True(
            checkedCount > 0,
            $"Property '{property}' checked 0 of {Cases} cases — the precondition rejects "
                + "everything, so this test proves nothing.");

        output?.WriteLine(
            $"{property}: {checkedCount} checked, {skipped} skipped, {cyclic} cyclic, "
                + $"largest {biggest} nodes (seed {Seed}, {shapes.Count} shapes).");
    }

    private static int CaseSeed(int seed, int index) => unchecked(seed * 397 ^ index);

    private static XunitException Report(
        string property,
        int caseIndex,
        TopologySpec original,
        Exception failure,
        Action<TopologySpec> assert,
        Func<TopologySpec, bool> precondition)
    {
        var identity = FailureIdentity(failure);

        // Same-failure only: without this the shrinker cheerfully minimizes its way into a
        // different failure and reports a counterexample for a property that isn't the one
        // that broke.
        bool StillFails(TopologySpec candidate)
        {
            if (!precondition(candidate))
                return false;
            try
            {
                assert(candidate);
                return false;
            }
            catch (Exception e)
            {
                return FailureIdentity(e) == identity;
            }
        }

        var shrunk = TopologyShrinker.Shrink(original, StillFails);

        var message = new StringBuilder()
            .AppendLine($"Property '{property}' failed.")
            .AppendLine()
            .AppendLine($"  seed        {Seed}   (PROGNOSIS_FUZZ_SEED)")
            .AppendLine($"  case        {caseIndex} of {Cases}")
            .AppendLine($"  shape       {original.Shape}")
            .AppendLine($"  generated   {original.Count} nodes, {original.EdgeCount} edges")
            .AppendLine($"  shrunk to   {shrunk.Count} nodes, {shrunk.EdgeCount} edges"
                + $", cyclic: {shrunk.HasCycle()}")
            .AppendLine()
            .AppendLine("  Pin this counterexample in TopologyFuzzTests.Corpus:")
            .AppendLine($"      \"{shrunk.ToLiteral()}\",")
            .AppendLine()
            .AppendLine(Indent(shrunk.ToMermaid()))
            .AppendLine("  Failure on the shrunk case:")
            .AppendLine(Indent(Rerun(shrunk, assert) ?? failure.ToString()))
            .ToString();

        return new XunitException(message);
    }

    // Node names, statuses, and counts vary between instances of the SAME assertion, so
    // they are masked out; what survives is the assertion's message template. Only used
    // when the call site is unavailable.
    private static readonly Regex CaseDetail = new(
        @"\bn\d+\b|\b(?:Healthy|Unknown|Degraded|Unhealthy)\b|\d+", RegexOptions.Compiled);

    /// <summary>
    /// A stable identity for a failure, used to keep shrinking on the <em>same</em>
    /// assertion. The exception type paired with the <b>call site</b> that threw — the
    /// first stack frame inside this assembly, identified by method and IL offset.
    /// <para>
    /// Coarser identities do not hold up. The type alone cannot separate two
    /// <see cref="Assert.True(bool, string)"/> calls in one property. The message template
    /// is better but still merges assertions that share a template — two structurally
    /// identical <c>Assert.Equal</c> collection comparisons, or two bare <c>Assert.True</c>
    /// calls with no user message, are indistinguishable by message. A call site is
    /// distinct per assertion by construction, and identical across every case that trips
    /// the same one, which is exactly the equivalence the shrinker needs: without it a
    /// candidate that stops reproducing the original failure but trips a different
    /// assertion is accepted, and the reported counterexample belongs to a defect nobody
    /// found.
    /// </para>
    /// <para>
    /// IL offset is used rather than a line number so this does not depend on symbols
    /// being present. If no frame resolves — an exception with no stack, or offsets
    /// erased by inlining — it falls back to the message template, which is still finer
    /// than the type alone.
    /// </para>
    /// </summary>
    private static string FailureIdentity(Exception failure)
    {
        var kind = failure.GetType().FullName;

        foreach (var frame in new StackTrace(failure, fNeedFileInfo: false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType?.Assembly != typeof(Fuzz).Assembly)
                continue; // still inside xunit's assertion plumbing

            var offset = frame.GetILOffset();
            if (offset == StackFrame.OFFSET_UNKNOWN)
                break;

            return $"{kind}|{method.DeclaringType.FullName}.{method.Name}+IL{offset}";
        }

        return $"{kind}|{CaseDetail.Replace(failure.Message ?? string.Empty, "*")}";
    }

    private static string? Rerun(TopologySpec spec, Action<TopologySpec> assert)
    {
        try
        {
            assert(spec);
            return null;
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    private static string Indent(string text) =>
        string.Join(
            Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => "      " + line));

    private static int ReadEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
