using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using Unity.XR.CoreUtils;
using UnityEngine;
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

public class SceneConfiguror : MonoBehaviour
{
    private const int TrackedBoneCount = 26;
    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };
    [Header("Action Recorder")]
    public ActionRecorder actionRecorder;
    [Header("HighlightCircle")]
    public GameObject highlightCirclePrefab;
    private List<GameObject> activeHighlightCircles = new();

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
    public string currentRouteName = string.Empty;
    public GhostHoldController ghostHoldController;

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
    private MoonBoardStudyCatalog routeCatalog;
    private string holdsDictionaryError = string.Empty;
    private MaterialPropertyBlock holdProperties;
    private MaterialPropertyBlock HoldProperties => holdProperties ??= new MaterialPropertyBlock();

    private void Awake()
    {
        EnsureRuntimeControllers();
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

        // 2) camera culling masks [Update 7/7/2026: Culling mask for circles on main camera might still be useful,
        // feel free to comment out the entire section so that the highlight circles can be shown. CAROLINE]
        if (indicatorLayer >= 0 && mainCamera != null)
        {
            int mask = 1 << indicatorLayer;
            Debug.Log($"[SC] mainCamera mask before:    {mainCamera.cullingMask:X8}");
            mainCamera.cullingMask    &= ~mask;
            Debug.Log($"[SC] mainCamera mask after:     {mainCamera.cullingMask:X8}");
            /*Debug.Log($"[SC] minimapCamera mask before: {minimapCamera.cullingMask:X8}");
            minimapCamera.cullingMask |=  mask;
            Debug.Log($"[SC] minimapCamera mask after:  {minimapCamera.cullingMask:X8}");*/
        }
        /*indicatorLayer = LayerMask.NameToLayer(indicatorLayerName);
        if (indicatorLayer == -1)
        {
            UnityEngine.Debug.LogError("Layer " + indicatorLayerName + " not found!");
        }
        else
        {
            int mask = 1 << indicatorLayer;
            // exclude from main camera
            mainCamera.cullingMask &= ~mask;
            // include on minimap camera
            //minimapCamera.cullingMask |= mask;
        }*/

        UnityEngine.Debug.Log("SceneConfiguror initializing.");
        CacheMoonBoardTransform();

        // Add all the children of the holds parent to the holds dictionary, to be accessed using the string [A-K][1-18]
        // Jace: Note that the holds are currently named [A-K][1-18].[001/002/003]
        EnsureHoldsDictionary();
        EnsureGripPipeline();

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
        centerEyePosition = centerEyeAnchor.transform.position;

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            SetInteractionVisual(
                leftHandInteractingClimbingHold,
                gripContactPipeline == null,
                interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            SetInteractionVisual(
                rightHandInteractingClimbingHold,
                gripContactPipeline == null,
                interactionColorMaxDistanceOverride);
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
                SetInteractionVisual(hoveredGameObject, gripContactPipeline == null);

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
            SetInteractionVisual(hoveredGameObject, false);

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
        if (!TryGetRouteHolds(routeName, out List<string> routeHolds))
        {
            UnityEngine.Debug.LogError("Route name " + routeName + " not found!");
            return;
        }

        activeRouteHoldsNamesList = routeHolds;
        currentRouteName = routeName;
        if (routeName != "[PREVIEW ALL (SHADER OFF)]")
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with holds " + string.Join(", ", activeRouteHoldsNamesList));
            SetUpRouteByHoldList(activeRouteHoldsNamesList);
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

    public bool SetRouteCatalog(MoonBoardStudyCatalog catalog, out string error)
    {
        if (catalog == null)
        {
            error = "MoonBoard route catalog is unavailable.";
            return false;
        }
        if (!catalog.TryValidate(out error))
        {
            return false;
        }

        routeCatalog = catalog;
        holdsDictionary = null;
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (holdsDictionary.Count != catalog.holds.Length)
        {
            error = $"Scene contains {holdsDictionary.Count} holds; catalog requires {catalog.holds.Length}.";
            return false;
        }
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            if (!holdsDictionary.TryGetValue(hold.coordinate, out GameObject sceneHold) ||
                sceneHold.GetComponent<MeshFilter>() == null ||
                sceneHold.GetComponent<MeshRenderer>() == null)
            {
                error = "Scene is missing usable MoonBoard 2016 hold " + hold.coordinate + ".";
                return false;
            }
        }
        foreach (string coordinate in holdsDictionary.Keys)
        {
            if (!catalog.TryGetHold(coordinate, out _))
            {
                error = "Scene contains hold outside the MoonBoard 2016 catalog: " + coordinate + ".";
                return false;
            }
        }

        currentRouteName = catalog.routes[0].id;
        SetUpRouteByName(currentRouteName);
        error = string.Empty;
        return true;
    }

    public bool TryGetRouteDefinition(string routeId, out MoonBoardRouteDefinition route)
    {
        route = null;
        return routeCatalog != null && routeCatalog.TryGetRoute(routeId, out route);
    }

    public string GetRouteDisplayName(string routeId)
    {
        return TryGetRouteDefinition(routeId, out MoonBoardRouteDefinition route) ? route.name : routeId;
    }

    public bool TryValidateRoute(string routeName, out string error)
    {
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (!TryGetRouteHolds(routeName, out List<string> routeHolds))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        string[] missing = routeHolds.Where(hold => !holdsDictionary.ContainsKey(hold)).ToArray();
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
        if (!TryValidateRoute(routeName, out error) || !TryGetRouteHolds(routeName, out List<string> routeHolds))
        {
            return false;
        }

        activeRouteHoldsNamesList = routeHolds;
        currentRouteName = routeName;
        error = string.Empty;
        return true;
    }

    private bool TryGetRouteHolds(string routeName, out List<string> routeHolds)
    {
        if (routeName == "[PREVIEW ALL (SHADER OFF)]")
        {
            EnsureHoldsDictionary();
            routeHolds = holdsDictionary.Keys.OrderBy(coordinate => coordinate).ToList();
            return true;
        }
        if (routeCatalog != null && routeCatalog.TryGetRoute(routeName, out MoonBoardRouteDefinition route))
        {
            routeHolds = route.moves.OrderBy(move => move.sequence).Select(move => move.coordinate).ToList();
            return true;
        }
        routeHolds = null;
        return false;
    }

    public void SetGameMode(GameMode newMode)
    {
        bool leavingGhostMode = gameMode == GameMode.Ghost && newMode != GameMode.Ghost;
        if (leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(false);
        }
        ResetInteractionState();
        gameMode = newMode;
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
        if (environment == null)
        {
            return;
        }

        environment.SetActive(true);
        Transform alignmentRoot = moonBoardEnv != null ? moonBoardEnv.transform.parent : null;
        foreach (Transform child in environment.transform)
        {
            child.gameObject.SetActive(visible || child == alignmentRoot);
        }
        if (moonBoardEnv != null)
        {
            moonBoardEnv.SetActive(visible);
        }
    }

    public bool IsGripFeedbackReady => gripContactPipeline != null && gripContactPipeline.IsSupported;

    public void SetStudyFeedbackVisible(bool visible)
    {
        foreach (HandBoneTracker tracker in FindObjectsByType<HandBoneTracker>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            tracker.SetFeedbackVisible(visible);
        }
        if (!visible)
        {
            gripContactPipeline?.ClearFeedback();
        }
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

    private void SetInteractionVisual(GameObject hold, bool active, float maxDistance = -1f)
    {
        if (hold != null && hold.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.GetPropertyBlock(HoldProperties);
            HoldProperties.SetInt("_IsBeingInteracted", active ? 1 : 0);
            if (maxDistance >= 0f)
            {
                HoldProperties.SetFloat("_InteractionColorMaxDistance", maxDistance);
            }
            meshRenderer.SetPropertyBlock(HoldProperties);
        }
    }

    private void SetHoldAlpha(Renderer renderer, float alpha)
    {
        renderer.GetPropertyBlock(HoldProperties);
        HoldProperties.SetFloat("_HoldAlpha", alpha);
        renderer.SetPropertyBlock(HoldProperties);
    }
    void SetUpRouteByHoldList(List<string> holdsList)
    {
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
                    SetHoldAlpha(renderer, inactiveHoldAlpha);
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
                SetHoldAlpha(renderer, activeHoldAlpha);
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
            /*
            if (highlightCirclePrefab != null)
            {
                // 1) Instantiate as a child of the hold, preserving the prefab's local transform
                var circle = Instantiate(
                highlightCirclePrefab,
                 holdsDictionary[holdName].transform,      // parent
                false                  // worldPositionStays = false
                );

                // 2) Zero out localPosition & localRotation (so it sits exactly on the hold)
                circle.transform.localPosition = Vector3.zero;
                circle.transform.localRotation = Quaternion.identity;

                // 3) Nudge it *just* off the wall along its local +Z to avoid z‑fighting
                circle.transform.localPosition += Vector3.forward * 0.01f;

                // 4) Scale it to match the hold’s size
                if ( holdsDictionary[holdName].TryGetComponent<Renderer>(out var rdr))
                {
                    float maxDim = Mathf.Max(
                    rdr.bounds.size.x,
                    rdr.bounds.size.y,
                    rdr.bounds.size.z
                    );
                    circle.transform.localScale = Vector3.one * (maxDim * 0.01f);
                }
                else
                {
                    circle.transform.localScale = Vector3.one * 0.01f;
                }

                // 5) Layermask it
                SetLayerRecursively(circle, indicatorLayer);


                activeHighlightCircles.Add(circle);

                Debug.Log($"[SC] Spawned circle at {circle.transform.position} on layer '{LayerMask.LayerToName(indicatorLayer)}'");

            }*/
        }
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
                    SetHoldAlpha(renderer, inactiveHoldAlpha);
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
                SetHoldAlpha(renderer, activeHoldAlpha);
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
        }
        MeshRenderer renderer = hold.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            sphere.center = renderer.localBounds.center;
            sphere.radius = renderer.localBounds.extents.magnitude;
        }
        sphere.isTrigger = true;

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

        holdsDictionary = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        holdsDictionaryError = string.Empty;
        if (holdsParentGameObject == null)
        {
            return;
        }
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0].ToUpperInvariant();
            if (!MoonBoardStudyCatalog.TryParseCoordinate(holdName, out _, out _))
            {
                holdsDictionaryError = "Invalid direct child in hold hierarchy: " + child.name + ".";
                continue;
            }
            if (!holdsDictionary.TryAdd(holdName, child.gameObject))
            {
                holdsDictionaryError = "Duplicate hold coordinate in scene: " + holdName + ".";
            }
        }
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
        if (gripContactPipeline != null || distanceToClosestBoneComputeShader == null)
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
            gripScoreConfig);
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
    }

}
