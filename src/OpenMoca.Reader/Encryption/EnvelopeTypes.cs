using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Encryption;

/// <summary>One recipient stanza of an envelope's age header. Package data: passed to the key provider unchanged.</summary>
/// <param name="Type">The stanza type, for example <c>X25519</c> or <c>scrypt</c>.</param>
/// <param name="Args">The arguments after the type.</param>
/// <param name="Body">The decoded stanza body.</param>
public sealed record EnvelopeStanza(string Type, IReadOnlyList<string> Args, byte[] Body);

/// <summary>What the Reader asks the host's key provider (encrypted packages §5.1).</summary>
/// <param name="Stanzas">The age stanzas of the file being opened.</param>
/// <param name="Outline">The envelope's outline: unverified, so never the only basis for choosing a key.</param>
/// <param name="File"><c>package</c> or <c>index</c>.</param>
public sealed record EnvelopeKeyRequest(IReadOnlyList<EnvelopeStanza> Stanzas, JsonObject? Outline, string File);

/// <summary>What a key provider returns: X25519 identities, a passphrase, or a file key the host unwrapped itself.</summary>
/// <param name="Identities"><c>AGE-SECRET-KEY-1…</c> identities.</param>
/// <param name="Passphrase">A passphrase for an <c>scrypt</c> stanza.</param>
/// <param name="FileKey">A 16-byte file key.</param>
public sealed record EnvelopeKey(IReadOnlyList<string>? Identities = null, string? Passphrase = null, byte[]? FileKey = null);

/// <summary>Why a decryptor stopped. The Reader maps each to its code.</summary>
public enum EnvelopeFailure
{
    /// <summary>The age header is malformed, unsupported or over a limit: E006.</summary>
    Header,

    /// <summary>No key, or a kind of key no stanza uses: E001.</summary>
    NoKey,

    /// <summary>A key of a kind the file uses failed, or is not a valid key: E002.</summary>
    NoMatch,

    /// <summary>The header MAC does not verify: E002.</summary>
    Mac,

    /// <summary>The payload failed authentication or was truncated: E003.</summary>
    Payload,

    /// <summary>The decrypted payload passed <see cref="ReaderLimits.MaxBytes"/>: T003.</summary>
    Limit,
}

/// <summary>Thrown by an <see cref="IEnvelopeDecryptor"/>; the Reader turns it into a diagnostic and never rethrows it.</summary>
public sealed class EnvelopeDecryptionException(EnvelopeFailure failure, string message) : Exception(message)
{
    /// <summary>Why decryption stopped.</summary>
    public EnvelopeFailure Failure { get; } = failure;
}

/// <summary>A parsed header, as the decryptor understands it.</summary>
public interface IEnvelopeHeader
{
    /// <summary>The recipient stanzas, in order.</summary>
    IReadOnlyList<EnvelopeStanza> Stanzas { get; }
}

/// <summary>
/// Decrypts the age files in an envelope (encrypted packages §4). <c>OpenMoca.Reader</c> has no implementation, so a
/// host that never opens envelopes never loads any cryptography for them; <c>OpenMoca.Reader.Age</c> provides one.
/// </summary>
public interface IEnvelopeDecryptor
{
    /// <summary>Parses a header strictly, refusing a work factor over the limit. Throws <see cref="EnvelopeDecryptionException"/>.</summary>
    IEnvelopeHeader ParseHeader(byte[] file, int maxScryptWorkFactor);

    /// <summary>Unwraps the file key, verifies the MAC and decrypts the whole payload. Throws <see cref="EnvelopeDecryptionException"/>.</summary>
    byte[] Decrypt(byte[] file, IEnvelopeHeader header, EnvelopeKey key, long maxBytes);
}

/// <summary>An envelope's part of a package result (encrypted packages §5.3).</summary>
/// <param name="Format">The marker's format, when <c>moca-enc.json</c> is valid.</param>
/// <param name="Opened">True only when the result is the inner package's.</param>
/// <param name="Outline">The unverified outline, when the envelope was not opened.</param>
public sealed record EnvelopeInfo(string? Format, bool Opened, JsonObject? Outline = null);
