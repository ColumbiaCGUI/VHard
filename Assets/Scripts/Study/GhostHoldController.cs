using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Detached ghost-hold inspection. The participant aims a stabilised hand ray at a route hold,
/// pinches, and a proxy copy of that hold is pulled to within arm's reach where it can be turned
/// over and gripped. Several proxies may be live at once; each keeps a tether back to the wall hold
/// it came from and a readout of how far it has been turned from the orientation that hold actually
/// holds on the wall.
/// </summary>
public sealed class GhostHoldController : MonoBehaviour
{
    [Header("Selection")]
    [SerializeField] private float maxRayDistance = 12f;
    [SerializeField] private float spawnDistance = 0.45f;
    [SerializeField] private float spawnVerticalOffset = -0.15f;
    [SerializeField] private float spawnSlotSeparationDegrees = 16f;
    [SerializeField] private float nearGrabPadding = 0.08f;

    [Header("Pointer")]
    [SerializeField] private float acquireHalfAngleDegrees =
        HandRayTargeting.DefaultAcquireHalfAngleDegrees;
    [SerializeField] private float releaseHalfAngleDegrees =
        HandRayTargeting.DefaultReleaseHalfAngleDegrees;
    [SerializeField] private float switchMarginDegrees =
        HandRayTargeting.DefaultSwitchMarginDegrees;
    [SerializeField] private float affordanceAcquireBonusDegrees = 1.5f;
    [SerializeField] private float pointerMinimumCutoffHertz =
        PointerOneEuroFilter.DefaultMinimumCutoffHertz;
    [SerializeField] private float pointerSpeedCoefficient =
        PointerOneEuroFilter.DefaultSpeedCoefficient;
    [SerializeField] private float pointerDerivativeCutoffHertz =
        PointerOneEuroFilter.DefaultDerivativeCutoffHertz;
    [SerializeField] private float pinchPressStrength = PinchLatch.DefaultPressStrength;
    [SerializeField] private float pinchReleaseStrength = PinchLatch.DefaultReleaseStrength;

    [Header("Ghosts")]
    [SerializeField] private int maximumLiveGhosts = GhostRegistryPolicy.DefaultMaximumLiveGhosts;

    [Header("Visuals")]
    [SerializeField] private Color rayColor = new(0.25f, 0.8f, 1f, 0.8f);
    [SerializeField] private Color markerColor = new(1f, 0.8f, 0.2f, 0.9f);
    [SerializeField] private Color tetherColor = new(1f, 0.8f, 0.2f, 0.35f);
    [SerializeField] private Color focusColor = new(1f, 1f, 1f, 0.75f);
    [SerializeField] private float markerWidth = 0.006f;
    [SerializeField] private float tetherWidth = 0.0022f;

    private const string DecorationRootName = "Ghost Inspection Decorations";
    private const int WallMarkerSegments = 48;
    private const int FocusRingSegments = 40;
    private const int MaximumArcSegments = 36;
    private const float ArcDegreesPerSegment = 6f;
    private const float ManipulationPoseRecordIntervalSeconds = 0.15f;
    private const float IndexTipBoneIndex = 10;
    private const float PalmUpDotThreshold = 0.55f;
    private const float TextTransformScale = 0.01f;

    private sealed class GhostInstance
    {
        public GameObject Root;
        public GameObject Source;
        public string SourceCoordinate;
        public Quaternion WallRotation;
        public Vector3 LockedScale;
        public float SpawnTime;
        public int Slot;
        public GameObject DismissAffordance;
        public TextMeshPro DismissLabel;
        public LineRenderer WallMarker;
        public LineRenderer Tether;
        public LineRenderer OrientationArc;
        public LineRenderer OrientationTick;
        public TextMeshPro OrientationLabel;
        public Transform IndicatorRoot;
    }

    private sealed class HandPointer
    {
        public Hand Side;
        public OVRHand Hand;
        public OVRSkeleton Skeleton;
        public PinchLatch Latch;
        public PointerOneEuroFilter OriginFilter;
        public PointerOneEuroFilter DirectionFilter;
        public LineRenderer Ray;
        public LineRenderer Focus;
        public MaterialPropertyBlock RayProperties;
        public MaterialPropertyBlock FocusProperties;
        public GameObject HoverObject;
        public GhostInstance Manipulated;
        public Vector3 GrabPositionOffset;
        public Quaternion GrabRotationOffset;
        public float NextPoseRecordTime;
        public int DiagnosticState = -1;
    }

    private enum CandidateKind
    {
        WallHold,
        Ghost,
        DismissGhost,
        DismissAll,
    }

    private struct Candidate
    {
        public CandidateKind Kind;
        public GameObject Object;
        public GhostInstance Ghost;
        public Vector3 Center;
        public float Radius;
        public float DistanceSqr;
    }

    private SceneConfiguror sceneConfiguror;
    private Camera userCamera;
    private HandPointer left;
    private HandPointer right;
    private Transform decorationRoot;
    private TMP_FontAsset fontAsset;
    private Material lineMaterial;
    private bool modeActive;
    private bool panelInputSuppressed;

    private readonly List<GhostInstance> ghosts = new();
    private readonly List<Candidate> candidates = new();
    private readonly List<float> candidateAngles = new();
    private readonly List<float> evictionSpawnTimes = new();
    private readonly List<bool> evictionManipulated = new();
    private GameObject dismissAllAffordance;
    private TextMeshPro dismissAllLabel;

    /// <summary>Most recently summoned proxy, or null. Kept for callers that predate multi-ghost
    /// inspection and only need to know whether anything is detached.</summary>
    public GameObject CurrentGhost => ghosts.Count > 0 ? ghosts[ghosts.Count - 1].Root : null;

    /// <summary>Wall hold behind <see cref="CurrentGhost"/>.</summary>
    public GameObject WallReferent => ghosts.Count > 0 ? ghosts[ghosts.Count - 1].Source : null;

    public bool HasGhosts => ghosts.Count > 0;

    public int LiveGhostCount => ghosts.Count;

    public void Initialize(SceneConfiguror configuror)
    {
        sceneConfiguror = configuror;
        userCamera = configuror.centerEyeAnchor != null
            ? configuror.centerEyeAnchor.GetComponent<Camera>()
            : null;
        userCamera = userCamera != null ? userCamera : Camera.main;
        left = BindHandPointer(left, Hand.Left, configuror.leftHandOVRSkeleton);
        right = BindHandPointer(right, Hand.Right, configuror.rightHandOVRSkeleton);
        EnsureDecorationRoot();
        EnsurePointerVisuals(left);
        EnsurePointerVisuals(right);
    }

    // Script order does not guarantee that the facade's Start runs before this component's first
    // Update, so initialization can arrive twice; rebinding rather than rebuilding keeps that from
    // leaving a second set of orphaned ray renderers behind.
    private HandPointer BindHandPointer(HandPointer existing, Hand side, OVRSkeleton skeleton)
    {
        HandPointer pointer = existing ?? new HandPointer
        {
            Side = side,
            Latch = new PinchLatch(pinchPressStrength, pinchReleaseStrength),
            OriginFilter = new PointerOneEuroFilter(
                pointerMinimumCutoffHertz,
                pointerSpeedCoefficient,
                pointerDerivativeCutoffHertz),
            DirectionFilter = new PointerOneEuroFilter(
                pointerMinimumCutoffHertz,
                pointerSpeedCoefficient,
                pointerDerivativeCutoffHertz),
        };
        pointer.Skeleton = skeleton;
        pointer.Hand = skeleton != null ? skeleton.GetComponent<OVRHand>() : null;
        return pointer;
    }

    public void SetModeActive(bool active)
    {
        modeActive = active;
        ResetPointerState(left);
        ResetPointerState(right);

        if (!active)
        {
            DismissAllGhosts("modeExit");
        }

        ApplyGhostGrabEnabled(active && !panelInputSuppressed);
        SetPointerVisible(left, false);
        SetPointerVisible(right, false);
    }

    public void SetPanelInputSuppressed(bool suppressed)
    {
        bool changed = panelInputSuppressed != suppressed;
        panelInputSuppressed = suppressed;
        if (changed)
        {
            RecordInputSuppression(suppressed);
        }
        if (suppressed)
        {
            ReleaseManipulation(left);
            ReleaseManipulation(right);
            SetPointerVisible(left, false);
            SetPointerVisible(right, false);
        }

        ApplyGhostGrabEnabled(modeActive && !suppressed);
    }

    public bool IsGhostHold(GameObject candidate)
    {
        return FindGhost(candidate) != null;
    }

    /// <summary>Root of the proxy that owns <paramref name="candidate"/>, or null.</summary>
    public GameObject GetGhostRoot(GameObject candidate)
    {
        GhostInstance ghost = FindGhost(candidate);
        return ghost?.Root;
    }

    /// <summary>Wall hold behind the proxy that owns <paramref name="candidate"/>, or null.</summary>
    public GameObject GetWallReferent(GameObject candidate)
    {
        GhostInstance ghost = FindGhost(candidate);
        return ghost?.Source;
    }

    /// <summary>Live proxy roots, oldest first.</summary>
    public void CollectGhostRoots(List<GameObject> destination)
    {
        destination.Clear();
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root != null)
            {
                destination.Add(ghost.Root);
            }
        }
    }

    private GhostInstance FindGhost(GameObject candidate)
    {
        if (candidate == null)
        {
            return null;
        }

        Transform candidateTransform = candidate.transform;
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root != null &&
                (candidateTransform == ghost.Root.transform ||
                 candidateTransform.IsChildOf(ghost.Root.transform)))
            {
                return ghost;
            }
        }
        return null;
    }

    private GhostInstance FindGhostForSource(GameObject sourceHold)
    {
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Source == sourceHold)
            {
                return ghost;
            }
        }
        return null;
    }

    public void SpawnGhost(GameObject sourceHold)
    {
        SpawnGhost(sourceHold, null);
    }

    public void SpawnGhost(GameObject sourceHold, Hand? summonedBy)
    {
        if (!modeActive || sourceHold == null || sceneConfiguror == null ||
            !sceneConfiguror.IsActiveRouteHold(sourceHold))
        {
            return;
        }

        // Aiming at a hold that is already detached recalls that proxy rather than duplicating it;
        // otherwise a participant who loses one behind their shoulder can never get it back.
        GhostInstance existing = FindGhostForSource(sourceHold);
        if (existing != null)
        {
            RecallGhost(existing);
            RecordGhostEvent("GhostRecall", existing, summonedBy, string.Empty);
            return;
        }

        int evictionIndex = SelectEvictionIndex();
        if (evictionIndex >= 0)
        {
            DismissGhost(ghosts[evictionIndex], "evicted");
        }

        GhostInstance ghost = new()
        {
            Source = sourceHold,
            SourceCoordinate = GetHoldCoordinate(sourceHold),
            WallRotation = sourceHold.transform.rotation,
            SpawnTime = Time.unscaledTime,
            Slot = AllocateSlot(),
        };

        ghost.Root = Instantiate(sourceHold);
        ghost.Root.name = ghost.SourceCoordinate + "#ghost";
        ghost.Root.transform.SetParent(null, true);
        ghost.Root.SetActive(true);
        ghost.LockedScale = ghost.Root.transform.localScale;
        FitSphereColliderToMesh(ghost.Root);

        foreach (Collider collider in ghost.Root.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = true;
        }

        Rigidbody body = ghost.Root.GetComponent<Rigidbody>();
        if (body == null)
        {
            body = ghost.Root.AddComponent<Rigidbody>();
        }
        body.useGravity = false;
        body.isKinematic = true;

        if (ghost.Root.TryGetComponent(out XRGrabInteractable grabInteractable))
        {
            grabInteractable.enabled = !panelInputSuppressed;
            grabInteractable.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grabInteractable.trackPosition = true;
            grabInteractable.trackRotation = true;
            grabInteractable.trackScale = false;
        }

        MoveRendererCenterTo(ghost.Root, GetSpawnCenter(ghost.Slot));

        ghosts.Add(ghost);
        EnsureDecorationRoot();
        CreateGhostDecorations(ghost);
        sceneConfiguror.RegisterGhostHold(ghost.Root);
        sceneConfiguror.PrepareGripHold(ghost.Root);
        RecordGhostEvent("GhostSpawn", ghost, summonedBy, sourceHold.name);
    }

    private int SelectEvictionIndex()
    {
        evictionSpawnTimes.Clear();
        evictionManipulated.Clear();
        foreach (GhostInstance ghost in ghosts)
        {
            evictionSpawnTimes.Add(ghost.SpawnTime);
            evictionManipulated.Add(
                (left != null && left.Manipulated == ghost) ||
                (right != null && right.Manipulated == ghost));
        }
        return GhostRegistryPolicy.SelectEvictionIndex(
            evictionSpawnTimes,
            evictionManipulated,
            GhostRegistryPolicy.ClampMaximumLiveGhosts(maximumLiveGhosts));
    }

    private int AllocateSlot()
    {
        for (int slot = 0; ; slot++)
        {
            bool taken = false;
            foreach (GhostInstance ghost in ghosts)
            {
                if (ghost.Slot == slot)
                {
                    taken = true;
                    break;
                }
            }
            if (!taken)
            {
                return slot;
            }
        }
    }

    private Vector3 GetSpawnCenter(int slot)
    {
        userCamera = userCamera != null ? userCamera : Camera.main;
        if (userCamera == null)
        {
            return transform.position + Vector3.forward * spawnDistance;
        }

        Transform camera = userCamera.transform;
        int magnitude = (slot + 1) / 2;
        float yaw = (slot % 2 == 1 ? 1f : -1f) * magnitude * spawnSlotSeparationDegrees;
        Vector3 direction = Quaternion.AngleAxis(yaw, camera.up) * camera.forward;
        return camera.position + direction * spawnDistance + camera.up * spawnVerticalOffset;
    }

    private void RecallGhost(GhostInstance ghost)
    {
        ReleaseManipulationOf(ghost);
        MoveRendererCenterTo(ghost.Root, GetSpawnCenter(ghost.Slot));
    }

    /// <summary>Dismisses every live proxy. Retained for callers that predate multi-ghost
    /// inspection and mean "clear the detached state".</summary>
    public void DismissGhost()
    {
        DismissAllGhosts("dismissAll");
    }

    public void DismissAllGhosts(string reason)
    {
        for (int index = ghosts.Count - 1; index >= 0; index--)
        {
            DismissGhost(ghosts[index], reason);
        }
    }

    private void DismissGhost(GhostInstance ghost, string reason)
    {
        ReleaseManipulationOf(ghost);
        if (left != null && left.HoverObject != null && FindGhost(left.HoverObject) == ghost)
        {
            left.HoverObject = null;
        }
        if (right != null && right.HoverObject != null && FindGhost(right.HoverObject) == ghost)
        {
            right.HoverObject = null;
        }

        ghosts.Remove(ghost);
        if (sceneConfiguror != null && ghost.Root != null)
        {
            sceneConfiguror.UnregisterGhostHold(ghost.Root);
            RecordGhostEvent("GhostDismiss", ghost, null, "reason=" + reason);
        }

        DestroyIfPresent(ghost.Root);
        DestroyIfPresent(ghost.DismissAffordance);
        DestroyIfPresent(ghost.IndicatorRoot != null ? ghost.IndicatorRoot.gameObject : null);
        DestroyIfPresent(ghost.WallMarker != null ? ghost.WallMarker.gameObject : null);
        DestroyIfPresent(ghost.Tether != null ? ghost.Tether.gameObject : null);
        ghost.Root = null;
        ghost.Source = null;
    }

    private static void DestroyIfPresent(GameObject target)
    {
        if (target != null)
        {
            Destroy(target);
        }
    }

    private void ApplyGhostGrabEnabled(bool enabled)
    {
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root != null && ghost.Root.TryGetComponent(out XRGrabInteractable grab))
            {
                grab.enabled = enabled;
            }
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

        float now = Time.unscaledTime;

        // The latches still advance while the console owns input, and their presses are discarded.
        // A pinch that was closed during suppression therefore spends its arming and cannot leak
        // into the technique when the console closes, while a hand that stayed open keeps its
        // arming and does not have to be opened and re-closed to select.
        if (panelInputSuppressed)
        {
            AdvanceSuppressedPointer(left, now);
            AdvanceSuppressedPointer(right, now);
            UpdateGhostDecorations();
            return;
        }

        UpdatePointer(left, now);
        UpdatePointer(right, now);
        UpdateManipulation(left);
        UpdateManipulation(right);
        UpdateGhostDecorations();

        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root != null)
            {
                ghost.Root.transform.localScale = ghost.LockedScale;
            }
        }
    }

    private void AdvanceSuppressedPointer(HandPointer pointer, float now)
    {
        if (pointer == null)
        {
            return;
        }

        bool trackingConfident = IsTrackingConfident(pointer.Hand);
        pointer.Latch.Update(trackingConfident, GetPinchStrength(pointer.Hand), IsReportedPinching(pointer.Hand));
        pointer.OriginFilter.Reset();
        pointer.DirectionFilter.Reset();
        pointer.HoverObject = null;
        SetPointerVisible(pointer, false);
    }

    private void UpdatePointer(HandPointer pointer, float now)
    {
        if (pointer == null)
        {
            return;
        }

        bool trackingConfident = IsTrackingConfident(pointer.Hand);
        bool pressed = pointer.Latch.Update(
            trackingConfident,
            GetPinchStrength(pointer.Hand),
            IsReportedPinching(pointer.Hand));
        bool hasRay = TryGetStabilisedRay(pointer, now, out Vector3 origin, out Vector3 direction);

        if (!hasRay)
        {
            pointer.HoverObject = null;
            SetPointerVisible(pointer, false);
            RecordInputDiagnostics(pointer, trackingConfident, false);
            return;
        }

        bool hasHover = TryResolveRayTarget(
            origin,
            direction,
            pointer.HoverObject,
            out GameObject hoveredObject,
            out Vector3 endPoint,
            out Candidate hovered);
        pointer.HoverObject = hoveredObject;
        UpdatePointerVisuals(pointer, origin, endPoint, hasHover, hovered);
        RecordInputDiagnostics(pointer, trackingConfident, true);

        if (!pressed)
        {
            return;
        }

        // The palm-up summon gesture owns pinches made with the palm turned up; letting the same
        // pinch also act on a hold would fire two unrelated actions from one gesture.
        if (IsPalmUp(pointer.Skeleton))
        {
            RecordSelection(pointer, hasHover ? hovered.Object : null, "inhibited_palmUpSummon", origin, direction);
            return;
        }

        if (!hasHover)
        {
            RecordSelection(pointer, null, "missed", origin, direction);
            return;
        }

        switch (hovered.Kind)
        {
            case CandidateKind.DismissAll:
                RecordSelection(pointer, null, "dismissAll", origin, direction);
                DismissAllGhosts("dismissAll");
                break;
            case CandidateKind.DismissGhost:
                RecordSelection(pointer, hovered.Ghost.Root, "dismiss", origin, direction);
                DismissGhost(hovered.Ghost, "participant");
                break;
            case CandidateKind.Ghost:
                RecordSelection(pointer, hovered.Ghost.Root, "grabGhost", origin, direction);
                BeginManipulation(pointer, hovered.Ghost);
                break;
            default:
                GameObject nearGhostTarget = FindNearGhostRoot(pointer.Skeleton);
                if (nearGhostTarget != null)
                {
                    RecordSelection(pointer, nearGhostTarget, "grabGhostNear", origin, direction);
                    BeginManipulation(pointer, FindGhost(nearGhostTarget));
                    break;
                }
                GameObject selectedHold = sceneConfiguror.GetActiveRouteHold(hovered.Object);
                if (selectedHold == null)
                {
                    RecordSelection(pointer, hovered.Object, "notRouteHold", origin, direction);
                    break;
                }
                RecordSelection(pointer, selectedHold, "spawned", origin, direction);
                SpawnGhost(selectedHold, pointer.Side);
                break;
        }
    }

    /// <summary>Which hold a ray points at, ignoring any previously held choice.</summary>
    private bool TryGetRayTarget(Ray ray, out GameObject target, out Vector3 targetPoint)
    {
        return TryResolveRayTarget(
            ray.origin,
            ray.direction.normalized,
            null,
            out target,
            out targetPoint,
            out _);
    }

    private bool TryResolveRayTarget(
        Vector3 origin,
        Vector3 direction,
        GameObject previousTarget,
        out GameObject target,
        out Vector3 targetPoint,
        out Candidate hovered)
    {
        BuildCandidates();
        ScoreCandidates(origin, direction);
        int selected = HandRayTargeting.SelectStickyTarget(
            IndexOfCandidate(previousTarget),
            candidateAngles,
            acquireHalfAngleDegrees,
            releaseHalfAngleDegrees,
            switchMarginDegrees);
        if (selected < 0)
        {
            target = null;
            targetPoint = origin + direction * maxRayDistance;
            hovered = default;
            return false;
        }

        hovered = candidates[selected];
        target = hovered.Object;
        // Stop at the near face rather than the centre, so the ray reads as touching the hold
        // instead of passing through it.
        float projection = Mathf.Max(0f, Vector3.Dot(hovered.Center - origin, direction));
        targetPoint = origin + direction * Mathf.Max(0.02f, projection - hovered.Radius);
        return true;
    }

    private void BuildCandidates()
    {
        candidates.Clear();
        if (dismissAllAffordance != null && dismissAllAffordance.activeSelf)
        {
            candidates.Add(new Candidate
            {
                Kind = CandidateKind.DismissAll,
                Object = dismissAllAffordance,
                Center = dismissAllAffordance.transform.position,
                Radius = 0.045f,
            });
        }

        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.DismissAffordance != null)
            {
                candidates.Add(new Candidate
                {
                    Kind = CandidateKind.DismissGhost,
                    Object = ghost.DismissAffordance,
                    Ghost = ghost,
                    Center = ghost.DismissAffordance.transform.position,
                    Radius = 0.045f,
                });
            }
            if (ghost.Root != null && TryGetCombinedBounds(ghost.Root, out Bounds ghostBounds))
            {
                candidates.Add(new Candidate
                {
                    Kind = CandidateKind.Ghost,
                    Object = ghost.Root,
                    Ghost = ghost,
                    Center = ghostBounds.center,
                    Radius = GetLargestExtent(ghostBounds),
                });
            }
        }

        if (sceneConfiguror?.activeHoldsList == null)
        {
            return;
        }
        foreach (GameObject hold in sceneConfiguror.activeHoldsList)
        {
            if (hold != null && TryGetCombinedBounds(hold, out Bounds holdBounds))
            {
                candidates.Add(new Candidate
                {
                    Kind = CandidateKind.WallHold,
                    Object = hold,
                    Center = holdBounds.center,
                    Radius = GetLargestExtent(holdBounds),
                });
            }
        }
    }

    private void ScoreCandidates(Vector3 origin, Vector3 direction)
    {
        // Nearest first, so that two holds the ray reads as equally on-axis resolve to the one in
        // front. On a board seen from the floor the far one is usually behind it anyway.
        for (int index = 0; index < candidates.Count; index++)
        {
            Candidate candidate = candidates[index];
            candidate.DistanceSqr = (candidate.Center - origin).sqrMagnitude;
            candidates[index] = candidate;
        }
        candidates.Sort(NearestFirst);

        candidateAngles.Clear();
        foreach (Candidate candidate in candidates)
        {
            float angle = HandRayTargeting.GetAcquisitionAngleDegrees(origin, direction, candidate.Center);
            if (angle == HandRayTargeting.NoTarget)
            {
                candidateAngles.Add(HandRayTargeting.NoTarget);
                continue;
            }

            // Charge the ray only for the gap between it and the candidate's silhouette, and give
            // the explicit affordances a head start so a dismiss target is never shadowed by the
            // proxy it sits beside.
            float relief = HandRayTargeting.GetAngularRadiusDegrees(origin, candidate.Center, candidate.Radius);
            if (candidate.Kind == CandidateKind.DismissGhost || candidate.Kind == CandidateKind.DismissAll)
            {
                relief += affordanceAcquireBonusDegrees;
            }
            candidateAngles.Add(Mathf.Max(0f, angle - relief));
        }
    }

    private int IndexOfCandidate(GameObject target)
    {
        if (target == null)
        {
            return -1;
        }
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].Object == target)
            {
                return index;
            }
        }
        return -1;
    }

    private static float GetLargestExtent(Bounds bounds)
    {
        return Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z));
    }

    private static readonly System.Comparison<Candidate> NearestFirst =
        (left, right) => left.DistanceSqr.CompareTo(right.DistanceSqr);

    private bool TryGetStabilisedRay(
        HandPointer pointer,
        float now,
        out Vector3 origin,
        out Vector3 direction)
    {
        origin = Vector3.zero;
        direction = Vector3.forward;
        OVRHand hand = pointer.Hand;
        if (hand == null || !hand.IsTracked || !hand.IsDataHighConfidence ||
            !hand.IsPointerPoseValid || hand.PointerPose == null)
        {
            pointer.OriginFilter.Reset();
            pointer.DirectionFilter.Reset();
            return false;
        }

        origin = pointer.OriginFilter.Update(hand.PointerPose.position, now);
        Vector3 filteredForward = pointer.DirectionFilter.Update(hand.PointerPose.forward, now);
        if (filteredForward.sqrMagnitude < 1e-8f)
        {
            return false;
        }
        direction = filteredForward.normalized;
        return true;
    }

    private void BeginManipulation(HandPointer pointer, GhostInstance ghost)
    {
        if (ghost?.Root == null || !TryGetHandPose(pointer.Skeleton, out Pose handPose))
        {
            return;
        }
        HandPointer other = pointer == left ? right : left;
        if (other != null && other.Manipulated == ghost)
        {
            return;
        }

        pointer.Manipulated = ghost;
        pointer.GrabPositionOffset = Quaternion.Inverse(handPose.rotation) *
                                     (ghost.Root.transform.position - handPose.position);
        pointer.GrabRotationOffset = Quaternion.Inverse(handPose.rotation) * ghost.Root.transform.rotation;
        pointer.NextPoseRecordTime = 0f;
    }

    private void UpdateManipulation(HandPointer pointer)
    {
        if (pointer?.Manipulated == null)
        {
            return;
        }
        if (pointer.Manipulated.Root == null || !pointer.Latch.IsClosed ||
            !TryGetHandPose(pointer.Skeleton, out Pose handPose))
        {
            ReleaseManipulation(pointer);
            return;
        }

        pointer.Manipulated.Root.transform.SetPositionAndRotation(
            handPose.position + handPose.rotation * pointer.GrabPositionOffset,
            handPose.rotation * pointer.GrabRotationOffset);
        RecordManipulatedPose(pointer, released: false);
    }

    private void ReleaseManipulation(HandPointer pointer)
    {
        if (pointer == null)
        {
            return;
        }
        RecordManipulatedPose(pointer, released: true);
        pointer.Manipulated = null;
    }

    private void ReleaseManipulationOf(GhostInstance ghost)
    {
        if (left != null && left.Manipulated == ghost)
        {
            ReleaseManipulation(left);
        }
        if (right != null && right.Manipulated == ghost)
        {
            ReleaseManipulation(right);
        }
    }

    /// <summary>
    /// The frame capture has no columns for proxy transforms, so manipulation is the one
    /// study-relevant motion the recording could not reconstruct; these rows close that gap.
    /// Throttled while tracking, and always emitted on release so every manipulation ends
    /// with its final pose and orientation deviation.
    /// </summary>
    private void RecordManipulatedPose(HandPointer pointer, bool released)
    {
        GhostInstance ghost = pointer.Manipulated;
        if (ghost?.Root == null || ghost.Source == null)
        {
            return;
        }
        float now = Time.unscaledTime;
        if (!released && now < pointer.NextPoseRecordTime)
        {
            return;
        }
        pointer.NextPoseRecordTime = now + ManipulationPoseRecordIntervalSeconds;

        Vector3 position = ghost.Root.transform.position;
        Quaternion rotation = ghost.Root.transform.rotation;
        float deviationDegrees = Quaternion.Angle(rotation, ghost.Source.transform.rotation);
        sceneConfiguror?.actionRecorder?.Record(
            "GhostPose",
            pointer.Side == Hand.Left ? "Left" : "Right",
            ghost.Root,
            System.FormattableString.Invariant(
                $"phase={(released ? "release" : "track")};pos=({position.x:F4},{position.y:F4},{position.z:F4});rot=({rotation.x:F4},{rotation.y:F4},{rotation.z:F4},{rotation.w:F4});deviationDeg={deviationDegrees:F1};live={ghosts.Count}"));
    }

    private void ResetPointerState(HandPointer pointer)
    {
        if (pointer == null)
        {
            return;
        }
        pointer.Latch.Reset();
        pointer.OriginFilter.Reset();
        pointer.DirectionFilter.Reset();
        pointer.HoverObject = null;
        pointer.Manipulated = null;
        pointer.DiagnosticState = -1;
    }

    private GameObject FindNearGhostRoot(OVRSkeleton skeleton)
    {
        if (skeleton == null || skeleton.Bones.Count <= (int)IndexTipBoneIndex)
        {
            return null;
        }

        Vector3 indexTip = skeleton.Bones[(int)IndexTipBoneIndex].Transform.position;
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root == null || !TryGetCombinedBounds(ghost.Root, out Bounds bounds))
            {
                continue;
            }
            bounds.Expand(nearGrabPadding * 2f);
            if (bounds.Contains(indexTip))
            {
                return ghost.Root;
            }
        }
        return null;
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

    // OpenXR hand joints put +Y out the back of the hand, so the palmar direction is -up. This
    // mirrors the console's summon detector so the two gestures cannot both claim one pinch.
    private static bool IsPalmUp(OVRSkeleton skeleton)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count == 0 ||
            skeleton.Bones[0].Transform == null)
        {
            return false;
        }
        return Vector3.Dot(-skeleton.Bones[0].Transform.up, Vector3.up) > PalmUpDotThreshold;
    }

    private static bool IsTrackingConfident(OVRHand hand)
    {
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence;
    }

    private static bool IsReportedPinching(OVRHand hand)
    {
        return hand != null && hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
    }

    private static float GetPinchStrength(OVRHand hand)
    {
        return hand != null ? hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) : 0f;
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

    private static string GetHoldCoordinate(GameObject hold)
    {
        string name = hold.name.Split('.')[0];
        int ghostMarker = name.IndexOf('#');
        return ghostMarker >= 0 ? name.Substring(0, ghostMarker) : name;
    }

    private void EnsureDecorationRoot()
    {
        if (decorationRoot != null)
        {
            return;
        }

        GameObject root = new(DecorationRootName)
        {
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };
        root.transform.SetParent(transform, false);
        decorationRoot = root.transform;
    }

    private Material EnsureLineMaterial()
    {
        if (lineMaterial == null)
        {
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default") ??
                                        UnityEngine.Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "Ghost inspection requires the Sprites/Default or URP Unlit shader.");
            }
            lineMaterial = new Material(shader) { name = "Ghost Inspection Line" };
        }
        return lineMaterial;
    }

    private TMP_FontAsset EnsureFontAsset()
    {
        if (fontAsset == null)
        {
            fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            if (fontAsset == null)
            {
                throw new System.InvalidOperationException(
                    "Ghost inspection requires the LiberationSans SDF font asset.");
            }
        }
        return fontAsset;
    }

    private LineRenderer CreateLine(string objectName, int positionCount, float width, Transform parent)
    {
        GameObject lineObject = new(objectName)
        {
            layer = 0,
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };
        lineObject.transform.SetParent(parent != null ? parent : decorationRoot, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.positionCount = positionCount;
        line.useWorldSpace = true;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = Color.white;
        line.endColor = Color.white;
        line.numCornerVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = EnsureLineMaterial();
        return line;
    }

    private TextMeshPro CreateLabel(string objectName, float worldFontSize, Transform parent)
    {
        GameObject textObject = new(objectName)
        {
            layer = 0,
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };
        textObject.transform.SetParent(parent != null ? parent : decorationRoot, false);
        textObject.transform.localScale = Vector3.one * TextTransformScale;
        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.font = EnsureFontAsset();
        text.rectTransform.sizeDelta = new Vector2(0.3f, 0.1f) / TextTransformScale;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = worldFontSize / TextTransformScale * 10f;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private void CreateGhostDecorations(GhostInstance ghost)
    {
        ghost.DismissAffordance = new GameObject("Ghost Dismiss")
        {
            layer = 0,
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };
        ghost.DismissAffordance.transform.SetParent(decorationRoot, false);
        ghost.DismissLabel = CreateLabel("Ghost Dismiss Glyph", 0.05f, ghost.DismissAffordance.transform);
        ghost.DismissLabel.text = "×";
        ghost.DismissLabel.color = new Color(1f, 0.45f, 0.35f, 1f);

        ghost.WallMarker = CreateLine("Ghost Wall Referent", WallMarkerSegments, markerWidth, null);
        ghost.WallMarker.loop = true;
        ghost.Tether = CreateLine("Ghost Wall Tether", 2, tetherWidth, null);

        GameObject indicatorObject = new("Ghost Orientation Indicator")
        {
            layer = 0,
            hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
        };
        indicatorObject.transform.SetParent(decorationRoot, false);
        ghost.IndicatorRoot = indicatorObject.transform;
        ghost.OrientationArc = CreateLine("Ghost Orientation Arc", 2, 0.0035f, ghost.IndicatorRoot);
        ghost.OrientationTick = CreateLine("Ghost Orientation True Mark", 2, 0.0035f, ghost.IndicatorRoot);
        ghost.OrientationLabel = CreateLabel("Ghost Orientation Readout", 0.03f, ghost.IndicatorRoot);
    }

    private void UpdateGhostDecorations()
    {
        userCamera = userCamera != null ? userCamera : Camera.main;
        if (userCamera == null)
        {
            return;
        }

        Transform camera = userCamera.transform;
        foreach (GhostInstance ghost in ghosts)
        {
            if (ghost.Root == null)
            {
                continue;
            }
            bool hasGhostBounds = TryGetCombinedBounds(ghost.Root, out Bounds ghostBounds);
            UpdateWallMarker(ghost, camera);
            UpdateTether(ghost, hasGhostBounds ? ghostBounds.center : ghost.Root.transform.position);
            if (hasGhostBounds)
            {
                UpdateDismissAffordance(ghost, ghostBounds, camera);
                UpdateOrientationIndicator(ghost, ghostBounds, camera);
            }
        }
        UpdateDismissAllAffordance(camera);
    }

    private void UpdateWallMarker(GhostInstance ghost, Transform camera)
    {
        if (ghost.WallMarker == null || ghost.Source == null ||
            !TryGetCombinedBounds(ghost.Source, out Bounds bounds))
        {
            return;
        }

        float radius = GetLargestExtent(bounds) * 1.35f;
        float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
        Color color = markerColor;
        color.a *= pulse;
        SetLineColor(ghost.WallMarker, color);

        Vector3 center = bounds.center - camera.forward * 0.006f;
        for (int i = 0; i < ghost.WallMarker.positionCount; i++)
        {
            float angle = i * Mathf.PI * 2f / ghost.WallMarker.positionCount;
            ghost.WallMarker.SetPosition(i, center +
                (camera.right * Mathf.Cos(angle) + camera.up * Mathf.Sin(angle)) * radius);
        }
    }

    private void UpdateTether(GhostInstance ghost, Vector3 ghostCenter)
    {
        if (ghost.Tether == null || ghost.Source == null)
        {
            return;
        }

        Vector3 wallPoint = TryGetCombinedBounds(ghost.Source, out Bounds bounds)
            ? bounds.center
            : ghost.Source.transform.position;
        ghost.Tether.SetPosition(0, ghostCenter);
        ghost.Tether.SetPosition(1, wallPoint);
        SetLineColor(ghost.Tether, tetherColor);
    }

    private void UpdateDismissAffordance(GhostInstance ghost, Bounds bounds, Transform camera)
    {
        if (ghost.DismissAffordance == null)
        {
            return;
        }

        float sideOffset = Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.07f;
        float topOffset = bounds.extents.y + 0.07f;
        ghost.DismissAffordance.transform.position = bounds.center +
                                                     camera.right * sideOffset +
                                                     camera.up * topOffset;
        ghost.DismissAffordance.transform.rotation = Quaternion.LookRotation(
            ghost.DismissAffordance.transform.position - camera.position,
            camera.up);
    }

    private void UpdateOrientationIndicator(GhostInstance ghost, Bounds bounds, Transform camera)
    {
        if (ghost.IndicatorRoot == null || ghost.OrientationArc == null)
        {
            return;
        }

        float deviation = GhostOrientationIndicatorPolicy.GetDeviationDegrees(
            ghost.Root.transform.rotation,
            GetLiveWallRotation(ghost));
        Color color = GhostOrientationIndicatorPolicy.GetIndicatorColor(deviation);
        float radius = GetLargestExtent(bounds) + 0.045f;
        Vector3 center = bounds.center - camera.up * (bounds.extents.y + 0.055f);
        ghost.IndicatorRoot.SetPositionAndRotation(center, camera.rotation);

        Vector3 up = camera.up;
        Vector3 right = camera.right;
        float sweep = GhostOrientationIndicatorPolicy.GetArcSweepDegrees(deviation);
        int segments = Mathf.Clamp(Mathf.CeilToInt(sweep / ArcDegreesPerSegment), 1, MaximumArcSegments);
        ghost.OrientationArc.enabled = sweep > 0.5f;
        if (ghost.OrientationArc.enabled)
        {
            ghost.OrientationArc.positionCount = segments + 1;
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Deg2Rad * (sweep * i / segments);
                ghost.OrientationArc.SetPosition(
                    i,
                    center + (up * Mathf.Cos(angle) + right * Mathf.Sin(angle)) * radius);
            }
            SetLineColor(ghost.OrientationArc, color);
        }

        // A short mark at the arc's origin keeps "closed" readable as "on the mark" rather than
        // as the indicator having disappeared.
        ghost.OrientationTick.SetPosition(0, center + up * (radius - 0.012f));
        ghost.OrientationTick.SetPosition(1, center + up * (radius + 0.012f));
        SetLineColor(ghost.OrientationTick, color);

        ghost.OrientationLabel.transform.position = center - up * 0.035f;
        ghost.OrientationLabel.transform.rotation = camera.rotation;
        ghost.OrientationLabel.text = GhostOrientationIndicatorPolicy.FormatDeviationDegrees(deviation);
        ghost.OrientationLabel.color = color;
    }

    /// <summary>
    /// The wall hold's orientation as it stands now. Board alignment and the ghost viewing standoff
    /// both move the board after a proxy is summoned, so the deviation has to be measured against
    /// the live hold rather than a pose captured at spawn.
    /// </summary>
    private static Quaternion GetLiveWallRotation(GhostInstance ghost)
    {
        return ghost.Source != null ? ghost.Source.transform.rotation : ghost.WallRotation;
    }

    private void UpdateDismissAllAffordance(Transform camera)
    {
        bool visible = ghosts.Count >= 2;
        if (!visible)
        {
            if (dismissAllAffordance != null)
            {
                dismissAllAffordance.SetActive(false);
            }
            return;
        }

        if (dismissAllAffordance == null)
        {
            EnsureDecorationRoot();
            dismissAllAffordance = new GameObject("Ghost Dismiss All")
            {
                layer = 0,
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
            };
            dismissAllAffordance.transform.SetParent(decorationRoot, false);
            dismissAllLabel = CreateLabel("Ghost Dismiss All Glyph", 0.03f, dismissAllAffordance.transform);
            dismissAllLabel.text = "× ALL";
            dismissAllLabel.color = new Color(1f, 0.45f, 0.35f, 1f);
        }

        dismissAllAffordance.SetActive(true);
        GhostInstance newest = ghosts[ghosts.Count - 1];
        Vector3 anchor = newest.Root != null && TryGetCombinedBounds(newest.Root, out Bounds bounds)
            ? bounds.center + camera.up * (bounds.extents.y + 0.16f)
            : camera.position + camera.forward * spawnDistance + camera.up * 0.2f;
        dismissAllAffordance.transform.position = anchor;
        dismissAllAffordance.transform.rotation = Quaternion.LookRotation(anchor - camera.position, camera.up);
    }

    private void EnsurePointerVisuals(HandPointer pointer)
    {
        if (pointer == null || pointer.Ray != null)
        {
            return;
        }

        EnsureDecorationRoot();
        string side = pointer.Side == Hand.Left ? "Left" : "Right";
        pointer.Ray = CreateLine(side + " Ghost Selection Ray", 2, 0.0025f, null);
        pointer.Ray.endWidth = 0.001f;
        pointer.RayProperties = new MaterialPropertyBlock();
        pointer.Focus = CreateLine(side + " Ghost Pointer Focus", FocusRingSegments, 0.0022f, null);
        pointer.Focus.loop = true;
        pointer.FocusProperties = new MaterialPropertyBlock();
        pointer.Ray.enabled = false;
        pointer.Focus.enabled = false;
    }

    private void UpdatePointerVisuals(
        HandPointer pointer,
        Vector3 origin,
        Vector3 endPoint,
        bool hasHover,
        Candidate hovered)
    {
        EnsurePointerVisuals(pointer);
        pointer.Ray.enabled = true;
        pointer.Ray.SetPosition(0, origin);
        pointer.Ray.SetPosition(1, endPoint);
        SetLineColor(pointer.Ray, rayColor, pointer.RayProperties);

        pointer.Focus.enabled = hasHover;
        if (!hasHover)
        {
            return;
        }

        // The reticle is a ring drawn around what a pinch would act on, sized to that object and
        // turned to face the participant, so the ray's endpoint is never ambiguous between two
        // holds that overlap from where they are standing.
        Vector3 toViewer = (origin - hovered.Center).normalized;
        Vector3 ringRight = Vector3.Cross(Vector3.up, toViewer);
        if (ringRight.sqrMagnitude < 1e-6f)
        {
            ringRight = Vector3.right;
        }
        ringRight.Normalize();
        Vector3 ringUp = Vector3.Cross(toViewer, ringRight);
        float radius = Mathf.Max(hovered.Radius * 1.3f, 0.035f);
        Vector3 center = hovered.Center + toViewer * (hovered.Radius + 0.004f);
        for (int i = 0; i < pointer.Focus.positionCount; i++)
        {
            float angle = i * Mathf.PI * 2f / pointer.Focus.positionCount;
            pointer.Focus.SetPosition(
                i,
                center + (ringRight * Mathf.Cos(angle) + ringUp * Mathf.Sin(angle)) * radius);
        }
        SetLineColor(pointer.Focus, focusColor, pointer.FocusProperties);
    }

    private static void SetPointerVisible(HandPointer pointer, bool visible)
    {
        if (pointer?.Ray != null)
        {
            pointer.Ray.enabled = visible;
        }
        if (pointer?.Focus != null)
        {
            pointer.Focus.enabled = visible;
        }
    }

    private static void SetLineColor(LineRenderer line, Color color, MaterialPropertyBlock properties = null)
    {
        if (line == null)
        {
            return;
        }

        line.startColor = color;
        line.endColor = color;
        Material material = line.sharedMaterial;
        if (material == null || properties == null)
        {
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

    private void RecordGhostEvent(string action, GhostInstance ghost, Hand? hand, string details)
    {
        string handToken = hand.HasValue ? (hand.Value == Hand.Left ? "Left" : "Right") : string.Empty;
        string liveDetails = string.IsNullOrEmpty(details)
            ? "live=" + ghosts.Count
            : details + ";live=" + ghosts.Count;
        sceneConfiguror?.actionRecorder?.Record(action, handToken, ghost.Root, liveDetails);
    }

    /// <summary>
    /// Every consumed pinch, whether or not it produced a proxy. The verbose diagnostics below are
    /// editor-only, which leaves a participant who never detaches a hold indistinguishable from a
    /// technique that silently refused every attempt; this event closes that gap in the shipped
    /// build without adding a per-frame stream.
    /// </summary>
    private void RecordSelection(
        HandPointer pointer,
        GameObject target,
        string outcome,
        Vector3 origin,
        Vector3 direction)
    {
        sceneConfiguror?.actionRecorder?.Record(
            "GhostSelection",
            pointer.Side == Hand.Left ? "Left" : "Right",
            target,
            "outcome=" + outcome + ";live=" + ghosts.Count);
        RecordSelectionAttempt(pointer, target, outcome, origin, direction);
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void RecordInputDiagnostics(HandPointer pointer, bool trackingConfident, bool hasRay)
    {
        ActionRecorder recorder = sceneConfiguror?.actionRecorder;
        if (recorder == null || !recorder.IsRecording)
        {
            pointer.DiagnosticState = -1;
            return;
        }

        OVRHand hand = pointer.Hand;
        bool tracked = hand != null && hand.IsTracked;
        bool highConfidence = hand != null && hand.IsDataHighConfidence;
        bool dataValid = hand != null && hand.IsDataValid;
        bool pointerPoseValid = hand != null && hand.IsPointerPoseValid && hand.PointerPose != null;
        OVRInput.ControllerInHandState controllerState = hand != null
            ? OVRInput.GetControllerIsInHandState((OVRInput.Hand)hand.GetHand())
            : OVRInput.ControllerInHandState.NoHand;
        bool palmUp = IsPalmUp(pointer.Skeleton);
        int state = (tracked ? 1 : 0) |
                    (highConfidence ? 1 << 1 : 0) |
                    (dataValid ? 1 << 2 : 0) |
                    (pointerPoseValid ? 1 << 3 : 0) |
                    (hasRay ? 1 << 4 : 0) |
                    (trackingConfident ? 1 << 5 : 0) |
                    (pointer.Latch.IsClosed ? 1 << 6 : 0) |
                    (pointer.Latch.IsArmed ? 1 << 7 : 0) |
                    (palmUp ? 1 << 8 : 0) |
                    (pointer.HoverObject != null ? 1 << 9 : 0) |
                    ((int)controllerState << 10) |
                    (hand != null ? (int)hand.m_showState << 12 : 0);
        if (state == pointer.DiagnosticState)
        {
            return;
        }
        pointer.DiagnosticState = state;

        recorder.Record(
            "GhostInputState",
            pointer.Side == Hand.Left ? "Left" : "Right",
            null,
            "tracked=" + tracked.ToString().ToLowerInvariant() +
            ";highConfidence=" + highConfidence.ToString().ToLowerInvariant() +
            ";dataValid=" + dataValid.ToString().ToLowerInvariant() +
            ";pointerPoseValid=" + pointerPoseValid.ToString().ToLowerInvariant() +
            ";hasRay=" + hasRay.ToString().ToLowerInvariant() +
            ";pinching=" + pointer.Latch.IsClosed.ToString().ToLowerInvariant() +
            ";pinchArmed=" + pointer.Latch.IsArmed.ToString().ToLowerInvariant() +
            ";palmUp=" + palmUp.ToString().ToLowerInvariant() +
            ";hovering=" + (pointer.HoverObject != null).ToString().ToLowerInvariant() +
            ";pinchStrength=" + GetPinchStrength(pointer.Hand).ToString(
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

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void RecordSelectionAttempt(
        HandPointer pointer,
        GameObject target,
        string outcome,
        Vector3 origin,
        Vector3 direction)
    {
        sceneConfiguror?.actionRecorder?.Record(
            "GhostSelectionAttempt",
            pointer.Side == Hand.Left ? "Left" : "Right",
            target,
            System.FormattableString.Invariant(
                $"outcome={outcome};live={ghosts.Count};rayOrigin=({origin.x:F4},{origin.y:F4},{origin.z:F4});rayDirection=({direction.x:F4},{direction.y:F4},{direction.z:F4})"));
    }

    private void OnDestroy()
    {
        if (lineMaterial != null)
        {
            Destroy(lineMaterial);
        }
    }
}
