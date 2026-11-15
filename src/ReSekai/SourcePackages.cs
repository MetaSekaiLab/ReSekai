using System.IO.Compression;
using System.Security.Cryptography;

namespace ReSekai;

sealed record SourcePackages(string Name, string Version, string Assembly, string Repository,
    string Revision, string Subdirectory, string License, string Sha256)
{
    public static readonly SourcePackages[] Embedded =
    [
        new("com.cysharp.unitask", "2.4.1", "UniTask", "Cysharp/UniTask",
            "cfe509a55602ba5be6df7fa0468b10298b8a053d", "src/UniTask/Assets/Plugins/UniTask", "LICENSE",
            "54B2CE6CD34D9A8AF2AEDCB07804564B658D7227C8F1227E73BA2BFB7A52EED9"),
        new("com.cysharp.zstring", "2.6.0", "ZString", "Cysharp/ZString",
            "3156134075b89a162fe95350a2e92a4c85ccca59", "src/ZString.Unity/Assets/Scripts/ZString", "LICENSE",
            "47E9211506D7103274A7A0696E723B06A2E83B8634263A42BC4128559BE6D07A"),
    ];

    public static readonly SourcePackages Spine = new("com.esotericsoftware.spine.spine-csharp", "4.1.2", "spine-csharp", "EsotericSoftware/spine-runtimes",
        "77a5db0ec6d16331f5efbaa7662bba9355bd3424", "spine-csharp/src", "spine-csharp/LICENSE",
        "E1F690112FAC802B5A0E016AEB4243243243844AAA44D6B61EF459862EC9DEDD");

    public static readonly SourcePackages Cubism = new("com.live2d.cubism", "5-r.1", "Assembly-CSharp", "Live2D/CubismUnityComponents",
        "9f1012b589b7f24f6a59a9933a3d021febd5ae46", "Assets/Live2D/Cubism", "Assets/Live2D/Cubism/LICENSE.md",
        "AA09B791EF34E553F38069EFA9025EA849C5ACE53C7A8CC5601550A1D4926914");

    public static async Task<ZipArchive> OpenArchive(HttpClient client, string key, string url, string sha256)
    {
        string? cache = Environment.GetEnvironmentVariable("RESEKAI_PACKAGE_CACHE");
        string? local = cache is null ? null : Path.Join(cache, key + ".zip");
        byte[] bytes = File.Exists(local) ? await File.ReadAllBytesAsync(local!) : await client.GetByteArrayAsync(url);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != sha256)
            throw new InvalidDataException($"包 SHA256 不匹配：{key}");
        return new ZipArchive(new MemoryStream(bytes));
    }

    public async Task Fetch(HttpClient client, string directory, Func<string, bool>? include = null)
    {
        using var archive = await OpenArchive(client, Name + "@" + Version, $"https://codeload.github.com/{Repository}/zip/{Revision}", Sha256);
        string root = Repository[(Repository.IndexOf('/') + 1)..] + "-" + Revision + "/";
        string prefix = root + Subdirectory + "/";
        var license = archive.GetEntry(root + License) ?? throw new InvalidDataException($"源码包缺少许可证：{Name}");
        foreach (var entry in archive.Entries.Where(e => e.Name.Length != 0 && e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string relative = entry.FullName[prefix.Length..];
            if (include is not null && !include(relative)) continue;
            // APK 只使用 UniTask 主程序集，独立扩展程序集有自己的依赖。
            if (Assembly == "UniTask" && (relative.StartsWith("Editor", StringComparison.Ordinal)
                || relative.StartsWith("Runtime/External", StringComparison.Ordinal)
                || relative.StartsWith("Runtime/Linq", StringComparison.Ordinal))) continue;
            Extract(entry, relative);
        }
        Extract(license, "LICENSE");

        void Extract(ZipArchiveEntry entry, string relative)
        {
            string destination = Output.Under(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}
