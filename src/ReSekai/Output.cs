using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReSekai;

sealed class Output(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public Dictionary<string, (int Count, string Example)> Issues { get; } = [];
    public Dictionary<string, string> AssemblySources { get; } = [];
    public int Errors { get; private set; }
    public long Files { get; private set; }
    readonly HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
    public static string Guid(string identity) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    public static string Under(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"路径超出输出目录：{relative}");
        return full;
    }
    public static string Safe(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim().Trim('.').TrimEnd('~');
        if (safe.Length == 0) return "_";
        if (System.Text.RegularExpressions.Regex.IsMatch(safe.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) safe = "_" + safe;
        return safe.Length > 80 ? safe[..64] + "_" + Guid(text)[..8] : safe;
    }
    public void Write(string path, string content) => Write(path, new UTF8Encoding(false).GetBytes(content));
    public void Write(string path, byte[] content)
    {
        if (!written.Add(path)) throw new InvalidDataException($"重复输出路径：{path}");
        string full = Under(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        Files++;
    }
    public void Meta(string path, string guid, string importer, string fields = "", string bundle = "") => Write(path + ".meta",
        $"fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  externalObjects: {{}}\n{fields}  userData: \n  assetBundleName: {Yaml.Quote(bundle)}\n  assetBundleVariant: \n");
    public void Note(string kind, string origin, string reason, bool error = false)
    {
        string key = kind + ": " + reason;
        var existing = Issues.GetValueOrDefault(key);
        Issues[key] = (existing.Count + 1, existing.Example is null || string.CompareOrdinal(origin, existing.Example) < 0 ? origin : existing.Example);
        if (error) Errors++;
    }
    public void Finish()
    {
        foreach (string directory in written.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).SelectMany(Parents).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray())
            if (!written.Contains(directory + ".meta")) Write(directory + ".meta", $"fileFormatVersion: 2\nguid: {Guid("folder:" + directory)}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n");
        static IEnumerable<string> Parents(string path)
        {
            for (string? parent = Path.GetDirectoryName(path)?.Replace('\\', '/'); parent is not null && parent != "Assets"; parent = Path.GetDirectoryName(parent)?.Replace('\\', '/')) yield return parent;
        }
    }
    public void Report(string scripts, object? recovery = null)
    {
        string path = Under(Root, ".resekai/report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { scripts, files = Files, errors = Errors, unityImport = "未验证", recovery,
            assemblies = AssemblySources.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(),
            issues = Issues.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { reason = p.Key, count = p.Value.Count, example = p.Value.Example }) }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
