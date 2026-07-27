using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Android;
using UnityEngine;

public class MetaAndroidNamespaceFixer : IPreprocessBuildWithReport, IPostGenerateGradleAndroidProject
{
    struct AarNamespacePatch
    {
        public string PackageName;
        public string RelativeAarPath;
        public string AndroidNamespace;
        public string GradleModuleName;
    }

    static readonly AarNamespacePatch[] Patches =
    {
        new AarNamespacePatch
        {
            PackageName = "com.meta.xr.sdk.core",
            RelativeAarPath = "Plugins/AndroidOpenXR/OVRPlugin.aar",
            AndroidNamespace = "com.meta.xr.sdk.ovrplugin",
            GradleModuleName = "OVRPlugin"
        },
        new AarNamespacePatch
        {
            PackageName = "com.meta.xr.sdk.interaction",
            RelativeAarPath = "Runtime/Plugins/Android/InteractionSdk.aar",
            AndroidNamespace = "com.meta.xr.sdk.interaction",
            GradleModuleName = "InteractionSdk"
        },
        new AarNamespacePatch
        {
            PackageName = "com.meta.xr.sdk.voice",
            RelativeAarPath = "Lib/Telemetry/Plugins/SDKTelemetry.aar",
            AndroidNamespace = "com.meta.xr.sdk.telemetry",
            GradleModuleName = "SDKTelemetry"
        }
    };

    public int callbackOrder => -1000;

    [MenuItem("Custom/Android/Fix Meta AAR Namespaces")]
    public static void FixMetaAarNamespaces()
    {
        PatchAars();
        PatchGradleTransformCache();
        AssetDatabase.Refresh();
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android)
        {
            return;
        }

        PatchAars();
        PatchGradleTransformCache();
    }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        PatchGradleProject(path);
        PatchGradleTransformCache();
    }

    static void PatchAars()
    {
        foreach (AarNamespacePatch patch in Patches)
        {
            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForPackageName(patch.PackageName);
            if (packageInfo == null)
            {
                continue;
            }

            string aarPath = Path.Combine(packageInfo.resolvedPath, patch.RelativeAarPath);
            PatchAarManifest(aarPath, patch.AndroidNamespace);
        }
    }

    static void PatchAarManifest(string aarPath, string androidNamespace)
    {
        if (!File.Exists(aarPath))
        {
            return;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "MetaAndroidNamespaceFixer", Guid.NewGuid().ToString("N"));
        string tempAarPath = aarPath + ".tmp";

        try
        {
            ZipFile.ExtractToDirectory(aarPath, tempDirectory);
            string manifestPath = Path.Combine(tempDirectory, "AndroidManifest.xml");

            if (!File.Exists(manifestPath))
            {
                return;
            }

            string manifestText = File.ReadAllText(manifestPath);
            string patchedManifest = PatchManifestPackage(manifestText, androidNamespace);
            if (patchedManifest == manifestText)
            {
                return;
            }

            File.WriteAllText(manifestPath, patchedManifest);

            if (File.Exists(tempAarPath))
            {
                File.Delete(tempAarPath);
            }

            ZipFile.CreateFromDirectory(tempDirectory, tempAarPath, System.IO.Compression.CompressionLevel.Optimal, false);
            File.Copy(tempAarPath, aarPath, true);
            Debug.Log($"Patched Android namespace in {aarPath} to {androidNamespace}");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }

            if (File.Exists(tempAarPath))
            {
                File.Delete(tempAarPath);
            }
        }
    }

    static void PatchGradleProject(string gradleProjectPath)
    {
        if (!Directory.Exists(gradleProjectPath))
        {
            return;
        }

        foreach (AarNamespacePatch patch in Patches)
        {
            foreach (string manifestPath in Directory.GetFiles(gradleProjectPath, "AndroidManifest.xml", SearchOption.AllDirectories))
            {
                if (manifestPath.IndexOf(patch.GradleModuleName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                PatchManifestFile(manifestPath, patch.AndroidNamespace);
            }
        }
    }

    static void PatchGradleTransformCache()
    {
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string gradleCachesPath = Path.Combine(userProfile, ".gradle", "caches");
            if (!Directory.Exists(gradleCachesPath))
            {
                return;
            }

            foreach (AarNamespacePatch patch in Patches)
            {
                foreach (string manifestPath in Directory.GetFiles(gradleCachesPath, "AndroidManifest.xml", SearchOption.AllDirectories))
                {
                    if (manifestPath.IndexOf("jetified-" + patch.GradleModuleName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    PatchManifestFile(manifestPath, patch.AndroidNamespace);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Could not patch Gradle transform cache: {exception.Message}");
        }
    }

    static void PatchManifestFile(string manifestPath, string androidNamespace)
    {
        string manifestText = File.ReadAllText(manifestPath);
        string patchedManifest = PatchManifestPackage(manifestText, androidNamespace);
        if (patchedManifest == manifestText)
        {
            return;
        }

        File.WriteAllText(manifestPath, patchedManifest);
        Debug.Log($"Patched Android namespace in {manifestPath} to {androidNamespace}");
    }

    static string PatchManifestPackage(string manifestText, string androidNamespace)
    {
        return Regex.Replace(
            manifestText,
            "package=\"com\\.oculus\\.Integration\"",
            $"package=\"{androidNamespace}\"",
            RegexOptions.CultureInvariant);
    }
}
