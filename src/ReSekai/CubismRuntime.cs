using System.IO.Compression;
using System.Text.RegularExpressions;
using ReSekai.Decompiler.Model;

namespace ReSekai;

sealed class CubismRuntime(string directory, byte[] core, byte[] license)
{
    const string CoreKey = "purism-core@1.1.0";
    static readonly Regex Namespace = new(@"\bnamespace\s+([\w.]+)\s*\{");
    // 固定的 R1 源码使用四个空格或一个制表符缩进顶层类型。
    static readonly Regex Type = new(@"(?m)^(?:    |\t)(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe)\s+)*(?:class|struct|enum|interface)\s+(\w+)");

    public static async Task<CubismRuntime> Fetch(HttpClient client, string directory)
    {
        await SourcePackages.Cubism.Fetch(client, directory, path => path == "NOTICE.md"
            || (path.StartsWith("Core/", StringComparison.Ordinal) || path.StartsWith("Framework/", StringComparison.Ordinal)
                || path.StartsWith("Rendering/", StringComparison.Ordinal)) && path.EndsWith(".cs", StringComparison.Ordinal));
        using var archive = await SourcePackages.OpenArchive(client, CoreKey,
            "https://github.com/SakuraMotion/PurismCore/releases/download/v1.1.0/PurismCore-v1.1.0.zip",
            "AE1B32AF28A06B27F47CDA61F8B5F5D4CAA047DC856CDDE1D6D839B850B6A70C");
        return new(directory, Read(archive, "dll-v5/windows/x86_64/PurismCore.dll"), Read(archive, "LICENSE"));

        static byte[] Read(ZipArchive archive, string path)
        {
            using var source = (archive.GetEntry("PurismCore-v1.1.0/" + path)
                ?? throw new InvalidDataException($"Purism Core 缺少文件：{path}")).Open();
            using MemoryStream result = new();
            source.CopyTo(result);
            return result.ToArray();
        }
    }

    public (Dictionary<string, string> Sources, HashSet<string> Replaced) Plan(Dictionary<string, TypeDef[]> types)
    {
        var generated = types.Where(p => p.Value.Any(t => t.Namespace.StartsWith("Live2D.Cubism.", StringComparison.Ordinal)))
            .SelectMany(p => p.Value.Select(t => (t.FullName, Path: p.Key, Type: t))).ToLookup(p => p.FullName, StringComparer.Ordinal);
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> replaced = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> owned = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            string content = File.ReadAllText(path);
            string ns = Namespace.Match(content).Groups[1].Value;
            if (ns.Length == 0 || ns.StartsWith("Live2D.Cubism.Editor", StringComparison.Ordinal) || file == "CubismFadeMotionImporter.cs")
                continue;
            string[] names = Type.Matches(content).Select(m => ns + "." + m.Groups[1].Value).ToArray();
            if (names.Length == 0 || names.Any(name => !owned.Add(name)))
                throw new InvalidDataException($"Cubism SDK 类型声明不匹配：{file}");
            var matches = names.SelectMany(name => generated[name]).ToArray();
            string? destination = matches.FirstOrDefault(m => Path.GetFileName(m.Path) == file).Path;
            destination ??= matches.Length == 1 && names.Length == 1 ? matches[0].Path : $"Live2D.Upstream/{ns}/{file}";
            foreach (var match in matches)
            {
                if (types[match.Path].Any(t => !names.Contains(t.FullName))
                    || match.Path != destination && Packages.IsScript(match.Type))
                    throw new InvalidDataException($"Cubism SDK 无法保留脚本归属：{match.FullName}");
                replaced.Add(match.Path);
            }
            sources.Add(destination, content);
        }
        return (sources, replaced);
    }

    public void Write(Output output, string folder)
    {
        output.Write(folder + "/Live2D.Upstream/LICENSE.md", File.ReadAllBytes(Path.Join(directory, "LICENSE")));
        output.Write(folder + "/Live2D.Upstream/NOTICE.md", File.ReadAllBytes(Path.Join(directory, "NOTICE.md")));
        const string plugin = "Assets/Plugins/x86_64/Live2DCubismCore.dll";
        output.Write(plugin, core);
        output.Write("Assets/Plugins/x86_64/PurismCore.LICENSE.txt", license);
        output.Meta(plugin, Output.Guid("native-plugin:purism-core:windows-x86_64"), "PluginImporter",
            """
              serializedVersion: 2
              isPreloaded: 0
              validateReferences: 1
              platformData:
              - first:
                  :
                second:
                  enabled: 0
                  settings: {}
              - first:
                  Editor: Editor
                second:
                  enabled: 1
                  settings:
                    CPU: x86_64
                    DefaultValueInitialized: true
                    OS: Windows
              - first:
                  Standalone: Win64
                second:
                  enabled: 1
                  settings:
                    CPU: x86_64

            """);
    }
}
