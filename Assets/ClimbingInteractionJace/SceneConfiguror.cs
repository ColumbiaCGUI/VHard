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
    public List<string> holdsList;
    public List<GameObject> activeHoldsList;

    [Header("Hands References")]
    public GameObject leftHand;
    public GameObject leftHandNearFarInteractor;
    public SkinnedMeshRenderer leftHandSkinnedMeshRenderer;
    public GameObject leftHandRootBone;
    public List<GameObject> leftHandBones;

    public GameObject rightHand;
    public GameObject rightHandNearFarInteractor;
    public SkinnedMeshRenderer rightHandSkinnedMeshRenderer;
    public GameObject rightHandRootBone;
    public List<GameObject> rightHandBones;

    [Header("Interaction Settings")]
    public float hoverRadiusOverride;
    public float interactionColorMaxDistanceOverride;
    public bool disableInactiveHolds;
    public float inactiveHoldAlpha;
    public float activeHoldAlpha;

    [Header("Interaction State (Changing this is usually a bad move, fix the underlying problem!)")]
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;

    [Header("Interaction Compute Shader Settings")]
    public ComputeShader distanceToClosestBoneComputeShader;
    public int kernelHandle;

    [Header("Interaction Compute Shader State (Changing this is usually a bad move, fix the underlying problem!)")]
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

        // Traverse root bone of each hand and add all bones to list
        leftHandBones = new List<GameObject>();
        rightHandBones = new List<GameObject>();
        TraverseBones(leftHandRootBone, leftHandBones);
        TraverseBones(rightHandRootBone, rightHandBones);

        // Set up compute shader
        kernelHandle = distanceToClosestBoneComputeShader.FindKernel("CSMain");

        // DEV: Turn on all holds by default
        SetUpRouteByName("[ALL]");
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
        // Override hover radius
        foreach (var nearFarInteractor in new GameObject[] { leftHandNearFarInteractor, rightHandNearFarInteractor })
        {
            IInteractionCaster nearInteractionCaster = nearFarInteractor.GetComponent<NearFarInteractor>().nearInteractionCaster;
            if (nearInteractionCaster is SphereInteractionCaster sphereInteractionCaster)
            {
                sphereInteractionCaster.castRadius = hoverRadiusOverride;
            }
        }

        if (leftHandInteractingClimbingHold == null && rightHandInteractingClimbingHold == null)
        {
            return;
        }

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            MeshRenderer leftHandMeshRenderer = leftHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            leftHandMeshRenderer.material.SetInt("_IsBeingInteracted", 1);
            leftHandMeshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            MeshRenderer rightHandMeshRenderer = rightHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            rightHandMeshRenderer.material.SetInt("_IsBeingInteracted", 1);
            rightHandMeshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
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
            leftHandBonesBuffer = new ComputeBuffer(leftHandBones.Count, sizeof(float) * 3); // World position of each bone of the left hand
            rightHandBonesBuffer = new ComputeBuffer(rightHandBones.Count, sizeof(float) * 3); // World position of each bone of the right hand
            leftHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the left hand
            rightHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the right hand

            // Calculate input buffer data
            for (int i = 0; i < climbingHoldVertices.Length; i++)
            {
                climbingHoldVertices[i] = climbingHold.transform.TransformPoint(climbingHoldVertices[i]); // Convert to world position
            }
            climbingHoldVerticesBuffer.SetData(climbingHoldVertices);
            leftHandBonesBuffer.SetData(leftHandBones.ConvertAll(bone => bone.transform.position).ToArray());
            rightHandBonesBuffer.SetData(rightHandBones.ConvertAll(bone => bone.transform.position).ToArray());

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


    public void LeftHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(leftHand, args);
    }
    public void RightHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(rightHand, args);
    }
    void HandHoverEnter(GameObject hand, HoverEnterEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            UnityEngine.Debug.Log("Hand hover enter: " + hand.name + " is now interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            if (activeHoldsList.Contains(hoveredGameObject))
            {
                UnityEngine.Debug.Log("Hand hover enter: " + hand.name + " is now interacting with Climbing Hold " + hoveredGameObject.name);

                MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
                meshRenderer.material.SetInt("_IsBeingInteracted", 1);
                meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);

                if (hand == leftHand)
                {
                    leftHandInteractingClimbingHold = hoveredGameObject;
                }
                else if (hand == rightHand)
                {
                    rightHandInteractingClimbingHold = hoveredGameObject;
                }
            }
        }
        else
        {
            UnityEngine.Debug.Log("Hand hover enter: " + hand.name + " is now interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void LeftHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(leftHand, args);
    }
    public void RightHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(rightHand, args);
    }
    void HandHoverExit(GameObject hand, HoverExitEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            UnityEngine.Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            UnityEngine.Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 0);

            if (hand == leftHand)
            {
                leftHandInteractingClimbingHold = null;
            }
            else if (hand == rightHand)
            {
                rightHandInteractingClimbingHold = null;
            }
        }
        else
        {
            UnityEngine.Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void SetUpRouteByName(string routeName)
    {
        UnityEngine.Debug.Log("Requested route by name: " + routeName);
        // Set up the holds for the route
        List<string> holdsList = new List<string>();
        switch (routeName)
        {
            case "DEATH STAR":
                holdsList = new List<string> { "D15", "D18", "G13", "H11", "I4", "J6", "K9" };
                break;
            case "SPEED":
                holdsList = new List<string> { "A5", "D5", "D15", "F12", "F18", "G8", "G10" };
                break;
            case "THE CRUSH ALT":
                holdsList = new List<string> { "B6", "C8", "D1", "D11", "F14", "F16", "K18" };
                break;
            case "TO JUG, OR NOT TO JUG...":
                holdsList = new List<string> { "D9", "D15", "F5", "F12", "G13", "H10", "H18" };
                break;
            case "WHITE JUGHAUL":
                holdsList = new List<string> { "F5", "H10", "H18", "I8", "K13", "K15", "K17" };
                break;
            case "[ALL]":
                holdsList = new List<string> { // this was the fastest way to get this working, sue me
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
        UnityEngine.Debug.Log("Setting up route " + routeName + " with holds " + string.Join(", ", holdsList));
        SetUpRouteByHoldList(holdsList);

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
                material.SetFloat("_HoldAlpha", inactiveHoldAlpha); // Replace "_Transparency" with the exact property name used in Shader Graph
                hold.GetComponent<XRGrabInteractable>().enabled = false;
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
                material.SetFloat("_HoldAlpha", activeHoldAlpha); // Replace "_Transparency" with the exact property name used in Shader Graph
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
    }
}
