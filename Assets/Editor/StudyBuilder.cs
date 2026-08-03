using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Reproducible Android build entry point for the study binary, callable from the command line:
///   Unity -quit -batchmode -projectPath &lt;proj&gt; -buildTarget Android \
///         -executeMethod StudyBuilder.BuildAndroid -logFile - [-buildOutput &lt;path.apk&gt;]
///
/// Batchmode is the correct tool for this: an interactive BuildPlayer can block forever on a
/// modal dialog (e.g. "Android SDK is missing required platform API"), which is exactly what
/// wedged a 3-hour attempt on 2026-07-17. Batchmode surfaces the same conditions as a non-zero
/// exit with a logged reason instead of a silent hang. Only the enabled build scenes are built,
/// so this always matches EditorBuildSettings (VHardStudy) rather than an ad-hoc scene list.
/// </summary>
public static class StudyBuilder
{
    public static void BuildAndroid()
    {
        string output = ArgValue("-buildOutput")
                        ?? Path.Combine(
                            Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath)) ?? ".",
                            "builds",
                            "VHard-study-" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".apk");

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0)
        {
            Fail("No enabled scenes in EditorBuildSettings; nothing to build.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        BuildPlayerOptions options = new()
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        Debug.Log($"[StudyBuilder] Building {string.Join(", ", scenes)} -> {output}");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log($"[StudyBuilder] result={summary.result} sizeBytes={summary.totalSize} " +
                  $"errors={summary.totalErrors} time={summary.totalTime}");

        if (summary.result != BuildResult.Succeeded)
        {
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type is LogType.Error or LogType.Exception)
                    {
                        Debug.LogError($"[StudyBuilder] {step.name}: {message.content}");
                    }
                }
            }
            Fail($"Build result was {summary.result} with {summary.totalErrors} error(s).");
            return;
        }

        if (!File.Exists(output))
        {
            Fail("Build reported success but the APK is missing: " + output);
            return;
        }

        Debug.Log($"[StudyBuilder] SUCCESS {output} ({new FileInfo(output).Length} bytes)");
        EditorApplication.Exit(0);
    }

    private static string ArgValue(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void Fail(string reason)
    {
        Debug.LogError("[StudyBuilder] FAILED: " + reason);
        EditorApplication.Exit(1);
    }
}
