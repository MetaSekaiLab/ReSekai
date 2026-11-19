using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class ReSekaiAndroidBuild
{
    public static void Run()
    {
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "-resekaiApk");
        string destination = Path.GetFullPath(index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1] : "Build/Android/ReSekai.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(destination));

#if UNITY_ANDROID
        string sdk = Environment.GetEnvironmentVariable("RESEKAI_LOCAL_ANDROID_SDK");
        if (!string.IsNullOrEmpty(sdk)) UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = sdk;
#endif
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android,
            Environment.GetEnvironmentVariable("RESEKAI_ANDROID_ID") ?? "net.resekai.rebuilt");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.useCustomKeystore = false;
        EditorUserBuildSettings.buildAppBundle = false;

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
            locationPathName = destination,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Android 构建失败：" + report.summary.result);
    }
}
