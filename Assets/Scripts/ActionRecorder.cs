using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEngine;

public sealed class ActionRecorder : MonoBehaviour
{
    private const int WriterShutdownTimeoutMilliseconds = 10000;

    public Transform playerHead;
    public SceneConfiguror sceneConfiguror;
    public bool recordToConsole;
    public bool recordToCsv = true;

    private readonly CaptureFrame captureFrame = new();
    private RecordingBlockSession recordingSession;
    private ExceptionDispatchInfo terminalFailure;
    private HoldAggregateData[] completedHoldAggregates = Array.Empty<HoldAggregateData>();
    private string currentDirectory;
    private OVRHand leftHand;
    private OVRHand rightHand;
    private GameObject cachedLeftTouchedHold;
    private GameObject cachedRightTouchedHold;
    private string cachedTouchedHoldName = string.Empty;

    public int DroppedCaptureFrames { get; private set; }
    public bool IsRecording { get; private set; }
    public string CurrentDirectory => currentDirectory;

    private void Start()
    {
        sceneConfiguror ??= FindAnyObjectByType<SceneConfiguror>();
        if (playerHead == null && sceneConfiguror != null)
        {
            playerHead = sceneConfiguror.centerEyeAnchor != null
                ? sceneConfiguror.centerEyeAnchor.transform
                : null;
        }
        CacheHandReferences();

        if (FindAnyObjectByType<StudyManager>() == null)
        {
            string adhocDirectory = System.IO.Path.Combine(
                Application.persistentDataPath,
                "study",
                $"adhoc_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            BeginBlock(adhocDirectory, null);
        }
    }

    public void BeginBlock(string directory, StudySessionManifest manifest)
    {
        if (recordingSession != null)
        {
            EndBlock();
        }
        terminalFailure?.Throw();

        RecordingBlockSession newSession;
        try
        {
            newSession = RecordingBlockSession.Begin(
                directory,
                recordToCsv,
                Time.realtimeSinceStartupAsDouble);
        }
        catch (Exception exception)
        {
            IsRecording = false;
            LogExceptionWithInnerExceptions(exception);
            throw;
        }

        recordingSession = newSession;
        currentDirectory = directory;
        DroppedCaptureFrames = 0;
        completedHoldAggregates = Array.Empty<HoldAggregateData>();
        cachedLeftTouchedHold = null;
        cachedRightTouchedHold = null;
        cachedTouchedHoldName = string.Empty;
        IsRecording = true;
        Debug.Log($"[ActionRecorder] Recording block to: {directory}");
    }

    public void EndBlock()
    {
        EndBlock(true);
    }

    private void EndBlock(bool throwOnFailure)
    {
        RecordingBlockSession session = recordingSession;
        if (session == null)
        {
            if (throwOnFailure)
            {
                terminalFailure?.Throw();
            }
            return;
        }

        IsRecording = false;
        try
        {
            session.End(
                WriterShutdownTimeoutMilliseconds,
                Time.realtimeSinceStartupAsDouble);
        }
        catch (TimeoutException exception)
        {
            LogExceptionWithInnerExceptions(exception);
            if (throwOnFailure)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            SurfaceTerminalFailure(exception);
            if (throwOnFailure)
            {
                throw;
            }
        }
        finally
        {
            DroppedCaptureFrames = session.DroppedCaptureFrames;
            completedHoldAggregates = session.GetHoldAggregates();
            if (session.IsFinalized && ReferenceEquals(recordingSession, session))
            {
                recordingSession = null;
            }
        }
    }

    public void Record(string action, string hand = "", GameObject hold = null, string details = "")
    {
        if (!IsRecording)
        {
            terminalFailure?.Throw();
            return;
        }

        try
        {
            RecordingBlockSession session = GetActiveSession();
            Vector3 position = playerHead != null ? playerHead.position : Vector3.zero;
            string holdName = hold != null ? hold.name : string.Empty;
            string line = RecordingCsvSerializer.BuildEventRow(
                DateTime.UtcNow,
                Time.time,
                Time.frameCount,
                position,
                action,
                hand,
                holdName,
                details);
            session.WriteEvent(line, action, holdName);

            if (recordToConsole)
            {
                Debug.Log($"[ActionRecorder] {line}");
            }
        }
        catch (Exception exception)
        {
            SurfaceTerminalFailure(exception);
            throw;
        }
    }

    public HoldAggregateData[] GetHoldAggregates()
    {
        HoldAggregateData[] source = recordingSession != null
            ? recordingSession.GetHoldAggregates()
            : completedHoldAggregates;
        HoldAggregateData[] result = new HoldAggregateData[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            HoldAggregateData aggregate = source[i];
            result[i] = new HoldAggregateData
            {
                hold = aggregate.hold,
                secondsTouched = aggregate.secondsTouched,
                gripsDetected = aggregate.gripsDetected,
                meanScore = aggregate.meanScore,
                maxScore = aggregate.maxScore,
            };
        }
        return result;
    }

    private void Update()
    {
        if (!IsRecording)
        {
            return;
        }

        try
        {
            RecordingBlockSession session = GetActiveSession();
            session.ThrowIfFaulted();
            double currentRealtime = Time.realtimeSinceStartupAsDouble;
            if (session.TryScheduleCapture(currentRealtime))
            {
                if (sceneConfiguror == null)
                {
                    session.DropScheduledCapture();
                }
                else
                {
                    GameObject leftTouched = sceneConfiguror.leftHandInteractingClimbingHold;
                    GameObject rightTouched = sceneConfiguror.rightHandInteractingClimbingHold;
                    FillCaptureFrame(captureFrame, currentRealtime, leftTouched, rightTouched);
                    session.EnqueueCapture(
                        captureFrame,
                        leftTouched != null ? leftTouched.name : null,
                        sceneConfiguror.leftHandGripScore,
                        rightTouched != null ? rightTouched.name : null,
                        sceneConfiguror.rightHandGripScore,
                        leftTouched != null && leftTouched == rightTouched);
                }
            }

            DroppedCaptureFrames = session.DroppedCaptureFrames;
            session.FlushEventsIfDue(currentRealtime);
        }
        catch (Exception exception)
        {
            SurfaceTerminalFailure(exception);
            throw;
        }
    }

    private void FillCaptureFrame(
        CaptureFrame frame,
        double currentRealtime,
        GameObject leftTouched,
        GameObject rightTouched)
    {
        frame.utcTicks = DateTime.UtcNow.Ticks;
        frame.sessionTime = Time.time;
        frame.frame = Time.frameCount;
        frame.blockTime = recordingSession.GetBlockTime(currentRealtime);
        frame.mode = GetModeName(sceneConfiguror.gameMode);
        frame.route = sceneConfiguror.currentRouteName ?? string.Empty;
        frame.headPosition = playerHead != null ? playerHead.position : Vector3.zero;
        frame.headRotation = playerHead != null ? playerHead.rotation : Quaternion.identity;
        CopyBones(sceneConfiguror.leftHandBonePositions, sceneConfiguror.leftHandBoneQuaternions,
            frame.leftPositions, frame.leftRotations);
        CopyBones(sceneConfiguror.rightHandBonePositions, sceneConfiguror.rightHandBoneQuaternions,
            frame.rightPositions, frame.rightRotations);

        CacheHandReferences();
        frame.leftConfidence = leftHand != null && leftHand.IsTracked && leftHand.IsDataHighConfidence ? 1 : 0;
        frame.rightConfidence = rightHand != null && rightHand.IsTracked && rightHand.IsDataHighConfidence ? 1 : 0;
        frame.hold = GetTouchedHoldName(leftTouched, rightTouched);
        frame.touchedHold = frame.hold;
        frame.gripFlag = sceneConfiguror.leftHandIsGripping || sceneConfiguror.rightHandIsGripping ? 1 : 0;
        frame.perFingerContactMask = sceneConfiguror.perFingerContactMask;
        frame.gripScore = sceneConfiguror.currentGripScore;
    }

    private static void CopyBones(
        List<Vector3> positions,
        List<Quaternion> rotations,
        Vector3[] destinationPositions,
        Quaternion[] destinationRotations)
    {
        int count = Mathf.Min(
            CaptureFrame.BoneCount,
            Mathf.Min(positions?.Count ?? 0, rotations?.Count ?? 0));
        for (int i = 0; i < count; i++)
        {
            destinationPositions[i] = positions[i];
            destinationRotations[i] = rotations[i];
        }
    }

    private void CacheHandReferences()
    {
        if (sceneConfiguror == null)
        {
            return;
        }

        leftHand ??= sceneConfiguror.leftHandOVRSkeleton != null
            ? sceneConfiguror.leftHandOVRSkeleton.GetComponent<OVRHand>()
            : null;
        rightHand ??= sceneConfiguror.rightHandOVRSkeleton != null
            ? sceneConfiguror.rightHandOVRSkeleton.GetComponent<OVRHand>()
            : null;
    }

    private static string GetModeName(GameMode mode)
    {
        return mode switch
        {
            GameMode.Grip => "Grip",
            GameMode.Ghost => "Ghost",
            _ => "Basic",
        };
    }

    private string GetTouchedHoldName(GameObject leftTouched, GameObject rightTouched)
    {
        if (ReferenceEquals(cachedLeftTouchedHold, leftTouched) &&
            ReferenceEquals(cachedRightTouchedHold, rightTouched))
        {
            return cachedTouchedHoldName;
        }

        cachedLeftTouchedHold = leftTouched;
        cachedRightTouchedHold = rightTouched;
        if (leftTouched != null && rightTouched != null && leftTouched != rightTouched)
        {
            cachedTouchedHoldName = "L:" + leftTouched.name + "|R:" + rightTouched.name;
        }
        else
        {
            GameObject touched = leftTouched != null ? leftTouched : rightTouched;
            cachedTouchedHoldName = touched != null ? touched.name : string.Empty;
        }
        return cachedTouchedHoldName;
    }

    public static string BuildCaptureHeader()
    {
        return RecordingCsvSerializer.BuildCaptureHeader();
    }

    private RecordingBlockSession GetActiveSession()
    {
        return recordingSession ?? throw new InvalidOperationException(
            "ActionRecorder is recording without an active block session.");
    }

    private void SurfaceTerminalFailure(Exception exception)
    {
        IsRecording = false;
        terminalFailure = ExceptionDispatchInfo.Capture(exception);
        recordingSession?.ReportFailure(exception);
        LogExceptionWithInnerExceptions(exception);
    }

    private void LogExceptionWithInnerExceptions(Exception exception)
    {
        Debug.LogException(exception, this);
        if (exception is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
            {
                Debug.LogException(innerException, this);
            }
        }
    }

    private void OnDestroy()
    {
        EndBlock(false);
    }
}
