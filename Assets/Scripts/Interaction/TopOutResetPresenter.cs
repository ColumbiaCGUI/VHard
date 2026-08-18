using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>World-space BACK TO START button for grip-locomotion rehearsal: once both hands are
/// latched on the route's finish hold, a pokeable button appears mounted flat on the upper
/// vertical board wall just above the finish, and tapping it with a fingertip releases the grips
/// and restores the board to its start pose so the climb can be repeated without opening the
/// experimenter console. Built at runtime, collider-free, Grip mode only; the run and its
/// recording continue across the reset.</summary>
public sealed class TopOutResetPresenter : MonoBehaviour
{
    public const string RootName = "TopOut Reset";

    // The upper wall lives in the locomotion scenery, so a button glued to it rides the board
    // through grip locomotion for free.
    private const string UpperWallPath =
        "Movement Harlem Reconstruction/Board Surround/Upper Vertical Board Wall";

    private const float ButtonWidthMeters = 0.30f;
    private const float ButtonHeightMeters = 0.12f;
    private const float SurfaceGapMeters = 0.012f;
    private const float HoverFacePadMeters = 0.03f;
    private const float HoverExtraDepthMeters = 0.05f;
    private const float TextTransformScale = 0.01f;
    // The console's queue layering: the surface stamps depth ahead of Meta's hand material so a
    // finger in front of the button occludes it per pixel, and the label draws over the surface.
    private const int ButtonSurfaceQueue = 2950;
    private const int ButtonTextQueue = 2975;

    private static readonly Color ButtonColor = new(0.055f, 0.32f, 0.50f, 1f);
    private static readonly Color ButtonHoverColor = new(0.08f, 0.62f, 0.80f, 1f);
    private static readonly Color ButtonPressColor = new(0.93f, 0.58f, 0.12f, 1f);

    private SceneConfiguror owner;
    private GripInteractionCoordinator coordinator;
    private GripEngagementSettings settings;
    private TopOutResetTracker tracker;
    private TopOutPressTracker press;
    private GameObject buttonRoot;
    private StudyPanelButton button;
    private Material buttonMaterial;
    private Material textMaterial;
    private Transform upperWall;
    private GameObject anchorHold;
    private bool buttonVisible;

    public bool IsButtonVisible => buttonVisible;

    public void Bind(
        SceneConfiguror owner,
        GripInteractionCoordinator gripCoordinator,
        GripEngagementSettings gripSettings)
    {
        this.owner = owner != null ? owner : throw new ArgumentNullException(nameof(owner));
        coordinator = gripCoordinator ?? throw new ArgumentNullException(nameof(gripCoordinator));
        settings = gripSettings != null
            ? gripSettings
            : throw new ArgumentNullException(nameof(gripSettings));
        if (this.owner.centerEyeAnchor == null)
        {
            throw new InvalidOperationException(
                "The top-out reset button needs the centre eye anchor to pick the wall face " +
                "the participant can read.");
        }
    }

    /// <summary>Called from the grip reset path, so every interaction reset — mode change, panel
    /// suppression, console RESET, or this button's own press — ends the episode.</summary>
    public void NotifyInteractionReset()
    {
        tracker?.Reset();
        press?.Reset();
        anchorHold = null;
        SetButtonVisible(false);
    }

    private void LateUpdate()
    {
        if (owner == null || coordinator == null || settings == null)
        {
            return;
        }
        if (!settings.topOutResetButtonEnabled || owner.gameMode != GameMode.Grip ||
            owner.IsPanelInputSuppressed)
        {
            NotifyInteractionReset();
            return;
        }

        EnsureTrackers();
        float now = Time.unscaledTime;
        GameObject leftHold = coordinator.GetLatchedHold(Hand.Left);
        GameObject rightHold = coordinator.GetLatchedHold(Hand.Right);
        string rightCoordinate = null;
        bool topOut = TryGetFinishCoordinate(leftHold, out string leftCoordinate) &&
                      TryGetFinishCoordinate(rightHold, out rightCoordinate);
        if (tracker.Update(topOut, now))
        {
            anchorHold = leftHold;
            owner.actionRecorder?.Record(
                "RouteTopOut",
                "",
                leftHold,
                "left=" + leftCoordinate + ";right=" + rightCoordinate);
        }
        if (topOut)
        {
            anchorHold = leftHold;
        }
        if (!tracker.IsButtonVisible || anchorHold == null)
        {
            press.Reset();
            SetButtonVisible(false);
            return;
        }

        if (!TryResolveButtonPose(out Pose buttonPose))
        {
            NotifyInteractionReset();
            return;
        }
        SetButtonVisible(true);
        buttonRoot.transform.SetPositionAndRotation(buttonPose.position, buttonPose.rotation);

        EvaluateFingertips(out bool touching, out bool hovering);
        button.SetHovered(hovering);
        bool pressed = press.Update(touching, now);
        button.SetSelected(press.Progress01 > 0f);
        if (!pressed)
        {
            return;
        }

        // ResetClimbToBase runs the shared interaction reset, which re-enters
        // NotifyInteractionReset through the coordinator and hides this button.
        owner.ResetClimbToBase("topout_button");
    }

    private void EnsureTrackers()
    {
        tracker ??= new TopOutResetTracker(
            Mathf.Max(0f, settings.topOutHoldSeconds),
            Mathf.Max(0.5f, settings.topOutLingerSeconds));
        press ??= new TopOutPressTracker(Mathf.Max(0f, settings.topOutPressDwellSeconds));
    }

    private bool TryGetFinishCoordinate(GameObject hold, out string coordinate)
    {
        coordinate = null;
        if (hold == null)
        {
            return false;
        }

        string holdCoordinate = TopOutResetPolicy.GetHoldCoordinate(hold.name);
        if (owner.GetRouteCueRole(holdCoordinate) != RouteCueRole.Finish)
        {
            return false;
        }
        coordinate = holdCoordinate;
        return true;
    }

    private bool TryResolveButtonPose(out Pose buttonPose)
    {
        buttonPose = default;
        if (anchorHold == null || !anchorHold.TryGetComponent(out Renderer anchorRenderer))
        {
            return false;
        }
        EnsureUpperWall();

        buttonPose = TopOutResetPolicy.GetWallMountedButtonPose(
            upperWall.position,
            upperWall.rotation,
            upperWall.lossyScale,
            anchorRenderer.bounds.center,
            owner.centerEyeAnchor.transform.position,
            Mathf.Max(0f, settings.topOutButtonAboveFinishMeters),
            SurfaceGapMeters,
            ButtonWidthMeters * 0.5f,
            ButtonHeightMeters * 0.5f);
        return true;
    }

    private void EnsureUpperWall()
    {
        if (upperWall != null)
        {
            return;
        }

        GameObject sceneryRoot = owner.gripLocomotionSceneryRoot;
        upperWall = sceneryRoot != null ? sceneryRoot.transform.Find(UpperWallPath) : null;
        if (upperWall == null)
        {
            throw new InvalidOperationException(
                "The top-out reset button requires the upper vertical board wall at '" +
                UpperWallPath + "' under the grip locomotion scenery root.");
        }
    }

    private void EvaluateFingertips(out bool touching, out bool hovering)
    {
        touching = false;
        hovering = false;
        AccumulateFingertips(
            coordinator.LeftTrackingValid,
            owner.leftHandBonePositions,
            ref touching,
            ref hovering);
        AccumulateFingertips(
            coordinator.RightTrackingValid,
            owner.rightHandBonePositions,
            ref touching,
            ref hovering);
    }

    private void AccumulateFingertips(
        bool trackingValid,
        List<Vector3> bonePositions,
        ref bool touching,
        ref bool hovering)
    {
        if (!trackingValid || bonePositions == null)
        {
            return;
        }

        float pressDepth = Mathf.Max(0.01f, settings.topOutPressDepthMeters);
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            int tipBone = GripEngagementGate.GetFingertipBoneIndex(finger);
            if (bonePositions.Count <= tipBone)
            {
                continue;
            }
            // The root carries no scale, so its local frame measures metres.
            Vector3 local = buttonRoot.transform.InverseTransformPoint(bonePositions[tipBone]);
            touching = touching || TopOutResetPolicy.IsFingertipOnButton(
                local,
                ButtonWidthMeters * 0.5f,
                ButtonHeightMeters * 0.5f,
                pressDepth,
                0f);
            hovering = hovering || TopOutResetPolicy.IsFingertipOnButton(
                local,
                ButtonWidthMeters * 0.5f,
                ButtonHeightMeters * 0.5f,
                pressDepth + HoverExtraDepthMeters,
                HoverFacePadMeters);
        }
    }

    private void SetButtonVisible(bool visible)
    {
        if (visible && buttonRoot == null)
        {
            BuildButton();
        }
        if (buttonRoot == null || buttonVisible == visible)
        {
            return;
        }

        buttonVisible = visible;
        buttonRoot.SetActive(visible);
        if (!visible && button != null)
        {
            button.SetHovered(false);
            button.SetSelected(false);
        }
    }

    private void BuildButton()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            throw new InvalidOperationException(
                "The top-out reset button requires the project UI layer.");
        }

        UnityEngine.Shader surfaceShader = UnityEngine.Shader.Find("Interaction/RoundedBoxUnlit") ??
                                           UnityEngine.Shader.Find("Oculus/Unlit Transparent Color");
        TMP_FontAsset fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        UnityEngine.Shader textShader =
            UnityEngine.Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
        if (surfaceShader == null || fontAsset == null || textShader == null)
        {
            throw new InvalidOperationException(
                "The top-out reset button requires LiberationSans SDF, the TMP mobile overlay " +
                "shader and a rounded or unlit button shader in the build.");
        }

        buttonMaterial = new Material(surfaceShader) { renderQueue = ButtonSurfaceQueue };
        if (buttonMaterial.HasProperty("_ZTest"))
        {
            buttonMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        }
        if (buttonMaterial.HasProperty("_ZWrite"))
        {
            buttonMaterial.SetFloat("_ZWrite", 1f);
        }
        textMaterial = new Material(fontAsset.material)
        {
            shader = textShader,
            renderQueue = ButtonTextQueue,
        };

        buttonRoot = new GameObject(RootName + " Button") { layer = uiLayer };
        buttonRoot.transform.SetParent(transform, false);

        GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Cube);
        surface.name = RootName + " Surface";
        surface.layer = uiLayer;
        surface.transform.SetParent(buttonRoot.transform, false);
        surface.transform.localScale = new Vector3(ButtonWidthMeters, ButtonHeightMeters, 0.015f);
        surface.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        // Pressing is a fingertip distance check, never physics: no collider may exist here to
        // intercept the console's pinch ray, which casts against this same UI layer.
        DestroyUnityObject(surface.GetComponent<Collider>());
        button = surface.AddComponent<StudyPanelButton>();
        button.ConfigureSurface(new Vector2(ButtonWidthMeters, ButtonHeightMeters));
        button.SetPalette(ButtonColor, ButtonHoverColor, ButtonPressColor);

        GameObject labelObject = new(RootName + " Label") { layer = uiLayer };
        labelObject.transform.SetParent(buttonRoot.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, -0.011f);
        labelObject.transform.localScale = Vector3.one * TextTransformScale;
        TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
        label.font = fontAsset;
        label.fontSharedMaterial = textMaterial;
        label.rectTransform.sizeDelta =
            new Vector2(ButtonWidthMeters * 0.94f, ButtonHeightMeters * 0.8f) / TextTransformScale;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 0.027f / TextTransformScale * 10f;
        label.fontStyle = FontStyles.Bold;
        label.color = Color.white;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.sortingOrder = 110;
        label.text = "BACK TO START";
        buttonRoot.SetActive(false);
    }

    private static void DestroyUnityObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return;
        }
        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }

    private void OnDestroy()
    {
        if (buttonMaterial != null)
        {
            Destroy(buttonMaterial);
        }
        if (textMaterial != null)
        {
            Destroy(textMaterial);
        }
    }
}
