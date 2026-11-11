using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OpenMoca.Reader.Content;

/// <summary>The result of splitting a concept document into frontmatter and body.</summary>
/// <param name="Present">True when the file opens with a <c>---</c> line.</param>
/// <param name="Data">The parsed mapping, when present and valid.</param>
/// <param name="Error">Why the frontmatter is invalid (C002), when it is.</param>
/// <param name="Body">The Markdown body.</param>
/// <param name="BodyOffset">UTF-8 byte offset of the body in the file.</param>
/// <param name="Line">1-based line of the error, when known.</param>
public sealed record FrontmatterSplit(bool Present, JsonObject? Data, string? Error, string Body, int BodyOffset, int Line);

/// <summary>
/// YAML frontmatter for OKF concept documents, parsed with YAML 1.2 core-schema rules (Reader contract §5):
/// timestamps stay strings exactly as written, and aliases are refused (contract §3).
/// </summary>
public static partial class Frontmatter
{
    private const string Bom = "\ufeff";

    [GeneratedRegex(@"^---[ \t]*\r?\n")]
    private static partial Regex Open();

    // Package spec §5.1: only LF ends a line (CRLF is LF after CR).
    [GeneratedRegex(@"(?:^|(?<=\n))---[ \t]*(?:\r?\n|\z)")]
    private static partial Regex Close();

    /// <summary>Splits a file's text into frontmatter and body and parses the frontmatter.</summary>
    /// <param name="text">The whole file, decoded as UTF-8.</param>
    /// <param name="maxBytes">Frontmatter larger than this is refused (untrusted input).</param>
    public static FrontmatterSplit Split(string text, int maxBytes = int.MaxValue)
    {
        var bom = text.StartsWith(Bom, StringComparison.Ordinal) ? Bom : "";
        var rest = text[bom.Length..];
        var open = Open().Match(rest);
        if (!open.Success)
        {
            return new FrontmatterSplit(false, null, null, text, Encoding.UTF8.GetByteCount(bom), 1);
        }

        var afterOpen = rest[open.Length..];
        var close = Close().Match(afterOpen);
        if (!close.Success)
        {
            return new FrontmatterSplit(true, null, "frontmatter block is not closed with ---", text, 0, 1);
        }

        var raw = afterOpen[..close.Index];
        var consumedLength = bom.Length + open.Length + close.Index + close.Length;
        var body = text[consumedLength..];
        var bodyOffset = Encoding.UTF8.GetByteCount(text.AsSpan(0, consumedLength));
        if (Encoding.UTF8.GetByteCount(raw) > maxBytes)
        {
            return new FrontmatterSplit(true, null, $"frontmatter is larger than the {maxBytes}-byte limit", body, bodyOffset, 1);
        }

        if (UnicodeText.JsTrim(raw).Length == 0)
        {
            return new FrontmatterSplit(true, [], null, body, bodyOffset, 1);
        }

        try
        {
            var node = CoreSchemaYaml.Parse(raw);
            return node is JsonObject mapping
                ? new FrontmatterSplit(true, mapping, null, body, bodyOffset, 1)
                : new FrontmatterSplit(true, null, "frontmatter is not a YAML mapping", body, bodyOffset, 1);
        }
        catch (YamlException ex)
        {
            // +2: line 1 of the YAML is line 2 of the file, after the opening ---.
            var line = (int)ex.Start.Line + 1;
            return new FrontmatterSplit(true, null, ex.Message, body, bodyOffset, line);
        }
    }
}

/// <summary>
/// A small YAML 1.2 core-schema loader that produces <see cref="JsonNode"/> values. Plain scalars
/// resolve to null, booleans, integers and floats by the core schema's rules; everything else,
/// including anything that looks like a timestamp, stays a string.
/// </summary>
internal static partial class CoreSchemaYaml
{
    [GeneratedRegex("^(?:null|Null|NULL|~)?\\z")]
    private static partial Regex NullPattern();

    [GeneratedRegex("^(?:true|True|TRUE|false|False|FALSE)\\z")]
    private static partial Regex BoolPattern();

    [GeneratedRegex("^[-+]?[0-9]+\\z")]
    private static partial Regex IntDecimal();

    [GeneratedRegex("^0o[0-7]+\\z")]
    private static partial Regex IntOctal();

    [GeneratedRegex("^0x[0-9a-fA-F]+\\z")]
    private static partial Regex IntHex();

    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?\z")]
    private static partial Regex FloatPattern();

    [GeneratedRegex(@"^[-+]?\.(?:inf|Inf|INF)\z")]
    private static partial Regex InfPattern();

    [GeneratedRegex(@"^\.(?:nan|NaN|NAN)\z")]
    private static partial Regex NanPattern();

    public static JsonNode? Parse(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (parser.Accept<StreamEnd>(out _))
        {
            return null;
        }

        parser.Consume<DocumentStart>();
        var value = ReadNode(parser);
        parser.Consume<DocumentEnd>();
        if (!parser.Accept<StreamEnd>(out _))
        {
            var next = parser.Current!;
            throw new YamlException(next.Start, next.End, "expected a single document in the frontmatter");
        }

        return value;
    }

    private static JsonNode? ReadNode(IParser parser)
    {
        if (parser.TryConsume<AnchorAlias>(out var alias))
        {
            // Aliases are refused: OKF frontmatter never needs them, and alias expansion is a
            // denial-of-service vector (Reader contract §3, "YAML aliases in frontmatter: 0").
            throw new YamlException(alias.Start, alias.End, "YAML aliases are not allowed in frontmatter");
        }

        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return ResolveScalar(scalar);
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var array = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                array.Add(ReadNode(parser));
            }

            return array;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            var mapping = new JsonObject();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var keyEvent = parser.Current!;
                var key = ReadNode(parser);
                var keyText = key switch
                {
                    null => "null",
                    JsonValue v => v.ToJsonString().Trim('"'),
                    _ => throw new YamlException(keyEvent.Start, keyEvent.End, "mapping keys must be scalars"),
                };
                if (key is JsonValue sv && sv.TryGetValue<string>(out var s))
                {
                    keyText = s;
                }

                if (mapping.ContainsKey(keyText))
                {
                    throw new YamlException(keyEvent.Start, keyEvent.End, $"duplicated mapping key \"{keyText}\"");
                }

                mapping[keyText] = ReadNode(parser);
            }

            return mapping;
        }

        var unexpected = parser.Current!;
        throw new YamlException(unexpected.Start, unexpected.End, $"unexpected YAML event {unexpected.GetType().Name}");
    }

    private static JsonNode? ResolveScalar(Scalar scalar)
    {
        var value = scalar.Value;
        // Quoted and block scalars, and explicitly tagged strings, are always strings.
        if (scalar.Style != ScalarStyle.Plain || (!scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:str"))
        {
            return JsonValue.Create(value);
        }

        if (NullPattern().IsMatch(value))
        {
            return null;
        }

        if (BoolPattern().IsMatch(value))
        {
            return JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (IntDecimal().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (IntOctal().IsMatch(value))
        {
            return JsonValue.Create(Convert.ToInt64(value[2..], 8));
        }

        if (IntHex().IsMatch(value))
        {
            return JsonValue.Create(Convert.ToInt64(value[2..], 16));
        }

        if (FloatPattern().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        // JSON has no infinity or NaN; keep them as their text rather than failing the node.
        if (InfPattern().IsMatch(value) || NanPattern().IsMatch(value))
        {
            return JsonValue.Create(value);
        }

        return JsonValue.Create(value);
    }
}
