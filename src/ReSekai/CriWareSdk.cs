using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReSekai;

sealed class CriWareSdk(byte[] windows, Dictionary<string, string> sources, JsonElement[] versionDefines)
{
    public const string Assembly = "CriMw.CriWare.Runtime";
    public const string Windows = "UNITY_EDITOR_WIN || (!UNITY_EDITOR && UNITY_STANDALONE_WIN)";
    const string ZipName = "criware_sdk_unity_v3_14_00_smartphone_en.zip";
    const string PackagePath = "CRIWARE/SDK/unity/plugin/criware_unity_plugin_v2_46_25_smartphone_en.unitypackage";
    const string PackageHash = "5FDF3A456003660A9521966C42C96749C4C7DA64C26DCBFA70E935AF3DBE168F";
    const string Runtime = "Assets/CRIMW/CriWare/Runtime/";
    const string WindowsPath = "Platforms/PC/x86_64/cri_ware_unity.dll";
    public JsonElement[] VersionDefines => versionDefines;

    public static CriWareSdk? LoadConfigured()
    {
        string? path = Environment.GetEnvironmentVariable("RESEKAI_CRIWARE_SDK");
        if (string.IsNullOrWhiteSpace(path))
        {
            string? cache = Environment.GetEnvironmentVariable("RESEKAI_PACKAGE_CACHE");
            path = cache is null ? null : Path.Join(cache, ZipName);
            if (path is null || !File.Exists(path)) return null;
        }
        using ZipArchive sdk = ZipFile.OpenRead(path);
        using var package = (sdk.GetEntry(PackagePath) ?? throw new InvalidDataException($"CRIWARE SDK 缺少 {PackagePath}")).Open();
        using MemoryStream bytes = new();
        package.CopyTo(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))) != PackageHash)
            throw new InvalidDataException("CRIWARE Unity 插件 2.46.25 SHA256 不匹配");
        bytes.Position = 0;
        using GZipStream gzip = new(bytes, CompressionMode.Decompress);
        using TarReader tar = new(gzip);
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        for (TarEntry? entry; (entry = tar.GetNextEntry()) is not null;)
        {
            if (entry.DataStream is not { } data) continue;
            string name = entry.Name.TrimStart('.').TrimStart('/');
            if (name.EndsWith("/pathname", StringComparison.Ordinal))
            {
                using StreamReader reader = new(data, Encoding.UTF8, leaveOpen: true);
                string asset = reader.ReadToEnd().Trim();
                if (asset.StartsWith(Runtime, StringComparison.Ordinal)) paths.Add(name[..^9], asset[Runtime.Length..]);
            }
            else if (name.EndsWith("/asset", StringComparison.Ordinal))
            {
                using MemoryStream content = new();
                data.CopyTo(content);
                files.Add(name[..^6], content.ToArray());
            }
        }
        byte[]? dll = null;
        JsonElement[]? defines = null;
        Dictionary<string, string> scripts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, relative) in paths)
        {
            if (!files.TryGetValue(key, out byte[]? data)) continue;
            if (relative == WindowsPath) dll = data;
            else if (relative == Assembly + ".asmdef")
            {
                using var json = JsonDocument.Parse(data);
                defines = json.RootElement.GetProperty("versionDefines").EnumerateArray().Select(v => v.Clone()).ToArray();
            }
            else if (relative.EndsWith(".cs", StringComparison.Ordinal)
                     && (!relative.StartsWith("Platforms/", StringComparison.Ordinal) || relative.StartsWith("Platforms/PC/", StringComparison.Ordinal)))
                scripts.Add(relative, Encoding.UTF8.GetString(data).TrimStart('\uFEFF').ReplaceLineEndings("\n"));
        }
        if (dll is null || defines is null || scripts.Count == 0) throw new InvalidDataException("CRIWARE Unity 插件缺少 Windows 运行时文件");
        return new(dll, scripts, defines);
    }

    public Dictionary<string, string> MatchFiles(IEnumerable<string> generated)
    {
        var names = generated.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        return sources.ToDictionary(p => names.GetValueOrDefault(Path.GetFileName(p.Key)) ?? "Official/" + p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static string PlatformSource(string relative, string? android, string? sdk)
    {
        // HCA 封装的原生入口在 Windows SDK 中相同，AudioSession 保留非 iOS 的行为。
        if ((relative is "CriHcaDecoder.cs" or "CriWare/CriAtomAudioSession.cs") && sdk is null) return android!;
        if (sdk is null) return $"#if !({Windows})\n{android}\n#endif\n";
        if (android is null) return $"#if {Windows}\n{sdk}\n#endif\n";
        return $"#if {Windows}\n{sdk}\n#else\n{android}\n#endif\n";
    }

    public void WriteWindows(Output output)
    {
        const string path = "Assets/Plugins/x86_64/cri_ware_unity.dll";
        output.Write(path, windows);
        output.Meta(path, Output.Guid("native-plugin:criware:windows-x86_64"), "PluginImporter",
            """
              serializedVersion: 2
              platformData:
              - first:
                  Any:
                second:
                  enabled: 0
                  settings: {}
              - first:
                  Editor: Editor
                second:
                  enabled: 1
                  settings:
                    CPU: x86_64
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
