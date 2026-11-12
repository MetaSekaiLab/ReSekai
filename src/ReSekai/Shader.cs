using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReSekai;

static class Shader
{
    sealed record Variant(string[] Keywords, string Vertex, string Fragment, int Version);
    static readonly string[] Blends = "Zero One DstColor SrcColor OneMinusDstColor SrcAlpha OneMinusSrcColor DstAlpha OneMinusDstAlpha SrcAlphaSaturate OneMinusSrcAlpha".Split(' ');
    static readonly string[] Comparisons = "Off Never Less Equal LEqual Greater NotEqual GEqual Always".Split(' ');
    static readonly string[] Stencils = "Keep Zero Replace IncrSat DecrSat Invert IncrWrap DecrWrap".Split(' ');
    static readonly HashSet<string> Stereo = ["STEREO_INSTANCING_ON", "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON", "UNITY_SINGLE_PASS_STEREO"];
    static string N(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Generate(Value shader, Action<string> issue)
    {
        Value form = shader["m_ParsedForm"];
        StringBuilder text = new($"// 从 GLES3 程序重建，需要 OpenGLCore 或 OpenGLES3。\nShader {Yaml.Quote(form["m_Name"].Text)}\n{{\n");
        text.AppendLine("Properties {");
        foreach (var property in form["m_PropInfo"]["m_Props"].Items) text.AppendLine(Property(property));
        text.AppendLine("}");
        byte[][] segments = Segments(shader);
        foreach (var sub in form["m_SubShaders"].Items)
        {
            text.AppendLine("SubShader {");
            text.AppendLine(Tags(sub["m_Tags"]));
            text.AppendLine("LOD " + sub["m_LOD"].Number);
            foreach (var pass in sub["m_Passes"].Items)
            {
                if (pass["m_Type"].Number == 1) { text.AppendLine("UsePass " + Yaml.Quote(pass["m_UseName"].Text)); continue; }
                if (pass["m_Type"].Number == 2) { text.AppendLine("GrabPass { " + (pass["m_TextureName"].Text.Length == 0 ? "" : Yaml.Quote(pass["m_TextureName"].Text)) + " }"); continue; }
                var state = pass["m_State"];
                text.AppendLine("Pass {");
                if (state["m_Name"].Text.Length > 0) text.AppendLine("Name " + Yaml.Quote(state["m_Name"].Text));
                text.AppendLine(Tags(state["m_Tags"]));
                text.Append(State(state));
                try { string program = Program(pass, segments, issue); text.AppendLine("GLSLPROGRAM").Append(program).AppendLine("ENDGLSL"); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                {
                    // 保留已经恢复的属性与渲染状态，失败的 Pass 显式标记。
                    issue($"Pass {state["m_Name"].Text} 程序未恢复：{error.Message}");
                    text.AppendLine("// 此 Pass 的程序未恢复。");
                }
                text.AppendLine("}");
            }
            text.AppendLine("}");
        }
        if (form["m_SubShaders"].Items.Length == 0) throw new InvalidDataException("没有 SubShader");
        if (form["m_FallbackName"].Text.Length > 0) text.AppendLine("Fallback " + Yaml.Quote(form["m_FallbackName"].Text));
        return text.AppendLine("}").ToString().ReplaceLineEndings("\n");
    }

    static string Property(Value p)
    {
        List<string> attributes = p["m_Attributes"].Items.Select(v => v.Text).ToList();
        string[] flags = ["HideInInspector", "PerRendererData", "NoScaleOffset", "Normal", "HDR", "Gamma", "", "MainTexture", "MainColor"];
        for (int i = 0; i < flags.Length; i++) if (flags[i].Length > 0 && (p["m_Flags"].Number & (1 << i)) != 0 && !attributes.Contains(flags[i])) attributes.Add(flags[i]);
        string[] d = Enumerable.Range(0, 4).Select(i => N(p[$"m_DefValue[{i}]"].Float)).ToArray();
        var texture = p["m_DefTexture"];
        (string kind, string value) = p["m_Type"].Number switch
        {
            0 => ("Color", "(" + string.Join(",", d) + ")"), 1 => ("Vector", "(" + string.Join(",", d) + ")"),
            2 => ("Float", d[0]), 3 => ($"Range({d[1]},{d[2]})", d[0]), 5 => ("Int", d[0]),
            4 => (texture["m_TexDim"].Number switch { 1 => "Any", 3 => "3D", 4 => "Cube", 5 => "2DArray", 6 => "CubeArray", _ => "2D" }, Yaml.Quote(texture["m_DefaultName"].Text) + " {}"),
            _ => throw new NotSupportedException("Shader 属性类型 " + p["m_Type"].Number)
        };
        return string.Concat(attributes.Select(a => "[" + a + "] ")) + $"{p["m_Name"].Text} ({Yaml.Quote(p["m_Description"].Text)}, {kind}) = {value}";
    }

    static string Tags(Value tags) => "Tags { " + string.Join(' ', tags["tags"].Items.Select(p => Yaml.Quote(p.Items[0].Text) + "=" + Yaml.Quote(p.Items[1].Text))) + " }";

    static string State(Value state)
    {
        StringBuilder text = new();
        string Get(Value owner, string name, string[]? words = null)
        {
            var value = owner[name];
            if (value["name"].Text is { Length: > 0 } parameter && parameter != "<noninit>") return "[" + parameter + "]";
            int number = (int)value["val"].Float;
            return words is null ? N(value["val"].Float) : number >= 0 && number < words.Length ? words[number] : throw new InvalidDataException($"Shader 状态 {name}={number}");
        }
        int targets = state["rtSeparateBlend"].Number != 0 ? 8 : 1;
        for (int i = 0; i < targets; i++)
        {
            var blend = state[$"rtBlend{i}"];
            string target = targets == 1 ? "" : i + " ";
            text.AppendLine($"Blend {target}{Get(blend, "srcBlend", Blends)} {Get(blend, "destBlend", Blends)}, {Get(blend, "srcBlendAlpha", Blends)} {Get(blend, "destBlendAlpha", Blends)}");
            string[] operations = ["Add", "Sub", "RevSub", "Min", "Max"];
            text.AppendLine($"BlendOp {target}{Get(blend, "blendOp", operations)}, {Get(blend, "blendOpAlpha", operations)}");
            string mask = Get(blend, "colMask");
            if (!mask.StartsWith('[')) { int bits = int.Parse(mask, CultureInfo.InvariantCulture); mask = bits == 0 ? "0" : string.Concat("RGBA".Where((_, bit) => (bits & (8 >> bit)) != 0)); }
            text.AppendLine("ColorMask " + mask + (targets == 1 ? "" : " " + i));
        }
        text.AppendLine("Cull " + Get(state, "culling", ["Off", "Front", "Back"]));
        text.AppendLine("ZTest " + Get(state, "zTest", Comparisons));
        text.AppendLine("ZWrite " + Get(state, "zWrite", ["Off", "On"]));
        text.AppendLine("ZClip " + Get(state, "zClip", ["False", "True"]));
        text.AppendLine("AlphaToMask " + Get(state, "alphaToMask", ["Off", "On"]));
        text.AppendLine($"Offset {Get(state, "offsetFactor")}, {Get(state, "offsetUnits")}");
        text.AppendLine("Stencil {");
        foreach (var (field, label) in new[] { ("stencilRef", "Ref"), ("stencilReadMask", "ReadMask"), ("stencilWriteMask", "WriteMask") }) text.AppendLine(label + " " + Get(state, field));
        foreach (var (field, suffix) in new[] { ("stencilOp", ""), ("stencilOpFront", "Front"), ("stencilOpBack", "Back") })
        {
            var stencil = state[field];
            string comparison = Get(stencil, "comp", Comparisons);
            if (comparison is not ("Off" or "Always")) text.AppendLine("Comp" + suffix + " " + comparison);
            foreach (var (name, label) in new[] { ("pass", "Pass"), ("fail", "Fail"), ("zFail", "ZFail") })
            {
                string operation = Get(stencil, name, Stencils);
                if (operation != "Keep") text.AppendLine(label + suffix + " " + operation);
            }
        }
        return text.AppendLine("}").ToString();
    }

    static byte[][] Segments(Value shader)
    {
        int platform = Array.FindIndex(shader["platforms"].Elements, v => v.Number == 9);
        if (platform < 0) return [];
        long[] Row(string name)
        {
            Value row = shader[name].Items[platform];
            if (row.Schema.Element is null) row = row.Items[0];
            return row.Elements.Select(v => v.Number).ToArray();
        }
        var offsets = Row("offsets"); var sizes = Row("decompressedLengths"); var lengths = Row("compressedLengths");
        if (offsets.Length != sizes.Length || sizes.Length != lengths.Length) throw new InvalidDataException("Shader 分段数量不符");
        byte[] blob = shader["compressedBlob"].Bytes;
        return offsets.Select((offset, i) => Bundle.Decode(blob.AsSpan(checked((int)offset), checked((int)lengths[i])), checked((int)sizes[i]), lengths[i] == sizes[i] ? 0 : 2)).ToArray();
    }

    static (string[] Keywords, string Source) Entry(byte[][] segments, int index)
    {
        if (segments.Length == 0) throw new InvalidDataException("缺少 GLES3 程序");
        Binary table = new(segments[0]);
        int count = table.Count(12);
        if (index < 0 || index >= count) throw new InvalidDataException("Shader 程序索引越界");
        table.Position = 4 + index * 12;
        int offset = table.Int(), length = table.Int(), segment = table.Int();
        if (segment < 0 || segment >= segments.Length) throw new InvalidDataException("Shader 分段索引越界");
        Binary reader = new(segments[segment], offset: offset, length: length);
        reader.Take(24);
        int keywords = reader.Count(4);
        string String() { string s = Encoding.UTF8.GetString(reader.Take(reader.Count())); reader.Align(); return s.TrimEnd('\0'); }
        string[] words = Enumerable.Range(0, keywords).Select(_ => String()).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (words.Any(w => !Regex.IsMatch(w, @"^[A-Za-z_][A-Za-z0-9_]*$"))) throw new InvalidDataException("Shader 关键字无效");
        string source = Encoding.UTF8.GetString(reader.Take(reader.Count())).TrimEnd('\0');
        if (!source.Contains("#version", StringComparison.Ordinal)) throw new NotSupportedException("Shader 程序不是 GLSL 文本");
        return (words, source);
    }

    static string Program(Value pass, byte[][] segments, Action<string> issue)
    {
        Dictionary<string, Variant> variants = [];
        foreach (var program in pass["progVertex"]["m_PlayerSubPrograms"].Items.SelectMany(tier => tier.Items))
        {
            int type = (int)program["m_GpuProgramType"].Number;
            if (type is not (3 or 4)) continue;
            var entry = Entry(segments, checked((int)program["m_BlobIndex"].Number));
            if (entry.Keywords.Any(Stereo.Contains) || entry.Source.Contains("GL_OVR_multiview") || entry.Source.Contains("GL_AMD_vertex_shader_layer")) continue;
            string key = string.Join(' ', entry.Keywords);
            int version = type == 4 ? 300 : 310;
            if (!variants.TryGetValue(key, out var previous) || version < previous.Version)
                variants[key] = new(entry.Keywords, Stage(entry.Source, "VERTEX"), Stage(entry.Source, "FRAGMENT"), version);
        }
        if (variants.Count == 0) throw new NotSupportedException("没有可用的非 VR GLES3 变体");
        var ordered = variants.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        var groups = KeywordGroups(ordered.Select(v => v.Keywords).ToArray());
        StringBuilder text = new();
        foreach (var group in groups) text.AppendLine("#pragma multi_compile _ " + string.Join(' ', group));
        double combinations = groups.Aggregate(1d, (n, group) => n * (group.Length + 1));
        if (combinations > 4096) issue($"关键字组合约 {combinations:0} 种，构建前需按实际变体裁剪");
        foreach (string stage in new[] { "VERTEX", "FRAGMENT" })
        {
            var programs = ordered.Select(v => (Words: v.Keywords.ToHashSet(StringComparer.Ordinal), Lines: Units(stage == "VERTEX" ? v.Vertex : v.Fragment))).ToArray();
            var merged = Merge(programs);
            foreach (var program in programs)
                if (!Expand(merged, program.Words).SequenceEqual(program.Lines)) throw new InvalidDataException("Shader 变体合并校验失败");
            text.AppendLine($"#ifdef {stage}\n#version {ordered.Max(v => v.Version)} es");
            Emit(merged, text);
            text.AppendLine("#endif");
        }
        return text.ToString();
    }

    static string Stage(string source, string stage)
    {
        var lines = source.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == "#ifdef " + stage);
        if (start < 0) throw new NotSupportedException("缺少 " + stage + " 阶段块");
        List<string> result = []; int depth = 1;
        foreach (var line in lines.Skip(start + 1))
        {
            string trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^#\s*if(?:def|ndef)?\b")) depth++;
            else if (Regex.IsMatch(trimmed, @"^#\s*endif\b") && --depth == 0) return string.Join('\n', result);
            if (!Regex.IsMatch(trimmed, @"^#\s*version\b")) result.Add(line);
        }
        throw new InvalidDataException("Shader 阶段块未闭合");
    }

    static string[] Units(string source)
    {
        List<string> result = [], block = []; int depth = 0;
        foreach (string line in source.Split('\n'))
        {
            if (line.Trim().Length == 0 && depth == 0) continue;
            if (Regex.IsMatch(line.Trim(), @"^#\s*if(?:def|ndef)?\b")) depth++;
            block.Add(line);
            if (Regex.IsMatch(line.Trim(), @"^#\s*endif\b")) depth--;
            if (depth < 0) throw new InvalidDataException("Shader 条件块不匹配");
            if (depth == 0) { result.Add(string.Join('\n', block)); block.Clear(); }
        }
        if (depth != 0) throw new InvalidDataException("Shader 条件块未闭合");
        return result.ToArray();
    }

    static string[][] KeywordGroups(string[][] sets)
    {
        var words = sets.SelectMany(s => s).Distinct().OrderByDescending(w => sets.Count(s => s.Contains(w))).ThenBy(w => w, StringComparer.Ordinal);
        List<List<string>> groups = [];
        foreach (var word in words)
        {
            var group = groups.FirstOrDefault(g => g.All(other => !sets.Any(s => s.Contains(word) && s.Contains(other))));
            if (group is null) groups.Add([word]); else group.Add(word);
        }
        return groups.Select(g => g.ToArray()).ToArray();
    }

    sealed record Choice(string Keyword, object[] On, object[] Off);
    static object[] Merge((HashSet<string> Words, string[] Lines)[] variants)
    {
        if (variants.All(v => v.Lines.SequenceEqual(variants[0].Lines))) return variants[0].Lines.Cast<object>().ToArray();
        string? split = variants.SelectMany(v => v.Words).Distinct().Where(w => variants.Any(v => !v.Words.Contains(w)))
            .OrderBy(w => Math.Abs(variants.Count(v => v.Words.Contains(w)) * 2 - variants.Length)).ThenBy(w => w, StringComparer.Ordinal).FirstOrDefault();
        if (split is null) throw new InvalidDataException("同一组关键字对应多个不同程序");
        object[] yes = Merge(variants.Where(v => v.Words.Contains(split)).ToArray()), no = Merge(variants.Where(v => !v.Words.Contains(split)).ToArray());
        List<object> result = []; int a = 0, b = 0;
        // 只共享按顺序出现的完整文本单元，条件块不会被拆开。
        var positions = no.Select((line, i) => (line, i)).Where(p => p.line is string).GroupBy(p => p.line).ToDictionary(g => g.Key, g => g.Select(p => p.i).ToArray());
        for (int i = 0; i < yes.Length; i++)
        {
            if (yes[i] is not string || !positions.TryGetValue(yes[i], out var candidates)) continue;
            int at = Array.BinarySearch(candidates, b); if (at < 0) at = ~at;
            if (at >= candidates.Length) continue;
            int j = candidates[at];
            if (i != a || j != b) result.Add(new Choice(split, yes[a..i], no[b..j]));
            result.Add(yes[i]); a = i + 1; b = j + 1;
        }
        if (a < yes.Length || b < no.Length) result.Add(new Choice(split, yes[a..], no[b..]));
        return result.ToArray();
    }
    static IEnumerable<string> Expand(object[] nodes, HashSet<string> keywords) => nodes.SelectMany(n => n is string line ? new[] { line } :
        Expand(keywords.Contains(((Choice)n).Keyword) ? ((Choice)n).On : ((Choice)n).Off, keywords));
    static void Emit(object[] nodes, StringBuilder text)
    {
        foreach (object node in nodes)
        {
            if (node is string line) { text.AppendLine(line); continue; }
            var choice = (Choice)node;
            text.AppendLine("#if " + (choice.On.Length == 0 ? "!" : "") + "defined(" + choice.Keyword + ")");
            Emit(choice.On.Length == 0 ? choice.Off : choice.On, text);
            if (choice.On.Length > 0 && choice.Off.Length > 0) { text.AppendLine("#else"); Emit(choice.Off, text); }
            text.AppendLine("#endif");
        }
    }

    public static void Setup(Output output)
    {
        const string path = "Assets/ReSekaiGraphics.cs";
        output.Write(path, """
            using UnityEngine;
            using UnityEngine.Rendering;
            #if UNITY_EDITOR
            using UnityEditor;
            [InitializeOnLoad]
            #endif
            static class ReSekaiGraphics
            {
                [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
                static void Configure() => GraphicsSettings.useScriptableRenderPipelineBatching = false;
                #if UNITY_EDITOR
                static ReSekaiGraphics()
                {
                    Set(BuildTarget.StandaloneWindows64, GraphicsDeviceType.OpenGLCore);
                    Set(BuildTarget.StandaloneLinux64, GraphicsDeviceType.OpenGLCore);
                    Set(BuildTarget.Android, GraphicsDeviceType.OpenGLES3);
                    Configure();
                }
                static void Set(BuildTarget target, GraphicsDeviceType api)
                {
                    var current = PlayerSettings.GetGraphicsAPIs(target);
                    if (PlayerSettings.GetUseDefaultGraphicsAPIs(target) || current.Length != 1 || current[0] != api)
                    {
                        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                        PlayerSettings.SetGraphicsAPIs(target, new[] { api });
                    }
                }
                #endif
            }
            """);
        output.Meta(path, Output.Guid("graphics-setup"), "MonoImporter");
    }
}
