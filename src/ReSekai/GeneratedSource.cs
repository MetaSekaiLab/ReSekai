using System.Text.RegularExpressions;

namespace ReSekai;

static class GeneratedSource
{
    public static string SpineMeshGenerator(string source, string runtime)
    {
        string original = source.ReplaceLineEndings("\n");
        string code = original;
        string upstream = runtime.ReplaceLineEndings("\n");
        foreach (var (name, address) in new[] { ("GenerateSingleSubmeshInstruction", 172783504), ("BuildMeshWithArrays", 172788204), ("FillVertexData", 172795124) })
        {
            string recovered = SpineRuntimeMethod(code, upstream, name, address);
            if (name == "GenerateSingleSubmeshInstruction" && recovered != code)
                recovered = "#define SPINE_TRIANGLECHECK\n" + recovered;
            code = recovered;
        }
        return code == original ? source : code;
    }

    public static string SpineEventQueue(string source, string runtime)
    {
        string original = source.ReplaceLineEndings("\n");
        string code = SpineRuntimeMethod(original, runtime.ReplaceLineEndings("\n"), "Drain", 172500904);
        return code == original ? source : code;
    }

    private static string SpineRuntimeMethod(string source, string runtime, string name, int address)
    {
        string signature = $@"\t\t(?:public|internal)(?: static)? void {name}";
        string stub = $@"(?m)^({signature}\([^\n]*\)\n\t\t\{{\n)\t{{3}}throw global::ReSekai\.Decompiler\.Unrecovered\.Fail\({address}uL\);$";
        if (!Regex.IsMatch(source, stub, RegexOptions.CultureInvariant)) return source;
        var method = Regex.Match(runtime, $@"(?ms)^{signature} \([^\n]*\) \{{\n(.*?)^\t\t\}}$", RegexOptions.CultureInvariant);
        if (!method.Success) throw new InvalidDataException($"Spine 源码缺少对应的 {name}");
        return ReplaceOnce(source, stub, match => match.Groups[1].Value + method.Groups[1].Value.TrimEnd(), name);
    }

    public static string HomeNewsAndroid(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^\t\tprivate NewsDialog OpenUnreadNews\([^\n]+\)\n\t\t\{\n",
        match => match.Value + """
        #if UNITY_ANDROID && !UNITY_EDITOR
                    try
                    {
                        using var webViewInterface = new global::UnityEngine.AndroidJavaClass("com.onevcat.uniwebview.UniWebViewInterface");
                    }
                    catch (global::UnityEngine.AndroidJavaException)
                    {
                        return null;
                    }
        #endif

        """.ReplaceLineEndings("\n"), "ScreenLayerHome.OpenUnreadNews");

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

    public static string CriDisposableRegistration(string source) => Regex.Replace(source.ReplaceLineEndings("\n"),
        @"(?m)^(\t\tpublic static void Register\(CriDisposable disposable, ModuleType type\)\n\t\t\{\n)\t\t\tthrow global::ReSekai\.Decompiler\.Unrecovered\.Fail\(\d+uL\);$",
        match => match.Groups[1].Value + "\t\t\tlock (refList)\n"
            + "\t\t\t{\n\t\t\t\tif (SearchForDisposable(disposable) >= 0)\n"
            + "\t\t\t\t{\n\t\t\t\t\tglobal::UnityEngine.Debug.LogWarning(\"[CRIWARE] Internal: Duplicated object GUID\");\n"
            + "\t\t\t\t\treturn;\n\t\t\t\t}\n"
            + "\t\t\t\trefList.Add(new ObjectRef(disposable.guid, disposable, type));\n\t\t\t}", RegexOptions.CultureInvariant);

    public static string CriCueInfoLayout(string source)
    {
        const string marshal = "global::System.Runtime.InteropServices.";
        string code = source.ReplaceLineEndings("\n");
        code = Regex.Replace(code, @"(?m)^(\t\t\t)(private float\[\] calculationParameters;)$",
            $"$1[{marshal}MarshalAs({marshal}UnmanagedType.ByValArray, SizeConst = 3)]\n$1$2",
            RegexOptions.CultureInvariant);
        return Regex.Replace(code, @"(?m)^(\t\t\t)(public ushort\[\] categories;)$",
            $"$1[{marshal}MarshalAs({marshal}UnmanagedType.ByValArray, SizeConst = 16)]\n$1$2",
            RegexOptions.CultureInvariant);
    }

    public static string CriMovieInfoLayout(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^(\t\t)public AudioInfo\[\] audioPrm;$",
        match => match.Groups[1].Value +
            "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]\n" +
            match.Value, "MovieInfo.audioPrm");

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

    public static string AppLovinStub(string source) => """
        #if UNITY_ANDROID
        [global::Beebyte.Obfuscator.SkipAttribute]
        public class AppLovinManager : global::CP.SingletonMonoBehaviour<AppLovinManager>
        {
            public enum AppLovinAdState
            {
                NotReady,
                Canceled,
                ReceivedReward
            }

            public void CheckATT(global::System.Action callback) => callback?.Invoke();

            public void StartSDKInitialize(global::System.Action initCallback)
            {
                global::CP.SingletonMonoBehaviour<global::Sekai.UserAccountManager>.Instance.LoadOrCreateGudids();
                initCallback?.Invoke();
            }

            public static bool IsSDKInitialized() => false;
            public bool IsChangeIOBufferDuration(double currentIOBufferDuration) => false;
            public void ResetIOBufferDuration(double preferredIOBufferDuration, double currentIOBufferDuration) { }
            public void TryRestoreAudio() { }
            public void DisplayMeditationDebugger() { }

            public void InheritSuccessProgress(global::System.Action callback)
            {
                global::Sekai.ApplicationLocalSettings.DeleteSettingsInherit();
                StartSDKInitialize(callback);
            }

            public void LoadAd(global::System.Action onSucceededAdLoad, global::System.Action onFailedAdLoadOneTime)
                => onFailedAdLoadOneTime?.Invoke();

            public void ShowRewardedAd(global::System.Action onSucceededAdDisplay, global::System.Action onFailedAdDisplay,
                string placement = null)
                => global::CP.SingletonMonoBehaviour<global::Sekai.AdRewardManager>.Instance.CompleteDisplayAd(AppLovinAdState.NotReady);
        }
        #else
        """ + "\n" + source.TrimEnd() + "\n#endif\n";

    public static string RegisterWithoutAds(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?ms)^(\t\tprivate static void OnFinishRegisterUser\(bool isSucccess\)\n\t\t\{\n\t\t\tif \(isSucccess\)\n\t\t\t\{\n)(.*?)(^\t\t\t\}\n\t\t\telse\n)",
        match => match.Groups[1].Value + "#if UNITY_ANDROID\n"
            + "\t\t\t\tglobal::CP.SingletonMonoBehaviour<global::AppLovinManager>.Instance.StartSDKInitialize(Authentication);\n"
            + "#else\n" + match.Groups[2].Value + "#endif\n" + match.Groups[3].Value,
        "TitleController.OnFinishRegisterUser");

    public static string DevelopmentLogging(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?m)^\t\t\tglobal::UnityEngine\.Debug\.unityLogger\.logEnabled = false;$",
        match => "#if !DEVELOPMENT_BUILD\n" + match.Value + "\n#endif", "EnvironmentConfig.SetupLogFliter");

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

    public static string MagicaClothEdgeCapsule(string source) => ReplaceOnce(source.ReplaceLineEndings("\n"),
        @"(?ms)^(\t\t\tprivate (?:unsafe )?bool CapsuleColliderDetection\([^\n]+\)\n)\t\t\t\{\n.*?^\t\t\t\}$",
        match => match.Groups[1].Value + """
                        {
                            var scale = Unity.Mathematics.math.dot(dir, boneSclList[transformIndexList[cindex]]);
                            var size = radiusList[cindex] * scale;
                            var axis = dir * size.x;
                            var previousRotation = rotList[cindex];
                            var previousCenter = posList[cindex];
                            var previousAxis = Unity.Mathematics.math.rotate(previousRotation, axis);
                            MathUtility.ClosestPtSegmentSegment(previousCenter - previousAxis, previousCenter + previousAxis,
                                nextpos0, nextpos1, out float capsuleT, out _, out var capsulePoint, out var edgePoint);

                            // 将上一帧的接触法线转到这帧，再用接触处半径构造碰撞平面。
                            var nextRotation = nextRotList[cindex];
                            var localNormal = Unity.Mathematics.math.rotate(Unity.Mathematics.math.inverse(previousRotation), edgePoint - capsulePoint);
                            var normal = Unity.Mathematics.math.normalize(Unity.Mathematics.math.rotate(nextRotation, localNormal));
                            var nextCenter = nextPosList[cindex];
                            var nextAxis = Unity.Mathematics.math.rotate(nextRotation, axis);
                            var planePoint = Unity.Mathematics.math.lerp(nextCenter - nextAxis, nextCenter + nextAxis, capsuleT)
                                + normal * (Unity.Mathematics.math.lerp(size.y, size.z, capsuleT) + radius);
                            var distance0 = Unity.Mathematics.math.dot(normal, nextpos0 - planePoint);
                            var distance1 = Unity.Mathematics.math.dot(normal, nextpos1 - planePoint);
                            if (distance0 < 0f) corr0 += (nextpos0 - normal * distance0) - nextpos0;
                            if (distance1 < 0f) corr1 += (nextpos1 - normal * distance1) - nextpos1;
                            return distance0 < 0f || distance1 < 0f;
                        }
            """, "EdgeCollisionCalcJob.CapsuleColliderDetection");

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
