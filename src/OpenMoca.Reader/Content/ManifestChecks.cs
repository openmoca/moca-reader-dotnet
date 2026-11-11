using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Content;

/// <summary>Manifest checks beyond the JSON Schema (package spec §4).</summary>
internal static partial class ManifestChecks
{
    public static readonly HashSet<string> ManifestKeys =
    [
        "$schema", "mocaVersion", "id", "version", "title", "description", "language", "locales", "license",
        "publisher", "validFrom", "validUntil", "members", "relations", "profiles",
    ];

    // URN namespaces registered with IANA that are commonly used as package ids. Not exhaustive: M007 is informational.
    /// <summary>
    /// Package spec §4.2: the IANA formal URN namespaces, as in moca-spec <c>data/urn-namespaces.json</c> (a test
    /// keeps them equal). Informal namespaces, <c>urn-&lt;number&gt;</c>, are registered too.
    /// </summary>
    internal static readonly HashSet<string> RegisteredUrnNamespaces =
    [
        "3gpp", "3gpp2", "adid", "alert", "bbf", "broadband-forum-org", "c2pa", "cablelabs", "ccsds", "cdx", "cgi",
        "clei", "csa", "cta", "ddi", "dev", "dgiwg", "dnp", "doi", "dslforum-org", "dvb", "ebu", "eic", "eidr", "epc",
        "epcglobal", "etsi", "eurosystem", "example", "fdc", "fipa", "gdr", "gdst", "geant", "globus", "gs1", "gsma",
        "gvat", "hbbtv", "ieee", "ietf", "iptc", "isan", "isbn", "isni", "iso", "issn", "itu", "ivis", "knx", "lei",
        "lex", "liberty", "mace", "mef", "meta", "mpeg", "mrn", "nan", "nato", "nbn", "nena", "newsml", "nfc", "nfi",
        "nzl", "oasis", "ogc", "ogf", "oid", "oipf", "oma", "onem2m", "onf", "pin", "pno", "publicid", "pwid", "reso",
        "s1000d", "said", "schac", "service", "smpte", "stalwart", "swift", "thread", "trivore", "tva", "uci",
        "ucode", "uic", "uuid", "web3d", "wfa", "wmo", "xmlorg", "xmpp",
    ];

    [GeneratedRegex(@"^urn-[0-9]+\z")]
    private static partial Regex InformalUrn();

    [GeneratedRegex(@"^x-[a-z0-9]+-[A-Za-z0-9-]+\z")]
    private static partial Regex VendorKey();

    [GeneratedRegex("^urn:([^:]+):", RegexOptions.IgnoreCase)]
    private static partial Regex Urn();

    public static void Check(JsonNode? manifest, DiagnosticBag diagnostics, IReadOnlySet<string> knownProfiles)
    {
        const string file = PackageDigest.ManifestFile;
        foreach (var error in MocaSchemas.Validate(MocaSchemas.Manifest, manifest))
        {
            diagnostics.Add(DiagnosticCodes.M002ManifestSchema, error, file);
        }

        if (manifest is not JsonObject m)
        {
            return;
        }

        foreach (var (key, _) in m)
        {
            if (!ManifestKeys.Contains(key) && !VendorKey().IsMatch(key))
            {
                diagnostics.Add(DiagnosticCodes.M004UnknownKey,
                    $"\"{key}\" is not a MOCA manifest key; it is ignored. Vendor keys use x-<vendor>-<key>.", file);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in (m["members"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (JsonValues.StringOf(member["id"]) is not { Length: > 0 } id)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                diagnostics.Add(DiagnosticCodes.M005DuplicateMember, $"member {id} is listed more than once", file);
            }
        }

        var from = Dates.Parse(m["validFrom"]);
        var until = Dates.Parse(m["validUntil"]);
        if (from is not null && until is not null && until <= from)
        {
            diagnostics.Add(DiagnosticCodes.M006ValidityWindowInvalid, "validUntil must be later than validFrom", file);
        }

        if (JsonValues.StringOf(m["id"]) is { } packageId && Urn().Match(packageId) is { Success: true } urn
            && !RegisteredUrnNamespaces.Contains(urn.Groups[1].Value.ToLowerInvariant())
            && !InformalUrn().IsMatch(urn.Groups[1].Value.ToLowerInvariant()))
        {
            diagnostics.Add(DiagnosticCodes.M007UnregisteredUrn,
                $"\"urn:{urn.Groups[1].Value}:\" is not a registered URN namespace (RFC 8141); prefer an https: or tag: URI", file);
        }

        if (m["profiles"] is JsonObject profiles)
        {
            foreach (var (uri, _) in profiles)
            {
                if (!knownProfiles.Contains(uri))
                {
                    diagnostics.Add(DiagnosticCodes.F001ProfileUnrecognised,
                        $"profile {uri} is not recognised; its data is preserved but not interpreted", file);
                }
            }
        }
    }
}
