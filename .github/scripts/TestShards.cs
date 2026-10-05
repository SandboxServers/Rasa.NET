// Splits the test classes in a test assembly across CI shards, one test-host process per shard.
//
//   dotnet run .github/scripts/TestShards.cs -- <Rasa.Test.dll> <shard count> <output dir> [timings dir]
//
// Writes <output dir>/shard-<i>.filter, a `dotnet test --filter` expression for each shard. Classes
// are found in the compiled assembly (every non-abstract type marked [TestClass]) and dealt out
// slowest first, each to the shard with the least work so far. A class's work is its total duration
// in the .trx files under [timings dir] (the results of an earlier run), or, for a class those files
// don't cover, its number of test cases times the average seconds per case.
//
// Every shard but the last lists its classes; the last runs everything the others don't list. So a
// class this script fails to find, or one added since, still runs exactly once, in the last shard.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: TestShards.cs <assembly> <shard count> <output dir> [timings dir]");
    return 2;
}

var assemblyPath = args[0];
var shardCount = int.Parse(args[1]);
var outputDir = args[2];
string? timingsDir = args.Length > 3 ? args[3] : null;

if (shardCount < 1)
{
    Console.Error.WriteLine("shard count must be at least 1");
    return 2;
}

var cases = FindTestClasses(assemblyPath);
if (cases.Count == 0)
{
    Console.Error.WriteLine($"no [TestClass] types found in {assemblyPath}");
    return 1;
}

var timings = ReadTimings(timingsDir);
var timedCases = cases.Where(c => timings.ContainsKey(c.Key)).Sum(c => c.Value);
var secondsPerCase = timedCases > 0
    ? cases.Keys.Where(timings.ContainsKey).Sum(c => timings[c]) / timedCases
    : 1.0;
var weights = cases.ToDictionary(c => c.Key,
    c => timings.TryGetValue(c.Key, out var seconds) ? seconds : c.Value * secondsPerCase);

var shards = Enumerable.Range(0, shardCount).Select(_ => new List<string>()).ToArray();
var load = new double[shardCount];
foreach (var name in weights.Keys.OrderByDescending(n => weights[n]).ThenBy(n => n, StringComparer.Ordinal))
{
    var target = Array.IndexOf(load, load.Min());
    shards[target].Add(name);
    load[target] += weights[name];
}

Directory.CreateDirectory(outputDir);
var listed = shards.Take(shardCount - 1).SelectMany(s => s).OrderBy(n => n, StringComparer.Ordinal).ToList();
for (var i = 0; i < shardCount; i++)
{
    string filter;
    if (i < shardCount - 1)
        filter = shards[i].Count > 0
            ? string.Join("|", shards[i].Select(n => $"ClassName={n}"))
            : "ClassName=__no_class_in_this_shard__";
    else
        filter = listed.Count > 0
            ? string.Join("&", listed.Select(n => $"ClassName!={n}"))
            : "FullyQualifiedName!=__run_everything__";
    File.WriteAllText(Path.Combine(outputDir, $"shard-{i}.filter"), filter);
}

var source = timings.Count > 0
    ? $"durations from {timings.Count} classes in earlier results; {cases.Count - cases.Keys.Count(timings.ContainsKey)} estimated from case counts"
    : "case counts (no earlier results)";
var report = new List<string>
{
    $"Sharded {cases.Count} test classes ({cases.Values.Sum()} cases) across {shardCount} shards, weighted by {source}.",
    "",
    "| Shard | Classes | Planned work |",
    "|---|---|---|",
};
for (var i = 0; i < shardCount; i++)
    report.Add($"| {i} | {shards[i].Count} | {(timings.Count > 0 ? $"{load[i] / 60:0.0} min" : $"{load[i]:0} cases")} |");
foreach (var line in report)
    Console.WriteLine(line);
var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(summary))
    File.AppendAllLines(summary, report.Prepend("### Test shards").Append(""));
return 0;

// Test class full name -> number of test cases, counting each [DataRow] as one case.
static Dictionary<string, int> FindTestClasses(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var result = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var handle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(handle);
        if ((type.Attributes & TypeAttributes.Abstract) != 0 || !HasAttribute(md, type.GetCustomAttributes(), "TestClassAttribute"))
            continue;

        var count = 0;
        foreach (var methodHandle in type.GetMethods())
        {
            var attributes = md.GetMethodDefinition(methodHandle).GetCustomAttributes();
            if (!HasAttribute(md, attributes, "TestMethodAttribute") && !HasAttribute(md, attributes, "DataTestMethodAttribute"))
                continue;
            count += Math.Max(1, attributes.Count(a => AttributeName(md, md.GetCustomAttribute(a)) == "DataRowAttribute"));
        }

        if (count > 0)
            result[FullName(md, type)] = count;
    }

    return result;
}

static bool HasAttribute(MetadataReader md, CustomAttributeHandleCollection attributes, string name) =>
    attributes.Any(a => AttributeName(md, md.GetCustomAttribute(a)) == name);

static string? AttributeName(MetadataReader md, CustomAttribute attribute)
{
    switch (attribute.Constructor.Kind)
    {
        case HandleKind.MemberReference:
            var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            return parent.Kind == HandleKind.TypeReference
                ? md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name)
                : null;
        case HandleKind.MethodDefinition:
            var declaring = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
            return md.GetString(md.GetTypeDefinition(declaring).Name);
        default:
            return null;
    }
}

static string FullName(MetadataReader md, TypeDefinition type)
{
    var name = md.GetString(type.Name);
    var declaring = type.GetDeclaringType();
    if (!declaring.IsNil)
        return FullName(md, md.GetTypeDefinition(declaring)) + "+" + name;
    var ns = md.GetString(type.Namespace);
    return ns.Length > 0 ? ns + "." + name : name;
}

// Test class full name -> total seconds, from every .trx file under the directory.
static Dictionary<string, double> ReadTimings(string? dir)
{
    var result = new Dictionary<string, double>(StringComparer.Ordinal);
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        return result;

    XNamespace t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    foreach (var file in Directory.EnumerateFiles(dir, "*.trx", SearchOption.AllDirectories))
    {
        var doc = XDocument.Load(file);
        var classOf = doc.Descendants(t + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => (string?)u.Element(t + "TestMethod")?.Attribute("className"));
        foreach (var r in doc.Descendants(t + "UnitTestResult"))
        {
            if (!classOf.TryGetValue((string?)r.Attribute("testId") ?? "", out var cls) || cls == null)
                continue;
            if (TimeSpan.TryParse((string?)r.Attribute("duration"), out var duration))
                result[cls] = result.GetValueOrDefault(cls) + duration.TotalSeconds;
        }
    }

    return result;
}
