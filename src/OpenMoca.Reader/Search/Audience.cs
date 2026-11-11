using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Search;

/// <summary>
/// Audience (package spec §5.3, Reader contract §8, moca-spec ADR-0016). A node's label is its <c>moca.audience</c>, a
/// non-empty string. A translation with no label of its own takes its default representation's. A label that is
/// present but not a non-empty string is malformed and never matches an audience set. Under an audience set, a
/// record with no label is outside it unless the host allows unlabelled content.
/// </summary>
public static class Audience
{
    /// <summary>The label and whether it is malformed, for a node representation.</summary>
    public static (string? Label, bool Invalid) Of(ContentNode node, Representation rep)
    {
        var source = rep.Moca.ContainsKey("audience") ? rep.Moca : node.Representations.Count > 0 ? node.Representations[0].Moca : null;
        if (source is null || !source.ContainsKey("audience"))
        {
            return (null, false);
        }

        return source["audience"] is JsonValue value && value.TryGetValue<string>(out var label) && label.Length > 0
            ? (label, false)
            : (null, true);
    }

    /// <summary>Whether a label passes the host's audience set.</summary>
    public static bool Allowed(string? label, bool invalid, IReadOnlyList<string>? audiences, bool allowUnlabelled)
    {
        if (audiences is null)
        {
            return true;
        }

        if (invalid)
        {
            return false;
        }

        return label is null ? allowUnlabelled : audiences.Contains(label);
    }

    /// <summary>The audience rule for a citation record, for the gate and store backends.</summary>
    public static bool Allowed(CitationRecord record, IReadOnlyList<string>? audiences, bool allowUnlabelled) =>
        Allowed(record.Audience, record.AudienceInvalid == true, audiences, allowUnlabelled);
}
