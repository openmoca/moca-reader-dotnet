using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace OpenMoca.Reader.Age;

/// <summary>Why age decryption stopped. Maps to the encrypted-package codes in the Reader.</summary>
public enum AgeFailure
{
    /// <summary>The header is malformed, unsupported or over a limit (E006).</summary>
    Header,

    /// <summary>No key, or a kind of key no stanza uses (E001).</summary>
    NoKey,

    /// <summary>A key of a kind the file uses unwrapped no stanza (E002).</summary>
    NoMatch,

    /// <summary>The header MAC does not verify (E002).</summary>
    Mac,

    /// <summary>The payload failed authentication or was truncated (E003).</summary>
    Payload,

    /// <summary>The decrypted payload passed the byte limit (T003).</summary>
    Limit,
}

/// <summary>A failure to decrypt an age file. Never thrown to a Reader's caller.</summary>
public sealed class AgeException(AgeFailure failure, string message) : Exception(message)
{
    /// <summary>Why decryption stopped.</summary>
    public AgeFailure Failure { get; } = failure;
}

/// <summary>One recipient stanza of an age header.</summary>
/// <param name="Type">The stanza type, for example <c>X25519</c>.</param>
/// <param name="Args">The arguments after the type.</param>
/// <param name="Body">The decoded body.</param>
public sealed record AgeStanza(string Type, IReadOnlyList<string> Args, byte[] Body);

/// <summary>A parsed age v1 header.</summary>
/// <param name="Stanzas">The recipient stanzas, in order.</param>
/// <param name="HeaderNoMac">The header bytes the MAC covers, up to and including <c>---</c>.</param>
/// <param name="Mac">The 32-byte header MAC.</param>
/// <param name="PayloadStart">Where the 16-byte payload nonce starts.</param>
public sealed record AgeHeader(IReadOnlyList<AgeStanza> Stanzas, byte[] HeaderNoMac, byte[] Mac, int PayloadStart);

/// <summary>What a key provider returned: X25519 identities, a passphrase, or a file key the host unwrapped.</summary>
/// <param name="Identities"><c>AGE-SECRET-KEY-1…</c> identities.</param>
/// <param name="Passphrase">A passphrase for an <c>scrypt</c> stanza.</param>
/// <param name="FileKey">A 16-byte file key.</param>
public sealed record AgeKey(IReadOnlyList<string>? Identities = null, string? Passphrase = null, byte[]? FileKey = null);

/// <summary>
/// Decrypt-only age v1 (https://c2sp.org/age) for encrypted packages (moca-spec encrypted packages §4): X25519
/// and scrypt recipients, no plugins, no armor. No plaintext is returned before the whole payload authenticates.
/// </summary>
public static partial class AgeDecryptor
{
    private const string VersionLine = "age-encryption.org/v1";
    private const int MaxHeaderBytes = 64 * 1024;
    private const int Chunk = 64 * 1024;
    private const int Tag = 16;
    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    [GeneratedRegex("^[A-Za-z0-9+/]*$")]
    private static partial Regex Base64Chars();

    [GeneratedRegex("^[\\x21-\\x7e]+$")]
    private static partial Regex ArgChars();

    [GeneratedRegex("^[\\x20-\\x7e]*$")]
    private static partial Regex LineChars();

    [GeneratedRegex("^[1-9][0-9]?$")]
    private static partial Regex WorkFactor();

    private static AgeException Fail(AgeFailure failure, string message) => new(failure, message);

    private static byte[] DecodeBase64(string text, string what)
    {
        if (!Base64Chars().IsMatch(text) || text.Length % 4 == 1)
        {
            throw Fail(AgeFailure.Header, $"{what} is not canonical unpadded base64");
        }

        var padded = text + new string('=', (4 - text.Length % 4) % 4);
        var bytes = Convert.FromBase64String(padded);
        if (Convert.ToBase64String(bytes).TrimEnd('=') != text)
        {
            throw Fail(AgeFailure.Header, $"{what} is not canonical base64");
        }

        return bytes;
    }

    /// <summary>
    /// Parses an age v1 header strictly. Throws <see cref="AgeException"/> (<see cref="AgeFailure.Header"/>) on any
    /// deviation, an scrypt stanza that is not alone, a work factor over <paramref name="maxScryptWorkFactor"/>, a
    /// malformed X25519 or scrypt stanza, a missing payload nonce, or a header over 64 KiB.
    /// </summary>
    public static AgeHeader ParseHeader(ReadOnlySpan<byte> file, int maxScryptWorkFactor = 20)
    {
        var bytes = file.ToArray();
        var pos = 0;

        (string Text, int Start) Line()
        {
            var end = Array.IndexOf(bytes, (byte)'\n', pos);
            if (end < 0 || end >= MaxHeaderBytes)
            {
                throw Fail(AgeFailure.Header, end < 0 ? "the header is not terminated" : "the header is over 64 KiB");
            }

            var text = Encoding.Latin1.GetString(bytes, pos, end - pos);
            if (!LineChars().IsMatch(text))
            {
                throw Fail(AgeFailure.Header, "the header has a character outside printable ASCII");
            }

            var start = pos;
            pos = end + 1;
            return (text, start);
        }

        if (Line().Text != VersionLine)
        {
            throw Fail(AgeFailure.Header, "not an age v1 file");
        }

        var stanzas = new List<AgeStanza>();
        while (true)
        {
            var (text, start) = Line();
            if (text.StartsWith("--- ", StringComparison.Ordinal))
            {
                var mac = DecodeBase64(text[4..], "the header MAC");
                if (mac.Length != 32)
                {
                    throw Fail(AgeFailure.Header, "the header MAC is not 32 bytes");
                }

                if (stanzas.Count == 0)
                {
                    throw Fail(AgeFailure.Header, "the header has no recipient stanza");
                }

                CheckStanzas(stanzas, maxScryptWorkFactor);
                // The 16-byte payload nonce counts as header for failure purposes, as the C2SP test kit does.
                if (bytes.Length - pos < 16)
                {
                    throw Fail(AgeFailure.Header, "the payload nonce is missing or short");
                }

                return new AgeHeader(stanzas, bytes[..(start + 3)], mac, pos);
            }

            if (!text.StartsWith("-> ", StringComparison.Ordinal))
            {
                throw Fail(AgeFailure.Header, "expected a stanza or the MAC line");
            }

            var args = text[3..].Split(' ');
            if (!args.All(a => ArgChars().IsMatch(a)))
            {
                throw Fail(AgeFailure.Header, "a stanza argument is empty or has a character outside %x21-7E");
            }

            // The body: full 64-column lines, then one shorter (possibly empty) final line.
            var body = new StringBuilder();
            while (true)
            {
                var line = Line().Text;
                if (line.Length > 64)
                {
                    throw Fail(AgeFailure.Header, "a stanza body line is longer than 64 columns");
                }

                body.Append(line);
                if (line.Length < 64)
                {
                    break;
                }
            }

            stanzas.Add(new AgeStanza(args[0], args[1..], DecodeBase64(body.ToString(), "a stanza body")));
        }
    }

    private static void CheckStanzas(List<AgeStanza> stanzas, int maxScryptWorkFactor)
    {
        if (stanzas.Any(s => s.Type == "scrypt") && stanzas.Count != 1)
        {
            throw Fail(AgeFailure.Header, "an scrypt stanza must be the only stanza");
        }

        foreach (var s in stanzas)
        {
            if (s.Type == "X25519")
            {
                if (s.Args.Count != 1)
                {
                    throw Fail(AgeFailure.Header, "an X25519 stanza takes one argument");
                }

                if (DecodeBase64(s.Args[0], "an X25519 share").Length != 32 || s.Body.Length != 32)
                {
                    throw Fail(AgeFailure.Header, "an X25519 share or body is not 32 bytes");
                }
            }
            else if (s.Type == "scrypt")
            {
                if (s.Args.Count != 2)
                {
                    throw Fail(AgeFailure.Header, "an scrypt stanza takes two arguments");
                }

                if (DecodeBase64(s.Args[0], "an scrypt salt").Length != 16)
                {
                    throw Fail(AgeFailure.Header, "an scrypt salt is not 16 bytes");
                }

                if (!WorkFactor().IsMatch(s.Args[1]))
                {
                    throw Fail(AgeFailure.Header, "an scrypt work factor is not a decimal number");
                }

                var workFactor = int.Parse(s.Args[1], System.Globalization.CultureInfo.InvariantCulture);
                if (workFactor > maxScryptWorkFactor)
                {
                    throw Fail(AgeFailure.Header, $"the scrypt work factor {workFactor} is over the limit of {maxScryptWorkFactor}");
                }

                if (s.Body.Length != 32)
                {
                    throw Fail(AgeFailure.Header, "an scrypt stanza body is not 32 bytes");
                }
            }
        }
    }

    private static byte[] Hkdf(byte[] ikm, byte[] salt, string info)
    {
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(ikm, salt, Encoding.ASCII.GetBytes(info)));
        var output = new byte[32];
        generator.GenerateBytes(output, 0, 32);
        return output;
    }

    /// <summary>ChaCha20-Poly1305 open, from BouncyCastle (not the platform-dependent BCL type); null when it fails.</summary>
    private static byte[]? Open(byte[] key, byte[] nonce, ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < Tag)
        {
            return null;
        }

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(key), Tag * 8, nonce));
        var input = sealedBytes.ToArray();
        var output = new byte[cipher.GetOutputSize(input.Length)];
        try
        {
            var n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            n += cipher.DoFinal(output, n);
            return output[..n];
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }

    /// <summary>The 32-byte X25519 scalar of an <c>AGE-SECRET-KEY-1…</c> identity, or null when it is not one.</summary>
    public static byte[]? DecodeIdentity(string text)
    {
        if (text != text.ToUpperInvariant() && text != text.ToLowerInvariant())
        {
            return null;
        }

        var s = text.ToLowerInvariant();
        var sep = s.LastIndexOf('1');
        if (sep < 0 || s[..sep] != "age-secret-key-")
        {
            return null;
        }

        var data = s[(sep + 1)..].Select(c => Bech32Charset.IndexOf(c)).ToArray();
        if (data.Length < 6 || data.Any(d => d < 0))
        {
            return null;
        }

        var hrp = s[..sep];
        var expanded = hrp.Select(c => c >> 5).Append(0).Concat(hrp.Select(c => c & 31)).Concat(data);
        if (Polymod(expanded) != 1)
        {
            return null;
        }

        int acc = 0, bits = 0;
        var output = new List<byte>();
        foreach (var v in data[..^6])
        {
            acc = (acc << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((acc >> bits) & 0xff));
            }
        }

        return bits >= 5 || (acc & ((1 << bits) - 1)) != 0 || output.Count != 32 ? null : [.. output];
    }

    private static int Polymod(IEnumerable<int> values)
    {
        int[] gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        var chk = 1;
        foreach (var v in values)
        {
            var top = chk >>> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    chk ^= gen[i];
                }
            }
        }

        return chk;
    }

    private static byte[] UnwrapX25519(AgeHeader header, IReadOnlyList<string> identities)
    {
        var keys = identities.Select(id =>
        {
            var scalar = DecodeIdentity(id) ?? throw Fail(AgeFailure.NoMatch, "an identity from the key provider is not a valid AGE-SECRET-KEY-1 identity");
            var recipient = new byte[32];
            X25519.ScalarMultBase(scalar, 0, recipient, 0);
            return (Scalar: scalar, Recipient: recipient);
        }).ToList();

        foreach (var s in header.Stanzas.Where(x => x.Type == "X25519"))
        {
            var share = Convert.FromBase64String(s.Args[0] + "=");
            foreach (var (scalar, recipient) in keys)
            {
                var shared = new byte[32];
                // False when the share is a low-order point: the all-zero secret, which age forbids.
                if (!X25519.CalculateAgreement(scalar, 0, share, 0, shared, 0))
                {
                    throw Fail(AgeFailure.Header, "an X25519 share is a low-order point");
                }

                var fileKey = Open(Hkdf(shared, [.. share, .. recipient], "age-encryption.org/v1/X25519"), new byte[12], s.Body);
                if (fileKey is { Length: 16 })
                {
                    return fileKey;
                }
            }
        }

        throw Fail(AgeFailure.NoMatch, "no identity unwraps an X25519 stanza");
    }

    private static byte[] UnwrapScrypt(AgeStanza stanza, string passphrase)
    {
        var salt = Encoding.ASCII.GetBytes("age-encryption.org/v1/scrypt").Concat(Convert.FromBase64String(stanza.Args[0] + "==")).ToArray();
        var n = 1 << int.Parse(stanza.Args[1], System.Globalization.CultureInfo.InvariantCulture);
        var key = SCrypt.Generate(Encoding.UTF8.GetBytes(passphrase), salt, n, 8, 1, 32);
        var fileKey = Open(key, new byte[12], stanza.Body);
        return fileKey is { Length: 16 } ? fileKey : throw Fail(AgeFailure.NoMatch, "the passphrase does not unwrap the scrypt stanza");
    }

    /// <summary>
    /// Unwraps the file key. <see cref="AgeFailure.NoKey"/> when the key is missing or of a kind no stanza uses;
    /// <see cref="AgeFailure.NoMatch"/> when it is of the right kind but fails.
    /// </summary>
    public static byte[] UnwrapFileKey(AgeHeader header, AgeKey? key)
    {
        if (key?.FileKey is { } fileKey)
        {
            return fileKey.Length == 16 ? fileKey : throw Fail(AgeFailure.NoMatch, "the file key from the key provider is not 16 bytes");
        }

        if (key?.Identities is { Count: > 0 } identities)
        {
            return header.Stanzas.Any(s => s.Type == "X25519")
                ? UnwrapX25519(header, identities)
                : throw Fail(AgeFailure.NoKey, "the key provider returned identities, and the file has no X25519 stanza");
        }

        if (key?.Passphrase is { } passphrase)
        {
            var stanza = header.Stanzas.FirstOrDefault(s => s.Type == "scrypt")
                ?? throw Fail(AgeFailure.NoKey, "the key provider returned a passphrase, and the file has no scrypt stanza");
            return UnwrapScrypt(stanza, passphrase);
        }

        throw Fail(AgeFailure.NoKey, "the key provider returned no key");
    }

    /// <summary>Verifies the header MAC with a constant-time compare. Throws <see cref="AgeFailure.Mac"/>.</summary>
    public static void VerifyMac(AgeHeader header, byte[] fileKey)
    {
        var hmac = new HMac(new Sha256Digest());
        hmac.Init(new KeyParameter(Hkdf(fileKey, [], "header")));
        hmac.BlockUpdate(header.HeaderNoMac, 0, header.HeaderNoMac.Length);
        var mac = new byte[32];
        hmac.DoFinal(mac, 0);
        if (!CryptographicOperations.FixedTimeEquals(mac, header.Mac))
        {
            throw Fail(AgeFailure.Mac, "the header MAC does not verify");
        }
    }

    /// <summary>
    /// Decrypts and authenticates the whole STREAM payload. Throws <see cref="AgeFailure.Payload"/> on any failure and
    /// <see cref="AgeFailure.Limit"/> once the plaintext passes <paramref name="maxBytes"/>. Returns nothing until
    /// every chunk has authenticated.
    /// </summary>
    public static byte[] DecryptPayload(ReadOnlySpan<byte> file, AgeHeader header, byte[] fileKey, long maxBytes = long.MaxValue)
    {
        var key = Hkdf(fileKey, file.Slice(header.PayloadStart, 16).ToArray(), "payload");
        using var output = new MemoryStream();
        var pos = header.PayloadStart + 16;
        for (long counter = 0; ; counter++)
        {
            var remaining = file.Length - pos;
            var last = remaining <= Chunk + Tag;
            var sealedBytes = file.Slice(pos, last ? remaining : Chunk + Tag);
            var nonce = new byte[12];
            for (var i = 0; i < 8; i++)
            {
                nonce[10 - i] = (byte)(counter >> (8 * i));
            }

            nonce[11] = last ? (byte)1 : (byte)0;
            var plain = Open(key, nonce, sealedBytes)
                ?? throw Fail(AgeFailure.Payload, last ? "the final chunk does not authenticate, or is missing" : $"chunk {counter} does not authenticate");
            if (last && plain.Length == 0 && counter > 0)
            {
                throw Fail(AgeFailure.Payload, "an empty final chunk follows a full chunk");
            }

            if (output.Length + plain.Length > maxBytes)
            {
                throw Fail(AgeFailure.Limit, $"the decrypted payload is over {maxBytes} bytes");
            }

            output.Write(plain);
            if (last)
            {
                return output.ToArray();
            }

            pos += Chunk + Tag;
        }
    }

    /// <summary>Unwraps the file key, verifies the MAC and decrypts the payload.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> file, AgeHeader header, AgeKey? key, long maxBytes = long.MaxValue)
    {
        var fileKey = UnwrapFileKey(header, key);
        VerifyMac(header, fileKey);
        return DecryptPayload(file, header, fileKey, maxBytes);
    }
}
