using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public static class StudyRehearsalTiming
{
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
