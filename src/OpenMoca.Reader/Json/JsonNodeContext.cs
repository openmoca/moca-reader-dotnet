using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenMoca.Reader.Json;

[JsonSerializable(typeof(JsonNode))]
internal sealed partial class JsonNodeContext : JsonSerializerContext;
