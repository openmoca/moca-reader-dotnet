using System.Text.Json.Nodes;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Search;

/// <summary>
/// A host's terms request for one search (Reader contract §15.4, Reader interface §5.1). With one, the terms gate also
/// removes records the creator's terms do not allow, and each record carries its resolved terms. Without one, terms
/// remove nothing.
/// </summary>
public sealed record TermsRequest
{
    /// <summary>
    /// What a Reader that hands content to a model applies when its host configures nothing (Reader contract §15.4): the
    /// destination <c>provider</c>, with no conditions.
    /// </summary>
    public static TermsRequest Provider { get; } = new() { Ai = new AiRequest(TermsDestinations.Provider) };

    /// <summary>Where the records are going and what for; null to apply no <c>ai</c> terms.</summary>
    public AiRequest? Ai { get; init; }

    /// <summary>A rating filter; null to apply none.</summary>
    public RatingRequest? Rating { get; init; }

    /// <summary>
    /// A request from its JSON form, <c>{ "ai": { destination, conditions, use }, "rating": { ... } }</c>. A rating
    /// request this version cannot honour is kept as one that removes every record (<see cref="RatingRequest.FromJson"/>).
    /// </summary>
    public static TermsRequest FromJson(JsonNode? json)
    {
        var o = json as JsonObject;
        return new TermsRequest
        {
            Ai = o?["ai"] is JsonObject ai ? AiRequest.FromJson(ai) : null,
            Rating = o is not null && o.TryGetPropertyValue("rating", out var rating) ? RatingRequest.FromJson(rating) : null,
        };
    }
}

/// <summary>The <c>ai</c> part of a terms request (Reader contract §15.4).</summary>
/// <param name="Destination">
/// <c>device</c>, <c>host</c> or <c>provider</c> (see <see cref="TermsDestinations"/>). Anything else is treated as the
/// strictest, <c>provider</c>.
/// </param>
/// <param name="Conditions">The provider conditions the host meets: <c>noTraining</c>, <c>noRetention</c>.</param>
/// <param name="Use"><c>answer</c>, <c>embed</c>, <c>summarise</c>, <c>train</c> or <c>translate</c>; null for none.</param>
public sealed record AiRequest(string Destination = TermsDestinations.Provider, IReadOnlyList<string>? Conditions = null, string? Use = null)
{
    /// <summary>An <c>ai</c> request from its JSON form.</summary>
    public static AiRequest FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new AiRequest(
            JsonValues.StringOf(json["destination"]) ?? TermsDestinations.Provider,
            json["conditions"] is JsonArray conditions ? [.. conditions.Select(JsonValues.StringOf).OfType<string>()] : null,
            JsonValues.StringOf(json["use"]));
    }
}

/// <summary>The destinations of Reader contract §15.4.</summary>
public static class TermsDestinations
{
    /// <summary>A model on the person's own device.</summary>
    public const string Device = "device";

    /// <summary>A model the host runs itself, under its own terms.</summary>
    public const string Host = "host";

    /// <summary>Any other party's model. The default for a Reader that hands content to a model and has no configuration.</summary>
    public const string Provider = "provider";
}

/// <summary>
/// The <c>rating</c> part of a terms request (Reader contract §15.4). <c>typicalAgeRange</c> never removes anything.
/// </summary>
public sealed record RatingRequest
{
    /// <summary>False: remove records whose <c>explicit</c> counts and is true. Null or true: no such check.</summary>
    public bool? Explicit { get; init; }

    /// <summary>
    /// The allowed values per content-rating scheme. A record is rated, for this filter, only by an <c>explicit</c> that
    /// counts or by an entry under a scheme listed here. A null list allows no value.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>?>? Schemes { get; init; }

    /// <summary>Keep unrated records.</summary>
    public bool AllowUnrated { get; init; }

    /// <summary>Let a feed's <c>explicit: false</c> count.</summary>
    public bool AcceptFeedRatings { get; init; }

    /// <summary>
    /// True for a request this version cannot honour in full (not an object, a member it does not define, or a
    /// <c>schemes</c> that is not an object). It is never run as a weaker filter: it removes every record.
    /// </summary>
    public bool Unhonourable { get; init; }

    private static readonly HashSet<string> Members = new(StringComparer.Ordinal) { "explicit", "schemes", "allowUnrated", "acceptFeedRatings" };

    /// <summary>A rating request from its JSON form; see <see cref="Unhonourable"/> for what it cannot honour.</summary>
    public static RatingRequest FromJson(JsonNode? json)
    {
        // Contract §15.4: a request that is not an object, has a member this version does not define, or whose schemes
        // is not an object removes every record.
        if (json is not JsonObject o || o.Any(kv => !Members.Contains(kv.Key))
            || (o.TryGetPropertyValue("schemes", out var s) && s is not JsonObject))
        {
            return new RatingRequest { Unhonourable = true };
        }

        Dictionary<string, IReadOnlyList<string>?>? schemes = null;
        if (o["schemes"] is JsonObject listed)
        {
            schemes = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            foreach (var (scheme, allowed) in listed)
            {
                // A scheme whose allowed set is not a list allows no value.
                schemes[scheme] = allowed is JsonArray values ? [.. values.Select(JsonValues.StringOf).OfType<string>()] : null;
            }
        }

        return new RatingRequest
        {
            Explicit = o["explicit"] is JsonValue e && e.TryGetValue<bool>(out var b) ? b : null,
            Schemes = schemes,
            AllowUnrated = o["allowUnrated"] is JsonValue u && u.TryGetValue<bool>(out var ub) && ub,
            AcceptFeedRatings = o["acceptFeedRatings"] is JsonValue f && f.TryGetValue<bool>(out var fb) && fb,
        };
    }
}

/// <summary>
/// The host's terms limits (Reader contract §15.3 step 4): floors, never values. Each applies only when it is stricter
/// than what the platform, the package, its nodes and composing packages give. A limit can never loosen anything.
/// </summary>
public sealed record TermsLimits
{
    /// <summary><c>ai.allowed.device</c>.</summary>
    public bool? Device { get; init; }

    /// <summary><c>ai.allowed.host</c>.</summary>
    public bool? Host { get; init; }

    /// <summary><c>ai.allowed.provider</c>.</summary>
    public bool? Provider { get; init; }

    /// <summary><c>ai.allowed.training</c>.</summary>
    public bool? Training { get; init; }

    /// <summary><c>ai.allowed.machineTranslation</c>.</summary>
    public bool? MachineTranslation { get; init; }

    /// <summary>
    /// <c>ai.allowed.mention</c>: false hides every package closed for a request from the model (moca-spec ADR-0041).
    /// </summary>
    public bool? Mention { get; init; }

    /// <summary><c>ai.answerSources</c>: <c>work</c> or <c>model</c>.</summary>
    public string? AnswerSources { get; init; }

    /// <summary><c>ai.providerRequires</c>: conditions every provider must meet.</summary>
    public IReadOnlyList<string>? ProviderRequires { get; init; }

    /// <summary>Limits from their JSON form, shaped as <c>terms.ai</c>.</summary>
    public static TermsLimits FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var allowed = json["allowed"] as JsonObject;
        static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        return new TermsLimits
        {
            Device = Bool(allowed?["device"]),
            Host = Bool(allowed?["host"]),
            Provider = Bool(allowed?["provider"]),
            Training = Bool(allowed?["training"]),
            MachineTranslation = Bool(allowed?["machineTranslation"]),
            Mention = Bool(allowed?["mention"]),
            AnswerSources = JsonValues.StringOf(json["answerSources"]),
            ProviderRequires = json["providerRequires"] is JsonArray p ? [.. p.Select(JsonValues.StringOf).OfType<string>()] : null,
        };
    }

    /// <summary>The limits in the shape of <c>terms.ai</c>, for resolution.</summary>
    internal JsonObject ToAi()
    {
        var allowed = new JsonObject();
        void Put(string key, bool? value)
        {
            if (value is { } v)
            {
                allowed[key] = v;
            }
        }

        Put("device", Device);
        Put("host", Host);
        Put("provider", Provider);
        Put("training", Training);
        Put("machineTranslation", MachineTranslation);
        Put("mention", Mention);
        var ai = new JsonObject { ["allowed"] = allowed };
        if (AnswerSources is not null)
        {
            ai["answerSources"] = AnswerSources;
        }

        if (ProviderRequires is not null)
        {
            ai["providerRequires"] = new JsonArray([.. ProviderRequires.Select(c => (JsonNode)JsonValue.Create(c))]);
        }

        return ai;
    }
}
