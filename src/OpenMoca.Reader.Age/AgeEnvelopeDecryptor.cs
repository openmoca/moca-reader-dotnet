using OpenMoca.Reader.Encryption;

namespace OpenMoca.Reader.Age;

/// <summary>
/// The <see cref="IEnvelopeDecryptor"/> a host sets as <see cref="ReadOptions.EnvelopeDecryptor"/> to enable the optional
/// feature <c>encryption</c>: <see cref="AgeDecryptor"/> behind the Reader's interface.
/// </summary>
public sealed class AgeEnvelopeDecryptor : IEnvelopeDecryptor
{
    /// <summary>The one instance; the decryptor holds no state.</summary>
    public static AgeEnvelopeDecryptor Instance { get; } = new();

    private sealed record Header(AgeHeader Age) : IEnvelopeHeader
    {
        public IReadOnlyList<EnvelopeStanza> Stanzas { get; } = Age.Stanzas.Select(s => new EnvelopeStanza(s.Type, s.Args, s.Body)).ToList();
    }

    /// <inheritdoc/>
    public IEnvelopeHeader ParseHeader(byte[] file, int maxScryptWorkFactor)
    {
        try
        {
            return new Header(AgeDecryptor.ParseHeader(file, maxScryptWorkFactor));
        }
        catch (AgeException ex)
        {
            throw Map(ex);
        }
    }

    /// <inheritdoc/>
    public byte[] Decrypt(byte[] file, IEnvelopeHeader header, EnvelopeKey key, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (header is not Header parsed)
        {
            throw new ArgumentException("The header was not parsed by this decryptor.", nameof(header));
        }

        try
        {
            return AgeDecryptor.Decrypt(file, parsed.Age, new AgeKey(key.Identities, key.Passphrase, key.FileKey), maxBytes);
        }
        catch (AgeException ex)
        {
            throw Map(ex);
        }
    }

    private static EnvelopeDecryptionException Map(AgeException ex) => new(ex.Failure switch
    {
        AgeFailure.Header => EnvelopeFailure.Header,
        AgeFailure.NoKey => EnvelopeFailure.NoKey,
        AgeFailure.NoMatch => EnvelopeFailure.NoMatch,
        AgeFailure.Mac => EnvelopeFailure.Mac,
        AgeFailure.Payload => EnvelopeFailure.Payload,
        _ => EnvelopeFailure.Limit,
    }, ex.Message);
}
