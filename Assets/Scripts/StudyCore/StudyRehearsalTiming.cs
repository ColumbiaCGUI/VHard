using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public static class StudyRehearsalTiming
{
    public const float RehearsalDurationSeconds = 300f;

    [Serializable]
    private sealed class RecoveryManifest
    {
        public string participant;
        public int block = int.MinValue;
        public string condition;
        public string route;
        public int retry = int.MinValue;
        public bool adhoc = true;
        public string endUtc;
        public string endReason;
    }

    private sealed class RecoveryCandidate
    {
        public string directory;
        public int retry;
    }

    private sealed class AggregateMergeState
    {
        public double secondsTouched;
        public int gripsDetected;
        public double scoreSum;
        public int scoreSamples;
        public float maxScore = -1f;
    }

    public sealed class ActiveManualRunRecovery
    {
        public string DirectoryPath { get; internal set; }
        public string SourceManifestPath { get; internal set; }
        public StudySessionManifest Manifest { get; internal set; }
        public DateTimeOffset RehearsalStartUtc { get; internal set; }
        public DateTimeOffset RehearsalDeadlineUtc { get; internal set; }

        public bool IsExpired(DateTimeOffset utcNow)
        {
            return utcNow >= RehearsalDeadlineUtc;
        }

        public float GetElapsedSeconds(DateTimeOffset utcNow)
        {
            if (utcNow < RehearsalStartUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(utcNow),
                    "Recovery time cannot precede the persisted rehearsal start.");
            }

            return (float)Math.Min(
                RehearsalDurationSeconds,
                (utcNow - RehearsalStartUtc).TotalSeconds);
        }
    }

    public static bool TryGetFirstInteraction(
        string condition,
        bool gripLocomotionActive,
        bool ghostDetached,
        out string interaction)
    {
        if (string.Equals(condition, "B", StringComparison.Ordinal) && gripLocomotionActive)
        {
            interaction = "GripLocomotionEngaged";
            return true;
        }
        if (string.Equals(condition, "C", StringComparison.Ordinal) && ghostDetached)
        {
            interaction = "HoldDetached";
            return true;
        }

        interaction = string.Empty;
        return false;
    }

    public static HoldAggregateData[] MergeHoldAggregates(
        HoldAggregateData[] baseline,
        HoldAggregateData[] currentSegment)
    {
        Dictionary<string, AggregateMergeState> merged = new(StringComparer.Ordinal);
        AddHoldAggregates(merged, baseline);
        AddHoldAggregates(merged, currentSegment);

        HoldAggregateData[] result = new HoldAggregateData[merged.Count];
        int index = 0;
        foreach (KeyValuePair<string, AggregateMergeState> pair in merged)
        {
            AggregateMergeState aggregate = pair.Value;
            result[index++] = new HoldAggregateData
            {
                hold = pair.Key,
                secondsTouched = (float)aggregate.secondsTouched,
                gripsDetected = aggregate.gripsDetected,
                meanScore = aggregate.scoreSamples > 0
                    ? (float)(aggregate.scoreSum / aggregate.scoreSamples)
                    : -1f,
                maxScore = aggregate.maxScore,
                scoreSamples = aggregate.scoreSamples,
            };
        }
        Array.Sort(result, (left, right) => string.CompareOrdinal(left.hold, right.hold));
        return result;
    }

    private static void AddHoldAggregates(
        IDictionary<string, AggregateMergeState> destination,
        IEnumerable<HoldAggregateData> source)
    {
        if (source == null)
        {
            return;
        }

        foreach (HoldAggregateData aggregate in source)
        {
            if (aggregate == null || string.IsNullOrWhiteSpace(aggregate.hold) ||
                float.IsNaN(aggregate.secondsTouched) || float.IsInfinity(aggregate.secondsTouched) ||
                aggregate.secondsTouched < 0f || aggregate.gripsDetected < 0 ||
                aggregate.scoreSamples < 0 || float.IsNaN(aggregate.meanScore) ||
                float.IsInfinity(aggregate.meanScore) || float.IsNaN(aggregate.maxScore) ||
                float.IsInfinity(aggregate.maxScore))
            {
                throw new InvalidDataException("Hold aggregate data is malformed.");
            }

            if (!destination.TryGetValue(aggregate.hold, out AggregateMergeState merged))
            {
                merged = new AggregateMergeState();
                destination.Add(aggregate.hold, merged);
            }
            merged.secondsTouched += aggregate.secondsTouched;
            merged.gripsDetected = checked(merged.gripsDetected + aggregate.gripsDetected);

            int scoreSamples = aggregate.scoreSamples;
            if (scoreSamples == 0 && aggregate.meanScore >= 0f && aggregate.secondsTouched > 0f)
            {
                scoreSamples = Math.Max(
                    1,
                    (int)Math.Round(
                        aggregate.secondsTouched / RecordingBlockSession.CaptureIntervalSeconds));
            }
            if (scoreSamples > 0)
            {
                if (aggregate.meanScore < 0f || aggregate.maxScore < 0f)
                {
                    throw new InvalidDataException("Scored hold aggregate data is malformed.");
                }
                merged.scoreSum += aggregate.meanScore * scoreSamples;
                merged.scoreSamples = checked(merged.scoreSamples + scoreSamples);
                merged.maxScore = Math.Max(merged.maxScore, aggregate.maxScore);
            }
        }
    }

    public static bool TryConsumeArmedPinch(
        bool trackingConfident,
        bool pinching,
        ref bool wasPinching,
        ref bool pinchArmed)
    {
        if (!trackingConfident)
        {
            wasPinching = false;
            pinchArmed = false;
            return false;
        }

        if (!pinching)
        {
            wasPinching = false;
            pinchArmed = true;
            return false;
        }

        bool pinchStarted = !wasPinching;
        wasPinching = true;
        if (!pinchStarted || !pinchArmed)
        {
            return false;
        }

        pinchArmed = false;
        return true;
    }

    public static string FormatElapsedSeconds(float elapsedSeconds)
    {
        if (float.IsNaN(elapsedSeconds) || float.IsInfinity(elapsedSeconds) || elapsedSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        int totalSeconds = Mathf.FloorToInt(elapsedSeconds);
        return (totalSeconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    public static string FormatRemainingSeconds(float remainingSeconds)
    {
        if (float.IsNaN(remainingSeconds) || float.IsInfinity(remainingSeconds) || remainingSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
        }

        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        return (totalSeconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    public static float ResolveElapsedSeconds(
        double elapsedBeforeCurrentProcess,
        double monotonicStartSeconds,
        double monotonicNowSeconds)
    {
        if (double.IsNaN(elapsedBeforeCurrentProcess) ||
            double.IsInfinity(elapsedBeforeCurrentProcess) ||
            elapsedBeforeCurrentProcess < 0d ||
            double.IsNaN(monotonicStartSeconds) || double.IsInfinity(monotonicStartSeconds) ||
            double.IsNaN(monotonicNowSeconds) || double.IsInfinity(monotonicNowSeconds) ||
            monotonicStartSeconds < 0d || monotonicNowSeconds < monotonicStartSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(monotonicNowSeconds));
        }

        return (float)(elapsedBeforeCurrentProcess + monotonicNowSeconds - monotonicStartSeconds);
    }

    public static bool TryRecoverActiveManualRun(
        string manualRoot,
        string expectedCatalogSha256,
        IEnumerable<string> approvedRoutes,
        DateTimeOffset utcNow,
        out ActiveManualRunRecovery recovery,
        out string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(manualRoot))
        {
            throw new ArgumentException("Manual study root is required.", nameof(manualRoot));
        }
        if (string.IsNullOrWhiteSpace(expectedCatalogSha256))
        {
            throw new ArgumentException("Approved catalog hash is required.", nameof(expectedCatalogSha256));
        }
        if (approvedRoutes == null)
        {
            throw new ArgumentNullException(nameof(approvedRoutes));
        }

        HashSet<string> approvedRouteIds = new(approvedRoutes, StringComparer.Ordinal);
        if (approvedRouteIds.Count == 0)
        {
            throw new ArgumentException("At least one approved route is required.", nameof(approvedRoutes));
        }

        recovery = null;
        diagnostic = string.Empty;
        if (!Directory.Exists(manualRoot))
        {
            return false;
        }

        List<string> diagnostics = new();
        List<ActiveManualRunRecovery> candidates = new();
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(manualRoot);
            Array.Sort(directories, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            diagnostic = "Failed to enumerate manual-run recovery candidates under '" +
                         manualRoot + "'." + Environment.NewLine + exception;
            return false;
        }

        foreach (string directory in directories)
        {
            if (!TryReadLatestCompleteManifest(
                    directory,
                    out StudySessionManifest manifest,
                    out string sourceManifestPath,
                    out string readDiagnostic))
            {
                diagnostics.Add("Rejected manual-run recovery directory '" + directory +
                                "': " + readDiagnostic);
                continue;
            }
            bool activeManifestValid = TryValidateActiveManualManifest(
                    manifest,
                    expectedCatalogSha256,
                    approvedRouteIds,
                    utcNow,
                    out DateTimeOffset rehearsalStartUtc,
                    out DateTimeOffset rehearsalDeadlineUtc,
                    out string rejection);
            string canonicalPath = Path.Combine(directory, "session.json");
            if (!activeManifestValid)
            {
                if (string.IsNullOrEmpty(rejection) &&
                    !string.Equals(sourceManifestPath, canonicalPath, StringComparison.Ordinal))
                {
                    StudyManifestStorage.RestoreCanonical(
                        canonicalPath,
                        File.ReadAllText(sourceManifestPath));
                }
                if (!string.IsNullOrEmpty(rejection))
                {
                    diagnostics.Add("Rejected manual-run recovery directory '" + directory +
                                    "': " + rejection);
                }
                continue;
            }
            if (!string.Equals(sourceManifestPath, canonicalPath, StringComparison.Ordinal))
            {
                StudyManifestStorage.RestoreCanonical(
                    canonicalPath,
                    File.ReadAllText(sourceManifestPath));
                sourceManifestPath = canonicalPath;
            }

            candidates.Add(new ActiveManualRunRecovery
            {
                DirectoryPath = directory,
                SourceManifestPath = sourceManifestPath,
                Manifest = manifest,
                RehearsalStartUtc = rehearsalStartUtc,
                RehearsalDeadlineUtc = rehearsalDeadlineUtc,
            });
        }

        candidates.Sort((left, right) =>
        {
            int timeComparison = left.RehearsalStartUtc.CompareTo(right.RehearsalStartUtc);
            return timeComparison != 0
                ? timeComparison
                : string.Compare(left.DirectoryPath, right.DirectoryPath, StringComparison.Ordinal);
        });
        if (candidates.Count == 0)
        {
            diagnostic = string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        if (candidates.Count > 1)
        {
            diagnostics.Add("Multiple active manual runs were found; recovery requires manual reconciliation.");
            diagnostic = string.Join(Environment.NewLine, diagnostics);
            return false;
        }
        recovery = candidates[0];
        diagnostic = string.Join(Environment.NewLine, diagnostics);
        return true;
    }

    private static bool TryReadLatestCompleteManifest(
        string directory,
        out StudySessionManifest manifest,
        out string sourceManifestPath,
        out string diagnostic)
    {
        manifest = null;
        sourceManifestPath = string.Empty;
        diagnostic = string.Empty;
        string canonicalPath = Path.Combine(directory, "session.json");
        string[] paths;
        try
        {
            paths = StudyManifestStorage.GetRecoveryPaths(canonicalPath);
        }
        catch (Exception exception)
        {
            diagnostic = "failed to enumerate session manifest files." + Environment.NewLine + exception;
            return false;
        }
        if (paths.Length == 0)
        {
            diagnostic = "session.json and its recovery files are missing.";
            return false;
        }

        Array.Sort(paths, (left, right) =>
        {
            int timeComparison = File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left));
            return timeComparison != 0
                ? timeComparison
                : string.Compare(right, left, StringComparison.Ordinal);
        });
        List<string> failures = new();
        foreach (string recoveryPath in paths)
        {
            string path = recoveryPath;
            try
            {
                string json = File.ReadAllText(path);
                string trimmedJson = json.Trim();
                if (trimmedJson.Length < 2 || trimmedJson[0] != '{' ||
                    trimmedJson[trimmedJson.Length - 1] != '}')
                {
                    throw new FormatException("manifest must contain one complete JSON object.");
                }

                StudySessionManifest parsed = JsonUtility.FromJson<StudySessionManifest>(trimmedJson);
                if (parsed == null)
                {
                    throw new FormatException("manifest JSON did not produce a session object.");
                }
                manifest = parsed;
                sourceManifestPath = path;
                diagnostic = string.Join(Environment.NewLine, failures);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add("Could not read '" + path + "'." + Environment.NewLine + exception);
            }
        }

        diagnostic = string.Join(Environment.NewLine, failures);
        return false;
    }

    private static bool TryValidateActiveManualManifest(
        StudySessionManifest manifest,
        string expectedCatalogSha256,
        ISet<string> approvedRoutes,
        DateTimeOffset utcNow,
        out DateTimeOffset rehearsalStartUtc,
        out DateTimeOffset rehearsalDeadlineUtc,
        out string rejection)
    {
        rehearsalStartUtc = default;
        rehearsalDeadlineUtc = default;
        if (!manifest.adhoc || !string.Equals(manifest.participant, "UNASSIGNED", StringComparison.Ordinal) ||
            manifest.block != 0)
        {
            rejection = "manifest is not an unassigned manual run.";
            return false;
        }
        if (!string.Equals(manifest.condition, "B", StringComparison.Ordinal) &&
            !string.Equals(manifest.condition, "C", StringComparison.Ordinal))
        {
            rejection = "manifest condition is not canonical B or C.";
            return false;
        }
        if (!approvedRoutes.Contains(manifest.route))
        {
            rejection = "manifest route is not in the approved catalog.";
            return false;
        }
        if (!string.Equals(
                manifest.routeCatalogSha256,
                expectedCatalogSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            rejection = "manifest catalog hash does not match the approved catalog.";
            return false;
        }
        if (!TryParseRoundTripTimestamp(manifest.startUtc, out DateTimeOffset sessionStartUtc))
        {
            rejection = "manifest startUtc is invalid.";
            return false;
        }
        if (!TryParseRoundTripTimestamp(manifest.rehearsalStartUtc, out rehearsalStartUtc))
        {
            rejection = "manifest rehearsalStartUtc is invalid.";
            return false;
        }
        if (!TryParseRoundTripTimestamp(manifest.rehearsalDeadlineUtc, out rehearsalDeadlineUtc))
        {
            rejection = "manifest rehearsalDeadlineUtc is invalid.";
            return false;
        }
        if (sessionStartUtc > rehearsalStartUtc)
        {
            rejection = "manifest rehearsal starts before its session.";
            return false;
        }
        if (sessionStartUtc > utcNow || rehearsalStartUtc > utcNow)
        {
            rejection = "manifest start timestamp is in the future.";
            return false;
        }
        double durationSeconds = (rehearsalDeadlineUtc - rehearsalStartUtc).TotalSeconds;
        if (Math.Abs(durationSeconds - RehearsalDurationSeconds) > 0.001d)
        {
            rejection = "manifest rehearsal deadline is not exactly five minutes after its start.";
            return false;
        }
        if (manifest.resumeCount < 0 || manifest.pendingResumeIndex < 0)
        {
            rejection = "manifest resume indices are negative.";
            return false;
        }
        if (manifest.pendingResumeIndex != 0 &&
            manifest.pendingResumeIndex != manifest.resumeCount + 1)
        {
            rejection = "manifest pendingResumeIndex is inconsistent with resumeCount.";
            return false;
        }

        bool hasEndTimestamp = !string.IsNullOrWhiteSpace(manifest.endUtc);
        bool hasRunningReason = string.Equals(manifest.endReason, "running", StringComparison.Ordinal);
        if (!hasEndTimestamp && hasRunningReason)
        {
            rejection = string.Empty;
            return true;
        }
        if (!hasEndTimestamp || hasRunningReason || string.IsNullOrWhiteSpace(manifest.endReason))
        {
            rejection = "manifest terminal state is inconsistent.";
            return false;
        }
        if (!TryParseRoundTripTimestamp(manifest.endUtc, out DateTimeOffset endUtc) ||
            endUtc < rehearsalStartUtc || endUtc > utcNow)
        {
            rejection = "manifest endUtc is invalid for its rehearsal interval.";
            return false;
        }
        if (manifest.pendingResumeIndex != 0)
        {
            rejection = "completed manifest still has a pending resume transaction.";
            return false;
        }

        rejection = string.Empty;
        return false;
    }

    private static bool TryParseRoundTripTimestamp(string value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
    }

    public static bool TryConfirmPanelAction(
        string actionKey,
        float now,
        int frame,
        float confirmationWindowSeconds,
        ref string pendingActionKey,
        ref float confirmationDeadline,
        ref int confirmationArmedFrame)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            throw new ArgumentException("Confirmation action key is required.", nameof(actionKey));
        }
        if (float.IsNaN(now) || float.IsInfinity(now) || now < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }
        if (float.IsNaN(confirmationWindowSeconds) || float.IsInfinity(confirmationWindowSeconds) ||
            confirmationWindowSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationWindowSeconds));
        }

        if (string.Equals(pendingActionKey, actionKey, StringComparison.Ordinal))
        {
            if (frame <= confirmationArmedFrame)
            {
                return false;
            }
            if (now <= confirmationDeadline)
            {
                pendingActionKey = string.Empty;
                confirmationDeadline = -1f;
                confirmationArmedFrame = -1;
                return true;
            }
        }

        pendingActionKey = actionKey;
        confirmationDeadline = now + confirmationWindowSeconds;
        confirmationArmedFrame = frame;
        return false;
    }

    public static bool RequiresPanelSummonDwell(bool blockRunning, bool auxiliarySequenceActive)
    {
        return blockRunning || auxiliarySequenceActive;
    }

    public static Vector3 ResolvePanelDragPosition(
        Vector3 pointerOrigin,
        Vector3 pointerDirection,
        float rayDistance,
        Vector3 worldOffset)
    {
        if (!IsFinite(pointerOrigin) || !IsFinite(pointerDirection) || !IsFinite(worldOffset))
        {
            throw new ArgumentException("Panel drag vectors must be finite.");
        }
        if (pointerDirection.sqrMagnitude < 0.000001f)
        {
            throw new ArgumentException("Panel drag direction must be non-zero.", nameof(pointerDirection));
        }
        if (float.IsNaN(rayDistance) || float.IsInfinity(rayDistance) || rayDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(rayDistance));
        }

        return pointerOrigin + pointerDirection.normalized * rayDistance + worldOffset;
    }

    public static Vector3 ClampPanelViewportPosition(
        Vector3 viewportPosition,
        Vector2 halfExtents,
        float margin,
        float minimumDepth,
        float maximumDepth)
    {
        if (!IsFinite(viewportPosition) || !IsFinite(halfExtents))
        {
            throw new ArgumentException("Panel viewport values must be finite.");
        }
        if (halfExtents.x < 0f || halfExtents.y < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(halfExtents));
        }
        if (float.IsNaN(margin) || float.IsInfinity(margin) || margin < 0f || margin >= 0.5f)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }
        if (float.IsNaN(minimumDepth) || float.IsInfinity(minimumDepth) || minimumDepth <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDepth));
        }
        if (float.IsNaN(maximumDepth) || float.IsInfinity(maximumDepth) || maximumDepth < minimumDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        float minimumX = margin + halfExtents.x;
        float maximumX = 1f - margin - halfExtents.x;
        float minimumY = margin + halfExtents.y;
        float maximumY = 1f - margin - halfExtents.y;
        return new Vector3(
            minimumX <= maximumX ? Mathf.Clamp(viewportPosition.x, minimumX, maximumX) : 0.5f,
            minimumY <= maximumY ? Mathf.Clamp(viewportPosition.y, minimumY, maximumY) : 0.5f,
            Mathf.Clamp(viewportPosition.z, minimumDepth, maximumDepth));
    }

    public static bool CanStartPractice(
        string participant,
        ISet<string> participantsWithPracticeRuns,
        ISet<string> participantsWithBlockRuns)
    {
        if (string.IsNullOrWhiteSpace(participant))
        {
            throw new ArgumentException("Participant is required.", nameof(participant));
        }
        if (participantsWithPracticeRuns == null)
        {
            throw new ArgumentNullException(nameof(participantsWithPracticeRuns));
        }
        if (participantsWithBlockRuns == null)
        {
            throw new ArgumentNullException(nameof(participantsWithBlockRuns));
        }

        return !participantsWithPracticeRuns.Contains(participant) &&
               !participantsWithBlockRuns.Contains(participant);
    }

    public static bool IsEstimationSelectionMatch(
        string selectedParticipant,
        int selectedBlock,
        string endedParticipant,
        int endedBlock)
    {
        if (string.IsNullOrWhiteSpace(selectedParticipant))
        {
            throw new ArgumentException("Selected participant is required.", nameof(selectedParticipant));
        }
        if (string.IsNullOrWhiteSpace(endedParticipant))
        {
            throw new ArgumentException("Ended participant is required.", nameof(endedParticipant));
        }
        if (selectedBlock < 1 || selectedBlock > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedBlock));
        }
        if (endedBlock < 1 || endedBlock > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(endedBlock));
        }

        return selectedBlock == endedBlock &&
               string.Equals(selectedParticipant, endedParticipant, StringComparison.Ordinal);
    }

    public static bool HasRecordedEstimation(string participantRoot, int block)
    {
        if (string.IsNullOrWhiteSpace(participantRoot))
        {
            throw new ArgumentException("Participant root is required.", nameof(participantRoot));
        }
        if (block < 1 || block > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(block));
        }
        if (!Directory.Exists(participantRoot))
        {
            return false;
        }

        string blockPrefix = "block" + block + "_";
        foreach (string blockDirectory in Directory.EnumerateDirectories(participantRoot))
        {
            if (!Path.GetFileName(blockDirectory).StartsWith(blockPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string childDirectory in Directory.EnumerateDirectories(blockDirectory))
            {
                string name = Path.GetFileName(childDirectory);
                if (name == "estimation" || name.StartsWith("estimation_retry", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool TryRecoverCompletedBlock(
        string studyRoot,
        StudyScheduleRow scheduleRow,
        out string recoveredDirectory,
        out string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(studyRoot))
        {
            throw new ArgumentException("Study root is required.", nameof(studyRoot));
        }
        ValidateRecoveryScheduleRow(scheduleRow);

        recoveredDirectory = string.Empty;
        diagnostic = string.Empty;
        string participantRoot = Path.Combine(studyRoot, scheduleRow.participant);
        if (!Directory.Exists(participantRoot))
        {
            return false;
        }

        string baseName = "block" + scheduleRow.block.ToString(CultureInfo.InvariantCulture) + "_" +
                          scheduleRow.condition + "_" + SanitizePathToken(scheduleRow.route);
        List<string> rejections = new();
        List<RecoveryCandidate> candidates = new();
        try
        {
            string[] directories = Directory.GetDirectories(participantRoot);
            Array.Sort(directories, StringComparer.Ordinal);
            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, baseName, StringComparison.Ordinal))
                {
                    candidates.Add(new RecoveryCandidate { directory = directory, retry = 0 });
                    continue;
                }

                string retryPrefix = baseName + "_retry";
                if (!name.StartsWith(retryPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string retryText = name.Substring(retryPrefix.Length);
                if (!int.TryParse(
                        retryText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int retry) ||
                    retry <= 0 ||
                    !string.Equals(
                        retryText,
                        retry.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    rejections.Add("Rejected completed-block recovery directory '" + directory +
                                   "': retry suffix is not a canonical positive integer.");
                    continue;
                }
                candidates.Add(new RecoveryCandidate { directory = directory, retry = retry });
            }
        }
        catch (Exception exception)
        {
            diagnostic = "Failed to enumerate completed-block recovery candidates under '" +
                         participantRoot + "'." + Environment.NewLine + exception;
            return false;
        }

        candidates.Sort((left, right) =>
        {
            int retryComparison = left.retry.CompareTo(right.retry);
            return retryComparison != 0
                ? retryComparison
                : string.Compare(left.directory, right.directory, StringComparison.Ordinal);
        });

        int recoveredRetry = -1;
        foreach (RecoveryCandidate candidate in candidates)
        {
            string manifestPath = Path.Combine(candidate.directory, "session.json");
            try
            {
                if (!File.Exists(manifestPath))
                {
                    rejections.Add("Rejected completed-block recovery candidate '" +
                                   candidate.directory + "': session.json is missing.");
                    continue;
                }

                string json = File.ReadAllText(manifestPath);
                if (!TryValidateRecoveryManifest(
                        json,
                        scheduleRow,
                        candidate.retry,
                        out string rejection))
                {
                    rejections.Add("Rejected completed-block recovery candidate '" +
                                   candidate.directory + "': " + rejection);
                    continue;
                }

                if (candidate.retry > recoveredRetry)
                {
                    recoveredDirectory = candidate.directory;
                    recoveredRetry = candidate.retry;
                }
            }
            catch (Exception exception)
            {
                rejections.Add("Rejected completed-block recovery candidate '" +
                               candidate.directory + "' while reading '" + manifestPath + "'." +
                               Environment.NewLine + exception);
            }
        }

        diagnostic = string.Join(Environment.NewLine, rejections);
        return recoveredRetry >= 0;
    }

    private static bool TryValidateRecoveryManifest(
        string json,
        StudyScheduleRow scheduleRow,
        int directoryRetry,
        out string rejection)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("session.json is empty.");
        }
        string trimmedJson = json.Trim();
        if (trimmedJson.Length < 2 || trimmedJson[0] != '{' ||
            trimmedJson[trimmedJson.Length - 1] != '}')
        {
            throw new FormatException("session.json must contain one complete JSON object.");
        }

        RecoveryManifest manifest = new();
        JsonUtility.FromJsonOverwrite(trimmedJson, manifest);
        if (!string.Equals(manifest.participant, scheduleRow.participant, StringComparison.Ordinal))
        {
            rejection = "manifest participant does not match the loaded schedule row.";
            return false;
        }
        if (manifest.block != scheduleRow.block)
        {
            rejection = "manifest block does not match the loaded schedule row.";
            return false;
        }
        if (!string.Equals(manifest.condition, scheduleRow.condition, StringComparison.Ordinal))
        {
            rejection = "manifest condition does not match the loaded schedule row.";
            return false;
        }
        if (!string.Equals(manifest.route, scheduleRow.route, StringComparison.Ordinal))
        {
            rejection = "manifest route does not match the loaded schedule row.";
            return false;
        }
        if (manifest.retry != directoryRetry)
        {
            rejection = "manifest retry does not match its block directory.";
            return false;
        }
        if (manifest.adhoc)
        {
            rejection = "manifest is adhoc or does not explicitly declare adhoc=false.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.endUtc))
        {
            rejection = "manifest endUtc is empty.";
            return false;
        }
        if (!DateTimeOffset.TryParse(
                manifest.endUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            rejection = "manifest endUtc is not a valid round-trip timestamp.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.endReason))
        {
            rejection = "manifest endReason is empty.";
            return false;
        }
        if (string.Equals(manifest.endReason.Trim(), "running", StringComparison.OrdinalIgnoreCase))
        {
            rejection = "manifest endReason is still running.";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    private static void ValidateRecoveryScheduleRow(StudyScheduleRow row)
    {
        if (row == null)
        {
            throw new ArgumentNullException(nameof(row));
        }
        if (!IsApprovedParticipantId(row.participant))
        {
            throw new ArgumentException("Schedule participant is invalid.", nameof(row));
        }
        if (row.block < 1 || row.block > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(row), "Schedule block must be 1, 2, or 3.");
        }
        if (row.condition != "A" && row.condition != "B" && row.condition != "C")
        {
            throw new ArgumentException("Schedule condition must be A, B, or C.", nameof(row));
        }
        if (string.IsNullOrWhiteSpace(row.route) || string.IsNullOrEmpty(SanitizePathToken(row.route)))
        {
            throw new ArgumentException("Schedule route is required.", nameof(row));
        }
    }

    private static bool IsApprovedParticipantId(string participant)
    {
        if (participant == null || (participant.Length != 3 && participant.Length != 4) ||
            participant[0] != 'P')
        {
            return false;
        }
        for (int index = 1; index < participant.Length; index++)
        {
            if (participant[index] < '0' || participant[index] > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizePathToken(string value)
    {
        StringBuilder output = new(value.Length);
        foreach (char character in value.ToUpperInvariant())
        {
            output.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        return output.ToString().Trim('_');
    }

    public static float ResolveDonningStartRealtime(
        float headsetPresentSinceRealtime,
        float blockStartRealtime)
    {
        if (float.IsNaN(blockStartRealtime) || float.IsInfinity(blockStartRealtime) ||
            blockStartRealtime < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(blockStartRealtime));
        }
        if (float.IsNaN(headsetPresentSinceRealtime) ||
            float.IsInfinity(headsetPresentSinceRealtime) ||
            headsetPresentSinceRealtime < 0f ||
            headsetPresentSinceRealtime > blockStartRealtime)
        {
            return blockStartRealtime;
        }
        return headsetPresentSinceRealtime;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y);
    }
}

public static class StudyManifestStorage
{
    public static void RestoreCanonical(string manifestPath, string contents)
    {
        WriteAtomically(manifestPath, contents, File.Exists(manifestPath));
        DeleteRecoveryFiles(manifestPath);
    }

    public static void WriteAtomically(string manifestPath, string contents, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        }
        if (contents == null)
        {
            throw new ArgumentNullException(nameof(contents));
        }

        string directory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Manifest directory does not exist: " + directory);
        }
        bool destinationExists = File.Exists(manifestPath);
        if (overwriteExisting != destinationExists)
        {
            throw new IOException(overwriteExisting
                ? "Active study manifest disappeared before update: " + manifestPath
                : "Refusing to overwrite existing study manifest: " + manifestPath);
        }

        string temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
        using (FileStream stream = new(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        if (!overwriteExisting)
        {
            File.Move(temporaryPath, manifestPath);
            return;
        }

        string backupPath = manifestPath + ".bak";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
        File.Replace(temporaryPath, manifestPath, backupPath);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    public static string[] GetRecoveryPaths(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        }

        string directory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        string fileName = Path.GetFileName(manifestPath);
        List<string> paths = new();
        if (File.Exists(manifestPath))
        {
            paths.Add(manifestPath);
        }
        string backupPath = manifestPath + ".bak";
        if (File.Exists(backupPath))
        {
            paths.Add(backupPath);
        }
        foreach (string temporaryPath in Directory.GetFiles(directory, fileName + "*.tmp"))
        {
            paths.Add(temporaryPath);
        }
        return paths.ToArray();
    }

    public static void DeleteRecoveryFiles(string manifestPath)
    {
        foreach (string path in GetRecoveryPaths(manifestPath))
        {
            if (!string.Equals(path, manifestPath, StringComparison.Ordinal) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
