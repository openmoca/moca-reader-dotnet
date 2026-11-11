namespace OpenMoca.Reader.Structure;

/// <summary>The layers structure facts come from, lowest first (moca-spec ADR-0008).</summary>
public static class StructureLayers
{
    /// <summary>The package's own <c>structure.ttl</c>.</summary>
    public const string Package = "package";

    /// <summary>An application overlay supplied by the host.</summary>
    public const string Application = "application";

    /// <summary>An organisation overlay supplied by the host.</summary>
    public const string Organisation = "organisation";

    /// <summary>Layers in precedence order: when two layers state the same fact, the lower one is reported.</summary>
    public static IReadOnlyList<string> Order { get; } = [Package, Application, Organisation];

    /// <summary>The rank of a layer in <see cref="Order"/>.</summary>
    public static int Rank(string layer) => layer switch
    {
        Package => 0,
        Application => 1,
        Organisation => 2,
        _ => 3,
    };
}

/// <summary>
/// One structure fact, normalised to one direction: <c>B skos:narrower A</c> becomes <c>A broader B</c>.
/// </summary>
/// <param name="S">The subject IRI.</param>
/// <param name="Kind">concept, collection, label, definition, broader, related, hasPart, requires, replaces, deprecated or member.</param>
/// <param name="O">The object IRI, for relations and collection members.</param>
/// <param name="Value">A literal value, for labels and definitions.</param>
/// <param name="Lang">The literal's language tag, or an empty string.</param>
/// <param name="Index">The position of a collection member.</param>
/// <param name="Layer">The layer that stated the fact.</param>
/// <param name="Origin">A package digest or an overlay id.</param>
public sealed record StructureFact(
    string S,
    string Kind,
    string? O = null,
    string? Value = null,
    string? Lang = null,
    int Index = 0,
    string Layer = StructureLayers.Package,
    string Origin = "");

/// <summary>A host-supplied overlay: Turtle loaded over the packages' structure (Reader interface §3).</summary>
/// <param name="Id">The overlay's id, used as the file name in diagnostics (<c>overlay:&lt;id&gt;</c>).</param>
/// <param name="Layer"><c>application</c> or <c>organisation</c>.</param>
/// <param name="Turtle">The Turtle text.</param>
public sealed record StructureOverlay(string Id, string Layer, string Turtle);
