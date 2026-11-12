using System.Text.Json;
using ReSekai;
using ReSekai.Decompiler.Input;

static class AnimationChecks
{
    public static void Basic()
    {
        static Value V(string name, object data) => new(new("test", name), data);
        static Value S(string name, params Value[] fields) => V(name, fields);
        static Value Clip(float stop, byte[] stream, int streamed, float[] dense, bool pointer = false) => S("clip",
            V("m_SampleRate", 2), S("m_ClipBindingConstant", V("genericBindings", new[] { S("binding", V("typeID", 20), V("attribute", 1), V("isPPtrCurve", pointer)) })),
            S("m_MuscleClip", V("m_StopTime", stop), S("m_Clip", S("data",
                S("m_StreamedClip", V("curveCount", pointer ? 0 : streamed), V("discreteCurveCount", pointer ? streamed : 0), V("data", stream)),
                S("m_DenseClip", V("m_CurveCount", dense.Length > 0 ? 1 : 0), V("m_FrameCount", dense.Length), V("m_SampleRate", 2f), V("m_BeginTime", 0f), V("m_SampleArray", dense.Select(x => V("data", x)).ToArray())),
                S("m_ConstantClip", V("data", Array.Empty<Value>()))))));
        static void Require(bool condition) { if (!condition) throw new Exception("动画解码回归检查失败"); }
        var dense = Animation.Decode(Clip(.75f, [], 0, [0, 1, 4]))[0].Channels[0];
        Require(dense.Count == 3 && dense[^1].Time == .75f && dense[^1].Number == 2.5f && dense[1].In == 2 && dense[1].Out == 6);
        using MemoryStream bytes = new();
        using BinaryWriter writer = new(bytes);
        void Frame(float time, params float[] polynomial)
        {
            writer.Write(time); writer.Write(polynomial.Length == 0 ? 0 : 1);
            if (polynomial.Length == 0) return;
            writer.Write(0);
            foreach (float value in polynomial) writer.Write(value);
        }
        Frame(float.NegativeInfinity); Frame(0, 1, 2, 3, 4); Frame(1, 0, 0, 0, 10); Frame(float.PositiveInfinity);
        var cubic = Animation.Decode(Clip(1, bytes.ToArray(), 1, []))[0].Channels[0];
        Require(cubic.Count == 2 && cubic[0].Number == 4 && cubic[0].Out == 3 && cubic[1].Number == 10 && cubic[1].In == 10);
        bytes.SetLength(0); bytes.Position = 0;
        Frame(float.NegativeInfinity, 0, 0, 0, 2); Frame(.5f, 0, 0, 0, 3); Frame(float.PositiveInfinity);
        var pointers = Animation.Decode(Clip(.5f, bytes.ToArray(), 1, [], true))[0].Channels[0];
        Require(pointers.Count == 2 && pointers[0].Time == 0 && pointers[0].Number == 2 && pointers[1].Number == 3);
        Require(AnimationNames.Hash("Parameters/ParamBrowRX") == 0x71f42c84 && AnimationNames.Hash("") == 0 && Animation.MuscleName(199) == "RightHandTDOF.z" && Animation.MuscleName(200) is null);
    }

    public static int Run(string input, string metadata, string bundles, string directory)
    {
        if (AnimationNames.Hash("Parameters/ParamBrowRX") != 0x71f42c84 || AnimationNames.Hash("") != 0) throw new Exception("CRC32 校验失败");
        using Apk apk = new(input);
        var model = ModelBuilder.Load(Path.Join(metadata, "libil2cpp.so"), Path.Join(metadata, "global-metadata.dat")).Model;
        Output output = new(directory);
        Project project = new(output, null!, new(model), new());
        Resources resources = new(apk, bundles);
        foreach (var unit in resources.Units) project.Scan(resources.Open(unit));
        project.Animations.Prepare();
        List<object> results = [];
        Dictionary<string, ScriptName> usedScripts = [];
        int failed = 0;
        HashSet<string> visited = [];
        foreach (var unit in resources.Units)
        {
            int written = 0, complete = 0, guessed = 0;
            foreach (var file in resources.Open(unit))
            {
                if (!visited.Add(file.Name)) continue;
                foreach (var item in file.Objects.Where(o => file.Types[o.Type].ClassId == 74))
                {
                    var document = project.Documents[file.Name].First(d => d.Objects.Contains(item.Id));
                    Value source = file.Read(item);
                    string name = source["m_Name"].Text;
                    int before = output.Errors;
                    try
                    {
                        var converted = project.Editor(file, item, source);
                        var curves = converted.Value.Items.Where(v => v.Schema.Name.EndsWith("Curves", StringComparison.Ordinal)).SelectMany(v => v.Items).ToArray();
                        int unknown = curves.Count(c => (c.Find("path")?.Text ?? "").StartsWith("__resekai_") || (c.Find("attribute")?.Text ?? "").StartsWith("__resekai_"));
                        Yaml yaml = new(pointer =>
                        {
                            if (pointer is null) return "{fileID: 0}";
                            var id = file.Reference(pointer);
                            if (!project.MonoScripts.TryGetValue(id, out var script)) return project.Reference(file, document, pointer);
                            string guid = Output.Guid("test-script:" + script.Key);
                            usedScripts[guid] = script;
                            return $"{{fileID: 11500000, guid: {guid}, type: 3}}";
                        });
                        yaml.Document(74, item.Id, converted.Schema, converted.Value);
                        output.Write(document.Path, yaml.ToString());
                        output.Meta(document.Path, document.Guid, "NativeFormatImporter", $"  mainObjectFileID: {item.Id}\n");
                        written++;
                        if (unknown == 0 && output.Errors == before) complete++;
                        results.Add(new { origin = unit.Name, name, path = document.Path, curves = curves.Length, unknown, errors = output.Errors - before });
                    }
                    catch (Exception error)
                    {
                        failed++;
                        output.Note("转换", name, error.ToString(), true);
                        Console.WriteLine(name + ": " + error.Message);
                    }
                }
            }
            guessed = output.Issues.Where(p => p.Key.Contains("路径为规则推断")).Sum(p => p.Value.Count);
            Console.WriteLine($"{unit.Name}: 写出 {written}，名称完整 {complete}，累计规则命中 {guessed}");
        }
        output.Write("animations.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        output.Write("scripts.json", JsonSerializer.Serialize(usedScripts, new JsonSerializerOptions { WriteIndented = true }));
        output.Report("resources");
        Console.WriteLine($"动画 {results.Count}，转换失败 {failed}，报告项 {output.Errors}");
        return failed == 0 ? 0 : 1;
    }
}
