using System.Security.Cryptography;
using BattleScribeSpec.NewRecruit;

var baseUrl = "https://www.newrecruit.eu";
var outputDir = ".testdata/newrecruit-har";
var headless = true;

// Parse args
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" when i + 1 < args.Length:
            baseUrl = args[++i];
            break;
        case "--output" or "-o" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "--headed":
            headless = false;
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

Directory.CreateDirectory(outputDir);
var harPath = Path.Combine(outputDir, "newrecruit.har");
var metadataPath = Path.Combine(outputDir, "metadata.json");

Console.WriteLine($"Recording HAR from {baseUrl}...");
Console.WriteLine($"Output: {Path.GetFullPath(outputDir)}");
Console.WriteLine($"Headless: {headless}");
Console.WriteLine();

await HarRecorder.RecordAsync(harPath, metadataPath, baseUrl, headless);

var harSize = new FileInfo(harPath).Length;
var harHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(harPath)));
var clientVersion = HarRecorder.ExtractClientVersion(harPath);

Console.WriteLine($"HAR recorded: {harPath} ({harSize / 1024} KB)");
Console.WriteLine($"SHA256: {harHash}");
Console.WriteLine($"NR client version: {clientVersion ?? "unknown"}");
Console.WriteLine($"Metadata: {metadataPath}");
Console.WriteLine();

if (clientVersion is not null)
{
    Console.WriteLine("To publish as a release:");
    Console.WriteLine($"  gh release create v{clientVersion} {harPath} {metadataPath} -R WarHub/newrecruit-har --title \"NR snapshot v{clientVersion}\"");
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        bs-nr-har-tool — Record frozen HAR snapshots of newrecruit.eu

        Usage: dotnet run -- [options]

        Options:
          --url <url>       Base URL to record (default: https://www.newrecruit.eu)
          -o, --output <dir>  Output directory (default: .testdata/newrecruit-har)
          --headed          Run browser in headed mode (visible)
          -h, --help        Show this help
        """);
}
