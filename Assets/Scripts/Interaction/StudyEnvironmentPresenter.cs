using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Owns the study-visibility state of the board, the surrounding scenery and the
/// camera backgrounds, plus the pristine board and room transforms that Condition changes restore.</summary>
public sealed class StudyEnvironmentPresenter
{
    private static readonly string[] SupplementalSceneryNameMarkers =
    {
        "water", "ocean", "terrain", "scenery", "landscape", "skybox",
    };

    private readonly SceneConfiguror configuror;
    private readonly Dictionary<GameObject, bool> supplementalSceneryActiveStates = new();
    private bool studyEnvironmentHidden;
    // The inspector mainCamera reference is a disabled legacy camera; the participant renders
    // through the OVR rig eye anchors, so background suppression must cover every live camera.
    private readonly Dictionary<Camera, (CameraClearFlags flags, Color background)>
        studyEnvironmentCameraStates = new();
    private Vector3 initialMoonBoardLocalPosition;
    private Quaternion initialMoonBoardLocalRotation;
    private Vector3 initialMoonBoardLocalScale;
    private Vector3 initialSceneryLocalPosition;
    private Quaternion initialSceneryLocalRotation;
    private Vector3 initialSceneryLocalScale;
    private bool hasInitialMoonBoardTransform;

    public StudyEnvironmentPresenter(SceneConfiguror configuror)
    {
        this.configuror = configuror;
    }

    public bool IsStudyFeedbackVisible { get; private set; } = true;

    public void CacheMoonBoardTransform()
    {
        GameObject moonBoardEnv = configuror.moonBoardEnv;
        GameObject sceneryRoot = configuror.gripLocomotionSceneryRoot;
        if (hasInitialMoonBoardTransform)
        {
            return;
        }
        if (moonBoardEnv == null || sceneryRoot == null)
        {
            throw new InvalidOperationException(
                "Grip locomotion requires both Moonboard and GripLocomotionSceneryRoot references.");
        }

        initialMoonBoardLocalPosition = moonBoardEnv.transform.localPosition;
        initialMoonBoardLocalRotation = moonBoardEnv.transform.localRotation;
        initialMoonBoardLocalScale = moonBoardEnv.transform.localScale;
        initialSceneryLocalPosition = sceneryRoot.transform.localPosition;
        initialSceneryLocalRotation = sceneryRoot.transform.localRotation;
        initialSceneryLocalScale = sceneryRoot.transform.localScale;
        hasInitialMoonBoardTransform = true;
    }

    public void MoveStudyEnvironment(Vector3 worldDelta)
    {
        if (float.IsNaN(worldDelta.x) || float.IsInfinity(worldDelta.x) ||
            float.IsNaN(worldDelta.y) || float.IsInfinity(worldDelta.y) ||
            float.IsNaN(worldDelta.z) || float.IsInfinity(worldDelta.z))
        {
            throw new ArgumentException("Study environment movement must be finite.", nameof(worldDelta));
        }

        CacheMoonBoardTransform();
        configuror.moonBoardEnv.transform.position += worldDelta;
        configuror.gripLocomotionSceneryRoot.transform.position += worldDelta;
    }

    public void ResetMoonBoardTransform()
    {
        CacheMoonBoardTransform();
        if (!hasInitialMoonBoardTransform)
        {
            return;
        }

        configuror.moonBoardEnv.transform.SetLocalPositionAndRotation(
            initialMoonBoardLocalPosition,
            initialMoonBoardLocalRotation);
        configuror.moonBoardEnv.transform.localScale = initialMoonBoardLocalScale;
        configuror.gripLocomotionSceneryRoot.transform.SetLocalPositionAndRotation(
            initialSceneryLocalPosition,
            initialSceneryLocalRotation);
        configuror.gripLocomotionSceneryRoot.transform.localScale = initialSceneryLocalScale;
    }

    public void SetStudyEnvironmentVisible(bool visible)
    {
        GameObject environment = configuror.environment;
        GameObject moonBoardEnv = configuror.moonBoardEnv;
        GameObject sceneryRoot = configuror.gripLocomotionSceneryRoot;
        Transform alignmentRoot = moonBoardEnv != null ? moonBoardEnv.transform.parent : null;
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
                // Keep the environment root and the alignment root active so spatial-anchor
                // registration survives Condition A; everything else hides.
                environment.SetActive(true);
                foreach (Transform child in environment.transform)
                {
                    child.gameObject.SetActive(child == alignmentRoot);
                }
            }
            if (moonBoardEnv != null)
            {
                moonBoardEnv.SetActive(false);
            }
            if (sceneryRoot != null)
            {
                sceneryRoot.SetActive(false);
            }
            return;
        }

        if (environment != null)
        {
            environment.SetActive(true);
            foreach (Transform child in environment.transform)
            {
                child.gameObject.SetActive(true);
            }
        }
        if (moonBoardEnv != null)
        {
            moonBoardEnv.SetActive(true);
        }
        if (sceneryRoot != null)
        {
            sceneryRoot.SetActive(true);
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
        GameObject environment = configuror.environment;
        supplementalSceneryActiveStates.Clear();
        int waterLayer = LayerMask.NameToLayer("Water");
        foreach (Transform candidate in UnityEngine.Object.FindObjectsByType<Transform>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            if (candidate == null || candidate.gameObject.scene != configuror.gameObject.scene ||
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
        GameObject environment = configuror.environment;
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
        foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
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

    /// <summary>Applies the resolved visibility to every hand tracker. The grip pipeline is
    /// driven by the facade so pipeline lifetime stays in one place.</summary>
    public void SetFeedbackVisible(bool effectiveVisibility)
    {
        IsStudyFeedbackVisible = effectiveVisibility;
        foreach (HandBoneTracker tracker in UnityEngine.Object.FindObjectsByType<HandBoneTracker>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            tracker.SetFeedbackVisible(effectiveVisibility);
        }
    }
}
