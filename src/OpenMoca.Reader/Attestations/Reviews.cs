namespace OpenMoca.Reader.Attestations;

/// <summary>Review status (package spec §11, Reader contract §7, moca-spec ADR-0017).</summary>
public static class Reviews
{
    /// <summary>
    /// <c>none</c>; <c>unverified</c> (reviews in an unsigned package); <c>approved</c> (signed, an accurate review
    /// and none against); or <c>disputed</c> (signed, and a current review says needs-change or inaccurate).
    /// </summary>
    public static string StatusOf(IReadOnlyCollection<AttestedReview>? reviews, bool signed)
    {
        if (reviews is null || reviews.Count == 0)
        {
            return "none";
        }

        if (!signed)
        {
            return "unverified";
        }

        return reviews.Any(r => r.Outcome != "accurate") ? "disputed" : "approved";
    }
}
