using System.Text.Json;
using ReSekai.Decompiler.Input;
using ReSekai.Decompiler.Pipeline;

namespace ReSekai;

static class Export
{
    public static async Task<int> Run(Options options, string? stagedScripts = null)
    {
        using Apk apk = new(options.Apk);
        string work = options.Extract ? options.Output : Path.Join(options.Output, ".resekai");
        Console.WriteLine("提取 IL2CPP 二进制与 metadata");
        apk.Extract(work);
        if (options.Extract) { Console.WriteLine($"已提取：{work}"); return 0; }
        Output output = new(options.Output);
        RecoveryRunResult? recovery = null;
        try
        {
            output.Write("Assets/csc.rsp", "-parallel-\n");
            output.Meta("Assets/csc.rsp", Output.Guid("compiler-options"), "DefaultImporter");
            Console.WriteLine("读取脚本类型");
            var model = ModelBuilder.Load(Path.Join(work, "libil2cpp.so"), Path.Join(work, "global-metadata.dat")).Model;
            Packages packages = new();
            await packages.Prepare(options, model, output);
            Scripts scripts = new(model, packages, output);
            recovery = scripts.Write(work, options.Full, stagedScripts);
            Project project = new(output, scripts, new(model), packages);
            Resources resources = new(apk, options.Resources);
            int processed = 0;
            foreach (var unit in resources.Units)
            {
                if (processed++ % 100 == 0) Console.WriteLine($"索引资源 {processed}/{resources.Units.Count}：{unit.Name}");
                project.Scan(resources.Open(unit));
            }
            project.PrepareTextures();
            project.Animations.Prepare();
            project.BindScripts();
            Shader.Setup(output);
            HashSet<string> exported = [];
            processed = 0;
            foreach (var unit in resources.Units)
            {
                if (processed++ % 100 == 0) Console.WriteLine($"写出资源 {processed}/{resources.Units.Count}：{unit.Name}");
                foreach (var file in resources.Open(unit))
                {
                    if (!exported.Add(file.Name) || !project.Documents.TryGetValue(file.Name, out var documents)) continue;
                    var objects = file.Objects.ToDictionary(i => i.Id);
                    foreach (var document in documents)
                    {
                        try
                        {
                            if (document.Texture is not null) Textures.Write(document, resources, output);
                            else if (document.Imported) Media.Write(file, objects[document.Main], document, project, resources, output);
                            else
                            {
                                Yaml yaml = new(pointer => project.Reference(file, document, pointer));
                                foreach (long id in document.Objects)
                                {
                                    var item = objects[id];
                                    Value value = Media.Inline(project.Read(file, item), resources);
                                    if (file.Types[item.Type].ClassId == 91)
                                    {
                                        foreach (var converted in new Controllers(value, document, reason => output.Note("控制器", document.Path, reason, true)).Convert())
                                            yaml.Document(converted.ClassId, converted.Id, converted.Value.Schema, converted.Value);
                                        continue;
                                    }
                                    var editor = project.Editor(file, item, value);
                                    yaml.Document(file.Types[item.Type].ClassId, document.FileId(id), editor.Schema, editor.Value, document.Settings);
                                }
                                output.Write(document.Path, yaml.ToString());
                                if (!document.Settings) output.Meta(document.Path, document.Guid, document.Prefab ? "PrefabImporter" : document.Scene ? "DefaultImporter" : "NativeFormatImporter",
                                    document.Prefab || document.Scene ? "" : $"  mainObjectFileID: {document.FileId(document.Main)}\n", document.Bundle);
                            }
                        }
                        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException)
                        {
                            output.Note("资源写出", document.Path, error.Message, true);
                        }
                    }
                }
            }
            foreach (var file in apk.Streaming())
            {
                string path = "Assets/StreamingAssets/" + file.Name;
                output.Write(path, file.Bytes);
                output.Meta(path, Output.Guid("streaming:" + file.Name), file.Name.EndsWith(".json", StringComparison.Ordinal) ? "TextScriptImporter" : "DefaultImporter");
            }
            output.Write("ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {Schemas.Version}\n");
            output.Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!159 &1\nEditorSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 12\n  m_SerializationMode: 2\n  m_SpritePackerMode: 4\n");
            string scenes = string.Join("", project.Documents.Values.SelectMany(v => v).Where(d => d.Scene).OrderBy(d => d.Path, StringComparer.Ordinal)
                .Select(d => $"  - enabled: 1\n    path: {d.Path}\n    guid: {d.Guid}\n"));
            output.Write("ProjectSettings/EditorBuildSettings.asset", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1045 &1\nEditorBuildSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Scenes:\n" + scenes + "  m_configObjects: {}\n");
            output.Write("Packages/manifest.json", JsonSerializer.Serialize(new { dependencies = model.Assemblies.Where(a => a.AssemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal) && a.AssemblyName.EndsWith("Module", StringComparison.Ordinal))
                .Select(a => a.AssemblyName[12..^6].ToLowerInvariant()).Where(ModuleNames.Contains).Distinct().Order(StringComparer.Ordinal).ToDictionary(n => "com.unity.modules." + n, _ => "1.0.0") }, new JsonSerializerOptions { WriteIndented = true }));
            output.Finish();
        }
        catch (Exception error)
        {
            output.Note("导出", options.Apk, error.ToString(), true);
            throw;
        }
        finally { output.Report(options.Full ? "full" : "stubs", recovery is null ? null : new { recovery.SelectedMethods, recovery.EmittedMethods, recovery.PartialMethods, recovery.EmitFailures }); }
        Console.WriteLine($"导出 {output.Files} 个文件，错误 {output.Errors}：{output.Root}");
        if (output.Errors != 0) Console.WriteLine($"未完成项：{Path.Join(work, "report.json")}");
        return output.Errors == 0 ? 0 : 1;
    }

    static readonly HashSet<string> ModuleNames = ["ai", "androidjni", "animation", "assetbundle", "audio", "cloth", "director", "imageconversion", "imgui", "jsonserialize", "particlesystem", "physics", "physics2d", "screencapture", "terrain", "terrainphysics", "tilemap", "ui", "uielements", "umbra", "unityanalytics", "unitywebrequest", "unitywebrequestassetbundle", "unitywebrequestaudio", "unitywebrequesttexture", "unitywebrequestwww", "vehicles", "video", "vr", "wind", "xr"];
}
