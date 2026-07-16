using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class ActionRecorder : MonoBehaviour
{
    private const int BoneCount = 26;
    private const int CaptureRate = 30;
    private const int QueueCapacity = CaptureRate * 2;
    private const float CaptureInterval = 1f / CaptureRate;
    private const float FlushIntervalSeconds = 5f;

    public Transform playerHead;
    public SceneConfiguror sceneConfiguror;
    public bool recordToConsole;
    public bool recordToCsv = true;

    private readonly object queueLock = new();
    private readonly CaptureFrame[] queue = new CaptureFrame[QueueCapacity];
    private readonly CaptureFrame writerFrame = new();
    private readonly Dictionary<string, HoldAggregateAccumulator> holdAggregates = new();
    private int queueReadIndex;
    private int queueWriteIndex;
    private int queueCount;
    private float sampleAccumulator;
    private float blockStartRealtime;
    private float lastEventFlushRealtime;
    private volatile bool writerRunning;
    private Thread writerThread;
    private FileStream captureFile;
    private StreamWriter eventWriter;
    private string currentDirectory;
    private OVRHand leftHand;
    private OVRHand rightHand;
    private GameObject cachedLeftTouchedHold;
    private GameObject cachedRightTouchedHold;
    private string cachedTouchedHoldName = string.Empty;

    public int DroppedCaptureFrames { get; private set; }
    public bool IsRecording { get; private set; }
    public string CurrentDirectory => currentDirectory;

    private void Awake()
    {
        EnsureQueueInitialized();
    }

    private void EnsureQueueInitialized()
    {
        for (int i = 0; i < queue.Length; i++)
        {
            queue[i] ??= new CaptureFrame();
        }
    }

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
            string adhocDirectory = Path.Combine(
                Application.persistentDataPath,
                "study",
                $"adhoc_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            BeginBlock(adhocDirectory, null);
        }
    }

    public void BeginBlock(string directory, StudySessionManifest manifest)
    {
        EnsureQueueInitialized();
        EndBlock();
        Directory.CreateDirectory(directory);
        currentDirectory = directory;
        DroppedCaptureFrames = 0;
        sampleAccumulator = 0f;
        blockStartRealtime = Time.realtimeSinceStartup;
        lastEventFlushRealtime = blockStartRealtime;
        holdAggregates.Clear();
        cachedLeftTouchedHold = null;
        cachedRightTouchedHold = null;
        cachedTouchedHoldName = string.Empty;

        lock (queueLock)
        {
            queueReadIndex = 0;
            queueWriteIndex = 0;
            queueCount = 0;
        }

        if (recordToCsv)
        {
            string eventPath = Path.Combine(directory, "events.csv");
            eventWriter = new StreamWriter(
                new FileStream(eventPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            eventWriter.WriteLine("utcTime,sessionTime,frame,playerPosition,action,hand,hold,details");
            eventWriter.Flush();

            string capturePath = Path.Combine(directory, "capture.csv.gz");
            captureFile = new FileStream(capturePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writerRunning = true;
            writerThread = new Thread(CaptureWriterLoop)
            {
                IsBackground = true,
                Name = "VHard Capture Writer",
            };
            writerThread.Start();
        }

        IsRecording = true;
        Debug.Log($"[ActionRecorder] Recording block to: {directory}");
    }

    public void EndBlock()
    {
        if (!IsRecording && writerThread == null)
        {
            return;
        }

        IsRecording = false;
        writerRunning = false;
        lock (queueLock)
        {
            Monitor.PulseAll(queueLock);
        }

        if (writerThread != null)
        {
            if (!writerThread.Join(10000))
            {
                Debug.LogError("[ActionRecorder] Capture writer did not stop within 10 seconds.");
            }
            writerThread = null;
        }

        eventWriter?.Flush();
        eventWriter?.Dispose();
        eventWriter = null;
        captureFile?.Flush(true);
        captureFile?.Dispose();
        captureFile = null;
    }

    public void Record(string action, string hand = "", GameObject hold = null, string details = "")
    {
        if (!IsRecording)
        {
            return;
        }

        string utcTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        string sessionTime = Time.time.ToString("F3", CultureInfo.InvariantCulture);
        string frame = Time.frameCount.ToString(CultureInfo.InvariantCulture);
        Vector3 position = playerHead != null ? playerHead.position : Vector3.zero;
        string holdName = hold != null ? hold.name : string.Empty;
        string line = string.Join(",",
            Escape(utcTime),
            Escape(sessionTime),
            Escape(frame),
            Escape(FormatVector(position)),
            Escape(action),
            Escape(hand),
            Escape(holdName),
            Escape(details));

        eventWriter?.WriteLine(line);
        if (action == "GripStart" && !string.IsNullOrEmpty(holdName))
        {
            GetOrCreateAggregate(holdName).gripsDetected++;
        }
        if (recordToConsole)
        {
            Debug.Log($"[ActionRecorder] {line}");
        }
    }

    public HoldAggregateData[] GetHoldAggregates()
    {
        HoldAggregateData[] result = new HoldAggregateData[holdAggregates.Count];
        int index = 0;
        foreach (KeyValuePair<string, HoldAggregateAccumulator> pair in holdAggregates)
        {
            HoldAggregateAccumulator aggregate = pair.Value;
            result[index++] = new HoldAggregateData
            {
                hold = pair.Key,
                secondsTouched = aggregate.secondsTouched,
                gripsDetected = aggregate.gripsDetected,
                meanScore = aggregate.scoreSamples > 0 ? aggregate.scoreSum / aggregate.scoreSamples : -1f,
                maxScore = aggregate.scoreSamples > 0 ? aggregate.maxScore : -1f,
            };
        }
        Array.Sort(result, (left, right) => string.CompareOrdinal(left.hold, right.hold));
        return result;
    }

    private void Update()
    {
        if (!IsRecording || sceneConfiguror == null)
        {
            return;
        }

        sampleAccumulator += Time.unscaledDeltaTime;
        if (sampleAccumulator >= CaptureInterval)
        {
            sampleAccumulator %= CaptureInterval;
            EnqueueCapture();
        }

        if (eventWriter != null && Time.realtimeSinceStartup - lastEventFlushRealtime >= FlushIntervalSeconds)
        {
            eventWriter.Flush();
            lastEventFlushRealtime = Time.realtimeSinceStartup;
        }
    }

    private void EnqueueCapture()
    {
        EnsureQueueInitialized();
        lock (queueLock)
        {
            if (queueCount == QueueCapacity)
            {
                queueReadIndex = (queueReadIndex + 1) % QueueCapacity;
                queueCount--;
                DroppedCaptureFrames++;
            }

            CaptureFrame frame = queue[queueWriteIndex];
            FillCaptureFrame(frame);
            queueWriteIndex = (queueWriteIndex + 1) % QueueCapacity;
            queueCount++;
            Monitor.Pulse(queueLock);
        }
    }

    private void FillCaptureFrame(CaptureFrame frame)
    {
        frame.utcTicks = DateTime.UtcNow.Ticks;
        frame.sessionTime = Time.time;
        frame.frame = Time.frameCount;
        frame.blockTime = Time.realtimeSinceStartup - blockStartRealtime;
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

        GameObject leftTouched = sceneConfiguror.leftHandInteractingClimbingHold;
        GameObject rightTouched = sceneConfiguror.rightHandInteractingClimbingHold;
        frame.hold = GetTouchedHoldName(leftTouched, rightTouched);
        frame.touchedHold = frame.hold;
        frame.gripFlag = sceneConfiguror.leftHandIsGripping || sceneConfiguror.rightHandIsGripping ? 1 : 0;
        frame.perFingerContactMask = sceneConfiguror.perFingerContactMask;
        frame.gripScore = sceneConfiguror.currentGripScore;

        if (leftTouched != null)
        {
            float score = rightTouched == leftTouched
                ? Mathf.Max(sceneConfiguror.leftHandGripScore, sceneConfiguror.rightHandGripScore)
                : sceneConfiguror.leftHandGripScore;
            UpdateHoldAggregate(leftTouched.name, score);
        }
        if (rightTouched != null && rightTouched != leftTouched)
        {
            UpdateHoldAggregate(rightTouched.name, sceneConfiguror.rightHandGripScore);
        }
    }

    private static void CopyBones(
        List<Vector3> positions,
        List<Quaternion> rotations,
        Vector3[] destinationPositions,
        Quaternion[] destinationRotations)
    {
        int count = Mathf.Min(BoneCount, Mathf.Min(positions?.Count ?? 0, rotations?.Count ?? 0));
        for (int i = 0; i < count; i++)
        {
            destinationPositions[i] = positions[i];
            destinationRotations[i] = rotations[i];
        }
    }

    private HoldAggregateAccumulator GetOrCreateAggregate(string hold)
    {
        if (!holdAggregates.TryGetValue(hold, out HoldAggregateAccumulator aggregate))
        {
            aggregate = new HoldAggregateAccumulator();
            holdAggregates.Add(hold, aggregate);
        }
        return aggregate;
    }

    private void UpdateHoldAggregate(string hold, float score)
    {
        HoldAggregateAccumulator aggregate = GetOrCreateAggregate(hold);
        aggregate.secondsTouched += CaptureInterval;
        if (score < 0f)
        {
            return;
        }

        aggregate.scoreSum += score;
        aggregate.scoreSamples++;
        aggregate.maxScore = Mathf.Max(aggregate.maxScore, score);
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

    private void CaptureWriterLoop()
    {
        StringBuilder chunk = new(1024 * 1024);
        chunk.AppendLine(BuildCaptureHeader());
        DateTime lastFlush = DateTime.UtcNow;

        while (writerRunning || HasQueuedFrames())
        {
            bool dequeued = TryDequeue(writerFrame);
            if (dequeued)
            {
                AppendCaptureRow(chunk, writerFrame);
            }

            bool flushDue = (DateTime.UtcNow - lastFlush).TotalSeconds >= FlushIntervalSeconds;
            if (chunk.Length > 0 && (flushDue || (!writerRunning && !HasQueuedFrames())))
            {
                WriteGzipMember(chunk);
                chunk.Clear();
                lastFlush = DateTime.UtcNow;
            }
        }

        if (chunk.Length > 0)
        {
            WriteGzipMember(chunk);
        }
    }

    private bool TryDequeue(CaptureFrame destination)
    {
        lock (queueLock)
        {
            if (queueCount == 0)
            {
                if (writerRunning)
                {
                    Monitor.Wait(queueLock, 50);
                }
                if (queueCount == 0)
                {
                    return false;
                }
            }

            destination.CopyFrom(queue[queueReadIndex]);
            queueReadIndex = (queueReadIndex + 1) % QueueCapacity;
            queueCount--;
            return true;
        }
    }

    private bool HasQueuedFrames()
    {
        lock (queueLock)
        {
            return queueCount > 0;
        }
    }

    private void WriteGzipMember(StringBuilder chunk)
    {
        if (captureFile == null || chunk.Length == 0)
        {
            return;
        }

        byte[] uncompressed = Encoding.UTF8.GetBytes(chunk.ToString());
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(
                   compressed,
                   System.IO.Compression.CompressionLevel.Optimal,
                   true))
        {
            gzip.Write(uncompressed, 0, uncompressed.Length);
        }
        byte[] member = compressed.ToArray();
        captureFile.Write(member, 0, member.Length);
        captureFile.Flush(true);
    }

    public static string BuildCaptureHeader()
    {
        StringBuilder header = new();
        header.Append("utc,sessionTime,frame,blockTime,mode,route,hold,");
        header.Append("headPosX,headPosY,headPosZ,headRotX,headRotY,headRotZ,headRotW,");
        AppendBoneHeader(header, 'L');
        header.Append("LConf,");
        AppendBoneHeader(header, 'R');
        header.Append("RConf,touchedHold,gripFlag,perFingerContactMask,gripScore");
        return header.ToString();
    }

    private static void AppendBoneHeader(StringBuilder output, char hand)
    {
        for (int i = 0; i < BoneCount; i++)
        {
            output.Append(hand).Append(i).Append("PosX,")
                .Append(hand).Append(i).Append("PosY,")
                .Append(hand).Append(i).Append("PosZ,")
                .Append(hand).Append(i).Append("RotX,")
                .Append(hand).Append(i).Append("RotY,")
                .Append(hand).Append(i).Append("RotZ,")
                .Append(hand).Append(i).Append("RotW,");
        }
    }

    private static void AppendCaptureRow(StringBuilder output, CaptureFrame frame)
    {
        output.Append(new DateTime(frame.utcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)).Append(',');
        AppendFloat(output, frame.sessionTime);
        output.Append(frame.frame).Append(',');
        AppendFloat(output, frame.blockTime);
        AppendEscaped(output, frame.mode);
        AppendEscaped(output, frame.route);
        AppendEscaped(output, frame.hold);
        AppendVector(output, frame.headPosition);
        AppendQuaternion(output, frame.headRotation);
        for (int i = 0; i < BoneCount; i++)
        {
            AppendVector(output, frame.leftPositions[i]);
            AppendQuaternion(output, frame.leftRotations[i]);
        }
        output.Append(frame.leftConfidence).Append(',');
        for (int i = 0; i < BoneCount; i++)
        {
            AppendVector(output, frame.rightPositions[i]);
            AppendQuaternion(output, frame.rightRotations[i]);
        }
        output.Append(frame.rightConfidence).Append(',');
        AppendEscaped(output, frame.touchedHold);
        output.Append(frame.gripFlag).Append(',')
            .Append(frame.perFingerContactMask).Append(',');
        AppendFloat(output, frame.gripScore, false);
        output.AppendLine();
    }

    private static void AppendVector(StringBuilder output, Vector3 value)
    {
        AppendFloat(output, value.x);
        AppendFloat(output, value.y);
        AppendFloat(output, value.z);
    }

    private static void AppendQuaternion(StringBuilder output, Quaternion value)
    {
        AppendFloat(output, value.x);
        AppendFloat(output, value.y);
        AppendFloat(output, value.z);
        AppendFloat(output, value.w);
    }

    private static void AppendFloat(StringBuilder output, float value, bool comma = true)
    {
        output.Append(value.ToString("F5", CultureInfo.InvariantCulture));
        if (comma)
        {
            output.Append(',');
        }
    }

    private static void AppendEscaped(StringBuilder output, string value)
    {
        output.Append(Escape(value)).Append(',');
    }

    private static string FormatVector(Vector3 value)
    {
        return FormattableString.Invariant($"({value.x:F3},{value.y:F3},{value.z:F3})");
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string escaped = value.Replace("\"", "\"\"");
        return escaped.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + escaped + "\""
            : escaped;
    }

    private void OnDestroy()
    {
        EndBlock();
    }

    private sealed class CaptureFrame
    {
        public long utcTicks;
        public float sessionTime;
        public int frame;
        public float blockTime;
        public string mode = string.Empty;
        public string route = string.Empty;
        public string hold = string.Empty;
        public Vector3 headPosition;
        public Quaternion headRotation = Quaternion.identity;
        public readonly Vector3[] leftPositions = new Vector3[BoneCount];
        public readonly Quaternion[] leftRotations = CreateIdentityRotations();
        public int leftConfidence;
        public readonly Vector3[] rightPositions = new Vector3[BoneCount];
        public readonly Quaternion[] rightRotations = CreateIdentityRotations();
        public int rightConfidence;
        public string touchedHold = string.Empty;
        public int gripFlag;
        public int perFingerContactMask = -1;
        public float gripScore = -1f;

        public void CopyFrom(CaptureFrame source)
        {
            utcTicks = source.utcTicks;
            sessionTime = source.sessionTime;
            frame = source.frame;
            blockTime = source.blockTime;
            mode = source.mode;
            route = source.route;
            hold = source.hold;
            headPosition = source.headPosition;
            headRotation = source.headRotation;
            Array.Copy(source.leftPositions, leftPositions, BoneCount);
            Array.Copy(source.leftRotations, leftRotations, BoneCount);
            leftConfidence = source.leftConfidence;
            Array.Copy(source.rightPositions, rightPositions, BoneCount);
            Array.Copy(source.rightRotations, rightRotations, BoneCount);
            rightConfidence = source.rightConfidence;
            touchedHold = source.touchedHold;
            gripFlag = source.gripFlag;
            perFingerContactMask = source.perFingerContactMask;
            gripScore = source.gripScore;
        }

        private static Quaternion[] CreateIdentityRotations()
        {
            Quaternion[] rotations = new Quaternion[BoneCount];
            for (int i = 0; i < rotations.Length; i++)
            {
                rotations[i] = Quaternion.identity;
            }
            return rotations;
        }
    }

    private sealed class HoldAggregateAccumulator
    {
        public float secondsTouched;
        public int gripsDetected;
        public float scoreSum;
        public int scoreSamples;
        public float maxScore;
    }
}
