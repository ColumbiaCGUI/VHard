using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public sealed class GhostHoldController : MonoBehaviour
{
    [Header("Selection")]
    [SerializeField] private float maxRayDistance = 12f;
    [SerializeField] private float spawnDistance = 0.45f;
    [SerializeField] private float spawnVerticalOffset = -0.15f;
    [SerializeField] private float nearGrabPadding = 0.08f;

    [Header("Visuals")]
    [SerializeField] private Color rayColor = new(0.25f, 0.8f, 1f, 0.8f);
    [SerializeField] private Color markerColor = new(1f, 0.8f, 0.2f, 0.9f);
    [SerializeField] private float markerWidth = 0.006f;

    private SceneConfiguror sceneConfiguror;
    private Camera userCamera;
    private OVRHand leftHand;
    private OVRHand rightHand;
    private OVRSkeleton leftSkeleton;
    private OVRSkeleton rightSkeleton;
    private bool modeActive;
    private bool panelInputSuppressed;
    private bool leftWasPinching;
    private bool rightWasPinching;
    private bool leftPinchArmed;
    private bool rightPinchArmed;
    private Hand? manipulatingHand;
    private Vector3 grabPositionOffset;
    private Quaternion grabRotationOffset;
    private Vector3 lockedGhostScale;
    private GameObject currentGhost;
    private GameObject wallReferent;
    private GameObject dismissAffordance;
    private LineRenderer wallMarker;
    private LineRenderer leftRay;
    private LineRenderer rightRay;
    private Material wallMarkerMaterial;
    private Material leftRayMaterial;
    private Material rightRayMaterial;
    private MaterialPropertyBlock wallMarkerProperties;
    private MaterialPropertyBlock leftRayProperties;
    private MaterialPropertyBlock rightRayProperties;
    private int leftInputDiagnosticState = -1;
    private int rightInputDiagnosticState = -1;

    public GameObject CurrentGhost => currentGhost;
    public GameObject WallReferent => wallReferent;

    public void Initialize(SceneConfiguror configuror)
    {
        sceneConfiguror = configuror;
        userCamera = configuror.centerEyeAnchor != null
            ? configuror.centerEyeAnchor.GetComponent<Camera>()
            : null;
        userCamera = userCamera != null ? userCamera : Camera.main;
        leftSkeleton = configuror.leftHandOVRSkeleton;
        rightSkeleton = configuror.rightHandOVRSkeleton;
        leftHand = leftSkeleton != null ? leftSkeleton.GetComponent<OVRHand>() : null;
        rightHand = rightSkeleton != null ? rightSkeleton.GetComponent<OVRHand>() : null;
        EnsureRayVisuals();
    }

    public void SetModeActive(bool active)
    {
        modeActive = active;
        manipulatingHand = null;
        leftWasPinching = false;
        rightWasPinching = false;
        leftPinchArmed = false;
        rightPinchArmed = false;
        ResetInputDiagnostics();

        if (!active)
        {
            DismissGhost();
        }

        SetCurrentGhostGrabEnabled(active && !panelInputSuppressed);
        SetRayVisible(leftRay, active && !panelInputSuppressed && IsPointerTracked(leftHand));
        SetRayVisible(rightRay, active && !panelInputSuppressed && IsPointerTracked(rightHand));
    }

    public void SetPanelInputSuppressed(bool suppressed)
    {
        bool changed = panelInputSuppressed != suppressed;
        panelInputSuppressed = suppressed;
        if (changed)
        {
            ResetInputDiagnostics();
            RecordInputSuppression(suppressed);
        }
        if (suppressed)
        {
            manipulatingHand = null;
            leftPinchArmed = false;
            rightPinchArmed = false;
        }

        SetCurrentGhostGrabEnabled(modeActive && !suppressed);
        SetRayVisible(leftRay, modeActive && !suppressed && IsPointerTracked(leftHand));
        SetRayVisible(rightRay, modeActive && !suppressed && IsPointerTracked(rightHand));
    }

    public bool IsGhostHold(GameObject candidate)
    {
        if (currentGhost == null || candidate == null)
        {
            return false;
        }

        return candidate == currentGhost || candidate.transform.IsChildOf(currentGhost.transform);
    }

    public void SpawnGhost(GameObject sourceHold)
    {
        if (!modeActive || sourceHold == null || sceneConfiguror == null ||
            !sceneConfiguror.IsActiveRouteHold(sourceHold))
        {
            return;
        }

        DismissGhost();

        currentGhost = Instantiate(sourceHold);
        currentGhost.name = sourceHold.name.Split('.')[0] + "#ghost";
        currentGhost.transform.SetParent(null, true);
        currentGhost.SetActive(true);
        lockedGhostScale = currentGhost.transform.localScale;
        FitSphereColliderToMesh(currentGhost);

        foreach (Collider collider in currentGhost.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = true;
        }

        Rigidbody rigidbody = currentGhost.GetComponent<Rigidbody>();
        if (rigidbody == null)
        {
            rigidbody = currentGhost.AddComponent<Rigidbody>();
        }
        rigidbody.useGravity = false;
        rigidbody.isKinematic = true;

        XRGrabInteractable grabInteractable = currentGhost.GetComponent<XRGrabInteractable>();
        if (grabInteractable != null)
        {
            grabInteractable.enabled = !panelInputSuppressed;
            grabInteractable.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grabInteractable.trackPosition = true;
            grabInteractable.trackRotation = true;
            grabInteractable.trackScale = false;
        }

        userCamera = userCamera != null ? userCamera : Camera.main;
        Vector3 targetCenter = userCamera != null
            ? userCamera.transform.position + userCamera.transform.forward * spawnDistance +
              Vector3.up * spawnVerticalOffset
            : sourceHold.transform.position + Vector3.forward * spawnDistance;
        MoveRendererCenterTo(currentGhost, targetCenter);

        wallReferent = sourceHold;
        CreateDismissAffordance();
        CreateWallMarker();
        sceneConfiguror.RegisterGhostHold(currentGhost);
        sceneConfiguror.PrepareGripHold(currentGhost);
        sceneConfiguror.actionRecorder?.Record("GhostSpawn", "", currentGhost, sourceHold.name);
    }

    private void SetCurrentGhostGrabEnabled(bool enabled)
    {
        if (currentGhost != null && currentGhost.TryGetComponent(out XRGrabInteractable grabInteractable))
        {
            grabInteractable.enabled = enabled;
        }
    }

    private static void FitSphereColliderToMesh(GameObject hold)
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
    }

    public void DismissGhost()
    {
        manipulatingHand = null;

        if (sceneConfiguror != null && currentGhost != null)
        {
            sceneConfiguror.UnregisterGhostHold(currentGhost);
            sceneConfiguror.actionRecorder?.Record("GhostDismiss", "", currentGhost);
        }

        if (currentGhost != null)
        {
            Destroy(currentGhost);
        }
        if (dismissAffordance != null)
        {
            Destroy(dismissAffordance);
        }
        if (wallMarker != null)
        {
            Destroy(wallMarker.gameObject);
        }

        currentGhost = null;
        dismissAffordance = null;
        wallMarker = null;
        wallReferent = null;
    }

    private void Update()
    {
        if (sceneConfiguror == null)
        {
            SceneConfiguror found = FindAnyObjectByType<SceneConfiguror>();
            if (found != null)
            {
                Initialize(found);
            }
        }

        if (!modeActive)
        {
            return;
        }

        if (panelInputSuppressed)
        {
            manipulatingHand = null;
            leftWasPinching = IsIndexPinching(leftHand);
            rightWasPinching = IsIndexPinching(rightHand);
            leftPinchArmed = false;
            rightPinchArmed = false;
            SetRayVisible(leftRay, false);
            SetRayVisible(rightRay, false);
            UpdateWallMarker();
            UpdateDismissAffordance();
            return;
        }

        HandleHand(
            Hand.Left,
            leftHand,
            leftSkeleton,
            ref leftWasPinching,
            ref leftPinchArmed,
            leftRay);
        HandleHand(
            Hand.Right,
            rightHand,
            rightSkeleton,
            ref rightWasPinching,
            ref rightPinchArmed,
            rightRay);
        UpdateManipulation();
        UpdateWallMarker();
        UpdateDismissAffordance();

        if (currentGhost != null)
        {
            currentGhost.transform.localScale = lockedGhostScale;
        }
    }

    private void HandleHand(
        Hand handSide,
        OVRHand hand,
        OVRSkeleton skeleton,
        ref bool wasPinching,
        ref bool pinchArmed,
        LineRenderer rayVisual)
    {
        bool trackingConfident = hand != null && hand.IsTracked && hand.IsDataHighConfidence;
        bool isPinching = IsIndexPinching(hand);
        bool pinchEnded = !isPinching && wasPinching;
        bool pinchStarted = StudyRehearsalTiming.TryConsumeArmedPinch(
            trackingConfident,
            isPinching,
            ref wasPinching,
            ref pinchArmed);

        bool hasRay = TryGetPointerRay(hand, out Ray ray);
        GameObject target = null;
        Vector3 targetPoint = ray.GetPoint(maxRayDistance);
        bool hasTarget = hasRay && TryGetRayTarget(ray, out target, out targetPoint);
        UpdateRayVisual(rayVisual, hasRay, ray, hasTarget ? targetPoint : ray.GetPoint(maxRayDistance));
        RecordInputDiagnostics(
            handSide,
            hand,
            trackingConfident,
            isPinching,
            pinchArmed,
            hasRay);

        if (pinchEnded && manipulatingHand == handSide)
        {
            manipulatingHand = null;
        }

        if (!pinchStarted)
        {
            return;
        }

        RecordSelectionAttempt(handSide, hasRay, hasTarget, target, ray);

        if (hasTarget && target == dismissAffordance)
        {
            DismissGhost();
            return;
        }

        if ((hasTarget && IsGhostHold(target)) || IsNearGhost(skeleton))
        {
            BeginManipulation(handSide, skeleton);
            return;
        }

        if (hasTarget)
        {
            GameObject selectedHold = sceneConfiguror.GetActiveRouteHold(target);
            if (selectedHold != null)
            {
                SpawnGhost(selectedHold);
            }
        }
    }

    private bool TryGetRayTarget(Ray ray, out GameObject target, out Vector3 targetPoint)
    {
        target = null;
        targetPoint = ray.GetPoint(maxRayDistance);
        float nearestDistance = maxRayDistance;

        if (dismissAffordance != null &&
            dismissAffordance.TryGetComponent(out Collider dismissCollider) &&
            dismissCollider.Raycast(ray, out RaycastHit dismissHit, maxRayDistance))
        {
            target = dismissAffordance;
            nearestDistance = dismissHit.distance;
            targetPoint = dismissHit.point;
        }

        ConsiderRendererBounds(currentGhost, ray, ref target, ref targetPoint, ref nearestDistance);
        if (sceneConfiguror?.activeHoldsList != null)
        {
            foreach (GameObject hold in sceneConfiguror.activeHoldsList)
            {
                ConsiderRendererBounds(hold, ray, ref target, ref targetPoint, ref nearestDistance);
            }
        }
        return target != null;
    }

    private static void ConsiderRendererBounds(
        GameObject candidate,
        Ray ray,
        ref GameObject target,
        ref Vector3 targetPoint,
        ref float nearestDistance)
    {
        if (candidate == null || !TryGetCombinedBounds(candidate, out Bounds bounds) ||
            !bounds.IntersectRay(ray, out float distance) || distance < 0f || distance >= nearestDistance)
        {
            return;
        }

        target = candidate;
        nearestDistance = distance;
        targetPoint = ray.GetPoint(distance);
    }

    private void BeginManipulation(Hand handSide, OVRSkeleton skeleton)
    {
        if (currentGhost == null || !TryGetHandPose(skeleton, out Pose handPose))
        {
            return;
        }

        manipulatingHand = handSide;
        grabPositionOffset = Quaternion.Inverse(handPose.rotation) *
                             (currentGhost.transform.position - handPose.position);
        grabRotationOffset = Quaternion.Inverse(handPose.rotation) * currentGhost.transform.rotation;
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void RecordInputDiagnostics(
        Hand handSide,
        OVRHand hand,
        bool trackingConfident,
        bool isPinching,
        bool pinchArmed,
        bool hasRay)
    {
        ActionRecorder recorder = sceneConfiguror?.actionRecorder;
        if (recorder == null || !recorder.IsRecording)
        {
            SetInputDiagnosticState(handSide, -1);
            return;
        }

        bool tracked = hand != null && hand.IsTracked;
        bool highConfidence = hand != null && hand.IsDataHighConfidence;
        bool dataValid = hand != null && hand.IsDataValid;
        bool pointerPoseValid = hand != null && hand.IsPointerPoseValid && hand.PointerPose != null;
        OVRInput.ControllerInHandState controllerState = hand != null
            ? OVRInput.GetControllerIsInHandState((OVRInput.Hand)hand.GetHand())
            : OVRInput.ControllerInHandState.NoHand;
        int state = (tracked ? 1 : 0) |
                    (highConfidence ? 1 << 1 : 0) |
                    (dataValid ? 1 << 2 : 0) |
                    (pointerPoseValid ? 1 << 3 : 0) |
                    (hasRay ? 1 << 4 : 0) |
                    (trackingConfident ? 1 << 5 : 0) |
                    (isPinching ? 1 << 6 : 0) |
                    (pinchArmed ? 1 << 7 : 0) |
                    ((int)controllerState << 8) |
                    (hand != null ? (int)hand.m_showState << 10 : 0);
        int previousState = handSide == Hand.Left
            ? leftInputDiagnosticState
            : rightInputDiagnosticState;
        if (state == previousState)
        {
            return;
        }
        SetInputDiagnosticState(handSide, state);

        float pinchStrength = hand != null
            ? hand.GetFingerPinchStrength(OVRHand.HandFinger.Index)
            : 0f;
        recorder.Record(
            "GhostInputState",
            handSide == Hand.Left ? "Left" : "Right",
            null,
            "tracked=" + tracked.ToString().ToLowerInvariant() +
            ";highConfidence=" + highConfidence.ToString().ToLowerInvariant() +
            ";dataValid=" + dataValid.ToString().ToLowerInvariant() +
            ";pointerPoseValid=" + pointerPoseValid.ToString().ToLowerInvariant() +
            ";hasRay=" + hasRay.ToString().ToLowerInvariant() +
            ";pinching=" + isPinching.ToString().ToLowerInvariant() +
            ";pinchArmed=" + pinchArmed.ToString().ToLowerInvariant() +
            ";pinchStrength=" + pinchStrength.ToString(
                "F3",
                System.Globalization.CultureInfo.InvariantCulture) +
            ";controllerState=" + controllerState +
            ";showState=" + (hand != null ? hand.m_showState.ToString() : "unavailable"));
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void RecordInputSuppression(bool suppressed)
    {
        if (modeActive && sceneConfiguror?.actionRecorder != null)
        {
            sceneConfiguror.actionRecorder.Record(
                "GhostInputSuppression",
                "",
                null,
                "suppressed=" + suppressed.ToString().ToLowerInvariant());
        }
    }

    private void SetInputDiagnosticState(Hand handSide, int state)
    {
        if (handSide == Hand.Left)
        {
            leftInputDiagnosticState = state;
        }
        else
        {
            rightInputDiagnosticState = state;
        }
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void ResetInputDiagnostics()
    {
        leftInputDiagnosticState = -1;
        rightInputDiagnosticState = -1;
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void RecordSelectionAttempt(
        Hand handSide,
        bool hasRay,
        bool hasTarget,
        GameObject target,
        Ray ray)
    {
        string details = "hasRay=" + hasRay.ToString().ToLowerInvariant() +
                         ";hasTarget=" + hasTarget.ToString().ToLowerInvariant() +
                         ";activeRouteHold=" +
                         (target != null && sceneConfiguror.IsActiveRouteHold(target))
                         .ToString().ToLowerInvariant() +
                         ";dismissTarget=" + (target == dismissAffordance).ToString().ToLowerInvariant() +
                         ";ghostTarget=" + IsGhostHold(target).ToString().ToLowerInvariant();
        if (hasRay)
        {
            details = System.FormattableString.Invariant(
                $"{details};rayOrigin=({ray.origin.x:F4},{ray.origin.y:F4},{ray.origin.z:F4});rayDirection=({ray.direction.x:F4},{ray.direction.y:F4},{ray.direction.z:F4})");
        }
        sceneConfiguror?.actionRecorder?.Record(
            "GhostSelectionAttempt",
            handSide == Hand.Left ? "Left" : "Right",
            target,
            details);
    }

    private void UpdateManipulation()
    {
        if (currentGhost == null || manipulatingHand == null)
        {
            return;
        }

        OVRSkeleton skeleton = manipulatingHand == Hand.Left ? leftSkeleton : rightSkeleton;
        OVRHand hand = manipulatingHand == Hand.Left ? leftHand : rightHand;
        if (!IsIndexPinching(hand) || !TryGetHandPose(skeleton, out Pose handPose))
        {
            manipulatingHand = null;
            return;
        }

        currentGhost.transform.SetPositionAndRotation(
            handPose.position + handPose.rotation * grabPositionOffset,
            handPose.rotation * grabRotationOffset);
    }

    private bool IsNearGhost(OVRSkeleton skeleton)
    {
        if (currentGhost == null || skeleton == null || skeleton.Bones.Count <= 10)
        {
            return false;
        }

        if (!TryGetCombinedBounds(currentGhost, out Bounds bounds))
        {
            return false;
        }

        bounds.Expand(nearGrabPadding * 2f);
        return bounds.Contains(skeleton.Bones[10].Transform.position);
    }

    private static bool TryGetHandPose(OVRSkeleton skeleton, out Pose pose)
    {
        if (skeleton != null && skeleton.Bones.Count > 0 && skeleton.Bones[0].Transform != null)
        {
            Transform palm = skeleton.Bones[0].Transform;
            pose = new Pose(palm.position, palm.rotation);
            return true;
        }

        pose = default;
        return false;
    }

    private static bool IsIndexPinching(OVRHand hand)
    {
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence &&
               hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
    }

    private static bool IsPointerTracked(OVRHand hand)
    {
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence &&
               hand.IsPointerPoseValid && hand.PointerPose != null;
    }

    private static bool TryGetPointerRay(OVRHand hand, out Ray ray)
    {
        if (IsPointerTracked(hand))
        {
            ray = new Ray(hand.PointerPose.position, hand.PointerPose.forward);
            return true;
        }

        ray = default;
        return false;
    }

    private static void MoveRendererCenterTo(GameObject target, Vector3 desiredCenter)
    {
        if (!TryGetCombinedBounds(target, out Bounds bounds))
        {
            target.transform.position = desiredCenter;
            return;
        }

        target.transform.position += desiredCenter - bounds.center;
    }

    private static bool TryGetCombinedBounds(GameObject target, out Bounds bounds)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        return true;
    }

    private void CreateDismissAffordance()
    {
        dismissAffordance = new GameObject("Dismiss Ghost");
        TextMesh label = dismissAffordance.AddComponent<TextMesh>();
        label.text = "X";
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 64;
        label.characterSize = 0.0025f;
        label.color = new Color(1f, 0.35f, 0.25f, 1f);
        SphereCollider collider = dismissAffordance.AddComponent<SphereCollider>();
        collider.radius = 0.045f;
        UpdateDismissAffordance();
    }

    private void UpdateDismissAffordance()
    {
        if (dismissAffordance == null || currentGhost == null || userCamera == null ||
            !TryGetCombinedBounds(currentGhost, out Bounds bounds))
        {
            return;
        }

        float sideOffset = Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.07f;
        float topOffset = bounds.extents.y + 0.07f;
        dismissAffordance.transform.position = bounds.center +
                                               userCamera.transform.right * sideOffset +
                                               userCamera.transform.up * topOffset;
        dismissAffordance.transform.rotation = Quaternion.LookRotation(
            dismissAffordance.transform.position - userCamera.transform.position,
            userCamera.transform.up);
    }

    private void CreateWallMarker()
    {
        GameObject markerObject = new("Ghost Wall Referent");
        wallMarker = markerObject.AddComponent<LineRenderer>();
        wallMarker.loop = true;
        wallMarker.useWorldSpace = true;
        wallMarker.positionCount = 48;
        wallMarker.startWidth = markerWidth;
        wallMarker.endWidth = markerWidth;
        wallMarker.startColor = Color.white;
        wallMarker.endColor = Color.white;
        wallMarker.numCornerVertices = 3;
        wallMarkerMaterial ??= CreateLineMaterial("Ghost Referent Ring Material");
        wallMarkerProperties ??= new MaterialPropertyBlock();
        wallMarker.sharedMaterial = wallMarkerMaterial;
        SetLineColor(wallMarker, wallMarkerProperties, markerColor);
        UpdateWallMarker();
    }

    private void UpdateWallMarker()
    {
        if (wallMarker == null || wallReferent == null || userCamera == null ||
            !TryGetCombinedBounds(wallReferent, out Bounds bounds))
        {
            return;
        }

        float radius = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) * 1.35f;
        float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
        Color color = markerColor;
        color.a *= pulse;
        SetLineColor(wallMarker, wallMarkerProperties, color);

        Vector3 center = bounds.center - userCamera.transform.forward * 0.006f;
        for (int i = 0; i < wallMarker.positionCount; i++)
        {
            float angle = i * Mathf.PI * 2f / wallMarker.positionCount;
            wallMarker.SetPosition(i, center +
                (userCamera.transform.right * Mathf.Cos(angle) +
                 userCamera.transform.up * Mathf.Sin(angle)) * radius);
        }
    }

    private void EnsureRayVisuals()
    {
        if (leftRay == null)
        {
            leftRayMaterial = CreateLineMaterial("Left Ghost Selection Ray Material");
            leftRayProperties = new MaterialPropertyBlock();
            leftRay = CreateRay("Left Ghost Selection Ray", leftRayMaterial, leftRayProperties);
        }
        if (rightRay == null)
        {
            rightRayMaterial = CreateLineMaterial("Right Ghost Selection Ray Material");
            rightRayProperties = new MaterialPropertyBlock();
            rightRay = CreateRay("Right Ghost Selection Ray", rightRayMaterial, rightRayProperties);
        }
    }

    private LineRenderer CreateRay(
        string objectName,
        Material material,
        MaterialPropertyBlock properties)
    {
        GameObject rayObject = new(objectName);
        rayObject.transform.SetParent(transform, false);
        LineRenderer ray = rayObject.AddComponent<LineRenderer>();
        ray.positionCount = 2;
        ray.useWorldSpace = true;
        ray.startWidth = 0.0025f;
        ray.endWidth = 0.001f;
        ray.startColor = Color.white;
        ray.endColor = Color.white;
        ray.sharedMaterial = material;
        SetLineColor(ray, properties, rayColor);
        ray.enabled = false;
        return ray;
    }

    private static Material CreateLineMaterial(string materialName)
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default") ??
                                    UnityEngine.Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null)
        {
            return new Material(shader) { name = materialName };
        }
        return null;
    }

    private static void SetLineColor(
        LineRenderer line,
        MaterialPropertyBlock properties,
        Color color)
    {
        if (line == null || properties == null)
        {
            return;
        }

        Material material = line.sharedMaterial;
        if (material == null)
        {
            line.startColor = color;
            line.endColor = color;
            return;
        }

        line.GetPropertyBlock(properties);
        if (material.HasProperty("_Color"))
        {
            properties.SetColor("_Color", color);
        }
        if (material.HasProperty("_BaseColor"))
        {
            properties.SetColor("_BaseColor", color);
        }
        line.SetPropertyBlock(properties);
    }

    private static void UpdateRayVisual(
        LineRenderer rayVisual,
        bool hasRay,
        Ray ray,
        Vector3 endPoint)
    {
        if (rayVisual == null)
        {
            return;
        }

        rayVisual.enabled = hasRay;
        if (hasRay)
        {
            rayVisual.SetPosition(0, ray.origin);
            rayVisual.SetPosition(1, endPoint);
        }
    }

    private static void SetRayVisible(LineRenderer ray, bool visible)
    {
        if (ray != null)
        {
            ray.enabled = visible;
        }
    }

    private void OnDestroy()
    {
        if (wallMarkerMaterial != null)
        {
            Destroy(wallMarkerMaterial);
        }
        if (leftRayMaterial != null)
        {
            Destroy(leftRayMaterial);
        }
        if (rightRayMaterial != null)
        {
            Destroy(rightRayMaterial);
        }
    }
}
