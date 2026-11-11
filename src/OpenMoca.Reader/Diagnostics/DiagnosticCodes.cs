namespace OpenMoca.Reader.Diagnostics;

/// <summary>
/// The diagnostic codes of Reader contract §11 and their default severities. Codes are the contract;
/// a change here without the matching spec change is a bug.
/// </summary>
public static class DiagnosticCodes
{
    // Target
    /// <summary>The target does not exist.</summary>
    public const string T001TargetNotFound = "T001_TARGET_NOT_FOUND";
    /// <summary>The target exists but has no moca.json at its root.</summary>
    public const string T002ManifestNotFound = "T002_MANIFEST_NOT_FOUND";
    /// <summary>An archive or host source broke a safety rule or limit.</summary>
    public const string T003ArchiveRejected = "T003_ARCHIVE_REJECTED";

    // Package entries and the digest
    /// <summary>The package contains a symbolic link or other non-regular file.</summary>
    public const string P001UnsupportedEntry = "P001_UNSUPPORTED_ENTRY";
    /// <summary>A package entry could not be read.</summary>
    public const string P002UnreadableEntry = "P002_UNREADABLE_ENTRY";
    /// <summary>Two package paths are equal after Unicode NFC normalisation or case folding.</summary>
    public const string P003PathCollision = "P003_PATH_COLLISION";

    // Manifest
    /// <summary>moca.json is not valid UTF-8 JSON.</summary>
    public const string M001ManifestInvalidJson = "M001_MANIFEST_INVALID_JSON";
    /// <summary>moca.json does not validate against the manifest schema.</summary>
    public const string M002ManifestSchema = "M002_MANIFEST_SCHEMA";
    /// <summary>The package has neither a content node nor a member.</summary>
    public const string M003NoContent = "M003_NO_CONTENT";
    /// <summary>A top-level manifest key is neither defined by the spec nor a vendor x-&lt;vendor&gt;-&lt;key&gt; key.</summary>
    public const string M004UnknownKey = "M004_UNKNOWN_KEY";
    /// <summary>The same member id appears more than once.</summary>
    public const string M005DuplicateMember = "M005_DUPLICATE_MEMBER";
    /// <summary>validUntil is not later than validFrom.</summary>
    public const string M006ValidityWindowInvalid = "M006_VALIDITY_WINDOW_INVALID";
    /// <summary>The package id uses a URN namespace that is not registered with IANA.</summary>
    public const string M007UnregisteredUrn = "M007_UNREGISTERED_URN";
    /// <summary>The package was written for a mocaVersion before 0.4; digests, pins and attestations made then will not match moca-digest-v2.</summary>
    public const string M008DigestV1Package = "M008_DIGEST_V1_PACKAGE";
    /// <summary>The package was written for a newer mocaVersion than this Reader implements; it is read on a best-effort basis.</summary>
    public const string M009MocaVersionNewer = "M009_MOCA_VERSION_NEWER";

    // Content (OKF bundle rules plus MOCA extensions)
    /// <summary>A concept document has no YAML frontmatter (OKF MUST).</summary>
    public const string C001FrontmatterMissing = "C001_FRONTMATTER_MISSING";
    /// <summary>Frontmatter is not parseable YAML or is not a mapping.</summary>
    public const string C002FrontmatterInvalid = "C002_FRONTMATTER_INVALID";
    /// <summary>Frontmatter has no non-empty type (OKF MUST).</summary>
    public const string C003TypeMissing = "C003_TYPE_MISSING";
    /// <summary>index.md or log.md breaks the OKF reserved-file rules.</summary>
    public const string C004ReservedFileInvalid = "C004_RESERVED_FILE_INVALID";
    /// <summary>A frontmatter field does not match the node schema.</summary>
    public const string C005FieldInvalid = "C005_FIELD_INVALID";
    /// <summary>moca.evidence names a source id that sources[] does not declare.</summary>
    public const string C006EvidenceSourceUnknown = "C006_EVIDENCE_SOURCE_UNKNOWN";
    /// <summary>An internal Markdown link does not resolve.</summary>
    public const string C007LinkUnresolved = "C007_LINK_UNRESOLVED";
    /// <summary>A package-relative path is absolute or escapes the package root.</summary>
    public const string C008UnsafePath = "C008_UNSAFE_PATH";
    /// <summary>A locale variant has no default-language file.</summary>
    public const string C009LocaleOrphan = "C009_LOCALE_ORPHAN";
    /// <summary>moca.valid_until is not later than moca.valid_from.</summary>
    public const string C010ValidityWindowInvalid = "C010_VALIDITY_WINDOW_INVALID";
    /// <summary>An evidence selector does not match the text source file it cites inside the package.</summary>
    public const string C011EvidenceSelectorUnmatched = "C011_EVIDENCE_SELECTOR_UNMATCHED";

    // Attestations
    /// <summary>An attestation file is not a parseable envelope and statement.</summary>
    public const string A001AttestationMalformed = "A001_ATTESTATION_MALFORMED";
    /// <summary>An attestation failed verification or does not match the package.</summary>
    public const string A002AttestationInvalid = "A002_ATTESTATION_INVALID";
    /// <summary>An attestation could not be checked because no trust root was supplied.</summary>
    public const string A003AttestationUnverifiable = "A003_ATTESTATION_UNVERIFIABLE";
    /// <summary>Online verification was requested but could not complete.</summary>
    public const string A004AttestationIndeterminate = "A004_ATTESTATION_INDETERMINATE";
    /// <summary>A review attestation covers a file whose bytes have since changed or been removed.</summary>
    public const string A006ReviewOutdated = "A006_REVIEW_OUTDATED";
    /// <summary>A review is dated later than the host clock, so it does not count.</summary>
    public const string A007ReviewDateFuture = "A007_REVIEW_DATE_FUTURE";
    /// <summary>A package attestation names this id and version with a different digest: the content changed after it was signed.</summary>
    public const string A008PackageDigestMismatch = "A008_PACKAGE_DIGEST_MISMATCH";
    /// <summary>The host requires a valid package attestation and the package has none.</summary>
    public const string A009SignatureRequired = "A009_SIGNATURE_REQUIRED";

    // Members
    /// <summary>A member could not be resolved by the host resolver.</summary>
    public const string R001MemberUnresolved = "R001_MEMBER_UNRESOLVED";
    /// <summary>A resolved member does not match its pinned digest.</summary>
    public const string R002MemberDigestMismatch = "R002_MEMBER_DIGEST_MISMATCH";
    /// <summary>Members form a cycle.</summary>
    public const string R003MemberCycle = "R003_MEMBER_CYCLE";
    /// <summary>A supersedes or conflictsWith relation is not authorised by a signer of the package, so it has no effect.</summary>
    public const string R004RelationUnauthorised = "R004_RELATION_UNAUTHORISED";

    // Library
    /// <summary>A library was given a second package with the same id and version but a different digest; the second is refused.</summary>
    public const string L001IdentityConflict = "L001_IDENTITY_CONFLICT";

    // Profiles
    /// <summary>A declared profile is not recognised by this reader.</summary>
    public const string F001ProfileUnrecognised = "F001_PROFILE_UNRECOGNISED";

    // Structure
    /// <summary>A structure or overlay file is over the size or triple limit, or is not parseable Turtle.</summary>
    public const string O001OntologyUnparseable = "O001_ONTOLOGY_UNPARSEABLE";
    /// <summary>A node is bound to a concept IRI that no ontology file in the package declares.</summary>
    public const string O002ConceptUndeclared = "O002_CONCEPT_UNDECLARED";
    /// <summary>An ontology file or concept binding relies on something a Reader would have to fetch or resolve: owl:imports, or an IRI that is not absolute.</summary>
    public const string O003RemoteReference = "O003_REMOTE_REFERENCE";
    /// <summary>requires, broader or hasPart forms a cycle.</summary>
    public const string O005StructureCycle = "O005_STRUCTURE_CYCLE";

    // Sidecar indexes
    /// <summary>index.json does not validate against the sidecar schema.</summary>
    public const string S001SidecarInvalid = "S001_SIDECAR_INVALID";
    /// <summary>The sidecar is bound to a different package id or version.</summary>
    public const string S002SidecarTargetMismatch = "S002_SIDECAR_TARGET_MISMATCH";
    /// <summary>The sidecar digest does not match the package; the sidecar is ignored.</summary>
    public const string S003SidecarStale = "S003_SIDECAR_STALE";
    /// <summary>A payload item breaks the addressing rules.</summary>
    public const string S004SidecarItemInvalid = "S004_SIDECAR_ITEM_INVALID";
    /// <summary>The payload format is not recognised; the sidecar is ignored.</summary>
    public const string S005SidecarFormatUnknown = "S005_SIDECAR_FORMAT_UNKNOWN";
    /// <summary>The host's embedder does not match the index's model; dense search is refused.</summary>
    public const string S006ModelMismatch = "S006_MODEL_MISMATCH";
    /// <summary>A loaded package has no vectors a dense backend can search; that package is not searched.</summary>
    public const string S007NoVectors = "S007_NO_VECTORS";

    private static readonly Dictionary<string, DiagnosticSeverity> Severities = new()
    {
        [T001TargetNotFound] = DiagnosticSeverity.Error,
        [T002ManifestNotFound] = DiagnosticSeverity.Error,
        [T003ArchiveRejected] = DiagnosticSeverity.Error,
        [P001UnsupportedEntry] = DiagnosticSeverity.Error,
        [P002UnreadableEntry] = DiagnosticSeverity.Error,
        [P003PathCollision] = DiagnosticSeverity.Error,
        [M001ManifestInvalidJson] = DiagnosticSeverity.Error,
        [M002ManifestSchema] = DiagnosticSeverity.Error,
        [M003NoContent] = DiagnosticSeverity.Error,
        [M004UnknownKey] = DiagnosticSeverity.Info,
        [M005DuplicateMember] = DiagnosticSeverity.Error,
        [M006ValidityWindowInvalid] = DiagnosticSeverity.Warning,
        [M007UnregisteredUrn] = DiagnosticSeverity.Info,
        [M008DigestV1Package] = DiagnosticSeverity.Info,
        [M009MocaVersionNewer] = DiagnosticSeverity.Warning,
        [C001FrontmatterMissing] = DiagnosticSeverity.Error,
        [C002FrontmatterInvalid] = DiagnosticSeverity.Error,
        [C003TypeMissing] = DiagnosticSeverity.Error,
        [C004ReservedFileInvalid] = DiagnosticSeverity.Error,
        [C005FieldInvalid] = DiagnosticSeverity.Warning,
        [C006EvidenceSourceUnknown] = DiagnosticSeverity.Warning,
        [C007LinkUnresolved] = DiagnosticSeverity.Warning,
        [C008UnsafePath] = DiagnosticSeverity.Error,
        [C009LocaleOrphan] = DiagnosticSeverity.Warning,
        [C010ValidityWindowInvalid] = DiagnosticSeverity.Warning,
        [C011EvidenceSelectorUnmatched] = DiagnosticSeverity.Warning,
        [A001AttestationMalformed] = DiagnosticSeverity.Error,
        [A002AttestationInvalid] = DiagnosticSeverity.Error,
        [A003AttestationUnverifiable] = DiagnosticSeverity.Warning,
        [A004AttestationIndeterminate] = DiagnosticSeverity.Warning,
        [A006ReviewOutdated] = DiagnosticSeverity.Warning,
        [A007ReviewDateFuture] = DiagnosticSeverity.Warning,
        [A008PackageDigestMismatch] = DiagnosticSeverity.Error,
        [A009SignatureRequired] = DiagnosticSeverity.Error,
        [R001MemberUnresolved] = DiagnosticSeverity.Warning,
        [R002MemberDigestMismatch] = DiagnosticSeverity.Error,
        [R003MemberCycle] = DiagnosticSeverity.Error,
        [R004RelationUnauthorised] = DiagnosticSeverity.Info,
        [L001IdentityConflict] = DiagnosticSeverity.Error,
        [F001ProfileUnrecognised] = DiagnosticSeverity.Info,
        [O001OntologyUnparseable] = DiagnosticSeverity.Warning,
        [O002ConceptUndeclared] = DiagnosticSeverity.Warning,
        [O003RemoteReference] = DiagnosticSeverity.Warning,
        [O005StructureCycle] = DiagnosticSeverity.Warning,
        [S001SidecarInvalid] = DiagnosticSeverity.Error,
        [S002SidecarTargetMismatch] = DiagnosticSeverity.Error,
        [S003SidecarStale] = DiagnosticSeverity.Warning,
        [S004SidecarItemInvalid] = DiagnosticSeverity.Error,
        [S005SidecarFormatUnknown] = DiagnosticSeverity.Info,
        [S006ModelMismatch] = DiagnosticSeverity.Warning,
        [S007NoVectors] = DiagnosticSeverity.Warning,
    };

    /// <summary>Every code this Reader can report.</summary>
    public static IReadOnlyCollection<string> All => Severities.Keys;

    /// <summary>The severity a code has outside strict mode.</summary>
    /// <exception cref="ArgumentException">The code is not in the contract.</exception>
    public static DiagnosticSeverity DefaultSeverity(string code) =>
        Severities.TryGetValue(code, out var severity)
            ? severity
            : throw new ArgumentException($"Unknown diagnostic code {code}.", nameof(code));

    /// <summary>
    /// True for the families that decide validity: an error in T, P, M or C makes a package invalid
    /// (Reader contract §11). Errors in other families only disable the feature concerned.
    /// </summary>
    public static bool IsInvalidating(string code) =>
        (code.Length > 0 && code[0] is 'T' or 'P' or 'M' or 'C') || code == A009SignatureRequired; // A009 only when the host requires a signature.
}
