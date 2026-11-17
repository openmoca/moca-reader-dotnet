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

    /// <summary>Reader contract §3: the header holds at most this many stanzas (E006).</summary>
    public const int MaxStanzas = 256;

    /// <summary>Reader contract §3: the smallest valid <c>maxScryptWorkFactor</c>.</summary>
    public const int MinWorkFactorLimit = 1;

    /// <summary>Reader contract §3: the largest valid <c>maxScryptWorkFactor</c>.</summary>
    public const int MaxWorkFactorLimit = 22;

    /// <summary>Whether a work-factor limit is an integer in 1–22 (Reader contract §3).</summary>
    public static bool ValidWorkFactorLimit(int limit) => limit is >= MinWorkFactorLimit and <= MaxWorkFactorLimit;

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
        // A limit outside 1–22 is refused, never read as "no limit" (and keeps 1 << n in range).
        if (!ValidWorkFactorLimit(maxScryptWorkFactor))
        {
            throw Fail(AgeFailure.Header, $"maxScryptWorkFactor must be from 1 to 22, not {maxScryptWorkFactor}");
        }

        // The header is at most 64 KiB, and the payload nonce follows it: nothing past that is copied or searched.
        var fileLength = file.Length;
        var bytes = file[..Math.Min(file.Length, MaxHeaderBytes + 16)].ToArray();
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
                if (fileLength - pos < 16)
                {
                    throw Fail(AgeFailure.Header, "the payload nonce is missing or short");
                }

                return new AgeHeader(stanzas, bytes[..(start + 3)], mac, pos);
            }

            if (!text.StartsWith("-> ", StringComparison.Ordinal))
            {
                throw Fail(AgeFailure.Header, "expected a stanza or the MAC line");
            }

            if (stanzas.Count >= MaxStanzas)
            {
                throw Fail(AgeFailure.Header, $"the header has more than {MaxStanzas} stanzas");
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

    /// <summary>
    /// ChaCha20-Poly1305 open, from BouncyCastle (not the platform-dependent BCL type), into <paramref name="output"/>.
    /// False when it fails; the caller then discards (and zeroes) whatever was written.
    /// </summary>
    private static bool OpenInto(byte[] key, byte[] nonce, ReadOnlySpan<byte> sealedBytes, Span<byte> output)
    {
        if (sealedBytes.Length < Tag || output.Length != sealedBytes.Length - Tag)
        {
            return false;
        }

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(key), Tag * 8, nonce));
        try
        {
            var n = cipher.ProcessBytes(sealedBytes, output);
            cipher.DoFinal(output[n..]);
            return true;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return false;
        }
    }

    /// <summary>ChaCha20-Poly1305 open of a small value (a wrapped file key); null when it fails.</summary>
    private static byte[]? Open(byte[] key, byte[] nonce, ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < Tag)
        {
            return null;
        }

        var output = new byte[sealedBytes.Length - Tag];
        if (OpenInto(key, nonce, sealedBytes, output))
        {
            return output;
        }

        CryptographicOperations.ZeroMemory(output);
        return null;
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

    // The file key an X25519 stanza unwraps with one of the identities, or null. A bad identity is NoMatch (E002).
    private static byte[]? UnwrapX25519(AgeHeader header, IReadOnlyList<string> identities)
    {
        var keys = new List<(byte[] Scalar, byte[] Recipient)>();
        try
        {
            foreach (var id in identities)
            {
                var scalar = (id is null ? null : DecodeIdentity(id))
                    ?? throw Fail(AgeFailure.NoMatch, "an identity from the key provider is not a valid AGE-SECRET-KEY-1 identity");
                var recipient = new byte[32];
                X25519.ScalarMultBase(scalar, 0, recipient, 0);
                keys.Add((scalar, recipient));
            }

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

                    var wrapKey = Hkdf(shared, [.. share, .. recipient], "age-encryption.org/v1/X25519");
                    var fileKey = Open(wrapKey, new byte[12], s.Body);
                    CryptographicOperations.ZeroMemory(shared);
                    CryptographicOperations.ZeroMemory(wrapKey);
                    if (fileKey is { Length: 16 })
                    {
                        return fileKey;
                    }

                    if (fileKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(fileKey);
                    }
                }
            }

            return null;
        }
        finally
        {
            foreach (var (scalar, _) in keys)
            {
                CryptographicOperations.ZeroMemory(scalar);
            }
        }
    }

    // The file key the passphrase unwraps from the scrypt stanza, or null.
    private static byte[]? UnwrapScrypt(AgeStanza stanza, string passphrase)
    {
        var salt = Encoding.ASCII.GetBytes("age-encryption.org/v1/scrypt").Concat(Convert.FromBase64String(stanza.Args[0] + "==")).ToArray();
        // The work factor was checked against the 1–22 limit when the header was parsed, so the shift cannot overflow.
        var n = 1 << int.Parse(stanza.Args[1], System.Globalization.CultureInfo.InvariantCulture);
        var secret = Encoding.UTF8.GetBytes(passphrase);
        var key = SCrypt.Generate(secret, salt, n, 8, 1, 32);
        CryptographicOperations.ZeroMemory(secret);
        var fileKey = Open(key, new byte[12], stanza.Body);
        CryptographicOperations.ZeroMemory(key);
        if (fileKey is { Length: 16 })
        {
            return fileKey;
        }

        if (fileKey is not null)
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }

        return null;
    }

    /// <summary>
    /// Finds the file key and verifies the header MAC with it (encrypted packages §5.1, §5.2 steps 4–5). A key's kinds
    /// are tried in order: identities, then the passphrase, then the file key; the first whose file key verifies the
    /// MAC wins. <see cref="AgeFailure.NoKey"/> when no kind the key has is one the file uses;
    /// <see cref="AgeFailure.NoMatch"/> or <see cref="AgeFailure.Mac"/> when one was tried and failed. The caller zeroes
    /// the returned key.
    /// </summary>
    public static byte[] FindFileKey(AgeHeader header, AgeKey? key)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (key is null)
        {
            throw Fail(AgeFailure.NoKey, "the key provider returned no key");
        }

        if (key.FileKey is { Length: not 16 })
        {
            throw Fail(AgeFailure.NoMatch, "the file key from the key provider is not 16 bytes");
        }

        var tried = false;
        var macFailed = false;
        byte[]? Accept(byte[]? fileKey)
        {
            if (fileKey is null)
            {
                return null;
            }

            if (MacOk(header, fileKey))
            {
                return fileKey;
            }

            macFailed = true;
            CryptographicOperations.ZeroMemory(fileKey);
            return null;
        }

        if (key.Identities is { Count: > 0 } identities && header.Stanzas.Any(s => s.Type == "X25519"))
        {
            tried = true;
            if (Accept(UnwrapX25519(header, identities)) is { } found)
            {
                return found;
            }
        }

        if (key.Passphrase is { } passphrase && header.Stanzas.FirstOrDefault(s => s.Type == "scrypt") is { } stanza)
        {
            tried = true;
            if (Accept(UnwrapScrypt(stanza, passphrase)) is { } found)
            {
                return found;
            }
        }

        if (key.FileKey is { } given)
        {
            tried = true;
            if (Accept([.. given]) is { } found)
            {
                return found;
            }
        }

        if (!tried)
        {
            throw Fail(AgeFailure.NoKey, key.Identities is { Count: > 0 } || key.Passphrase is not null
                ? "the key provider returned keys of kinds no stanza in the file uses"
                : "the key provider returned no key");
        }

        throw macFailed ? Fail(AgeFailure.Mac, "the header MAC does not verify") : Fail(AgeFailure.NoMatch, "no key from the key provider unwraps a stanza");
    }

    private static bool MacOk(AgeHeader header, byte[] fileKey)
    {
        var macKey = Hkdf(fileKey, [], "header");
        var hmac = new HMac(new Sha256Digest());
        hmac.Init(new KeyParameter(macKey));
        hmac.BlockUpdate(header.HeaderNoMac, 0, header.HeaderNoMac.Length);
        var mac = new byte[32];
        hmac.DoFinal(mac, 0);
        CryptographicOperations.ZeroMemory(macKey);
        return CryptographicOperations.FixedTimeEquals(mac, header.Mac);
    }

    /// <summary>Verifies the header MAC with a constant-time compare. Throws <see cref="AgeFailure.Mac"/>.</summary>
    public static void VerifyMac(AgeHeader header, byte[] fileKey)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(fileKey);
        if (!MacOk(header, fileKey))
        {
            throw Fail(AgeFailure.Mac, "the header MAC does not verify");
        }
    }

    /// <summary>
    /// The plaintext length of a STREAM payload, from its length alone (encrypted packages §5.2 step 6): every chunk
    /// but the last is 64 KiB + 16, and the last is 16 to 64 KiB + 16. <see cref="AgeFailure.Payload"/> for a length no
    /// payload can have; <see cref="AgeFailure.Limit"/> over <paramref name="maxBytes"/> or over what one array holds.
    /// </summary>
    public static long PlaintextLength(long ciphertextLength, long maxBytes)
    {
        const long full = Chunk + Tag;
        var chunks = Math.Max(1, (ciphertextLength + full - 1) / full);
        var last = ciphertextLength - ((chunks - 1) * full);
        if (ciphertextLength < Tag || last < Tag || (last == Tag && chunks > 1))
        {
            throw Fail(AgeFailure.Payload, "the payload length is impossible: the final chunk is missing, short or empty after a full chunk");
        }

        var length = ciphertextLength - (chunks * Tag);
        if (length > maxBytes || length > Array.MaxLength)
        {
            throw Fail(AgeFailure.Limit, $"the decrypted payload would be {length} bytes, over {Math.Min(maxBytes, Array.MaxLength)}");
        }

        return length;
    }

    /// <summary>
    /// Decrypts and authenticates the whole STREAM payload into one array of the exact size, computed first.
    /// <see cref="AgeFailure.Payload"/> on any failure and <see cref="AgeFailure.Limit"/> over
    /// <paramref name="maxBytes"/>, both before decrypting. Returns nothing until every chunk has authenticated; on
    /// failure the partial plaintext is zeroed.
    /// </summary>
    public static byte[] DecryptPayload(ReadOnlySpan<byte> file, AgeHeader header, byte[] fileKey, long maxBytes = 512L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(fileKey);
        var start = header.PayloadStart + 16;
        var output = new byte[PlaintextLength(file.Length - start, maxBytes)];
        var key = Hkdf(fileKey, file.Slice(header.PayloadStart, 16).ToArray(), "payload");
        try
        {
            var pos = start;
            var written = 0;
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
                var size = sealedBytes.Length - Tag;
                if (!OpenInto(key, nonce, sealedBytes, output.AsSpan(written, size)))
                {
                    throw Fail(AgeFailure.Payload, last ? "the final chunk does not authenticate, or is missing" : $"chunk {counter} does not authenticate");
                }

                written += size;
                if (last)
                {
                    return output;
                }

                pos += Chunk + Tag;
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Finds the file key, verifies the MAC and decrypts the payload; the file key is zeroed after.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> file, AgeHeader header, AgeKey? key, long maxBytes = 512L * 1024 * 1024)
    {
        var fileKey = FindFileKey(header, key);
        try
        {
            return DecryptPayload(file, header, fileKey, maxBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }
}
