namespace OpenMoca.Reader;

/// <summary>
/// Profiles this Reader recognises. Recognising a profile only stops F001 being reported; profile data
/// stays opaque to core (package spec §9).
/// </summary>
public static class Profiles
{
    /// <summary>The ontology profile.</summary>
    public const string Ontology = "https://w3id.org/moca/profiles/ontology/v1";

    /// <summary>Every profile recognised by default.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>([Ontology], StringComparer.Ordinal);
}
