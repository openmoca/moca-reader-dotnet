using OpenMoca.Reader.Json;
// moca-spec's runner protocol (conformance/README.md): one JSON object { root, case } on stdin,
// one JSON result on stdout.
//
//   node ../moca-spec/scripts/run-conformance.mjs --reader "dotnet run --project tests/OpenMoca.Reader.ConformanceAdapter"
using System.Text.Json.Nodes;
using OpenMoca.Reader.ConformanceAdapter;
using OpenMoca.Reader.Structure;

var input = JsonNode.Parse(await Console.In.ReadToEndAsync()) as JsonObject
    ?? throw new InvalidOperationException("expected a JSON object { root, case } on standard input");
var root = input["root"]!.GetValue<string>();
var output = await ConformanceCases.ActualForAsync(root, (JsonObject)input["case"]!);
Console.Out.Write(output.ToJsonString(JsonValues.CompactJson));
return 0;
