using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

public class SceneConfiguror : MonoBehaviour
{
    [Header("Scene References")]
    public GameObject holdsParentGameObject;
    public Dictionary<string, GameObject> holdsDictionary;
    public List<string> activeRouteHoldsNamesList;
    public List<GameObject> activeHoldsList;

    [Header("Hands References (Left = 0, Right = 1)")]
    public OVRSkeleton leftHandOVRSkeleton;
    public OVRSkeleton rightHandOVRSkeleton;
    public GameObject leftHandNearFarInteractor;
    public GameObject rightHandNearFarInteractor;

    [Header("Hands State")]
    public int numBonesPerHand;
    public List<Vector3> leftHandBonePositions = new List<Vector3>();
    public List<Vector3> rightHandBonePositions = new List<Vector3>();

    [Header("Interaction Settings")]
    public float hoverRadiusOverride;
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

    [Header("Interaction Compute Shader State")]
    public ComputeBuffer climbingHoldVerticesBuffer;
    public ComputeBuffer leftHandBonesBuffer;
    public ComputeBuffer rightHandBonesBuffer;
    public ComputeBuffer leftHandDistancesBuffer;
    public ComputeBuffer rightHandDistancesBuffer;

    void Start()
    {
        // Add all the children of the holds parent to the holds dictionary, to be accessed using the string [A-K][1-18]
        // Jace: Note that the holds are currently named [A-K][1-18].[001/002/003]
        holdsDictionary = new Dictionary<string, GameObject>();
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0];
            holdsDictionary[holdName] = child.gameObject;
            // UnityEngine.Debug.Log("Found and added hold " + holdName);
        }

        // Access hand bones
        foreach (OVRBone bone in leftHandOVRSkeleton.Bones)
        {
            leftHandBonePositions.Add(bone.Transform.position);
        }
        foreach (OVRBone bone in rightHandOVRSkeleton.Bones)
        {
            rightHandBonePositions.Add(bone.Transform.position);
        }
        numBonesPerHand = leftHandBonePositions.Count;
        UnityEngine.Debug.Log("Found " + numBonesPerHand + " bones per hand.");
        UnityEngine.Debug.Log("SceneConfiguror initializing.");

        // Set up compute shader
        kernelHandle = distanceToClosestBoneComputeShader.FindKernel("CSMain");

        // DEV: Turn on all holds by default
        // SetUpRouteByName("[PREVIEW ALL (SHADER OFF)]");
        SetUpRouteByName("DEATH STAR");
    }

    void Update()
    {
        // Override hover radius
        IInteractionCaster leftHandNearInteractionCaster = leftHandNearFarInteractor.GetComponent<NearFarInteractor>().nearInteractionCaster;
        if (leftHandNearInteractionCaster is SphereInteractionCaster leftHandSphereInteractionCaster)
        {
            leftHandSphereInteractionCaster.castRadius = hoverRadiusOverride;
        }
        IInteractionCaster rightHandNearInteractionCaster = rightHandNearFarInteractor.GetComponent<NearFarInteractor>().nearInteractionCaster;
        if (rightHandNearInteractionCaster is SphereInteractionCaster rightHandSphereInteractionCaster)
        {
            rightHandSphereInteractionCaster.castRadius = hoverRadiusOverride;
        }

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = leftHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = rightHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }

        // Update hand bone info
        numBonesPerHand = leftHandOVRSkeleton.Bones.Count;
        leftHandBonePositions = new List<Vector3>(leftHandOVRSkeleton.Bones.Select(bone => bone.Transform.position));
        rightHandBonePositions = new List<Vector3>(rightHandOVRSkeleton.Bones.Select(bone => bone.Transform.position));
        // DEBUG: Print first element if not empty (if empty, raise a stink...)
        // if (leftHandBonePositions.Count > 0)
        // {
        //     UnityEngine.Debug.Log("Left hand bone 0: " + leftHandBonePositions[0]);
        // }
        // else
        // {
        //     // UnityEngine.Debug.Log("Left hand bone positions list is empty!");
        // }
        // if (rightHandBonePositions.Count > 0)
        // {
        //     UnityEngine.Debug.Log("Right hand bone 0: " + rightHandBonePositions[0]);
        // }
        // else
        // {
        //     // UnityEngine.Debug.Log("Right hand bone positions list is empty!");
        // }

        if (leftHandInteractingClimbingHold == null && rightHandInteractingClimbingHold == null)
        {
            return;
        }
        List<GameObject> interactingClimbingHolds = new List<GameObject>();
        if (leftHandInteractingClimbingHold != null)
        {
            interactingClimbingHolds.Add(leftHandInteractingClimbingHold);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            interactingClimbingHolds.Add(rightHandInteractingClimbingHold);
        }
        // WARNING: Here be dragons.
        // The big idea is that for each vertex of the climbing hold, we find the distance to the closest bone of each hand, and save to two arrays.
        // Then, we encode these distances in the UVs (channel 2) of the climbing hold's mesh vertices, and access them in the shader.
        foreach (GameObject climbingHold in interactingClimbingHolds)
        {
            // Get information about the climbing hold
            MeshFilter climbingHoldMeshFilter = climbingHold.GetComponent<MeshFilter>();
            Mesh climbingHoldMesh = climbingHoldMeshFilter.mesh;
            Vector3[] climbingHoldVertices = climbingHoldMesh.vertices;
            int climbingHoldVerticesCount = climbingHoldVertices.Length;

            // Initialize buffers for compute shader
            climbingHoldVerticesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float) * 3); // World position of each vertex of the climbing hold
            leftHandBonesBuffer = new ComputeBuffer(numBonesPerHand, sizeof(float) * 3); // World position of each bone of the left hand
            rightHandBonesBuffer = new ComputeBuffer(numBonesPerHand, sizeof(float) * 3); // World position of each bone of the right hand
            leftHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the left hand
            rightHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the right hand

            // Calculate input buffer data
            for (int i = 0; i < climbingHoldVertices.Length; i++)
            {
                climbingHoldVertices[i] = climbingHold.transform.TransformPoint(climbingHoldVertices[i]); // Convert to world position
            }
            climbingHoldVerticesBuffer.SetData(climbingHoldVertices);
            leftHandBonesBuffer.SetData(leftHandBonePositions.ToArray());
            rightHandBonesBuffer.SetData(rightHandBonePositions.ToArray());

            // Pass buffers to compute shader
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "climbingHoldVertices", climbingHoldVerticesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "leftHandBones", leftHandBonesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "rightHandBones", rightHandBonesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "leftHandDistances", leftHandDistancesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "rightHandDistances", rightHandDistancesBuffer);

            // Dispatch compute shader and retrieve output buffer data
            distanceToClosestBoneComputeShader.Dispatch(kernelHandle, climbingHoldVerticesCount / 128, 1, 1);
            float[] leftHandDistances = new float[climbingHoldVerticesCount];
            float[] rightHandDistances = new float[climbingHoldVerticesCount];
            leftHandDistancesBuffer.GetData(leftHandDistances);
            rightHandDistancesBuffer.GetData(rightHandDistances);

            // Release buffers
            climbingHoldVerticesBuffer.Release();
            leftHandBonesBuffer.Release();
            rightHandBonesBuffer.Release();
            leftHandDistancesBuffer.Release();
            rightHandDistancesBuffer.Release();

            // Encode the distances in the UVs and set them in the climbing hold's mesh so that in the shader
            // This works because the order of the vertices is the same in Mesh.vertices and Mesh.uv, both in Unity and in the shader
            Vector2[] newClimbingHoldMeshUVs = new Vector2[climbingHoldVerticesCount];
            for (int i = 0; i < climbingHoldVerticesCount; i++)
            {
                newClimbingHoldMeshUVs[i] = new Vector2(leftHandDistances[i], rightHandDistances[i]);
            }
            climbingHoldMesh.SetUVs(2, newClimbingHoldMeshUVs.ToList());
        }
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
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            if (activeHoldsList.Contains(hoveredGameObject))
            {
                UnityEngine.Debug.Log("Hand hover enter: " + handOVRHand.name + " is now interacting with Climbing Hold " + hoveredGameObject.name);

                MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
                meshRenderer.material.SetInt("_IsBeingInteracted", 1);
                meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);

                if (hand == 0)
                {
                    leftHandInteractingClimbingHold = hoveredGameObject;
                }
                else if (hand == 1)
                {
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
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            UnityEngine.Debug.Log("Hand hover exit: " + ovrHand.name + " is no longer interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 0);

            if (hand == 0)
            {
                leftHandInteractingClimbingHold = null;
            }
            else if (hand == 1)
            {
                rightHandInteractingClimbingHold = null;
            }
        }
        else
        {
            UnityEngine.Debug.Log("Hand hover exit: " + ovrHand.name + " is no longer interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void SetUpRouteByName(string routeName)
    {
        UnityEngine.Debug.Log("Requested route by name: " + routeName);
        // Set up the holds for the route
        switch (routeName)
        {
            case "DEATH STAR":
                activeRouteHoldsNamesList = new List<string> { "D15", "D18", "G13", "H11", "I4", "J6", "K9" };
                break;
            case "SPEED":
                activeRouteHoldsNamesList = new List<string> { "A5", "D5", "D15", "F12", "F18", "G8", "G10" };
                break;
            case "THE CRUSH ALT":
                activeRouteHoldsNamesList = new List<string> { "B6", "C8", "D1", "D11", "F14", "F16", "K18" };
                break;
            case "TO JUG, OR NOT TO JUG...":
                activeRouteHoldsNamesList = new List<string> { "D9", "D15", "F5", "F12", "G13", "H10", "H18" };
                break;
            case "WHITE JUGHAUL":
                activeRouteHoldsNamesList = new List<string> { "F5", "H10", "H18", "I8", "K13", "K15", "K17" };
                break;
            case "[PREVIEW ALL (SHADER OFF)]":
                activeRouteHoldsNamesList = new List<string> { // this was the fastest way to get this working, sue me
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
                };
                break;
            default:
                UnityEngine.Debug.LogError("Route name " + routeName + " not found!");
                break;
        }
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
    }
    void SetUpRouteByHoldList(List<string> holdsList)
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
                Material material = renderer.material;
                material.SetFloat("_HoldAlpha", inactiveHoldAlpha);
                hold.GetComponent<XRGrabInteractable>().enabled = false;
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
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                holdsDictionary[holdName].GetComponent<XRGrabInteractable>().enabled = true;
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
                Material material = renderer.material;
                material.SetFloat("_HoldAlpha", inactiveHoldAlpha);
                hold.GetComponent<XRGrabInteractable>().enabled = false;
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
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                holdsDictionary[holdName].GetComponent<XRGrabInteractable>().enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                Material material = renderer.material;
                material.SetFloat("_HoldAlpha", activeHoldAlpha);
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
    }
}
