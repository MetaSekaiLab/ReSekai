using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

public sealed class ReSekaiFirebaseAndroidBuild : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => int.MaxValue;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        WriteFirebaseResources(path);
        string libraryPath = Path.Combine(path, "build.gradle");
        string library = File.ReadAllText(libraryPath);
        if (!library.Contains("com.google.firebase:firebase-bom:"))
        {
            library = InsertAfter(library, "dependencies {", "\n" +
                "    implementation platform('com.google.firebase:firebase-bom:33.11.0')\n" +
                "    implementation 'com.google.firebase:firebase-analytics:22.4.0'\n" +
                "    implementation 'com.google.firebase:firebase-auth:23.2.0'\n" +
                "    implementation 'com.google.firebase:firebase-messaging'\n");
        }
        if (!library.Contains("com.google.android.gms:play-services-games-v2:"))
            library = InsertAfter(library, "dependencies {", "\n    implementation 'com.google.android.gms:play-services-games-v2:21.0.0'\n");
        if (File.Exists(Path.Combine(Application.dataPath, "Plugins", "Android", "firebase-messaging-cpp.aar")))
        {
            if (!library.Contains("com.google.firebase:firebase-iid:"))
                library = InsertAfter(library, "dependencies {", "\n    implementation 'com.google.firebase:firebase-iid:21.1.0'\n");
            if (!library.Contains("com.google.flatbuffers:flatbuffers-java:"))
                library = InsertAfter(library, "dependencies {", "\n    implementation 'com.google.flatbuffers:flatbuffers-java:1.12.0'\n");
            WriteMessagingServices(path);
        }
        File.WriteAllText(libraryPath, library);

        string settingsPath = Path.Combine(path, "..", "settings.gradle");
        string settings = File.ReadAllText(settingsPath);
        if (!settings.Contains("com.android.tools:r8:"))
        {
            settings = InsertAfter(settings, "pluginManagement {", "\n" +
                "    buildscript {\n" +
                "        repositories { google(); mavenCentral() }\n" +
                "        dependencies { classpath 'com.android.tools:r8:8.6.17' }\n" +
                "    }\n");
        }

        string mirror = Environment.GetEnvironmentVariable("RESEKAI_GOOGLE_MAVEN");
        if (!string.IsNullOrEmpty(mirror))
        {
            if (!Uri.TryCreate(mirror, UriKind.Absolute, out Uri uri) || uri.Scheme != "https" || uri.AbsoluteUri.Contains("'"))
                throw new ArgumentException("RESEKAI_GOOGLE_MAVEN 需要 HTTPS 仓库地址。", nameof(mirror));
            settings = settings.Replace("google()", "maven { url '" + uri.AbsoluteUri + "' }");
        }
        File.WriteAllText(settingsPath, settings);
    }

    static void WriteMessagingServices(string path)
    {
        string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        var manifest = XDocument.Load(file);
        var application = manifest.Root.Element("application");
        XNamespace android = "http://schemas.android.com/apk/res/android";
        Add("com.google.firebase.messaging.cpp.ListenerService", new XElement("intent-filter",
            new XElement("action", new XAttribute(android + "name", "com.google.firebase.MESSAGING_EVENT"))));
        Add("com.google.firebase.messaging.cpp.RegistrationIntentService", null);
        manifest.Save(file);

        void Add(string name, XElement filter)
        {
            if (application.Elements("service").Any(s => (string)s.Attribute(android + "name") == name)) return;
            application.Add(new XElement("service", new XAttribute(android + "name", name), new XAttribute(android + "exported", "false"), filter));
        }
    }

    static void WriteFirebaseResources(string path)
    {
        string configPath = Path.Combine(Application.streamingAssetsPath, "google-services-desktop.json");
        if (!File.Exists(configPath)) return;

        var config = JsonUtility.FromJson<FirebaseConfig>(File.ReadAllText(configPath));
        var client = config.client.Length == 1 ? config.client[0]
            : config.client.Single(c => c.client_info.android_client_info.package_name == PlayerSettings.applicationIdentifier);
        var project = config.project_info;
        var values = new XElement("resources");
        Add("google_app_id", client.client_info.mobilesdk_app_id);
        Add("google_api_key", client.api_key.FirstOrDefault()?.current_key);
        Add("gcm_defaultSenderId", project.project_number);
        Add("project_id", project.project_id);
        Add("google_storage_bucket", project.storage_bucket);
        Add("firebase_database_url", project.firebase_url);

        string directory = Path.Combine(path, "src", "main", "res", "values");
        Directory.CreateDirectory(directory);
        values.Save(Path.Combine(directory, "resekai_firebase.xml"));

        void Add(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            values.Add(new XElement("string", new XAttribute("name", name), new XAttribute("translatable", "false"), value));
        }
    }

    static string InsertAfter(string source, string marker, string text)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Gradle 配置缺少 " + marker);
        return source.Insert(start + marker.Length, text);
    }

#pragma warning disable CS0649 // JsonUtility populates these fields.
    [Serializable]
    sealed class FirebaseConfig { public ProjectInfo project_info; public Client[] client; }
    [Serializable]
    sealed class ProjectInfo { public string project_number, project_id, storage_bucket, firebase_url; }
    [Serializable]
    sealed class Client { public ClientInfo client_info; public ApiKey[] api_key; }
    [Serializable]
    sealed class ClientInfo { public string mobilesdk_app_id; public AndroidClientInfo android_client_info; }
    [Serializable]
    sealed class AndroidClientInfo { public string package_name; }
    [Serializable]
    sealed class ApiKey { public string current_key; }
#pragma warning restore CS0649
}
