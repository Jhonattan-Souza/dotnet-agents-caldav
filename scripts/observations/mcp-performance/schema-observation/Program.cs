using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: SchemaObservation <assembly-directory> <page-json> <tool-name> <build-manifest>");
    return 64;
}

// Run a copy of this project outside the repository. Resolve the exact build's
// dependencies and invoke its unmodified internal guard on a real MCP result.
var directory = Path.GetFullPath(args[0]);
var payload = File.ReadAllBytes(args[1]);
using var manifest = JsonDocument.Parse(File.ReadAllBytes(args[3]));
var expectedFiles = JsonSerializer.Deserialize<Dictionary<string, string>>(
    manifest.RootElement.GetProperty("runtime_files_sha256").GetRawText())!;
string RuntimePath(string name)
{
    var path = Path.GetFullPath(Path.Combine(directory, name));
    var relative = Path.GetRelativePath(directory, path);
    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException("Runtime manifest path escapes its assembly directory.");
    return path;
}
Dictionary<string, string> RuntimeFiles() => expectedFiles.Keys.OrderBy(name => name, StringComparer.Ordinal)
    .ToDictionary(name => name, name => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RuntimePath(name)))), StringComparer.Ordinal);
bool SameFiles(Dictionary<string, string> left, Dictionary<string, string> right) =>
    left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);
var runtimeFiles = RuntimeFiles();
if (!SameFiles(runtimeFiles, expectedFiles))
    throw new InvalidOperationException("Schema observation runtime differs from the prepared build.");
AssemblyLoadContext.Default.Resolving += (_, name) =>
    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
var assemblyPath = Path.Combine(directory, "DotnetAgents.CalDav.Mcp.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var method = assembly.GetType("DotnetAgents.CalDav.Mcp.Hosting.CalendarOutputSchemaGuard", true)!
    .GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
var resultType = method.GetParameters()[1].ParameterType;
var result = Activator.CreateInstance(resultType)!;
using var document = JsonDocument.Parse(payload);
resultType.GetProperty("StructuredContent")!.SetValue(result, document.RootElement.Clone());
object?[] parameters = [args[2], result];
for (var i = 0; i < 20; i++)
    method.Invoke(null, parameters);
var samples = new List<object>();
for (var i = 0; i < 100; i++)
{
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var gen0 = GC.CollectionCount(0);
    var gen1 = GC.CollectionCount(1);
    var gen2 = GC.CollectionCount(2);
    var start = Stopwatch.GetTimestamp();
    method.Invoke(null, parameters);
    var elapsed = Stopwatch.GetElapsedTime(start);
    samples.Add(new
    {
        elapsedMilliseconds = elapsed.TotalMilliseconds,
        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
        gen0 = GC.CollectionCount(0) - gen0,
        gen1 = GC.CollectionCount(1) - gen1,
        gen2 = GC.CollectionCount(2) - gen2
    });
}
if (!SameFiles(runtimeFiles, RuntimeFiles()))
    throw new InvalidOperationException("Runtime files changed during schema observation.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = Environment.Version.ToString(),
    assemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
    runtimeFilesSha256 = runtimeFiles,
    payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
    tool = args[2],
    boundary = "CalendarOutputSchemaGuard.Validate via reflection; no transport; current-thread allocations",
    samples
}));
return 0;
