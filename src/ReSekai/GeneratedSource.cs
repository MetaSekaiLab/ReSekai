using System.Text.RegularExpressions;

namespace ReSekai;

static class GeneratedSource
{
    public static string PaletteEditor(string source)
    {
        source = source.ReplaceLineEndings("\n");
        source = ReplaceOnce(source, @"(?m)^(\s*)_instance = (?:global::UnityEngine\.)?ScriptableObject\.CreateInstance<PaletteStore>\(\);$", match =>
            "#if UNITY_EDITOR\n" + match.Groups[1].Value + "_instance = LoadEditorAsset();\n#else\n" + match.Value + "\n#endif", "PaletteStore.Instance");
        return ReplaceOnce(source, @"(?m)^(\t\tprivate void OnEnable\(\)\n\t\t\{\n)(\t\t\t_instance = this;\n)(\t\t\})$", match =>
            match.Groups[1].Value + "#if !UNITY_EDITOR\n" + match.Groups[2].Value + "#endif\n" + match.Groups[3].Value + "\n\n" +
            """
            #if UNITY_EDITOR
                private static PaletteStore LoadEditorAsset()
                {
                    foreach (string guid in global::UnityEditor.AssetDatabase.FindAssets("t:PaletteStore", new[] { "Assets" }))
                    {
                        string path = global::UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                        var store = global::UnityEditor.AssetDatabase.LoadAssetAtPath<PaletteStore>(path);
                        if (store != null) return store;
                    }
                    return null;
                }
            #endif
            """, "PaletteStore.OnEnable");
    }

    public static string CriAudioClock(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?ms)^(\tpublic void Execute\(float unityTime\)\n\t\{\n)(.*?)(^\t\})", match =>
            match.Groups[1].Value + $"#if {CriWareSdk.Windows}\n\t\tplaybackTimeMs = debugAudioSyncedTime = playback.GetTimeSyncedWithAudio();\n#else\n" +
            match.Groups[2].Value + "#endif\n" + match.Groups[3].Value, "AudioSyncedUnityTimer.Execute");

    static string ReplaceOnce(string source, string pattern, MatchEvaluator replacement, string method)
    {
        var matches = Regex.Matches(source, pattern, RegexOptions.CultureInvariant);
        if (matches.Count != 1) throw new InvalidDataException($"导出适配无法匹配 {method}");
        Match match = matches[0];
        return source[..match.Index] + replacement(match) + source[(match.Index + match.Length)..];
    }

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
