using ReSekai;

static class ResourceChecks
{
    public static int Controllers(string input, string directory)
    {
        using Apk apk = new(input);
        Output output = new(directory);
        Resources resources = new(apk, null);
        Project project = new(output, null!, null!, new());
        var files = resources.Open(resources.Units[0]);
        project.Scan(files);
        int total = 0;
        foreach (var file in files)
        foreach (var document in (project.Documents.GetValueOrDefault(file.Name) ?? []).Where(d => d.ClassId == 91))
        {
            try
            {
                var original = file.Read(file.Objects.First(i => i.Id == document.Main));
                var converted = new Controllers(original, document, reason => output.Note("控制器", document.Path, reason, true)).Convert();
                Yaml yaml = new(pointer => project.Reference(file, document, pointer));
                foreach (var item in converted) yaml.Document(item.ClassId, item.Id, item.Value.Schema, item.Value);
                output.Write(document.Path, yaml.ToString());
                total++;
            }
            catch (Exception error) { output.Note("控制器", document.Path, error.ToString(), true); Console.WriteLine(document.Path + ": " + error.Message); }
        }
        output.Report("resources");
        Console.WriteLine($"控制器 {total}，错误 {output.Errors}");
        return output.Errors == 0 ? 0 : 1;
    }

    public static int Textures(string input, string directory)
    {
        using Apk apk = new(input);
        Output output = new(directory);
        Resources resources = new(apk, null);
        Project project = new(output, null!, null!, new());
        var files = resources.Open(resources.Units[0]);
        project.Scan(files);
        project.PrepareTextures();
        foreach (var document in project.Documents.Values.SelectMany(d => d).Where(d => d.Texture is not null))
        {
            try { ReSekai.Textures.Write(document, resources, output); }
            catch (Exception error) { output.Note("纹理", document.Path, error.ToString(), true); Console.WriteLine(document.Path + ": " + error.Message); }
        }
        output.Finish();
        output.Report("resources");
        Console.WriteLine($"纹理文件 {output.Files}，错误 {output.Errors}");
        return output.Errors == 0 ? 0 : 1;
    }

    public static int Shaders(string input, string directory)
    {
        using Apk apk = new(input);
        Output output = new(directory);
        Resources resources = new(apk, null);
        int total = 0, generated = 0;
        foreach (var file in resources.Open(resources.Units[0]))
        foreach (var item in file.Objects.Where(o => file.Types[o.Type].ClassId == 48))
        {
            Value value = file.Read(item);
            string name = value["m_ParsedForm"]["m_Name"].Text;
            total++;
            try
            {
                string source = Shader.Generate(value, reason => output.Note("Shader", name, reason, !reason.StartsWith("关键字组合", StringComparison.Ordinal)));
                if (source.Contains("GLSLPROGRAM")) generated++;
                output.Write($"{Output.Safe(name.Replace('/', '_'))}_{file.Name}_{item.Id}.shader", source);
            }
            catch (Exception error) { output.Note("读取", name, error.ToString(), true); Console.WriteLine(name + ": " + error.Message); }
            if (total % 25 == 0) Console.WriteLine($"Shader {total}，含程序 {generated}");
        }
        output.Report("resources");
        Console.WriteLine($"Shader {generated}/{total}，问题 {output.Errors}");
        return generated > 0 ? 0 : 1;
    }
}
