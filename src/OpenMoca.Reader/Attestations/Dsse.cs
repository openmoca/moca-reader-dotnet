using OpenMoca.Reader.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Content;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;

namespace OpenMoca.Reader.Attestations;

/// <summary>
/// DSSE (Dead Simple Signing Envelope) verification: the pre-authentication encoding and a signature
/// check against keys the host trusts. Ed25519 comes from BouncyCastle because .NET has none built in.
/// </summary>
public static partial class Dsse
{
    /// <summary>
    /// Pre-Authentication Encoding: <c>DSSEv1 &lt;len&gt; &lt;type&gt; &lt;len&gt; &lt;body&gt;</c>, lengths in
    /// ASCII decimal bytes.
    /// </summary>
    public static byte[] PreAuthEncode(string payloadType, byte[] payload)
    {
        var type = Encoding.UTF8.GetBytes(payloadType);
        var head = Encoding.UTF8.GetBytes($"DSSEv1 {type.Length} ");
        var middle = Encoding.UTF8.GetBytes($" {payload.Length} ");
        return [.. head, .. type, .. middle, .. payload];
    }

    /// <summary>
    /// Verifies an envelope. <paramref name="resolvePublicKey"/> returns a PEM (SPKI) public key for a
    /// key id, or null when the key is unknown or not trusted.
    /// </summary>
    /// <returns>The key id whose signature verified, or the reason none did.</returns>
    public static (bool Ok, string? KeyId, string? Reason) Verify(JsonObject envelope, Func<string, string?> resolvePublicKey)
    {
        var payloadText = JsonValues.StringOf(envelope["payload"]);
        var payloadType = JsonValues.StringOf(envelope["payloadType"]);
        if (payloadText is null || payloadType is null || envelope["signatures"] is not JsonArray { Count: > 0 } signatures)
        {
            return (false, null, "malformed DSSE envelope");
        }

        if (DecodeBase64(payloadText) is not { } payload)
        {
            return (false, null, "envelope payload is not valid base64");
        }

        var pae = PreAuthEncode(payloadType, payload);
        foreach (var entry in signatures.OfType<JsonObject>())
        {
            var keyId = JsonValues.StringOf(entry["keyid"]);
            var sig = JsonValues.StringOf(entry["sig"]);
            if (keyId is null || sig is null || resolvePublicKey(keyId) is not { } pem)
            {
                continue;
            }

            if (VerifySignature(pem, pae, sig))
            {
                return (true, keyId, null);
            }
        }

        return (false, null, "no signature verified against a trusted key");
    }

    private static bool VerifySignature(string publicKeyPem, byte[] data, string signatureBase64)
    {
        try
        {
            if (DecodeBase64(signatureBase64) is not { } signature)
            {
                return false;
            }

            using var reader = new StringReader(publicKeyPem);
            // Attestations §5: only an Ed25519 key verifies. A trusted key of any other type verifies nothing.
            if (new PemReader(reader).ReadObject() is not Ed25519PublicKeyParameters key)
            {
                return false;
            }

            // Attestations §5: strict encodings first, so every Reader's library reaches the same answer.
            if (!StrictEncodings(key.GetEncoded(), signature))
            {
                return false;
            }

            var signer = new Ed25519Signer();
            signer.Init(false, key);
            signer.BlockUpdate(data, 0, data.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or InvalidCastException or ArgumentException or CryptoException or PemException)
        {
            return false;
        }
    }

    private static readonly System.Numerics.BigInteger GroupOrder =
        System.Numerics.BigInteger.Pow(2, 252) + System.Numerics.BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Attestations §5: the public key A and the signature's R are canonical encodings of points of prime order L,
    /// and S is less than L. Ed25519 libraries disagree on small-order, mixed-order and non-canonical inputs; with
    /// these checks first, the cofactored and cofactorless equations agree.
    /// </summary>
    internal static bool StrictEncodings(byte[] publicKey, byte[] signature) =>
        signature.Length == 64
        && new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true) < GroupOrder
        && Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(publicKey, 0)
        && Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(signature[..32], 0);

    /// <summary>
    /// Attestations §7: DSSE payloads and signatures are base64 (RFC 4648 §4) or base64url (§5), with or without
    /// padding. Anything else is not base64, and null is returned.
    /// </summary>
    internal static byte[]? DecodeBase64(string text)
    {
        if (!Base64Text().IsMatch(text))
        {
            return null;
        }

        var body = text.TrimEnd('=');
        if (body.Length % 4 == 1 || (text.Length != body.Length && text.Length % 4 != 0))
        {
            return null;
        }

        var standard = body.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard + new string('=', (4 - (standard.Length % 4)) % 4));
    }

    [GeneratedRegex("^[A-Za-z0-9+/_-]*={0,2}\\z")]
    private static partial Regex Base64Text();
}
