using OpenMoca.Reader.Attestations;

namespace OpenMoca.Reader;

/// <summary>A member a composed package pins (package spec §7.1).</summary>
/// <param name="Id">The member package's id.</param>
/// <param name="Version">Its exact version.</param>
/// <param name="Digest">Its pinned digest.</param>
public sealed record MemberReference(string Id, string? Version, string Digest);

/// <summary>Options for <see cref="MocaReader.ReadPackageAsync"/>. Everything here comes from the host, never a package.</summary>
public sealed record ReadOptions
{
    /// <summary>The trust root attestations are verified against. Null: every attestation is unverifiable (A003).</summary>
    public TrustRoot? TrustRoot { get; init; }

    /// <summary>Finds a member package. Null: members are exposed unresolved, with no diagnostic.</summary>
    public Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>>? ResolveMember { get; init; }

    /// <summary>Promote warnings to errors without changing codes (Reader contract §11).</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Require a valid package attestation (attestations §8): a package without one is invalid
    /// (<c>A009_SIGNATURE_REQUIRED</c>).
    /// </summary>
    public bool RequireSignature { get; init; }

    /// <summary>Safety limits; the defaults are the reference implementation's.</summary>
    public ReaderLimits Limits { get; init; } = ReaderLimits.Default;

    /// <summary>Profiles the host recognises; others are reported as F001.</summary>
    public IReadOnlySet<string> KnownProfiles { get; init; } = Profiles.Known;

    /// <summary>
    /// Verifies Sigstore bundles, offline, against the trust root's <c>sigstore.trustedRoot</c> (attestations §6).
    /// Null: Sigstore attestations are unverifiable (A003). The <c>OpenMoca.Reader.Sigstore</c> package provides one.
    /// </summary>
    public ISigstoreVerifier? SigstoreVerifier { get; init; }

    /// <summary>The clock for trust-root key expiry.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}
