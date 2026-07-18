using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using Debug = UnityEngine.Debug;

public enum GameMode
{
    Basic = 0, // Only shader
    Grip = 1, // JACE: In development, fixed-grip movement mode
    Ghost = 2,
}

public enum RoutesLoadState
{
    Loading,
    Ready,
    Failed,
}

public class SceneConfiguror : MonoBehaviour
{
    private const int TrackedBoneCount = 26;
    private const string StudyHoldsLayerName = "StudyHolds";
    private const string StudyGhostHoldsLayerName = "StudyGhostHolds";
    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };
    [Header("Action Recorder")]
    public ActionRecorder actionRecorder;
    [Header("HighlightCircle")]
    public GameObject highlightCirclePrefab;
    private List<GameObject> activeHighlightCircles = new();
    private Material highlightCircleMaterial;
    private static readonly Color StartHaloColor = new(0f, 0.85f, 0.25f, 1f);
    private static readonly Color IntermediateHaloColor = new(0.2f, 0.55f, 1f, 1f);
    private static readonly Color FinishHaloColor = new(0.95f, 0.15f, 0.1f, 1f);

    [Header("Minimap Settings")]
    public Camera mainCamera;     // assign your main/world camera here
    //public Camera minimapCamera;  // assign your minimap camera here [Update 7/7/2026: we don't need a minimap anymore. CAROLINE]
    public string indicatorLayerName = "HighlightCircle"; // assign your highlight circle layer here
    private int indicatorLayer;

    [Header("Scene References")]
    public GameObject environment;
    public GameObject holdsParentGameObject;
    public Dictionary<string, GameObject> holdsDictionary;
    public List<string> activeRouteHoldsNamesList;
    public List<GameObject> activeHoldsList;
    private Light examinationHeadlamp;
    private int studyHoldsLayer = -1;
    private int studyGhostHoldsLayer = -1;
    private static readonly string[] SupplementalSceneryNameMarkers =
    {
        "water", "ocean", "terrain", "scenery", "landscape", "skybox",
    };
    private readonly Dictionary<GameObject, bool> supplementalSceneryActiveStates = new();
    private bool studyEnvironmentHidden;
    // The inspector mainCamera reference is a disabled legacy camera; the participant renders
    // through the OVR rig eye anchors, so background suppression must cover every live camera.
    private readonly Dictionary<Camera, (CameraClearFlags flags, Color background)>
        studyEnvironmentCameraStates = new();

    [Header("Hands References")]
    public GameObject centerEyeAnchor;
    public OVRSkeleton leftHandOVRSkeleton;
    public OVRSkeleton rightHandOVRSkeleton;

    [Header("Other Player's Avatar")]
    public bool shouldOtherPlayerHandsBeActive;
    public NetworkedHands networkedHands;
    public AvatarHand otherPlayerLeftHand;
    public AvatarHand otherPlayerRightHand;

    [Header("Hands State")]
    public int numBonesPerHand;
    public Vector3 centerEyePosition;
    public List<Vector3> leftHandBonePositions = new List<Vector3>();
    public List<Vector3> rightHandBonePositions = new List<Vector3>();
    public List<Quaternion> leftHandBoneQuaternions = new List<Quaternion>();
    public List<Quaternion> rightHandBoneQuaternions = new List<Quaternion>();

    [Header("Climb Settings")]
    public GameMode gameMode;
    public string currentRouteName = "DEATH STAR";
    public GhostHoldController ghostHoldController;

    // Ad-hoc routes loaded from StreamingAssets/routes.json (e.g. MoonBoard benchmarks
    // converted via tools/moonboard_to_routes.py). Built-in study routes always win;
    // RouteLibrary rejects any file that tries to shadow them.
    private static readonly string[] BuiltInRouteNames =
    {
        "DEATH STAR", "TO JUG, OR NOT TO JUG...",
    };
    private readonly Dictionary<string, RouteDefinition> jsonRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> jsonRouteNames = new();
    public RouteDefinition ActiveRouteDefinition { get; private set; }
    public RoutesLoadState RoutesJsonLoadState { get; private set; } = RoutesLoadState.Loading;
    public string RoutesLoadFailureReason { get; private set; } = string.Empty;
    public string RoutesJsonSha256 { get; private set; }

    [Header("Interaction Settings")]
    public float interactionColorMaxDistanceOverride;
    public bool disableInactiveHolds;
    public float inactiveHoldAlpha;
    public float activeHoldAlpha;

    [Header("Interaction State")]
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;

    [Header("Interaction Compute Shader Settings")]
    public ComputeShader distanceToClosestBoneComputeShader;
    public int kernelHandle;

    [Header("Grip Settings")]
    public GameObject moonBoardEnv;
    public float gripFingertipRange;
    public GameObject leftHandGripStatusDisplayHelper;
    public GameObject rightHandGripStatusDisplayHelper;
    public GripScoreConfig gripScoreConfig;

    [Header("Grip State")]
    public float[] leftHandBoneToHoldMinDistances;
    public float[] rightHandBoneToHoldMinDistances;
    public bool isGripLocomotionActive;
    public bool leftHandIsGripping;
    public bool rightHandIsGripping;
    public int perFingerContactMask = -1;
    public float currentGripScore = -1f;
    public int leftFingerContactMask;
    public int rightFingerContactMask;
    public float leftHandGripScore;
    public float rightHandGripScore;
    public Vector3 leftHandGripStartPosition;
    public Vector3 leftHandGripLastPosition;
    public Vector3 rightHandGripStartPosition;
    public Vector3 rightHandGripLastPosition;
    public List<Vector3> leftHandGripStartPose;
    public List<Vector3> leftHandGripCurrentPose;
    public List<Vector3> rightHandGripStartPose;
    public List<Vector3> rightHandGripCurrentPose;
    private GripContactPipeline gripContactPipeline;
    private Vector3 initialMoonBoardLocalPosition;
    private Quaternion initialMoonBoardLocalRotation;
    private Vector3 initialMoonBoardLocalScale;
    private bool hasInitialMoonBoardTransform;
    private OVRHand leftTrackedHand;
    private OVRHand rightTrackedHand;
    private Hand? gripLocomotionHand;
    private bool gripRecoveryAttempted;
    private bool debugForceGripReadbackFailures;
    public bool IsGripFeedbackDegraded { get; private set; }
    public string GripFeedbackDegradedUtc { get; private set; }

    private void Awake()
    {
        EnsureRuntimeControllers();
        EnsureExaminationHeadlamp();
    }

    void Start()
    {
            // 1) layer lookup
        indicatorLayer = LayerMask.NameToLayer(indicatorLayerName);
        Debug.Log($"[SC] indicatorLayerName='{indicatorLayerName}' → layerIndex={indicatorLayer}");
        if (indicatorLayer < 0)
        {
            Debug.LogError($"[SC] Layer '{indicatorLayerName}' not found! Check Project Settings > Tags & Layers.");
        }

        // Route halos are a world-space board cue and must be visible in the main camera.
        if (indicatorLayer >= 0)
        {
            int mask = 1 << indicatorLayer;
            Debug.Log($"[SC] mainCamera mask before:    {mainCamera.cullingMask:X8}");
            mainCamera.cullingMask |= mask;
            Debug.Log($"[SC] mainCamera mask after:     {mainCamera.cullingMask:X8}");
        }

        UnityEngine.Debug.Log("SceneConfiguror initializing.");
        CacheMoonBoardTransform();

        // Add all the children of the holds parent to the holds dictionary, to be accessed using the string [A-K][1-18]
        // Jace: Note that the holds are currently named [A-K][1-18].[001/002/003]
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        StartCoroutine(LoadRoutesJson());

        // DEV: Turn on all holds by default
        // SetUpRouteByName("[PREVIEW ALL (SHADER OFF)]");
        SetUpRouteByName(currentRouteName);

        EnsureRuntimeControllers();
        ghostHoldController.Initialize(this);
        SetGameMode(gameMode);
    }

    void TraverseBones(GameObject rootBone, List<GameObject> bones)
    {
        bones.Add(rootBone);
        foreach (Transform child in rootBone.transform)
        {
            TraverseBones(child.gameObject, bones);
        }
    }

    void Update()
    {
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        gripContactPipeline?.Update(Time.unscaledTime);
        HandleGripPipelineHealth();
        centerEyePosition = centerEyeAnchor.transform.position;

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = leftHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt(
                "_IsBeingInteracted",
                gripContactPipeline == null && !IsGripFeedbackDegraded ? 1 : 0);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = rightHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt(
                "_IsBeingInteracted",
                gripContactPipeline == null && !IsGripFeedbackDegraded ? 1 : 0);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }

        bool leftTrackingValid = IsHandTrackingValid(leftHandOVRSkeleton, ref leftTrackedHand);
        bool rightTrackingValid = IsHandTrackingValid(rightHandOVRSkeleton, ref rightTrackedHand);
        if (!leftTrackingValid)
        {
            ClearHandInteractionState(0);
        }
        if (!rightTrackingValid)
        {
            ClearHandInteractionState(1);
        }
        numBonesPerHand = leftTrackingValid || rightTrackingValid ? TrackedBoneCount : 0;
        if (leftTrackingValid)
        {
            CopyHandBones(leftHandOVRSkeleton, leftHandBonePositions, leftHandBoneQuaternions, TrackedBoneCount);
        }
        if (rightTrackingValid)
        {
            CopyHandBones(rightHandOVRSkeleton, rightHandBonePositions, rightHandBoneQuaternions, TrackedBoneCount);
        }
        EnsureGripDistanceArrays(TrackedBoneCount);
        gripContactPipeline?.Process(
            leftHandInteractingClimbingHold,
            rightHandInteractingClimbingHold,
            leftHandBonePositions,
            rightHandBonePositions,
            leftTrackingValid,
            rightTrackingValid);

        if (!leftTrackingValid && !rightTrackingValid)
        {
            return;
        }

        // Grip state is shared by in-context and detached examination. Only Grip mode may move the board.
        if (gameMode == GameMode.Grip)
        {
            UpdateGripMode();
        }
        else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                 ghostHoldController.CurrentGhost != null)
        {
            UpdateGripMode(false);
        }

        // Update networked hands
        if (!shouldOtherPlayerHandsBeActive)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else if (networkedHands == null)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else if (networkedHands.leftHandJointPositionsNetworked.Count != numBonesPerHand
             || networkedHands.rightHandJointPositionsNetworked.Count != numBonesPerHand
             || networkedHands.leftHandJointQuaternionNetworked.Count != numBonesPerHand
             || networkedHands.rightHandJointQuaternionNetworked.Count != numBonesPerHand)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else
        {
            otherPlayerLeftHand.gameObject.SetActive(true);
            otherPlayerRightHand.gameObject.SetActive(true);
            for (int i = 0; i < numBonesPerHand; i++)
            {
                otherPlayerLeftHand.joints[i].transform.position = networkedHands.leftHandJointPositionsNetworked[i] + environment.transform.position;
                otherPlayerRightHand.joints[i].transform.position = networkedHands.rightHandJointPositionsNetworked[i] + environment.transform.position;
                otherPlayerLeftHand.joints[i].transform.rotation = networkedHands.leftHandJointQuaternionNetworked[i];
                otherPlayerRightHand.joints[i].transform.rotation = networkedHands.rightHandJointQuaternionNetworked[i];
            }
        }

    }

    private static void CopyHandBones(
        OVRSkeleton skeleton,
        List<Vector3> positions,
        List<Quaternion> rotations,
        int count)
    {
        positions.Clear();
        rotations.Clear();
        if (positions.Capacity < count)
        {
            positions.Capacity = count;
        }
        if (rotations.Capacity < count)
        {
            rotations.Capacity = count;
        }

        for (int i = 0; i < count; i++)
        {
            Transform bone = skeleton.Bones[i].Transform;
            positions.Add(bone.position);
            rotations.Add(bone.rotation);
        }
    }

    private void EnsureGripDistanceArrays(int count)
    {
        if (leftHandBoneToHoldMinDistances == null || leftHandBoneToHoldMinDistances.Length != count)
        {
            leftHandBoneToHoldMinDistances = new float[count];
            Array.Fill(leftHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
        if (rightHandBoneToHoldMinDistances == null || rightHandBoneToHoldMinDistances.Length != count)
        {
            rightHandBoneToHoldMinDistances = new float[count];
            Array.Fill(rightHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
    }

    private void ResetHandDistances(int hand)
    {
        float[] distances = hand == 0
            ? leftHandBoneToHoldMinDistances
            : rightHandBoneToHoldMinDistances;
        if (distances != null)
        {
            Array.Fill(distances, float.PositiveInfinity);
        }
    }

    public void UpdateGripMode(bool allowLocomotion = true)
    {
        float gripPoseBoneDriftThreshold = 0.05f;
        if (!leftHandIsGripping)
        {
            // If a hand was not gripping, we check if it started gripping by calling CheckIfHandIsGrippingHold() to check for the "start condition".
            leftHandIsGripping = leftHandInteractingClimbingHold == null ? false : CheckIfHandIsGrippingHold(0, leftHandInteractingClimbingHold);
            if (leftHandIsGripping)
            {
                // "Wasn't gripping, now gripping"
                // Save the start pose of the hand (joint positions).
                CopyRelativePose(leftHandBonePositions, ref leftHandGripStartPose);
                actionRecorder?.Record(
                    "GripStart",
                    "Left",
                    leftHandInteractingClimbingHold
                );                                                                  // Save "relative positions" by subtracting the base position (index 0) from all other positions.
            }
        }
        else if (leftHandInteractingClimbingHold == null)
        {
            leftHandIsGripping = false;
        }
        else
        {
            // However, if a hand is already gripping, we instead check if the hand is still gripping by comparing the start pose and the current pose.
            CopyRelativePose(leftHandBonePositions, ref leftHandGripCurrentPose);
            leftHandIsGripping = AreHandPosesApproximatelyEqual(leftHandGripStartPose, leftHandGripCurrentPose, gripPoseBoneDriftThreshold);
        }
        if (!rightHandIsGripping)
        {
            rightHandIsGripping = rightHandInteractingClimbingHold == null ? false : CheckIfHandIsGrippingHold(1, rightHandInteractingClimbingHold);
            if (rightHandIsGripping)
            {
                CopyRelativePose(rightHandBonePositions, ref rightHandGripStartPose);
                actionRecorder?.Record(
                    "GripStart",
                    "Right",
                    rightHandInteractingClimbingHold
                );
            }
        }
        else if (rightHandInteractingClimbingHold == null)
        {
            rightHandIsGripping = false;
        }
        else
        {
            CopyRelativePose(rightHandBonePositions, ref rightHandGripCurrentPose);
            rightHandIsGripping = AreHandPosesApproximatelyEqual(rightHandGripStartPose, rightHandGripCurrentPose, gripPoseBoneDriftThreshold);
        }

        if (!allowLocomotion)
        {
            isGripLocomotionActive = false;
            return;
        }

        // leftHandIsGripping = leftHandInteractingClimbingHold == null ? false : CheckIfHandIsGrippingHold(0, leftHandInteractingClimbingHold);
        // rightHandIsGripping = rightHandInteractingClimbingHold == null ? false : CheckIfHandIsGrippingHold(1, rightHandInteractingClimbingHold);

        // If neither hand is gripping, don't move
        if (!leftHandIsGripping && !rightHandIsGripping)
        {
            if (isGripLocomotionActive)
            {
                UnityEngine.Debug.Log("[SceneConfiguror] Was gripping with only one hand and moving, but now not gripping with either hand. Stopping movement.");
                StopGripLocomotion();
            }
            return;
        }

        // If both hands are gripping, don't move
        if (leftHandIsGripping && rightHandIsGripping)
        {
            if (isGripLocomotionActive)
            {
                UnityEngine.Debug.Log("[SceneConfiguror] Was gripping with only one hand and moving, but now gripping with both hands. Stopping movement.");
                StopGripLocomotion();
            }
            return;
        }

        // At this point, only one hand is gripping
        // First, check isGripLocomotionActive -- If false, we are just starting to grip and need to record the grip start position
        if (!isGripLocomotionActive)
        {
            UnityEngine.Debug.Log("Started gripping with only one hand, now moving!");
            actionRecorder?.Record(
                "LocomotionStart",
                leftHandIsGripping ? "Left" : "Right",
                leftHandIsGripping ? leftHandInteractingClimbingHold : rightHandInteractingClimbingHold,
                "one-hand grip locomotion started"
            );
            isGripLocomotionActive = true;
            if (leftHandIsGripping)
            {
                gripLocomotionHand = Hand.Left;
                leftHandGripStartPosition = leftHandBonePositions[0];
                leftHandGripLastPosition = leftHandGripStartPosition;
            }
            else
            {
                gripLocomotionHand = Hand.Right;
                rightHandGripStartPosition = rightHandBonePositions[0];
                rightHandGripLastPosition = rightHandGripStartPosition;
            }
        }

        // Finally, we are sure that the player needs to move.
        // We move by getting the distance since last frame, and moving the player by that distance, then recording the new last position.
        Vector3 vectorToMovePlayer = Vector3.zero;
        if (gripLocomotionHand == Hand.Left && leftHandIsGripping)
        {
            vectorToMovePlayer = leftHandBonePositions[0] - leftHandGripLastPosition;
            // Compensate by moving the hands back
            // leftHand.transform.position -= vectorToMovePlayer;
            leftHandGripLastPosition = leftHandBonePositions[0];
        }
        else if (gripLocomotionHand == Hand.Right && rightHandIsGripping)
        {
            vectorToMovePlayer = rightHandBonePositions[0] - rightHandGripLastPosition;
            // Compensate by moving the hands back
            // rightHand.transform.position -= vectorToMovePlayer;
            rightHandGripLastPosition = rightHandBonePositions[0];
        }
        else
        {
            StopGripLocomotion();
            return;
        }
        // cameraOffset.transform.position -= vectorToMovePlayer; // Move player
        moonBoardEnv.transform.position += vectorToMovePlayer;
    }
    public bool CheckIfHandIsGrippingHold(int handIndex, GameObject climbingHold)
    {
        // If we don't have access to the expected hand bone positions, return false.
        // NOTE: The expected hand bone positions refer to the standard number and ordering of hand bone positions as returned from OVRSkeleton.Bones

        // Clarify left or right hand
        float[] handBoneToHoldMinDistances = null;
        if (handIndex == 0)
        {
            handBoneToHoldMinDistances = leftHandBoneToHoldMinDistances;
        }
        else if (handIndex == 1)
        {
            handBoneToHoldMinDistances = rightHandBoneToHoldMinDistances;
        }

        if (handBoneToHoldMinDistances == null || handBoneToHoldMinDistances.Length <= 25)
        {
            return false;
        }

        // Keep the legacy all-five-fingertips event threshold for locomotion and event compatibility.
        // Currently, the following will check if each fingertip is close to the hold. (When using Meta SDK v71's new OpenXR Hand Skeleton, bone indices 5, 10, 15, 20, 25 for thumb, pointer, middle, ring, and little fingertips respectively)
        bool isGripping = true;
        foreach (int boneIndex in FingertipBoneIndices)
        {
            if (handBoneToHoldMinDistances[boneIndex] > gripFingertipRange)
            {
                isGripping = false;
                break;
            }
        }

        if (isGripping)
        {
            // UnityEngine.Debug.Log("[SceneConfiguror] DEV: Hand " + handIndex + " is gripping hold " + climbingHold.name);
            UnityEngine.Debug.Log(
                        $"[GripCheck] hand={handIndex}, hold={climbingHold.name}, " +
                        $"range={gripFingertipRange}, " +
                        $"thumb={handBoneToHoldMinDistances[5]:F4}, " +
                        $"index={handBoneToHoldMinDistances[10]:F4}, " +
                        $"middle={handBoneToHoldMinDistances[15]:F4}, " +
                        $"ring={handBoneToHoldMinDistances[20]:F4}, " +
                        $"pinky={handBoneToHoldMinDistances[25]:F4}");
            return true;
        }
        else
        {
            return false;
        }
    }
    public bool AreHandPosesApproximatelyEqual(List<Vector3> pose1, List<Vector3> pose2, float threshold)
    {
        if (pose1.Count != pose2.Count)
        {
            return false;
        }
        for (int i = 2; i < pose1.Count; i++) // Skip bones 0 (palm) and 1 (wrist)
        {
            if (Vector3.Distance(pose1[i], pose2[i]) > threshold)
            {
                return false;
            }
        }
        return true;
    }

    public OVRSkeleton GetOVRSkeletonFromHandIndex(int handIndex)
    {
        if (handIndex == 0)
        {
            return leftHandOVRSkeleton;
        }
        else if (handIndex == 1)
        {
            return rightHandOVRSkeleton;
        }
        else
        {
            UnityEngine.Debug.LogError("Hand index " + handIndex + " not found!");
            return null;
        }
    }
    public void HandHoverEnter(int hand, GameObject hoveredGameObject)
    {
        UnityEngine.Debug.Log("SceneConfiguror: HandHoverEnter() triggered with hand " + hand + " and GameObject " + hoveredGameObject.name);
        OVRSkeleton handOVRSkeleton = GetOVRSkeletonFromHandIndex(hand);
        OVRHand handOVRHand = handOVRSkeleton.GetComponent<OVRHand>();
        if (hoveredGameObject.CompareTag("ClimbingHold"))
        {
            bool isGhost = IsGhostHold(hoveredGameObject);
            if (gameMode == GameMode.Basic || (gameMode == GameMode.Ghost && !isGhost) ||
                (gameMode == GameMode.Grip && isGhost))
            {
                return;
            }

            if (IsActiveRouteHold(hoveredGameObject) || isGhost)
            {
                UnityEngine.Debug.Log("Hand hover enter: " + handOVRHand.name + " is now interacting with Climbing Hold " + hoveredGameObject.name);
                actionRecorder?.Record(
                    "HoverEnter",
                    hand == 0 ? "Left" : "Right",
                    hoveredGameObject
                );
                MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
                meshRenderer.material.SetInt(
                    "_IsBeingInteracted",
                    gripContactPipeline == null && !IsGripFeedbackDegraded ? 1 : 0);

            if (hand == 0)
            {
                ResetHandDistances(0);
                if (!IsSameHold(leftHandInteractingClimbingHold, hoveredGameObject))
                {
                    leftHandIsGripping = false;
                    StopGripLocomotion(Hand.Left);
                }
                leftHandInteractingClimbingHold = hoveredGameObject;
            }
            else if (hand == 1)
            {
                ResetHandDistances(1);
                if (!IsSameHold(rightHandInteractingClimbingHold, hoveredGameObject))
                {
                    rightHandIsGripping = false;
                    StopGripLocomotion(Hand.Right);
                }
                rightHandInteractingClimbingHold = hoveredGameObject;
                }
            }
        }
    }
    public void HandHoverExit(int hand, GameObject hoveredGameObject)
    {
        UnityEngine.Debug.Log("SceneConfiguror: HandHoverExit() triggered with hand " + hand + " and GameObject " + hoveredGameObject.name);
        OVRSkeleton handOVRSkeleton = GetOVRSkeletonFromHandIndex(hand);
        OVRHand ovrHand = handOVRSkeleton.GetComponent<OVRHand>();
        if (hoveredGameObject.CompareTag("ClimbingHold"))
        {
            bool isGhost = IsGhostHold(hoveredGameObject);
            if (gameMode == GameMode.Basic || (gameMode == GameMode.Ghost && !isGhost) ||
                (gameMode == GameMode.Grip && isGhost))
            {
                return;
            }
            UnityEngine.Debug.Log("Hand hover exit: " + ovrHand.name + " is no longer interacting with Climbing Hold " + hoveredGameObject.name);
            actionRecorder?.Record(
                "HoverExit",
                hand == 0 ? "Left" : "Right",
                hoveredGameObject
            );
            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 0);

            if (hand == 0)
            {
                if (IsSameHold(leftHandInteractingClimbingHold, hoveredGameObject))
                {
                    leftHandInteractingClimbingHold = null;
                    leftHandIsGripping = false;
                    StopGripLocomotion(Hand.Left);
                }
            }
            else if (hand == 1)
            {
                if (IsSameHold(rightHandInteractingClimbingHold, hoveredGameObject))
                {
                    rightHandInteractingClimbingHold = null;
                    rightHandIsGripping = false;
                    StopGripLocomotion(Hand.Right);
                }
            }
        }
        else
        {
            UnityEngine.Debug.Log("Hand hover exit: " + ovrHand.name + " is no longer interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void SetUpRouteByName(string routeName)
    {
        EnsureHoldsDictionary();
        UnityEngine.Debug.Log("Requested route by name: " + routeName);
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            UnityEngine.Debug.LogError("Route name " + routeName + " not found!");
            return;
        }

        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        if (routeName != "[PREVIEW ALL (SHADER OFF)]")
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with holds " + string.Join(", ", activeRouteHoldsNamesList));
            SetUpRouteByHoldList(route);
        }
        else
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with all holds");
            PreviewAllHolds();
        }
        ApplyModeToRouteHolds();
        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
    }

    public bool TryValidateRoute(string routeName, out string error)
    {
        EnsureHoldsDictionary();
        if (!TryEnsureRouteSourceReady(routeName, out error))
        {
            return false;
        }
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        string[] missing = route.holds.Where(hold => !holdsDictionary.ContainsKey(hold)).ToArray();
        if (missing.Length > 0)
        {
            error = routeName + " is missing hold" + (missing.Length == 1 ? " " : "s ") +
                    string.Join(", ", missing) + ".";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TrySelectBaselineRoute(string routeName, out string error)
    {
        if (!TryEnsureRouteSourceReady(routeName, out error))
        {
            return false;
        }
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        error = string.Empty;
        return true;
    }

    private bool TryGetRouteDefinition(string routeName, out RouteDefinition route)
    {
        if (TryGetBuiltInRouteDefinition(routeName, out route))
        {
            return true;
        }
        if (routeName != null && jsonRoutes.TryGetValue(routeName, out RouteDefinition jsonRoute))
        {
            route = jsonRoute;
            return true;
        }
        route = null;
        return false;
    }

    public bool IsBuiltInRoute(string routeName)
    {
        return TryGetBuiltInRouteDefinition(routeName, out _);
    }

    public string GetRoutesLoadStatusLine()
    {
        return RoutesJsonLoadState switch
        {
            RoutesLoadState.Ready => "READY (" + jsonRouteNames.Count + " imported)",
            RoutesLoadState.Failed => "FAILED: " + RoutesLoadFailureReason,
            _ => "LOADING",
        };
    }

    private bool TryEnsureRouteSourceReady(string routeName, out string error)
    {
        if (IsBuiltInRoute(routeName) || RoutesJsonLoadState == RoutesLoadState.Ready)
        {
            error = string.Empty;
            return true;
        }

        error = RoutesJsonLoadState == RoutesLoadState.Loading
            ? "routes.json is still loading; imported route '" + routeName + "' cannot start yet."
            : "routes.json failed to load; imported route '" + routeName + "' is unavailable: " +
              RoutesLoadFailureReason;
        return false;
    }

    /// <summary>Built-in study routes first, then routes.json entries, in file order.</summary>
    public List<string> GetAvailableRouteNames()
    {
        List<string> names = new(BuiltInRouteNames.Length + jsonRouteNames.Count);
        names.AddRange(BuiltInRouteNames);
        names.AddRange(jsonRouteNames);
        return names;
    }

    private IEnumerator LoadRoutesJson()
    {
        RoutesJsonLoadState = RoutesLoadState.Loading;
        RoutesLoadFailureReason = string.Empty;
        RoutesJsonSha256 = null;
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/routes.json";
        string json = null;
        byte[] jsonBytes = null;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
                jsonBytes = request.downloadHandler.data;
            }
            else if (request.responseCode != 404)
            {
                SetRoutesLoadFailed("request failed: " + request.error);
                yield break;
            }
        }
        else if (File.Exists(path))
        {
            json = File.ReadAllText(path);
            jsonBytes = File.ReadAllBytes(path);
        }

        if (json == null)
        {
            SetRoutesLoadFailed("file not found in StreamingAssets.");
            yield break;
        }

        if (!RouteLibrary.TryParseJson(json, BuiltInRouteNames, out List<RouteDefinition> parsed, out string error))
        {
            SetRoutesLoadFailed(error);
            yield break;
        }

        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        foreach (RouteDefinition route in parsed)
        {
            jsonRoutes[route.name] = route;
            jsonRouteNames.Add(route.name);
        }
        RoutesJsonSha256 = ComputeSha256(jsonBytes);
        RoutesJsonLoadState = RoutesLoadState.Ready;
        Debug.Log("[SceneConfiguror] Loaded " + jsonRouteNames.Count + " route(s) from routes.json: " +
                  string.Join(", ", jsonRouteNames));
    }

    private void SetRoutesLoadFailed(string reason)
    {
        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        RoutesJsonSha256 = null;
        RoutesLoadFailureReason = reason;
        RoutesJsonLoadState = RoutesLoadState.Failed;
        Debug.LogError("[SceneConfiguror] routes.json failed: " + reason);
    }

    private static string ComputeSha256(byte[] value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(value);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool TryGetBuiltInRouteDefinition(string routeName, out RouteDefinition route)
    {
        switch (routeName)
        {
            case "DEATH STAR":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "DEATH STAR",
                    holds = new[] { "D15", "D18", "G13", "H11", "I4", "J6", "K9" },
                    start = new[] { "I4", "J6" },
                    finish = new[] { "D18" },
                };
                return true;
            case "TO JUG, OR NOT TO JUG...":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "TO JUG, OR NOT TO JUG...",
                    holds = new[] { "D9", "D15", "F5", "F12", "G13", "H10", "H18" },
                    start = new[] { "F5" },
                    finish = new[] { "H18" },
                };
                return true;
            case "[PREVIEW ALL (SHADER OFF)]":
                route = new RouteDefinition
                {
                    name = "[PREVIEW ALL (SHADER OFF)]",
                    holds = new[] { // this was the fastest way to get this working, sue me
                    "A1", "B1", "C1", "D1", "E1", "F1", "G1", "H1", "I1", "J1", "K1",
                    "A2", "B2", "C2", "D2", "E2", "F2", "G2", "H2", "I2", "J2", "K2",
                    "A3", "B3", "C3", "D3", "E3", "F3", "G3", "H3", "I3", "J3", "K3",
                    "A4", "B4", "C4", "D4", "E4", "F4", "G4", "H4", "I4", "J4", "K4",
                    "A5", "B5", "C5", "D5", "E5", "F5", "G5", "H5", "I5", "J5", "K5",
                    "A6", "B6", "C6", "D6", "E6", "F6", "G6", "H6", "I6", "J6", "K6",
                    "A7", "B7", "C7", "D7", "E7", "F7", "G7", "H7", "I7", "J7", "K7",
                    "A8", "B8", "C8", "D8", "E8", "F8", "G8", "H8", "I8", "J8", "K8",
                    "A9", "B9", "C9", "D9", "E9", "F9", "G9", "H9", "I9", "J9", "K9",
                    "A10", "B10", "C10", "D10", "E10", "F10", "G10", "H10", "I10", "J10", "K10",
                    "A11", "B11", "C11", "D11", "E11", "F11", "G11", "H11", "I11", "J11", "K11",
                    "A12", "B12", "C12", "D12", "E12", "F12", "G12", "H12", "I12", "J12", "K12",
                    "A13", "B13", "C13", "D13", "E13", "F13", "G13", "H13", "I13", "J13", "K13",
                    "A14", "B14", "C14", "D14", "E14", "F14", "G14", "H14", "I14", "J14", "K14",
                    "A15", "B15", "C15", "D15", "E15", "F15", "G15", "H15", "I15", "J15", "K15",
                    "A16", "B16", "C16", "D16", "E16", "F16", "G16", "H16", "I16", "J16", "K16",
                    "A17", "B17", "C17", "D17", "E17", "F17", "G17", "H17", "I17", "J17", "K17",
                    "A18", "B18", "C18", "D18", "E18", "F18", "G18", "H18", "I18", "J18", "K18"
                    },
                };
                return true;
            default:
                route = null;
                return false;
        }
    }

    public void SetGameMode(GameMode newMode)
    {
        EnsureExaminationHeadlamp();
        bool leavingGhostMode = gameMode == GameMode.Ghost && newMode != GameMode.Ghost;
        if (leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(false);
        }
        ResetInteractionState();
        gameMode = newMode;
        if (newMode == GameMode.Basic)
        {
            ClearHighlightCircles();
        }
        if (examinationHeadlamp != null)
        {
            examinationHeadlamp.enabled = newMode == GameMode.Grip || newMode == GameMode.Ghost;
        }
        ApplyModeToRouteHolds();
        if (!leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(newMode == GameMode.Ghost);
        }
        if (newMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
        else
        {
            gripContactPipeline?.ClearFeedback();
            gripContactPipeline?.Prepare((IReadOnlyList<GameObject>)null);
        }
        actionRecorder?.Record("ModeChanged", "", null, newMode.ToString());
    }

    public void PrepareGripHold(GameObject hold)
    {
        gripContactPipeline?.Prepare(hold);
    }

    public void ResetMoonBoardTransform()
    {
        CacheMoonBoardTransform();
        if (!hasInitialMoonBoardTransform)
        {
            return;
        }

        moonBoardEnv.transform.SetLocalPositionAndRotation(
            initialMoonBoardLocalPosition,
            initialMoonBoardLocalRotation);
        moonBoardEnv.transform.localScale = initialMoonBoardLocalScale;
    }

    public void SetStudyEnvironmentVisible(bool visible)
    {
        if (!visible)
        {
            if (!studyEnvironmentHidden)
            {
                CaptureAndHideSupplementalScenery();
                CaptureAndHideStudyCameraBackground();
                studyEnvironmentHidden = true;
            }
            if (environment != null)
            {
                environment.SetActive(false);
            }
            return;
        }

        if (environment != null)
        {
            environment.SetActive(true);
        }
        if (studyEnvironmentHidden)
        {
            RestoreSupplementalScenery();
            RestoreStudyCameraBackground();
            studyEnvironmentHidden = false;
        }
    }

    private void CaptureAndHideSupplementalScenery()
    {
        supplementalSceneryActiveStates.Clear();
        int waterLayer = LayerMask.NameToLayer("Water");
        foreach (Transform candidate in FindObjectsByType<Transform>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            if (candidate == null || candidate.gameObject.scene != gameObject.scene ||
                (environment != null &&
                 (candidate == environment.transform || candidate.IsChildOf(environment.transform))))
            {
                continue;
            }

            GameObject sceneryRoot = FindSupplementalSceneryRoot(candidate, waterLayer);
            if (sceneryRoot != null && !supplementalSceneryActiveStates.ContainsKey(sceneryRoot))
            {
                supplementalSceneryActiveStates.Add(sceneryRoot, sceneryRoot.activeSelf);
            }
        }

        foreach (GameObject scenery in supplementalSceneryActiveStates.Keys)
        {
            if (scenery != null)
            {
                scenery.SetActive(false);
            }
        }
    }

    private GameObject FindSupplementalSceneryRoot(Transform candidate, int waterLayer)
    {
        Transform match = null;
        for (Transform current = candidate; current != null; current = current.parent)
        {
            if (environment != null && current == environment.transform)
            {
                return null;
            }
            if ((waterLayer >= 0 && current.gameObject.layer == waterLayer) ||
                SupplementalSceneryNameMarkers.Any(marker =>
                    current.name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                match = current;
            }
        }
        return match != null ? match.gameObject : null;
    }

    private void RestoreSupplementalScenery()
    {
        foreach (KeyValuePair<GameObject, bool> entry in supplementalSceneryActiveStates)
        {
            if (entry.Key != null)
            {
                entry.Key.SetActive(entry.Value);
            }
        }
        supplementalSceneryActiveStates.Clear();
    }

    private void CaptureAndHideStudyCameraBackground()
    {
        studyEnvironmentCameraStates.Clear();
        foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (camera == null || !camera.isActiveAndEnabled ||
                camera.targetTexture != null)
            {
                continue;
            }
            studyEnvironmentCameraStates[camera] = (camera.clearFlags, camera.backgroundColor);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
        }
    }

    private void RestoreStudyCameraBackground()
    {
        foreach (KeyValuePair<Camera, (CameraClearFlags flags, Color background)> entry
                 in studyEnvironmentCameraStates)
        {
            if (entry.Key == null)
            {
                continue;
            }
            entry.Key.clearFlags = entry.Value.flags;
            entry.Key.backgroundColor = entry.Value.background;
        }
        studyEnvironmentCameraStates.Clear();
    }

    public bool IsGripFeedbackReady => !IsGripFeedbackDegraded &&
                                       gripContactPipeline != null && gripContactPipeline.IsSupported;

    public void SetStudyFeedbackVisible(bool visible)
    {
        bool effectiveVisibility = visible && !IsGripFeedbackDegraded;
        foreach (HandBoneTracker tracker in FindObjectsByType<HandBoneTracker>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            tracker.SetFeedbackVisible(effectiveVisibility);
        }
        if (!effectiveVisibility)
        {
            gripContactPipeline?.ClearFeedback();
        }
    }

    public void DebugInjectGripReadbackFailures(int epochCount = 1)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        EnsureGripPipeline();
        gripContactPipeline?.DebugInjectReadbackFailures(epochCount);
    }

    public void DebugSetGripReadbackFailures(bool enabled)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        debugForceGripReadbackFailures = enabled;
        EnsureGripPipeline();
        gripContactPipeline?.DebugSetReadbackFailures(enabled);
    }

    public Transform GetGripNormalReference(GameObject hold)
    {
        return IsGhostHold(hold) && ghostHoldController.WallReferent != null
            ? ghostHoldController.WallReferent.transform
            : hold.transform;
    }

    public bool IsActiveRouteHold(GameObject candidate)
    {
        return GetActiveRouteHold(candidate) != null;
    }

    public GameObject GetActiveRouteHold(GameObject candidate)
    {
        if (candidate == null || activeHoldsList == null)
        {
            return null;
        }

        Transform candidateTransform = candidate.transform;
        foreach (GameObject activeHold in activeHoldsList)
        {
            if (activeHold != null &&
                (candidateTransform == activeHold.transform || candidateTransform.IsChildOf(activeHold.transform)))
            {
                return activeHold;
            }
        }
        return null;
    }

    public bool IsGhostHold(GameObject candidate)
    {
        return ghostHoldController != null && ghostHoldController.IsGhostHold(candidate);
    }

    public void RegisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        ResolveStudyLayers();
        if (studyGhostHoldsLayer >= 0)
        {
            SetLayerRecursively(ghost, studyGhostHoldsLayer);
        }

        XRGrabInteractable grab = ghost.GetComponent<XRGrabInteractable>();
        if (grab != null)
        {
            grab.enabled = true;
        }
    }

    public void UnregisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        if (leftHandInteractingClimbingHold != null &&
            (leftHandInteractingClimbingHold == ghost ||
             leftHandInteractingClimbingHold.transform.IsChildOf(ghost.transform)))
        {
            actionRecorder?.Record("HoverExit", "Left", ghost);
            leftHandInteractingClimbingHold = null;
        }
        if (rightHandInteractingClimbingHold != null &&
            (rightHandInteractingClimbingHold == ghost ||
             rightHandInteractingClimbingHold.transform.IsChildOf(ghost.transform)))
        {
            actionRecorder?.Record("HoverExit", "Right", ghost);
            rightHandInteractingClimbingHold = null;
        }
        leftHandIsGripping = false;
        rightHandIsGripping = false;
    }

    private void ApplyModeToRouteHolds()
    {
        if (activeHoldsList == null)
        {
            return;
        }

        bool enableWallColliders = gameMode != GameMode.Basic;
        bool enableWallGrab = gameMode == GameMode.Grip;
        foreach (GameObject hold in activeHoldsList)
        {
            if (hold == null)
            {
                continue;
            }

            XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
            if (grab != null)
            {
                grab.enabled = enableWallGrab;
            }
            foreach (Collider collider in hold.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = enableWallColliders;
            }
        }
    }

    private void ResetInteractionState()
    {
        StopGripLocomotion();
        SetInteractionVisual(leftHandInteractingClimbingHold, false);
        if (rightHandInteractingClimbingHold != leftHandInteractingClimbingHold)
        {
            SetInteractionVisual(rightHandInteractingClimbingHold, false);
        }
        leftHandInteractingClimbingHold = null;
        rightHandInteractingClimbingHold = null;
        leftHandIsGripping = false;
        rightHandIsGripping = false;
        isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    private void ClearHandInteractionState(int hand)
    {
        GameObject hold = hand == 0
            ? leftHandInteractingClimbingHold
            : rightHandInteractingClimbingHold;
        if (hold != null)
        {
            actionRecorder?.Record(
                "HoverExit",
                hand == 0 ? "Left" : "Right",
                hold,
                "tracking_lost");
        }
        SetInteractionVisual(hold, false);
        if (hand == 0)
        {
            leftHandInteractingClimbingHold = null;
            leftHandIsGripping = false;
            ResetHandDistances(0);
        }
        else
        {
            rightHandInteractingClimbingHold = null;
            rightHandIsGripping = false;
            ResetHandDistances(1);
        }
        StopGripLocomotion(hand == 0 ? Hand.Left : Hand.Right);
    }

    private void StopGripLocomotion(Hand? hand = null)
    {
        if (!isGripLocomotionActive || (hand.HasValue && gripLocomotionHand != hand))
        {
            return;
        }

        actionRecorder?.Record("LocomotionStop", "", null, "grip locomotion stopped");
        isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    private static bool IsSameHold(GameObject current, GameObject candidate)
    {
        return current != null && candidate != null &&
               (current == candidate || current.transform.IsChildOf(candidate.transform) ||
                candidate.transform.IsChildOf(current.transform));
    }

    private static void SetInteractionVisual(GameObject hold, bool active)
    {
        if (hold != null && hold.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.material.SetInt("_IsBeingInteracted", active ? 1 : 0);
        }
    }
    void SetUpRouteByHoldList(RouteDefinition route)
    {
        ActiveRouteDefinition = route;
        List<string> holdsList = new(route.holds);
        ClearHighlightCircles();
        // Disable all holds
        activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material.SetFloat("_HoldAlpha", inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in holdsList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                Material material = renderer.material;
                material.SetFloat("_HoldAlpha", activeHoldAlpha);
            }

            CoACD coACD = holdsDictionary[holdName].GetComponent<CoACD>();
            if (coACD != null)
            {
                holdsDictionary[holdName].GetComponent<CoACD>().enabled = true;
                MeshCollider[] meshColliders = holdsDictionary[holdName].GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = true;
                }
            }
            SphereCollider sphere = holdsDictionary[holdName].GetComponent<SphereCollider>();
            if (sphere != null)
            {
                holdsDictionary[holdName].GetComponent<SphereCollider>().enabled = true;
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
        SpawnRouteHalos(route);
    }

    private void SpawnRouteHalos(RouteDefinition route)
    {
        if (highlightCirclePrefab == null || holdsParentGameObject == null || route?.holds == null)
        {
            return;
        }

        Transform board = holdsParentGameObject.transform;
        // The imported MoonBoard grid lies in the holds parent's local XZ plane; local Y is
        // the trusted wall-normal axis used by the scanned-hold seating offsets.
        Vector3 boardNormal = board.up.normalized;
        Vector3 boardHorizontal = board.right.normalized;
        Vector3 boardVertical = board.forward.normalized;
        Vector3 boardPlanePoint = board.position;
        Transform viewer = centerEyeAnchor != null ? centerEyeAnchor.transform : mainCamera?.transform;
        if (viewer != null && Vector3.Dot(boardNormal, viewer.position - boardPlanePoint) < 0f)
        {
            boardNormal = -boardNormal;
        }

        bool hasRoles = route.start != null && route.start.Length > 0 &&
                        route.finish != null && route.finish.Length > 0;
        HashSet<string> starts = hasRoles
            ? new HashSet<string>(route.start, StringComparer.OrdinalIgnoreCase)
            : null;
        HashSet<string> finishes = hasRoles
            ? new HashSet<string>(route.finish, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string holdName in route.holds)
        {
            if (!holdsDictionary.TryGetValue(holdName, out GameObject hold) ||
                !TryGetCombinedRendererBounds(hold, out Bounds bounds))
            {
                continue;
            }

            float width = ProjectedBoundsDiameter(bounds, boardHorizontal);
            float height = ProjectedBoundsDiameter(bounds, boardVertical);
            float outerDiameter = Mathf.Clamp(Mathf.Max(width, height) * 1.35f, 0.14f, 0.30f);
            Vector3 projectedCenter = bounds.center -
                                      boardNormal * Vector3.Dot(bounds.center - boardPlanePoint, boardNormal);
            Vector3 position = projectedCenter + boardNormal * 0.015f;
            Quaternion rotation = Quaternion.LookRotation(boardNormal, boardVertical);

            bool isStart = hasRoles && starts.Contains(holdName);
            bool isFinish = hasRoles && finishes.Contains(holdName);
            Color color = isStart
                ? StartHaloColor
                : isFinish
                    ? FinishHaloColor
                    : IntermediateHaloColor;
            CreateHaloRing(holdName, position, rotation, outerDiameter, color, 0);
            if (isStart || isFinish)
            {
                CreateHaloRing(holdName, position, rotation, outerDiameter * 0.65f, color, 1);
            }
        }
    }

    private void CreateHaloRing(
        string holdName,
        Vector3 position,
        Quaternion rotation,
        float diameter,
        Color color,
        int ringIndex)
    {
        GameObject circle = Instantiate(highlightCirclePrefab);
        circle.name = holdName + (ringIndex == 0 ? " Route Halo" : " Route Halo Inner");
        circle.transform.SetPositionAndRotation(position, rotation);
        if (circle.TryGetComponent(out SpriteRenderer spriteRenderer))
        {
            spriteRenderer.color = color;
            spriteRenderer.sharedMaterial = GetHighlightCircleMaterial();
            spriteRenderer.sortingLayerName = "Default";
            spriteRenderer.sortingOrder = -100 + ringIndex;
            if (spriteRenderer.sprite != null)
            {
                Vector3 spriteSize = spriteRenderer.sprite.bounds.size;
                float sourceDiameter = Mathf.Max(spriteSize.x, spriteSize.y);
                circle.transform.localScale = Vector3.one * (diameter / sourceDiameter);
            }
        }
        if (indicatorLayer >= 0)
        {
            SetLayerRecursively(circle, indicatorLayer);
        }
        circle.transform.SetParent(holdsParentGameObject.transform, true);
        activeHighlightCircles.Add(circle);
    }

    private Material GetHighlightCircleMaterial()
    {
        if (highlightCircleMaterial == null)
        {
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default");
            if (shader != null)
            {
                highlightCircleMaterial = new Material(shader) { name = "Route Halo Material" };
            }
        }
        return highlightCircleMaterial;
    }

    private static float ProjectedBoundsDiameter(Bounds bounds, Vector3 axis)
    {
        Vector3 extents = bounds.extents;
        return 2f * (Mathf.Abs(axis.x) * extents.x +
                     Mathf.Abs(axis.y) * extents.y +
                     Mathf.Abs(axis.z) * extents.z);
    }

    private static bool TryGetCombinedRendererBounds(GameObject target, out Bounds bounds)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++)
        {
            bounds.Encapsulate(renderers[index].bounds);
        }
        return true;
    }
    /// <summary>
    /// Recursively sets go and all its children to the given layer.
    /// </summary>
    void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    void PreviewAllHolds()
    {
        // Disable all holds
        activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material.SetFloat("_HoldAlpha", inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in activeRouteHoldsNamesList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                Material material = renderer.material;
                material.SetFloat("_HoldAlpha", activeHoldAlpha);
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
    }
    private void ClearHighlightCircles()
    {
        foreach (var circle in activeHighlightCircles)
        {
            Destroy(circle);
        }
        activeHighlightCircles.Clear();
    }

    private static XRGrabInteractable EnsureHoldInteractionComponents(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        if (sphere == null)
        {
            sphere = hold.AddComponent<SphereCollider>();
            FitHoldHoverCollider(hold);
        }

        XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
        if (grab == null)
        {
            grab = hold.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
        }
        if (!grab.colliders.Contains(sphere))
        {
            grab.colliders.Add(sphere);
        }
        return grab;
    }

    private void EnsureHoldsDictionary()
    {
        if (holdsDictionary != null && holdsDictionary.Count > 0)
        {
            return;
        }

        holdsDictionary = new Dictionary<string, GameObject>();
        if (holdsParentGameObject == null)
        {
            return;
        }
        ResolveStudyLayers();
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0];
            holdsDictionary[holdName] = child.gameObject;
            if (studyHoldsLayer >= 0)
            {
                SetLayerRecursively(child.gameObject, studyHoldsLayer);
            }
            FitHoldHoverCollider(child.gameObject);
        }
    }

    private void EnsureExaminationHeadlamp()
    {
        if (examinationHeadlamp != null || centerEyeAnchor == null)
        {
            return;
        }

        ResolveStudyLayers();
        Transform existing = centerEyeAnchor.transform.Find("Examination Headlamp");
        GameObject headlampObject;
        if (existing != null)
        {
            headlampObject = existing.gameObject;
            examinationHeadlamp = headlampObject.GetComponent<Light>();
        }
        else
        {
            headlampObject = new GameObject("Examination Headlamp");
            headlampObject.transform.SetParent(centerEyeAnchor.transform, false);
        }

        examinationHeadlamp ??= headlampObject.AddComponent<Light>();
        headlampObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        examinationHeadlamp.type = LightType.Spot;
        examinationHeadlamp.color = Color.white;
        examinationHeadlamp.intensity = 2f;
        examinationHeadlamp.range = 3.5f;
        examinationHeadlamp.spotAngle = 70f;
        examinationHeadlamp.innerSpotAngle = 55f;
        examinationHeadlamp.shadows = LightShadows.None;
        examinationHeadlamp.renderMode = LightRenderMode.ForcePixel;
        examinationHeadlamp.cullingMask =
            (studyHoldsLayer >= 0 ? 1 << studyHoldsLayer : 0) |
            (studyGhostHoldsLayer >= 0 ? 1 << studyGhostHoldsLayer : 0);
        examinationHeadlamp.enabled = false;
    }

    private void ResolveStudyLayers()
    {
        if (studyHoldsLayer < 0)
        {
            studyHoldsLayer = LayerMask.NameToLayer(StudyHoldsLayerName);
        }
        if (studyGhostHoldsLayer < 0)
        {
            studyGhostHoldsLayer = LayerMask.NameToLayer(StudyGhostHoldsLayerName);
        }
        if (studyHoldsLayer < 0 || studyGhostHoldsLayer < 0)
        {
            Debug.LogError("Study hold layers are missing; the examination headlamp cannot be isolated.");
        }
    }

    // The scene's baked hold SphereColliders are authored with a hardcoded local radius that
    // the FBX import scale inflates to ~2 m world (measured live 2026-07-16): every hover
    // volume overlapped most of the board, hover targeting was last-enter-wins from meters
    // away, and the solid spheres intercepted the experimenter panel's pinch raycast. Fit the
    // sphere to the actual mesh bounds instead (the same math GhostHoldController applies to
    // ghost clones) and make it a trigger so HandHoverCollider still fires while
    // Physics.Raycast(..., QueryTriggerInteraction.Ignore) passes straight through.
    private static void FitHoldHoverCollider(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        MeshFilter meshFilter = hold.GetComponent<MeshFilter>();
        if (sphere == null || meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        Bounds meshBounds = meshFilter.sharedMesh.bounds;
        sphere.center = meshBounds.center;
        sphere.radius = meshBounds.extents.magnitude;
        sphere.isTrigger = true;
    }

    private void CacheMoonBoardTransform()
    {
        if (hasInitialMoonBoardTransform || moonBoardEnv == null)
        {
            return;
        }

        initialMoonBoardLocalPosition = moonBoardEnv.transform.localPosition;
        initialMoonBoardLocalRotation = moonBoardEnv.transform.localRotation;
        initialMoonBoardLocalScale = moonBoardEnv.transform.localScale;
        hasInitialMoonBoardTransform = true;
    }

    private static bool IsHandTrackingValid(OVRSkeleton skeleton, ref OVRHand hand)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count < TrackedBoneCount)
        {
            return false;
        }

        hand ??= skeleton.GetComponent<OVRHand>();
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence;
    }

    private static void CopyRelativePose(List<Vector3> source, ref List<Vector3> destination)
    {
        destination ??= new List<Vector3>(source.Count);
        destination.Clear();
        if (destination.Capacity < source.Count)
        {
            destination.Capacity = source.Count;
        }

        Vector3 origin = source[0];
        for (int i = 0; i < source.Count; i++)
        {
            destination.Add(source[i] - origin);
        }
    }

    private void EnsureGripPipeline()
    {
        if (gripContactPipeline != null || IsGripFeedbackDegraded ||
            distanceToClosestBoneComputeShader == null)
        {
            return;
        }

        gripScoreConfig ??= Resources.Load<GripScoreConfig>("GripScoreConfig");
        if (gripScoreConfig == null)
        {
            gripScoreConfig = ScriptableObject.CreateInstance<GripScoreConfig>();
            Debug.LogWarning("GripScoreConfig asset was not found; using runtime defaults.");
        }
        gripContactPipeline = new GripContactPipeline(
            this,
            distanceToClosestBoneComputeShader,
            gripScoreConfig,
            gripRecoveryAttempted);
        gripContactPipeline.DebugSetReadbackFailures(debugForceGripReadbackFailures);
    }

    private void HandleGripPipelineHealth()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gripContactPipeline.IsRecoveryReady)
        {
            Debug.LogWarning("[SceneConfiguror] Grip feedback recovery attempt after sustained readback failure.");
            actionRecorder?.Record(
                "GripFeedbackRecoveryAttempt",
                "",
                null,
                "recreating GPU readback pipeline");
            gripContactPipeline.ClearFeedback();
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
            gripRecoveryAttempted = true;
            EnsureGripPipeline();
            PrepareCurrentGripTargets();
        }
        else if (gripContactPipeline.IsDegradationReady)
        {
            IsGripFeedbackDegraded = true;
            GripFeedbackDegradedUtc = DateTime.UtcNow.ToString("o");
            Debug.LogError("[SceneConfiguror] Grip feedback entered DEGRADED; block continues.");
            SetStudyFeedbackVisible(false);
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
        }
    }

    private void PrepareCurrentGripTargets()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline.Prepare(activeHoldsList);
        }
        else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                 ghostHoldController.CurrentGhost != null)
        {
            gripContactPipeline.Prepare(ghostHoldController.CurrentGhost);
        }
    }

    private void EnsureRuntimeControllers()
    {
        ghostHoldController = ghostHoldController != null
            ? ghostHoldController
            : FindAnyObjectByType<GhostHoldController>();
        if (ghostHoldController == null)
        {
            GameObject controllerObject = new("GhostHoldController");
            ghostHoldController = controllerObject.AddComponent<GhostHoldController>();
        }
        if (ghostHoldController.GetComponent<StudyManager>() == null)
        {
            ghostHoldController.gameObject.AddComponent<StudyManager>();
        }
    }

    private void OnDestroy()
    {
        gripContactPipeline?.Dispose();
        if (highlightCircleMaterial != null)
        {
            Destroy(highlightCircleMaterial);
        }
    }

}
