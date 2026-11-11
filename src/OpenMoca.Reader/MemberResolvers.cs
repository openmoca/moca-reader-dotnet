using OpenMoca.Reader.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;

namespace OpenMoca.Reader;

/// <summary>Simple member resolvers. Hosts with a registry supply their own.</summary>
public static class MemberResolvers
{
    /// <summary>
    /// Finds members among the package directories directly inside (or equal to) each search directory,
    /// by <c>id</c> and <c>version</c>. The Reader still checks each against its pinned digest.
    /// </summary>
    public static Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>> Directories(IEnumerable<string> directories)
    {
        var candidates = new List<(string Id, string? Version, string Root)>();
        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var root in Directory.GetDirectories(dir).Order(StringComparer.Ordinal).Prepend(dir))
            {
                var manifestPath = Path.Combine(root, PackageDigest.ManifestFile);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject m && JsonValues.StringOf(m["id"]) is { } id)
                    {
                        candidates.Add((id, JsonValues.StringOf(m["version"]), root));
                    }
                }
                catch (JsonException)
                {
                    // An unreadable manifest is not a candidate.
                }
            }
        }

        return (member, _) =>
        {
            var match = candidates.FirstOrDefault(c => c.Id == member.Id && c.Version == member.Version);
            return ValueTask.FromResult(match.Root is null ? null : PackageTarget.FromPath(match.Root));
        };
    }
}
