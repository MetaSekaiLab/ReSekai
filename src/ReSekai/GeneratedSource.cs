using System.Text.RegularExpressions;

namespace ReSekai;

static class GeneratedSource
{
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

    static string ReplaceOnce(string source, string pattern, MatchEvaluator replacement, string method)
    {
        var matches = Regex.Matches(source, pattern, RegexOptions.CultureInvariant);
        if (matches.Count != 1) throw new InvalidDataException($"导出适配无法匹配 {method}");
        Match match = matches[0];
        return source[..match.Index] + replacement(match) + source[(match.Index + match.Length)..];
    }
}
