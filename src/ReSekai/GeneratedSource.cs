using System.Text.RegularExpressions;

namespace ReSekai;

static class GeneratedSource
{
    public static string MagicaClothCulling(string source)
    {
        // 空列表直接可见，非空列表遍历到第一个可见 Renderer，两个分支在更新状态前汇合。
        const string method = @"(?s)(internal override void UpdateCullingMode\(CoreComponent caller\).*?)";
        const string jump = @"if \(list\.Count < 1\)\s*\{\s*flag3 = true;\s*goto IL_0300;\s*\}";
        const string body = @"\s*using global::System\.Collections\.Generic\.List<global::UnityEngine\.Renderer>\.Enumerator enumerator = list\.GetEnumerator\(\);"
            + @"\s*global::UnityEngine\.Renderer current;\s*do\s*\{\s*flag3 = enumerator\.MoveNext\(\);\s*if \(!flag3\)\s*\{\s*break;\s*\}"
            + @"\s*current = enumerator\.Current;\s*\}\s*while \(!current \|\| !current\.isVisible\);";
        const string join = @"(.*?)\s*IL_0300:(\s*RuntimeStatus runtimeStatus = status;)";
        const string replacement = "${1}flag3 = list.Count == 0;\n"
            + "\t\t\t\t\t\tforeach (global::UnityEngine.Renderer renderer in list)\n"
            + "\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\tif (renderer && renderer.isVisible)\n"
            + "\t\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\tflag3 = true;\n"
            + "\t\t\t\t\t\t\t\tbreak;\n"
            + "\t\t\t\t\t\t\t}\n"
            + "\t\t\t\t\t\t}${2}${3}";
        return Regex.Replace(source, method + jump + body + join, replacement, RegexOptions.CultureInvariant);
    }
}
