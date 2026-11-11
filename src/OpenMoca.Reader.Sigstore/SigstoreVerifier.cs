using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Attestations;

namespace OpenMoca.Reader.Sigstore;

/// <summary>
/// Verifies Sigstore bundles offline against a trusted root the host supplies (attestations §6, §7). Nothing is
/// fetched. For a bundle to be valid:
/// <list type="number">
/// <item>every transparency-log entry has a signed entry timestamp or an inclusion proof with a signed checkpoint,
/// from a log in the trusted root, and records this bundle's signature and payload;</item>
/// <item>the certificate chains to a certificate authority in the trusted root at the logged time;</item>
/// <item>the certificate carries a signed certificate timestamp from a CT log in the trusted root;</item>
/// <item>the DSSE signature verifies with the certificate's key.</item>
/// </list>
/// The certificate's identity is returned for the Reader to match against the trust root.
/// </summary>
public sealed partial class SigstoreVerifier : ISigstoreVerifier
{
    private const string IssuerV1Oid = "1.3.6.1.4.1.57264.1.1";
    private const string IssuerV2Oid = "1.3.6.1.4.1.57264.1.8";
    private const string SctOid = "1.3.6.1.4.1.11129.2.4.2";
    private const string SubjectAltNameOid = "2.5.29.17";
    private const string PayloadType = "application/vnd.in-toto+json";

    [GeneratedRegex("— (\\S+) (\\S+)\n")]
    private static partial Regex CheckpointSignature();

    /// <inheritdoc />
    public SigstoreVerification Verify(JsonObject bundle, JsonObject trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(trustedRoot);
        TrustMaterial material;
        try
        {
            material = TrustMaterial.Parse(trustedRoot);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
        {
            return new SigstoreVerification("unverifiable", $"sigstore.trustedRoot cannot be used: {ex.Message}");
        }

        try
        {
            return VerifyBundle(bundle, material);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or AsnContentException or InvalidOperationException or ArgumentException or OverflowException or IndexOutOfRangeException or NullReferenceException)
        {
            return SigstoreVerification.Invalid($"not a valid Sigstore bundle: {ex.Message}");
        }
    }

    private static SigstoreVerification VerifyBundle(JsonObject bundle, TrustMaterial material)
    {
        var mediaType = Str(bundle["mediaType"]);
        if (mediaType is null || !mediaType.StartsWith("application/vnd.dev.sigstore.bundle", StringComparison.Ordinal))
        {
            return SigstoreVerification.Invalid("not a Sigstore bundle (mediaType)");
        }

        var vm = bundle["verificationMaterial"] as JsonObject ?? throw new FormatException("no verificationMaterial");
        var leafBytes = Bytes((vm["certificate"] as JsonObject)?["rawBytes"])
            ?? Bytes(((vm["x509CertificateChain"] as JsonObject)?["certificates"] as JsonArray)?[0]?["rawBytes"])
            ?? throw new FormatException("no signing certificate");
        using var leaf = X509CertificateLoader.LoadCertificate(leafBytes);

        var envelope = bundle["dsseEnvelope"] as JsonObject ?? throw new FormatException("no dsseEnvelope");
        var payload = Bytes(envelope["payload"]) ?? throw new FormatException("no payload");
        var payloadType = Str(envelope["payloadType"]) ?? throw new FormatException("no payloadType");
        if (envelope["signatures"] is not JsonArray { Count: 1 } signatures)
        {
            return SigstoreVerification.Invalid("a Sigstore DSSE envelope has exactly one signature");
        }

        var signature = Bytes(signatures[0]?["sig"]) ?? throw new FormatException("no signature");

        // 1. Transparency log: inclusion, and a body that records this signature and payload.
        if (vm["tlogEntries"] is not JsonArray { Count: > 0 } entries)
        {
            return SigstoreVerification.Invalid("no transparency-log entry");
        }

        var timestamps = new List<DateTimeOffset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            var entry = node as JsonObject ?? throw new FormatException("tlog entry is not an object");
            var logId = Bytes((entry["logId"] as JsonObject)?["keyId"]) ?? throw new FormatException("tlog entry has no logId");
            var logIndex = Long(entry["logIndex"]);
            if (!seen.Add($"{Convert.ToHexString(logId)}/{logIndex}"))
            {
                return SigstoreVerification.Invalid("duplicate tlog entry");
            }

            var body = Bytes(entry["canonicalizedBody"]) ?? throw new FormatException("tlog entry has no body");
            var integrated = DateTimeOffset.FromUnixTimeSeconds(Long(entry["integratedTime"]));
            var included = false;
            if (entry["inclusionPromise"] is JsonObject promise)
            {
                if (!VerifyPromise(material, logId, logIndex, integrated, body, Bytes(promise["signedEntryTimestamp"])))
                {
                    return SigstoreVerification.Invalid("inclusion promise could not be verified");
                }

                included = true;
                timestamps.Add(integrated);
            }

            if (entry["inclusionProof"] is JsonObject proof)
            {
                if (VerifyProof(material, proof, body) is { } problem)
                {
                    return SigstoreVerification.Invalid(problem);
                }

                included = true;
            }

            if (!included)
            {
                return SigstoreVerification.Invalid("inclusion could not be verified");
            }

            var kindVersion = entry["kindVersion"] as JsonObject;
            if (VerifyBody(body, Str(kindVersion?["kind"]), Str(kindVersion?["version"]), signature, payload) is { } bodyProblem)
            {
                return SigstoreVerification.Invalid(bodyProblem);
            }
        }

        if (timestamps.Count == 0)
        {
            return SigstoreVerification.Invalid("no verified timestamp: a tlog entry needs a signed entry timestamp");
        }

        // 2. The certificate chains to a trusted CA at each logged time, and 3. carries a verified SCT.
        X509Certificate2? issuer = null;
        foreach (var time in timestamps)
        {
            issuer = ChainToTrustedCa(leaf, material, time);
            if (issuer is null)
            {
                return SigstoreVerification.Invalid("Failed to verify certificate chain");
            }
        }

        if (!VerifySct(leaf, issuer!, material))
        {
            return SigstoreVerification.Invalid("no verified signed certificate timestamp");
        }

        // 4. The DSSE signature, with the certificate's key.
        if (!VerifyDsse(leaf, payloadType, payload, signature))
        {
            return SigstoreVerification.Invalid("signature verification failed");
        }

        var (issuerName, san) = Identity(leaf);
        return issuerName is null || san is null
            ? SigstoreVerification.Invalid("the certificate has no OIDC issuer or subject alternative name")
            : new SigstoreVerification("valid", null, issuerName, san);
    }

    private static bool VerifyPromise(TrustMaterial material, byte[] logId, long logIndex, DateTimeOffset integrated, byte[] body, byte[]? set)
    {
        if (set is null)
        {
            return false;
        }

        // The SET signs RFC 8785 canonical JSON: keys in order, no white space.
        var data = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{{\"body\":\"{Convert.ToBase64String(body)}\",\"integratedTime\":{integrated.ToUnixTimeSeconds()},\"logID\":\"{Convert.ToHexString(logId).ToLowerInvariant()}\",\"logIndex\":{logIndex}}}"));
        return material.TLogs.Where(l => l.KeyId.AsSpan().SequenceEqual(logId) && l.ValidAt(integrated)).Any(l => l.Verify(data, set));
    }

    private static string? VerifyProof(TrustMaterial material, JsonObject proof, byte[] body)
    {
        var checkpoint = Str((proof["checkpoint"] as JsonObject)?["envelope"]);
        if (checkpoint is null)
        {
            return "inclusion proof has no checkpoint";
        }

        var split = checkpoint.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
        {
            return "missing checkpoint separator";
        }

        var note = checkpoint[..(split + 1)];
        var noteBytes = Encoding.UTF8.GetBytes(note);
        var signed = false;
        foreach (Match m in CheckpointSignature().Matches(checkpoint[(split + 2)..]))
        {
            var sig = Convert.FromBase64String(m.Groups[2].Value);
            if (sig.Length < 5)
            {
                return "malformed checkpoint signature";
            }

            signed |= material.TLogs.Any(l => l.KeyId.AsSpan(0, 4).SequenceEqual(sig.AsSpan(0, 4))
                && l.BaseUrl.Contains(m.Groups[1].Value, StringComparison.Ordinal)
                && l.Verify(noteBytes, sig[4..]));
        }

        if (!signed)
        {
            return "invalid checkpoint signature";
        }

        var lines = note.TrimEnd('\n').Split('\n');
        if (lines.Length < 3 || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var treeSize))
        {
            return "malformed checkpoint";
        }

        var root = Convert.FromBase64String(lines[2]);
        var index = Long(proof["logIndex"]);
        if (index < 0 || index >= treeSize)
        {
            return $"invalid index: {index}";
        }

        var hashes = (proof["hashes"] as JsonArray ?? []).Select(h => Bytes(h) ?? throw new FormatException("bad proof hash")).ToList();
        var inner = BitLength(index ^ (treeSize - 1));
        var border = long.PopCount(index >> inner);
        if (hashes.Count != inner + border)
        {
            return "invalid hash count";
        }

        // RFC 9162 §2.1.3.2: recompute the root from the leaf and the proof.
        var hash = SHA256.HashData([0x00, .. body]);
        for (var i = 0; i < inner; i++)
        {
            hash = ((index >> i) & 1) == 0 ? Node(hash, hashes[i]) : Node(hashes[i], hash);
        }

        foreach (var h in hashes.Skip(inner))
        {
            hash = Node(h, hash);
        }

        return hash.AsSpan().SequenceEqual(root) ? null : "calculated root hash does not match the checkpoint";

        static byte[] Node(byte[] left, byte[] right) => SHA256.HashData([0x01, .. left, .. right]);
        static int BitLength(long x) => x == 0 ? 0 : 64 - (int)long.LeadingZeroCount(x);
    }

    private static string? VerifyBody(byte[] body, string? kind, string? version, byte[] signature, byte[] payload)
    {
        var json = JsonNode.Parse(body) as JsonObject ?? throw new FormatException("invalid canonicalized body");
        if (Str(json["kind"]) != kind || Str(json["apiVersion"]) != version)
        {
            return "tlog entry kind or version mismatch";
        }

        if (kind != "dsse" || version != "0.0.1")
        {
            return $"unsupported tlog entry kind: {kind} {version}";
        }

        var spec = json["spec"] as JsonObject;
        if (spec?["signatures"] is not JsonArray { Count: 1 } sigs)
        {
            return "signature count mismatch";
        }

        if (!(Bytes(sigs[0]?["signature"]) ?? []).AsSpan().SequenceEqual(signature))
        {
            return "tlog entry signature mismatch";
        }

        var payloadHash = spec["payloadHash"] as JsonObject;
        return Str(payloadHash?["algorithm"]) == "sha256"
            && string.Equals(Str(payloadHash?["value"]), Convert.ToHexString(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase)
            ? null
            : "DSSE payload hash mismatch";
    }

    private static X509Certificate2? ChainToTrustedCa(X509Certificate2 leaf, TrustMaterial material, DateTimeOffset time)
    {
        foreach (var ca in material.Authorities.Where(a => a.ValidAt(time)))
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = time.UtcDateTime;
            chain.ChainPolicy.VerificationTimeIgnored = false;
            chain.ChainPolicy.CustomTrustStore.Add(ca.Chain[^1]);
            chain.ChainPolicy.ExtraStore.AddRange(ca.Chain.Take(ca.Chain.Count - 1).ToArray());
            if (chain.Build(leaf) && chain.ChainElements.Count >= 2
                && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.Chain[^1].RawData))
            {
                return chain.ChainElements[1].Certificate;
            }
        }

        return null;
    }

    private static bool VerifySct(X509Certificate2 leaf, X509Certificate2 issuer, TrustMaterial material)
    {
        var extension = leaf.Extensions.Cast<X509Extension>().FirstOrDefault(e => e.Oid?.Value == SctOid);
        if (extension is null)
        {
            return false;
        }

        var list = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadOctetString();
        var tbs = TbsWithoutSct(leaf.RawData);
        var issuerKeyHash = SHA256.HashData(issuer.PublicKey.ExportSubjectPublicKeyInfo());
        var position = 2;
        var end = 2 + ((list[0] << 8) | list[1]);
        var verified = 0;
        while (position < end)
        {
            var length = (list[position] << 8) | list[position + 1];
            var sct = list.AsSpan(position + 2, length);
            position += 2 + length;
            var version = sct[0];
            var logId = sct.Slice(1, 32).ToArray();
            var timestamp = sct.Slice(33, 8);
            var extensionsLength = (sct[41] << 8) | sct[42];
            var extensions = sct.Slice(43, extensionsLength);
            var rest = sct[(43 + extensionsLength)..];
            var hashAlgorithm = rest[0];
            var signatureLength = (rest[2] << 8) | rest[3];
            var sctSignature = rest.Slice(4, signatureLength).ToArray();
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(timestamp));

            // RFC 6962 §3.2: the digitally-signed struct over the precertificate entry.
            var data = new List<byte> { version, 0x00 };
            data.AddRange(timestamp.ToArray());
            data.AddRange([0x00, 0x01]);
            data.AddRange(issuerKeyHash);
            data.AddRange([(byte)(tbs.Length >> 16), (byte)(tbs.Length >> 8), (byte)tbs.Length]);
            data.AddRange(tbs);
            data.AddRange([(byte)(extensionsLength >> 8), (byte)extensionsLength]);
            data.AddRange(extensions.ToArray());
            var hash = hashAlgorithm switch { 4 => HashAlgorithmName.SHA256, 5 => HashAlgorithmName.SHA384, _ => default };
            if (hash == default)
            {
                return false;
            }

            if (!material.CtLogs.Where(l => l.KeyId.AsSpan().SequenceEqual(logId) && l.ValidAt(when)).Any(l => l.Verify([.. data], sctSignature, hash)))
            {
                return false;
            }

            verified++;
        }

        return verified > 0;
    }

    /// <summary>The certificate's TBSCertificate, re-encoded without the SCT extension (RFC 6962 §3.2).</summary>
    private static byte[] TbsWithoutSct(byte[] certificate)
    {
        var tbs = new AsnReader(certificate, AsnEncodingRules.DER).ReadSequence().ReadEncodedValue();
        var fields = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
        var writer = new AsnWriter(AsnEncodingRules.DER);
        var extensionsTag = new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true);
        using (writer.PushSequence())
        {
            while (fields.HasData)
            {
                if (fields.PeekTag() != extensionsTag)
                {
                    writer.WriteEncodedValue(fields.ReadEncodedValue().Span);
                    continue;
                }

                var extensions = fields.ReadSequence(extensionsTag).ReadSequence();
                using (writer.PushSequence(extensionsTag))
                using (writer.PushSequence())
                {
                    while (extensions.HasData)
                    {
                        var encoded = extensions.ReadEncodedValue();
                        var oid = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence().ReadObjectIdentifier();
                        if (oid != SctOid)
                        {
                            writer.WriteEncodedValue(encoded.Span);
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    private static bool VerifyDsse(X509Certificate2 leaf, string payloadType, byte[] payload, byte[] signature)
    {
        var pae = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"DSSEv1 {Encoding.UTF8.GetByteCount(payloadType)} {payloadType} {payload.Length} ")).Concat(payload).ToArray();
        if (payloadType != PayloadType)
        {
            return false;
        }

        using var ecdsa = leaf.GetECDsaPublicKey();
        if (ecdsa is null)
        {
            return false;
        }

        var hash = ecdsa.KeySize > 256 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;
        return ecdsa.VerifyData(pae, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>The OIDC issuer (Fulcio's v2 extension, else v1) and the subject alternative name (URI, else email).</summary>
    private static (string? Issuer, string? San) Identity(X509Certificate2 leaf)
    {
        string? issuer = null;
        string? uri = null;
        string? email = null;
        foreach (var ext in leaf.Extensions.Cast<X509Extension>())
        {
            switch (ext.Oid?.Value)
            {
                case IssuerV2Oid:
                    issuer = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.UTF8String);
                    break;
                case IssuerV1Oid when issuer is null:
                    issuer = Encoding.ASCII.GetString(ext.RawData);
                    break;
                case SubjectAltNameOid:
                    var names = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
                    while (names.HasData)
                    {
                        var tag = names.PeekTag();
                        if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue is 1 or 6)
                        {
                            var value = names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, tag.TagValue));
                            if (tag.TagValue == 6)
                            {
                                uri ??= value;
                            }
                            else
                            {
                                email ??= value;
                            }
                        }
                        else
                        {
                            names.ReadEncodedValue();
                        }
                    }

                    break;
            }
        }

        return (issuer, uri ?? email);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static byte[]? Bytes(JsonNode? node) => Str(node) is { } s ? Convert.FromBase64String(s) : null;

    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n)
        ? n
        : long.Parse(Str(node) ?? throw new FormatException("missing number"), NumberStyles.None, CultureInfo.InvariantCulture);
}
