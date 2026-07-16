using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class StudyBuildRevisionBuilder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    private const string AssetPath = "Assets/Resources/StudyBuildRevision.txt";
    private const string BackupPath = "Library/VHardStudyBuildRevision.backup";

    [InitializeOnLoadMethod]
    private static void RestoreAfterInterruptedBuild()
    {
        EditorApplication.update -= RestoreWhenBuildIsIdle;
        EditorApplication.update += RestoreWhenBuildIsIdle;
    }

    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string backupPath = Path.Combine(projectRoot, BackupPath);
        if (!File.Exists(backupPath))
        {
            File.WriteAllBytes(backupPath, File.ReadAllBytes(Path.Combine(projectRoot, AssetPath)));
        }
        string revision = RunGit(projectRoot, "rev-parse HEAD");
        if (!string.IsNullOrWhiteSpace(RunGit(projectRoot, "status --porcelain")))
        {
            revision += "-dirty." + ComputeDirtyDigest(projectRoot);
        }

        File.WriteAllText(
            Path.Combine(projectRoot, AssetPath),
            revision + "\n",
            new UTF8Encoding(false));
        AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        RestorePreviousContents();
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo);
        if (process == null)
        {
            throw new BuildFailedException("Could not start git to stamp the study build revision.");
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
        {
            throw new BuildFailedException("Could not stamp the study build revision: " + error.Trim());
        }
        return output.Trim();
    }

    private static string ComputeDirtyDigest(string projectRoot)
    {
        StringBuilder input = new();
        input.Append(RunGit(projectRoot, "diff --binary HEAD -- ."));
        string[] untrackedPaths = RunGit(
                projectRoot,
                "-c core.quotepath=false ls-files --others --exclude-standard")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(untrackedPaths, StringComparer.Ordinal);

        using SHA256 sha256 = SHA256.Create();
        foreach (string relativePath in untrackedPaths)
        {
            string path = Path.Combine(projectRoot, relativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            string fileHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
            input.Append('\0').Append(relativePath).Append(':').Append(fileHash);
        }

        byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(input.ToString()));
        return string.Concat(digest.Take(6).Select(value => value.ToString("x2")));
    }

    private static void RestorePreviousContents()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string backupPath = Path.Combine(projectRoot, BackupPath);
        if (!File.Exists(backupPath))
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(projectRoot, AssetPath), File.ReadAllBytes(backupPath));
        File.Delete(backupPath);
        AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
    }

    private static void RestoreWhenBuildIsIdle()
    {
        if (BuildPipeline.isBuildingPlayer)
        {
            return;
        }

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (File.Exists(Path.Combine(projectRoot, BackupPath)))
        {
            RestorePreviousContents();
            return;
        }

        string assetPath = Path.Combine(projectRoot, AssetPath);
        if (File.Exists(assetPath) && File.ReadAllText(assetPath).Trim() != "development")
        {
            File.WriteAllText(assetPath, "development\n", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
        }
    }
}
