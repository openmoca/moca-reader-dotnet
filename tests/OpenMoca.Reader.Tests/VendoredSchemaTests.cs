using OpenMoca.Reader.Json;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>The Reader embeds copies of moca-spec's schemas; they must stay byte-identical to the spec's.</summary>
public sealed class VendoredSchemaTests
{
    public static TheoryData<string> Names() =>
    [
        MocaSchemas.Manifest, MocaSchemas.Node, MocaSchemas.CitationRecord, MocaSchemas.SidecarIndex,
        MocaSchemas.ReviewPredicate, MocaSchemas.TrustRoot, MocaSchemas.SearchHit, MocaSchemas.Structure,
    ];

    [Theory]
    [MemberData(nameof(Names))]
    public void EmbeddedSchema_WhenComparedWithSpec_IsIdentical(string name)
    {
        var spec = File.ReadAllText(Path.Combine(MocaSpec.Root, "schemas", "v1", $"{name}.schema.json"));

        Assert.Equal(spec, MocaSchemas.Text(name));
    }
}
