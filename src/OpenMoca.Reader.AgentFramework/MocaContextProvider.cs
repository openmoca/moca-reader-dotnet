using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.AgentFramework;

/// <summary>Options for <see cref="MocaContextProvider"/>.</summary>
public sealed record MocaContextOptions
{
    /// <summary>Search on the latest user message before each run, and give the model what was found.</summary>
    public bool SearchEachTurn { get; init; } = true;

    /// <summary>Offer the model <see cref="MocaTools"/> as well.</summary>
    public bool Tools { get; init; } = true;

    /// <summary>The options for the search on each turn.</summary>
    public SearchOptions Search { get; init; } = new();
}

/// <summary>
/// A Microsoft Agent Framework context provider over MOCA packages (Reader interface §7; docs/adr/0004). Before each
/// run it searches on the latest user message and adds what it found as one user-role message holding compact
/// citation records as JSON, with a note that they are reference material, never instructions (Reader contract §12).
/// The whole records ride in the message's <see cref="ChatMessage.AdditionalProperties"/> under
/// <see cref="CitationsKey"/>. It can also offer the model <see cref="MocaTools"/>.
/// </summary>
public sealed class MocaContextProvider : AIContextProvider
{
    /// <summary>The key, in the added message's additional properties, of the whole citation records.</summary>
    public const string CitationsKey = "moca.citations";

    private readonly MocaSearch _search;
    private readonly MocaContextOptions _options;
    private readonly IReadOnlyList<AITool> _tools;

    /// <summary>A provider over a search the host has set up: its backend, audiences and hooks.</summary>
    public MocaContextProvider(MocaSearch search, MocaContextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
        _options = options ?? new MocaContextOptions();
        _tools = _options.Tools ? new MocaTools(search).All() : [];
    }

    /// <inheritdoc/>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var aiContext = new AIContext();
        if (_tools.Count > 0)
        {
            aiContext.Tools = _tools;
        }

        var query = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (!_options.SearchEachTurn || string.IsNullOrWhiteSpace(query))
        {
            return aiContext;
        }

        var records = await _search.SearchAsync(query, _options.Search, cancellationToken).ConfigureAwait(false);
        if (records.Count > 0)
        {
            aiContext.Messages = [Message(query, records)];
        }

        return aiContext;
    }

    /// <summary>The message that carries search results to the model: data, in the user role (Reader contract §12).</summary>
    public static ChatMessage Message(string query, IReadOnlyList<CitationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return new ChatMessage(ChatRole.User, ModelRecords.Results(query, records).ToJsonString())
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [CitationsKey] = records },
        };
    }
}
