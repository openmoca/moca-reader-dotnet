using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Attestations;

/// <summary>
/// Verifies a Sigstore bundle offline against a host-supplied trusted root (attestations §6, §7). The Reader
/// itself verifies only DSSE; a host that wants Sigstore adds the <c>OpenMoca.Reader.Sigstore</c> package and sets
/// <see cref="ReadOptions.SigstoreVerifier"/>. The verifier checks the cryptography and returns the certificate's
/// identity; the Reader matches that identity against the trust root.
/// </summary>
public interface ISigstoreVerifier
{
    /// <summary>
    /// Checks the bundle's certificate chain, certificate-transparency proof, transparency-log entry and DSSE
    /// signature against <paramref name="trustedRoot"/>, offline. Never throws for a malformed bundle or root.
    /// </summary>
    /// <param name="bundle">The <c>*.sigstore.json</c> document.</param>
    /// <param name="trustedRoot">The trust root's <c>sigstore.trustedRoot</c>.</param>
    SigstoreVerification Verify(JsonObject bundle, JsonObject trustedRoot);
}

/// <summary>What a Sigstore verifier concluded.</summary>
/// <param name="Outcome"><c>valid</c>; <c>invalid</c>; or <c>unverifiable</c> when the trusted root cannot be used.</param>
/// <param name="Reason">Why, when not valid.</param>
/// <param name="Issuer">The certificate's OIDC issuer, when valid.</param>
/// <param name="SubjectAlternativeName">The certificate's subject alternative name (email or URI), when valid.</param>
public sealed record SigstoreVerification(string Outcome, string? Reason, string? Issuer = null, string? SubjectAlternativeName = null)
{
    /// <summary>A bundle that does not verify.</summary>
    public static SigstoreVerification Invalid(string reason) => new("invalid", reason);
}
