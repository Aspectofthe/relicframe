using System.Text.Json;
using RelicFrame.Core;

if (args.Length == 3 && args[0] == "companion-folder")
{
    var archiveSummary = CompanionChatArchive.Build(args[1], args[2]);
    Console.WriteLine(JsonSerializer.Serialize(archiveSummary, Json.Options));
    Console.WriteLine("Portable local chat archive created. Source files were not changed.");
    return 0;
}
if (args.Length < 3 || args[0] != "companion-export")
{
    Console.WriteLine("Usage: RelicFrame.Tools companion-export <output-directory> <export.html|export.json> [...]");
    Console.WriteLine("       RelicFrame.Tools companion-folder <TXT/image input-directory> <output-directory>");
    Console.WriteLine("Reads originals without modifying them; outputs messages and companion price-evidence JSONL.");
    return 2;
}

var output = Path.GetFullPath(args[1]); var sources = args.Skip(2).Select(Path.GetFullPath).ToArray();
if (sources.Any(path => !File.Exists(path))) throw new FileNotFoundException("One or more export files do not exist.");
var summary = CompanionExports.BuildEvidence(sources, output);
Console.WriteLine(JsonSerializer.Serialize(summary, Json.Options));
Console.WriteLine("Private output was not added to Git. Review price_evidence_deduplicated.jsonl before using it for appraisals.");
return 0;
