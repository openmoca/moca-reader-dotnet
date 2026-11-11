using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Sigstore;

/// <summary>A Sigstore trusted root (sigstore/protobuf-specs <c>trusted_root.json</c>), parsed.</summary>
internal sealed class TrustMaterial
{
    public required IReadOnlyList<Authority> Authorities { get; init; }

    public required IReadOnlyList<Log> TLogs { get; init; }

    public required IReadOnlyList<Log> CtLogs { get; init; }

    public static TrustMaterial Parse(JsonObject root)
    {
        var authorities = (root["certificateAuthorities"] as JsonArray ?? []).Select(node =>
        {
            var ca = (JsonObject)node!;
            var chain = ((JsonArray)((JsonObject)ca["certChain"]!)["certificates"]!)
                .Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(((JsonObject)c!)["rawBytes"]!.GetValue<string>())))
                .ToList();
            if (chain.Count == 0)
            {
                throw new FormatException("a certificate authority has no certificates");
            }

            var (start, end) = Window(ca["validFor"]);
            return new Authority(chain, start, end);
        }).ToList();
        var tlogs = Logs(root["tlogs"]);
        var ctlogs = Logs(root["ctlogs"]);
        if (authorities.Count == 0 || tlogs.Count == 0)
        {
            throw new FormatException("the trusted root has no certificate authority or no transparency log");
        }

        return new TrustMaterial { Authorities = authorities, TLogs = tlogs, CtLogs = ctlogs };
    }

    private static List<Log> Logs(JsonNode? node) => (node as JsonArray ?? []).Select(n =>
    {
        var log = (JsonObject)n!;
        var key = (JsonObject)log["publicKey"]!;
        var spki = Convert.FromBase64String(key["rawBytes"]!.GetValue<string>());
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
        var keyId = Convert.FromBase64String(((JsonObject)log["logId"]!)["keyId"]!.GetValue<string>());
        var (start, end) = Window(key["validFor"]);
        return new Log(keyId, log["baseUrl"]?.GetValue<string>() ?? "", ecdsa, start, end);
    }).ToList();

    private static (DateTimeOffset Start, DateTimeOffset? End) Window(JsonNode? validFor)
    {
        var start = validFor?["start"]?.GetValue<string>() is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
        DateTimeOffset? end = validFor?["end"]?.GetValue<string>() is { } e ? DateTimeOffset.Parse(e, CultureInfo.InvariantCulture) : null;
        return (start, end);
    }
}

/// <summary>A certificate authority: its chain, leaf-most first, root last.</summary>
internal sealed record Authority(IReadOnlyList<X509Certificate2> Chain, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool ValidAt(DateTimeOffset time) => Start <= time && (End is null || time <= End);
}

/// <summary>A transparency or certificate-transparency log.</summary>
internal sealed record Log(byte[] KeyId, string BaseUrl, ECDsa Key, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool ValidAt(DateTimeOffset time) => Start <= time && (End is null || time <= End);

    public bool Verify(byte[] data, byte[] signature, HashAlgorithmName? hash = null)
    {
        try
        {
            return Key.VerifyData(data, signature, hash ?? (Key.KeySize > 256 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256), DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
