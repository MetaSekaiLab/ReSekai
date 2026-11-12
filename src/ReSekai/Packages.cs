using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReSekai.Decompiler.Model;

namespace ReSekai;

sealed class Packages
{
    public Dictionary<string, string> Assemblies { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Dlls { get; } = [];
    public Dictionary<string, (string Guid, long Id)> Scripts { get; } = [];
    public Dictionary<string, string> Shaders { get; } = [];
    public Dictionary<string, string> Versions { get; } = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonElement> catalog;
    readonly JsonElement[] scriptMap;

    public Packages()
    {
        using var data = Schemas.Data("upm.json.gz");
        catalog = data.RootElement.GetProperty("packages").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.Clone());
        scriptMap = data.RootElement.GetProperty("scripts").EnumerateArray().Select(p => p.Clone()).ToArray();
    }

    public async Task Prepare(Options options, ProgramModel model, Output output)
    {
        if (options.Upm == "none") return;
        bool automatic = options.Upm == "auto";
        var input = model.Assemblies.Select(a => a.AssemblyName).ToHashSet();
        string[] selected = automatic ? catalog.Where(p => !Modified(p.Value) && p.Value.GetProperty("assemblies").EnumerateArray().Any(a => input.Contains(a.GetString()!))).Select(p => p.Key).ToArray()
            : options.Upm.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in selected)
            if (!catalog.ContainsKey(name) || Modified(catalog[name])) throw new ArgumentException($"没有适配的原版包：{name}");
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
        Dictionary<string, string> available = [];
        foreach (string root in selected.Order(StringComparer.Ordinal))
        {
            Dictionary<string, string> closure = [];
            try { await Resolve(root, catalog[root].GetProperty("version").GetString()!, closure); }
            catch (Exception error) when (automatic && error is HttpRequestException or TaskCanceledException or InvalidDataException or DirectoryNotFoundException)
            {
                output.Note("UPM", root, $"改为自行生成代码：{error.Message}");
                continue;
            }
            foreach (var pair in closure)
            {
                Versions[pair.Key] = Versions.TryGetValue(pair.Key, out string? version) ? Higher(version, pair.Value) : pair.Value;
            }
        }
        foreach (var pair in Versions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string source = available[pair.Key + "@" + pair.Value];
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (file.EndsWith(".asmdef", StringComparison.Ordinal)) output.Write($"Packages/{pair.Key}/{relative}", RuntimeDefines(File.ReadAllText(file), input));
                else output.Write($"Packages/{pair.Key}/{relative}", File.ReadAllBytes(file));
                if (relative.Split('/').Any(p => p.StartsWith('.') || p.EndsWith('~'))) continue;
                if (file.EndsWith(".asmdef", StringComparison.Ordinal))
                {
                    using var asm = JsonDocument.Parse(File.ReadAllText(file));
                    Register(asm.RootElement.GetProperty("name").GetString()!, pair.Key);
                }
                else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    // 原生插件没有 CLI 头，不作为托管程序集登记。
                    using var stream = File.OpenRead(file);
                    if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') continue;
                    stream.Position = 0;
                    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                    if (pe.HasMetadata)
                    {
                        using var module = Mono.Cecil.ModuleDefinition.ReadModule(file);
                        Register(module.Assembly.Name.Name, pair.Key);
                        Dlls.Add(module.Assembly.Name.Name);
                    }
                }
                else if (file.EndsWith(".shader", StringComparison.Ordinal) && File.Exists(file + ".meta"))
                {
                    string name = Regex.Match(File.ReadAllText(file), "Shader\\s+\"([^\"]+)\"").Groups[1].Value;
                    string guid = Regex.Match(File.ReadAllText(file + ".meta"), @"(?m)^guid: ([0-9a-f]{32})").Groups[1].Value;
                    if (name.Length > 0 && guid.Length == 32) Shaders.TryAdd(name, guid);
                }
            }
        }
        foreach (var entry in scriptMap.Where(s => Assemblies.ContainsKey(s.GetProperty("assembly").GetString()!)))
        {
            var script = new ScriptName(entry.GetProperty("assembly").GetString()!, entry.GetProperty("namespace").GetString()!, entry.GetProperty("name").GetString()!);
            Scripts[script.Key] = (entry.GetProperty("guid").GetString()!, entry.GetProperty("fileId").GetInt64());
        }
        void Register(string assembly, string package)
        {
            if (Assemblies.TryGetValue(assembly, out string? owner) && owner != package) throw new InvalidDataException($"包重复提供程序集：{assembly}");
            Assemblies[assembly] = package;
        }
        async Task Resolve(string name, string version, Dictionary<string, string> closure)
        {
            if (name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) return;
            if (!Regex.IsMatch(name, @"^com\.unity\.[a-z0-9.-]+$") || !Regex.IsMatch(version, @"^[0-9][a-zA-Z0-9.+-]*$")) throw new InvalidDataException($"未支持的包依赖：{name}@{version}");
            if (catalog.TryGetValue(name, out var known))
            {
                if (Modified(known)) throw new InvalidDataException($"依赖 {name} 包含游戏修改，不能用原版替换");
                version = known.GetProperty("version").GetString()!;
            }
            if (Versions.TryGetValue(name, out var installed)) version = Higher(installed, version);
            if (closure.TryGetValue(name, out var previous))
            {
                version = Higher(previous, version);
                if (previous == version) return;
            }
            closure[name] = version;
            string key = name + "@" + version;
            if (!available.TryGetValue(key, out string? directory))
            {
                directory = Path.Join(output.Root, ".resekai", "packages", key);
                string? editor = EditorPath();
                string? builtin = editor is null ? null : Path.Join(Path.GetDirectoryName(editor), "Data", "Resources", "PackageManager", "BuiltInPackages", name);
                string cached = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "cache", "packages", "packages.unity.com", key);
                if (builtin is not null && File.Exists(Path.Join(builtin, "package.json"))) directory = builtin;
                else if (File.Exists(Path.Join(cached, "package.json"))) directory = cached;
                else
                {
                    Console.WriteLine($"下载包 {name}@{version}");
                    byte[] archive = await client.GetByteArrayAsync($"https://packages.unity.com/{name}/-/{name}-{version}.tgz");
                    using GZipStream gzip = new(new MemoryStream(archive), CompressionMode.Decompress);
                    using TarReader tar = new(gzip);
                    while (tar.GetNextEntry() is { } entry)
                    {
                        if (entry.EntryType == TarEntryType.Directory) continue;
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !entry.Name.StartsWith("package/", StringComparison.Ordinal))
                            throw new InvalidDataException($"包条目无效：{entry.Name}");
                        string destination = Output.Under(directory, entry.Name[8..]);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        using var file = File.Create(destination);
                        if (entry.DataStream is { } content) content.CopyTo(file);
                        else if (entry.Length != 0) throw new InvalidDataException($"包文件缺少内容：{entry.Name}");
                    }
                }
                available[key] = directory;
            }
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "package.json")));
            if (manifest.RootElement.GetProperty("name").GetString() != name || manifest.RootElement.GetProperty("version").GetString() != version)
                throw new InvalidDataException($"包身份不符：{key}");
            if (manifest.RootElement.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject().OrderBy(d => d.Name, StringComparer.Ordinal)) await Resolve(dependency.Name, dependency.Value.GetString()!, closure);
        }
    }

    static bool Modified(JsonElement package) => package.GetProperty("embedded").GetBoolean() || package.GetProperty("missingFields").GetInt32() != 0;

    // versionDefines 只识别包身份；自行生成的运行时程序集同样提供该能力。
    // Editor 集成需要 APK 中不存在的编辑器程序集，不在这里启用。
    string RuntimeDefines(string source, HashSet<string> input)
    {
        using var json = JsonDocument.Parse(source);
        JsonObject asm = new();
        foreach (var property in json.RootElement.EnumerateObject()) asm[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        if (asm["includePlatforms"] is JsonArray platforms && platforms.Any(p => p?.GetValue<string>() == "Editor")
            || asm["versionDefines"] is not JsonArray defines) return source;
        bool changed = false;
        foreach (var define in defines.OfType<JsonObject>())
        {
            if (define["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            string expression = define["expression"]?.GetValue<string>() ?? "0";
            if (Versions.ContainsKey(name) || !catalog.TryGetValue(name, out var known)
                || !known.GetProperty("assemblies").EnumerateArray().Any(a => input.Contains(a.GetString()!))
                || !Regex.IsMatch(expression, @"^\d+(\.\d+){0,3}(-[\w.-]+)?$")
                || Number(known.GetProperty("version").GetString()!) < Number(expression)) continue;
            define["name"] = "Unity";
            define["expression"] = Schemas.Version;
            changed = true;
        }
        return changed ? asm.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : source;
        static Version Number(string value) => new(string.Join('.', value.Split('-')[0].Split('.').Concat(["0", "0"]).Take(3)));
    }

    static string Higher(string left, string right)
    {
        if (left == right) return left;
        if (!Version.TryParse(left.Split('-')[0], out var a) || !Version.TryParse(right.Split('-')[0], out var b) || a.Major != b.Major)
            throw new InvalidDataException($"包依赖版本不兼容：{left} / {right}");
        return a >= b ? left : right;
    }

    public static string? EditorPath()
    {
        string hub = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");
        string secondary = Path.Join(hub, "secondaryInstallPath.json");
        var roots = new List<string> { Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor") };
        if (File.Exists(secondary) && JsonSerializer.Deserialize<string>(File.ReadAllText(secondary)) is { } location) roots.Add(location);
        return roots.Select(r => Path.Join(r, Schemas.Version, "Editor", "Unity.exe")).FirstOrDefault(File.Exists);
    }
}
