using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Attestations §5: DSSE keys are Ed25519. A trusted key of any other type verifies nothing, so a signature
/// that is correct for an ECDSA or RSA key still does not verify.
/// </summary>
public sealed class DsseTests
{
    private const string PayloadType = "application/vnd.in-toto+json";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"_type":"https://in-toto.io/Statement/v1"}""");

    private static JsonObject Envelope(byte[] signature) => new()
    {
        ["payload"] = Convert.ToBase64String(Payload),
        ["payloadType"] = PayloadType,
        ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = "k", ["sig"] = Convert.ToBase64String(signature) }),
    };

    [Fact]
    public void Verify_Ed25519Signature_Verifies()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var signer = new Ed25519Signer();
        signer.Init(true, pair.Private);
        var pae = Dsse.PreAuthEncode(PayloadType, Payload);
        signer.BlockUpdate(pae, 0, pae.Length);
        using var text = new StringWriter();
        new PemWriter(text).WriteObject(pair.Public);

        var result = Dsse.Verify(Envelope(signer.GenerateSignature()), _ => text.ToString());

        Assert.True(result.Ok);
    }

    [Fact]
    public void Verify_EcdsaSignatureWithTrustedKey_DoesNotVerify()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ecdsa.SignData(Dsse.PreAuthEncode(PayloadType, Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var result = Dsse.Verify(Envelope(signature), _ => ecdsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Ok);
        Assert.Equal("no signature verified against a trusted key", result.Reason);
    }

    [Fact]
    public void Verify_RsaSignatureWithTrustedKey_DoesNotVerify()
    {
        using var rsa = RSA.Create(2048);
        var signature = rsa.SignData(Dsse.PreAuthEncode(PayloadType, Payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var result = Dsse.Verify(Envelope(signature), _ => rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Ok);
        Assert.Equal("no signature verified against a trusted key", result.Reason);
    }

    // Attestations §6 and §7 (0.6): strict base64 and trust-root rules.
    [Fact]
    public void Base64_accepts_both_alphabets_with_or_without_padding_and_nothing_else()
    {
        Assert.Equal("hi", Encoding.UTF8.GetString(Dsse.DecodeBase64("aGk=")!));
        Assert.Equal("hi", Encoding.UTF8.GetString(Dsse.DecodeBase64("aGk")!));
        Assert.Equal(Dsse.DecodeBase64("/+8="), Dsse.DecodeBase64("_-8"));
        foreach (var bad in new[] { "aG k=", "a", "aGk==", "aGk=x", "!!!!" })
        {
            Assert.Null(Dsse.DecodeBase64(bad));
        }
    }

    [Fact]
    public void Trust_root_refuses_a_repeated_keyid_and_a_key_without_roles()
    {
        JsonObject Key(JsonArray? roles) => roles is null
            ? new JsonObject { ["keyid"] = "k", ["publicKey"] = "x" }
            : new JsonObject { ["keyid"] = "k", ["publicKey"] = "x", ["roles"] = roles };
        Assert.Throws<ArgumentException>(() => TrustRoot.Load(new JsonObject { ["keys"] = new JsonArray(Key(new JsonArray("package")), Key(new JsonArray("package"))) }));
        Assert.Throws<ArgumentException>(() => TrustRoot.Load(new JsonObject { ["keys"] = new JsonArray(Key(null)) }));
        Assert.Null(TrustRoot.Load(new JsonObject { ["keys"] = new JsonArray(Key(new JsonArray())) }).Key("k", "package", DateTimeOffset.UnixEpoch));
    }
}
