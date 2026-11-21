namespace OpenMoca.Reader;

/// <summary>
/// Whether a package is closed, and hidden, for one terms request (Reader contract §15.4, "Closed and hidden";
/// moca-spec ADR-0041).
/// </summary>
/// <param name="Closed">
/// The package's package-level resolved terms fail the gate's <c>ai</c> test for the request's destination and
/// conditions. Its records are removed, its structure is left out, and a model may be told only its catalogue entry.
/// </param>
/// <param name="ClosedBy">
/// The gate's reasons, as field names: <c>ai.allowed.device</c>, <c>ai.allowed.host</c>, <c>ai.allowed.provider</c> or
/// <c>ai.providerRequires</c>. Empty when the package is open.
/// </param>
/// <param name="Hidden">
/// Closed, and its resolved <c>ai.allowed.mention</c> is false: nothing handed to a model may name it.
/// </param>
public sealed record PackageClosure(bool Closed, IReadOnlyList<string> ClosedBy, bool Hidden)
{
    /// <summary>A package that is open for the request (never hidden).</summary>
    public static PackageClosure Open { get; } = new(false, [], false);
}
