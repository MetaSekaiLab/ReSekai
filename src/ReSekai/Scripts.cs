using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using ReSekai.Decompiler.Model;
using ReSekai.Decompiler.Output;
using ReSekai.Decompiler.Pipeline;

namespace ReSekai;

sealed class Scripts(ProgramModel model, Packages packages, Output output)
{
    readonly Dictionary<string, string> targets = [];
    readonly Dictionary<string, bool> stripped = [];
    readonly Dictionary<string, TypeDef> scriptTypes = model.Types.Where(t => t.DeclaringType is null)
        .ToDictionary(t => new ScriptName(t.Assembly, t.Namespace, t.Name).Key);
    public static bool Engine(string name) => name is "mscorlib" or "netstandard" or "Mono.Security" or "System" or "UnityEngine" or "__Generated"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("UnityEngine.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal);

    public (string Guid, long Id)? Target(ScriptName name, bool component)
    {
        if (packages.Scripts.TryGetValue(name.Key, out var package)) return package;
        if (scriptTypes.TryGetValue(name.Key, out var type) && !Packages.IsScript(type)
            && type.Fields.Count == 0 && type.Methods.All(m => m.Name == ".ctor" && m.Parameters.Count == 0))
            return Stripped(name, component);
        if (targets.TryGetValue(name.Key, out string? guid)) return (guid, 11500000);
        return null;
    }

    public (string Guid, long Id) Stripped(ScriptName name, bool component)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name.Name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException($"剥离脚本名称无效：{name.Name}");
        if (stripped.TryGetValue(name.Name, out bool previous))
        {
            if (previous != component) throw new InvalidDataException($"剥离脚本同时作为组件和资源：{name.Name}");
        }
        else
        {
            string path = "Assets/Scripts/ReSekai.Stripped/" + name.Name + ".cs";
            output.Write(path, $"// 类型元数据已剥离，仅用于保留资源引用的兼容空壳。\nnamespace ReSekai.Stripped {{ public class {name.Name} : UnityEngine.{(component ? "MonoBehaviour" : "ScriptableObject")} {{ }} }}\n");
            output.Meta(path, Output.Guid("stripped:" + name.Name), "MonoImporter");
            stripped[name.Name] = component;
            output.Note("脚本", name.Name, "类型元数据已剥离，仅写出兼容空壳");
        }
        return (Output.Guid("stripped:" + name.Name), 11500000);
    }

    public RecoveryRunResult? Write(string work, bool full, string? stagedScripts = null)
    {
        SerializedFieldNames fieldNames = new(model);
        var recover = model.Assemblies.Where(a => !Engine(a.AssemblyName) && !packages.Assemblies.ContainsKey(a.AssemblyName)).ToArray();
        foreach (var assembly in model.Assemblies)
            output.AssemblySources[assembly.AssemblyName] = packages.Assemblies.TryGetValue(assembly.AssemblyName, out var package)
                ? $"UPM {package}@{packages.Versions[package]}"
                : packages.WriteManagedRuntime(assembly.AssemblyName, output) ?? (Engine(assembly.AssemblyName) ? "Unity" : full ? "反编译" : "空桩");
        if (recover.Length == 0) return null;
        string stage = stagedScripts ?? Path.Join(work, "scripts");
        string assemblies = Path.Join(stage, "assemblies"), sources = Path.Join(stagedScripts is null ? stage : work, "src");
        RecoveryRunResult? result = null;
        if (stagedScripts is not null)
        {
            if (!full) throw new InvalidOperationException("只可复用完整模式的恢复产物");
            result = ReadRecovery(stage, work, recover.Select(a => a.Name));
        }
        else if (full)
        {
            result = RecoverySession.Run(new(Path.Join(work, "libil2cpp.so"), Path.Join(work, "global-metadata.dat"), stage)
            {
                Assemblies = recover.Select(a => a.Name).ToArray(),
                SourceLayout = "project",
                NoSource = true,
                KeepGoing = true,
                Jobs = 1,
                MethodTimeoutSeconds = 600,
            }, Console.WriteLine);
        }
        else
        {
            // 空桩只依赖类型模型，不建立机器码事实或扫描方法体。
            new Skeleton(model).Write(assemblies);
        }
        if (result is not null && (result.EmitFailures != 0 || result.EmittedMethods != result.SelectedMethods || result.PartialMethods != 0))
            output.Note("方法体", "scripts", $"选择 {result.SelectedMethods}，写出 {result.EmittedMethods}，部分恢复 {result.PartialMethods}，写出失败 {result.EmitFailures}", true);
        // 方法恢复返回后再生成源码，避免恢复图与 C# 语法树同时驻留。
        var stubs = new Lazy<string>(() =>
        {
            string directory = Path.Join(stage, "assemblies-stubs");
            new Skeleton(model).Write(directory);
            return directory;
        });
        // 复用机器码恢复结果，但源码必须经过当前版本的转换规则。
        foreach (var assembly in recover)
        {
            Console.WriteLine($"串行生成{(full ? "源码" : "空桩")} {assembly.AssemblyName}");
            SourceProject.Write(Path.Join(assemblies, assembly.Name), assemblies, sources, _ => true,
                transforms: fieldNames, stubs: full ? () => stubs.Value : null, jobs: 1);
        }
        var available = recover.Select(a => a.AssemblyName).Concat(packages.Assemblies.Keys).ToHashSet();
        foreach (var assembly in recover)
        {
            string from = Path.Join(sources, assembly.Name);
            string folder = assembly.AssemblyName == "Assembly-CSharp-firstpass" ? "Assets/Plugins/Assembly-CSharp-firstpass" : "Assets/Scripts/" + Output.Safe(assembly.AssemblyName);
            var types = assembly.Types.Where(t => t.DeclaringType is null).GroupBy(t => $"{(t.Namespace.Length == 0 ? "" : Output.Safe(t.Namespace) + "/")}{Output.Safe(t.SimpleName)}.cs", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var cubism = assembly.AssemblyName == SourcePackages.Cubism.Assembly ? packages.Cubism : null;
            var (upstream, replaced) = cubism is null ? (new Dictionary<string, string>(), new HashSet<string>()) : cubism.Plan(types);
            if (cubism is not null)
            {
                cubism.Write(output, folder);
                output.AssemblySources[assembly.AssemblyName] = $"反编译 + Cubism Unity SDK {SourcePackages.Cubism.Version} + Purism Core 1.1.0 v5";
            }
            var generated = Directory.EnumerateFiles(from, "*.cs", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(from, path).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            var criware = assembly.AssemblyName == CriWareSdk.Assembly ? packages.CriWare : null;
            var sdkSources = criware?.MatchFiles(generated.Keys.Where(p => !p.StartsWith("references/", StringComparison.Ordinal)));
            if (criware is not null)
            {
                criware.WriteWindows(output);
                output.AssemblySources[assembly.AssemblyName] = "Windows CRIWARE Unity SDK 2.46.25 + Android 反编译及 APK 原生库";
            }
            else if (full && assembly.AssemblyName == CriWareSdk.Assembly)
                output.AssemblySources[assembly.AssemblyName] = "反编译 + Android APK 原生库";
            var sdkOnly = sdkSources?.Keys.Where(path => !generated.ContainsKey(path)) ?? [];
            foreach (string relative in generated.Keys.Where(path => !replaced.Contains(path)).Concat(upstream.Keys).Concat(sdkOnly).Order(StringComparer.Ordinal))
            {
                if (relative.StartsWith("references/", StringComparison.Ordinal) || relative == "Properties/StrippedAttributes.cs") continue;
                TypeDef[] declared = types.GetValueOrDefault(relative) ?? [];
                if (declared.Length > 0 && declared.All(t => t.CustomAttributes.HasAttribute("Microsoft.CodeAnalysis.EmbeddedAttribute") || t.CustomAttributes.HasAttribute("System.CodeDom.Compiler.GeneratedCodeAttribute"))) continue;
                string safe = string.Join('/', relative.Split('/').Select(part => new[] { ".framework", ".bundle", ".plugin", ".androidlib" }.Any(suffix => part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ? part.Replace('.', '_') : part));
                string path = folder + "/" + safe;
                string guid = Output.Guid("source:" + assembly.AssemblyName + "/" + relative);
                foreach (var type in declared) targets[new ScriptName(assembly.AssemblyName, type.Namespace, type.Name).Key] = guid;
                string? recovered = generated.TryGetValue(relative, out string? file) ? File.ReadAllText(file) : null;
                string code = sdkSources is null ? upstream.GetValueOrDefault(relative) ?? recovered!
                    : CriWareSdk.PlatformSource(relative, recovered, sdkSources.GetValueOrDefault(relative));
                if (full && assembly.AssemblyName == "Assembly-CSharp" && relative == "Sekai/ScreenLayerHome.cs")
                    code = GeneratedSource.HomeNewsAndroid(code);
                if (full && assembly.AssemblyName == "Unity.RenderPipelines.Core.Runtime" && relative == "UnityEngine.Rendering/CoreUtils.cs")
                    code = GeneratedSource.EditorRenderCleanup(code);
                if (full && assembly.AssemblyName == "Assembly-CSharp" && relative == "AppLovinManager.cs")
                    code = GeneratedSource.AppLovinStub(code);
                if (full && assembly.AssemblyName == "Assembly-CSharp" && relative == "Sekai/TitleController.cs")
                {
                    code = GeneratedSource.EditorDirectTitle(code);
                    if (scriptTypes.ContainsKey(new ScriptName("Assembly-CSharp", "", "AppLovinManager").Key))
                        code = GeneratedSource.RegisterWithoutAds(code);
                }
                if (full && assembly.AssemblyName == "Assembly-CSharp" && relative == "Sekai/EnvironmentConfig.cs")
                    code = GeneratedSource.DevelopmentLogging(code);
                if (full && assembly.AssemblyName == "Assembly-CSharp" && relative == "Sekai/SekaiSingletonManager.cs")
                    code = GeneratedSource.RegisterFormatters(code);
                if (full && assembly.AssemblyName == "FastAES" && relative == "FastAESAlgorithmFactory.cs")
                    code = GeneratedSource.WindowsFastAes(code);
                if (full && assembly.AssemblyName == "uPalette" && relative == "uPalette.Runtime.Core/PaletteStore.cs")
                {
                    code = GeneratedSource.PaletteEditor(code);
                    output.AssemblySources[assembly.AssemblyName] = "反编译 + Editor 调色板加载";
                }
                if (packages.CriWare is not null && assembly.AssemblyName == "Assembly-CSharp" && relative == "AudioSyncedUnityTimer.cs"
                    && code.Contains("GetTimeAndScaleSyncedWithAudio(", StringComparison.Ordinal))
                    code = GeneratedSource.CriAudioClock(code);
                if (relative == NativeMathSource.Path)
                    code = NativeMathSource.ForUnity(code, Path.Join(assemblies, assembly.Name));
                output.Write(path, code);
                output.Meta(path, guid, "MonoImporter", "  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n");
            }
            if (assembly.AssemblyName is "Assembly-CSharp" or "Assembly-CSharp-firstpass") continue;
            using var module = ModuleDefinition.ReadModule(Path.Join(assemblies, assembly.Name));
            string[] references = module.AssemblyReferences.Select(r => r.Name).Where(r => available.Contains(r) && !packages.Dlls.Contains(r)).Order(StringComparer.Ordinal).ToArray();
            if (references.Any(Predefined))
            {
                if (UsesPredefined(module, from)) throw new InvalidDataException($"{assembly.AssemblyName} 引用预定义程序集，无法生成有效 asmdef");
                references = references.Where(r => !Predefined(r)).ToArray();
            }
            string asmdef = folder + "/" + Output.Safe(assembly.AssemblyName) + ".asmdef";
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            if (criware is null)
                output.Write(asmdef, JsonSerializer.Serialize(new { name = assembly.AssemblyName, references, allowUnsafeCode = true }, jsonOptions));
            else
            {
                if (available.Contains("Unity.Profiling.Core"))
                    references = references.Append("Unity.Profiling.Core").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                output.Write(asmdef, JsonSerializer.Serialize(new { name = assembly.AssemblyName, references, allowUnsafeCode = true, versionDefines = criware.VersionDefines }, jsonOptions));
            }
            output.Meta(asmdef, Output.Guid("assembly:" + assembly.AssemblyName), "AssemblyDefinitionImporter");
        }
        return result;
    }

    static bool Predefined(string name) => name is "Assembly-CSharp" or "Assembly-CSharp-firstpass";

    internal static bool UsesPredefined(ModuleDefinition module, string sources)
    {
        static bool External(TypeReference type) => type.Scope is AssemblyNameReference reference && Predefined(reference.Name);
        var localTypeNames = module.GetTypeReferences().Where(External).Select(t => t.Name.Split('`')[0]).Distinct().ToArray();
        if (localTypeNames.Length == 0) return false;

        // 局部变量声明会被源码反编译重建。排除它们后，仍有引用就不能省略依赖。
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var variable in method.Body.Variables)
                variable.VariableType = module.TypeSystem.Object;
        using MemoryStream bytes = new();
        module.Write(bytes);
        bytes.Position = 0;
        using var reduced = ModuleDefinition.ReadModule(bytes);
        if (reduced.GetTypeReferences().Any(External)) return true;

        foreach (string path in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path);
            if (localTypeNames.Any(name => text.Contains(name, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    static RecoveryRunResult ReadRecovery(string stage, string work, IEnumerable<string> assemblies)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(stage, "run.json")));
        var root = manifest.RootElement;
        var request = root.GetProperty("request");
        if (root.GetProperty("status").GetString() != "complete"
            || !assemblies.ToHashSet().IsSubsetOf(request.GetProperty("Assemblies").EnumerateArray().Select(a => a.GetString()!)))
            throw new InvalidDataException("恢复产物未完成或程序集范围不一致");
        var engine = typeof(RecoverySession).Assembly;
        using (var stream = File.OpenRead(engine.Location))
        {
            if (!root.TryGetProperty("implementation", out var implementation)
                || !implementation.TryGetProperty(engine.GetName().Name!, out var hash)
                || Convert.ToHexString(SHA256.HashData(stream)) != hash.GetString())
                throw new InvalidDataException("恢复产物使用了不同版本的反编译器，请重新恢复或直接全量导出");
        }
        var inputs = root.GetProperty("inputs");
        foreach (var (key, file) in new[] { ("binary", "libil2cpp.so"), ("metadata", "global-metadata.dat") })
        {
            using var stream = File.OpenRead(Path.Join(work, file));
            if (Convert.ToHexString(SHA256.HashData(stream)) != inputs.GetProperty(key).GetString())
                throw new InvalidDataException($"恢复产物与 APK 输入不一致：{file}");
        }
        var outcome = root.GetProperty("outcome");
        return new(stage, File.ReadAllText(Path.Join(stage, "holes.txt")), outcome.GetProperty("SelectedMethods").GetInt32(),
            outcome.GetProperty("EmittedMethods").GetInt32(), outcome.GetProperty("EmitFailures").GetInt32(), outcome.GetProperty("PartialMethods").GetInt32());
    }
}
