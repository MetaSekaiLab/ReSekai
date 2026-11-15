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

    public static string EditorRenderCleanup(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^(\t\tpublic static void Destroy\(global::UnityEngine.Object obj\)\n\t\t\{\n\t\t\tif \(obj != null\)\n\t\t\t\{\n)(\t\t\t\tglobal::UnityEngine.Object.Destroy\(obj\);)$",
        match => match.Groups[1].Value + """
        #if UNITY_EDITOR
                        if (!global::UnityEngine.Application.isPlaying)
                        {
                            global::UnityEngine.Object.DestroyImmediate(obj);
                            return;
                        }
        #endif

        """ + match.Groups[2].Value, "CoreUtils.Destroy");

    public static string EditorDirectTitle(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^\t\tprotected override void Awake\(\)\n\t\t\{\n",
        match => match.Value + "#if UNITY_EDITOR\n\t\t\tSekaiSingletonManager.RegisterFormatters();\n#endif\n", "TitleController.Awake");

    public static string RegisterFormatters(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^(\t\tpublic override void OnInitialize\(\)\n\t\t\{\n)(\t\t\tglobal::MessagePack.Resolvers.CompositeResolver.RegisterAndSetAsDefault\([^\n]+\);)$",
        match => "\t\tinternal static void RegisterFormatters()\n\t\t{\n"
            + "\t\t\tif (ReferenceEquals(global::MessagePack.MessagePackSerializer.DefaultResolver, global::MessagePack.Resolvers.CompositeResolver.Instance)) return;\n"
            + match.Groups[2].Value + "\n\t\t}\n\n" + match.Groups[1].Value + "\t\t\tRegisterFormatters();", "SekaiSingletonManager.OnInitialize");

    public static string WindowsFastAes(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?ms)^(\tpublic static IFastAESAlgorithm Create\(\)\n\t\{\n)(.*?)(^\t\})$",
        match => match.Groups[1].Value + $"#if {CriWareSdk.Windows}\n\t\treturn new FastAESAlgorithmDotnet();\n#else\n"
            + match.Groups[2].Value + "#endif\n" + match.Groups[3].Value, "FastAESAlgorithmFactory.Create");

    public static string MagicaClothPostProcess(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?ms)^(\t\tinternal struct PostProcessTeamDataJob\b.*?^\t\t\})$",
        match => match.Value
            .Replace("void* unsafeBufferPointerWithoutChecks = global::Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(teamData);",
                "ref TeamData team = ref global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.ArrayElementAsRef<TeamData>(global::Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(teamData), index);", StringComparison.Ordinal)
            .Replace("((TeamData*)((byte*)unsafeBufferPointerWithoutChecks + index * (uint)sizeof(TeamData)))->", "team.", StringComparison.Ordinal)
            .Replace("void* unsafeBufferPointerWithoutChecks2 = global::Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(teamWorldInfluenceList);",
                "ref WorldInfluence influence = ref global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.ArrayElementAsRef<WorldInfluence>(global::Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(teamWorldInfluenceList), index);", StringComparison.Ordinal)
            .Replace("((WorldInfluence*)((byte*)unsafeBufferPointerWithoutChecks2 + index * (uint)sizeof(WorldInfluence)))->", "influence.", StringComparison.Ordinal),
        "PostProcessTeamDataJob.Execute");

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
