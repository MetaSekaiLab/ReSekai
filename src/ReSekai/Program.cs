using System.Text;
using ReSekai;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args.Length == 0 || args is ["--help"] or ["-h"])
    {
        Console.WriteLine("""
            resekai <apk> [输出目录] [--resources <目录>] [--scripts stubs|full] [--upm auto|none|包名列表]
            resekai extract <apk> [输出目录]

            默认导出空桩工程，自动选用已适配的 UPM 包。
            """);
        return 0;
    }
    return await Export.Run(Options.Parse(args));
}
catch (Exception error)
{
    Console.Error.WriteLine($"失败：{error.Message}");
    return 1;
}

namespace ReSekai
{
    sealed record Options(string Apk, string Output, string? Resources, bool Full, string Upm, bool Extract)
    {
        public static Options Parse(string[] args)
        {
            bool extract = args[0] == "extract";
            int i = extract ? 1 : 0;
            if (i == args.Length || args[i].StartsWith('-')) throw new ArgumentException("缺少 APK 路径");
            string apk = Path.GetFullPath(args[i++]);
            string output = i < args.Length && !args[i].StartsWith('-') ? Path.GetFullPath(args[i++])
                : Path.ChangeExtension(apk, extract ? ".il2cpp" : ".unity");
            string? resources = null;
            string scripts = "stubs", upm = "auto";
            HashSet<string> seen = [];
            while (i < args.Length)
            {
                string flag = args[i++];
                if (extract || flag is not ("--resources" or "--scripts" or "--upm")) throw new ArgumentException($"未知选项：{flag}");
                if (!seen.Add(flag)) throw new ArgumentException($"选项重复：{flag}");
                if (i == args.Length || args[i].StartsWith('-')) throw new ArgumentException($"{flag} 缺少值");
                string value = args[i++];
                switch (flag)
                {
                    case "--resources": resources = Path.GetFullPath(value); break;
                    case "--scripts": scripts = value; break;
                    case "--upm": upm = value; break;
                }
            }
            if (scripts is not ("stubs" or "full")) throw new ArgumentException("--scripts 必须为 stubs 或 full");
            if (upm.Length == 0) throw new ArgumentException("--upm 不能为空");
            if (!File.Exists(apk)) throw new FileNotFoundException("APK 不存在", apk);
            if (resources is not null && !Directory.Exists(resources)) throw new DirectoryNotFoundException(resources);
            if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException($"输出目录必须为空：{output}");
            return new(apk, output, resources, scripts == "full", upm, extract);
        }
    }
}
