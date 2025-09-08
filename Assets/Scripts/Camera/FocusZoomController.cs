using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[RequireComponent(typeof(CinemachineCamera))]
public class FocusZoomController : MonoBehaviour
{
    [Header("Refs")]
    public Camera mainCam;
    public Transform focusTarget;      // vCam tracks this
    public Transform pivotCenter;      // tank center
    public Collider tankBounds;        // inner water volume (BoxCollider ideal)
    public LayerMask tankMask;         // fish/plants (no glass)

    [Header("Input")]
    public InputActionReference zoomAction;   // Mouse Scroll Y (Value/Axis)
    public float zoomStep = 0.75f;            // radius delta per tick

    [Header("Radius Limits & Smooth")]
    public float minRadius = 3f;
    public float maxRadius = 10f;
    public float radiusSmoothTime = 0.15f;

    [Header("General Zoom (toward look)")]
    public float pullTowardLook = 0.35f;  // bias when zooming in
    public float returnToCenter = 0.15f;  // bias when zooming out
    public float focusLerp = 12f;         // exp-smoothing toward goal

    [Header("Focus Zoom")]
    public float focusRadiusThreshold = 4.2f; // when radius <= this, we consider snapping
    public float centerWindow = 0.20f;        // viewport radius from center to allow snap (0..0.5 is tight)
    public float snapDamping = 16f;           // smooth follow while focused
    public float unsnapRadiusHysteresis = 4.6f; // radius above which we release focus (hysteresis prevents flicker)
    public float unsnapCenterWindow = 0.33f;    // if target wanders too far off center
    public float maxSnapDistance = 15f;         // world-space max distance to candidate

    [Header("Pan (move focus point)")]
    public InputActionReference panAction;   // Value/Vector2 (Mouse Delta or stick)
    public float panSpeed = 1.0f;            // base speed
    public float panDistanceScale = 0.002f;  // scales with camera distance (units per pixel)


    CinemachineCamera vcam;
    CinemachineOrbitalFollow orbital;

    float desiredRadius;
    float radiusVel;
    Vector3 goal;
    Transform focused;               // current focused transform (null if not in focus)
    static readonly List<FocusTarget> pool = new();   // simple cache

    void Awake()
    {
        vcam = GetComponent<CinemachineCamera>();
        orbital = GetComponent<CinemachineOrbitalFollow>();
        if (focusTarget) goal = focusTarget.position;
        RebuildPool();
    }

    void OnEnable()
    {
        zoomAction?.action.Enable();
        panAction?.action.Enable(); //enable pan
        if (orbital) desiredRadius = orbital.Radius;
    }

    void Update()
    {
        if (!mainCam || !focusTarget || !pivotCenter || orbital == null) return;

        // Read zoom input
        float dz = zoomAction?.action.ReadValue<float>() ?? 0f;
        if (Mathf.Abs(dz) < 0.01f) dz = 0f;

        bool zoomEvent = dz != 0f;
        if (zoomEvent)
        {
            desiredRadius = Mathf.Clamp(desiredRadius - dz * zoomStep, minRadius, maxRadius);

            // If already focused, we don't need raycast pull; we follow the target.
            if (focused == null)
            {
                // Pull general focus toward what we look at when zooming in
                Vector3 lookPoint = pivotCenter.position;
                Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
                if (Physics.Raycast(ray, out var hit, 200f, tankMask))
                    lookPoint = hit.point;

                bool zoomingIn = desiredRadius < orbital.Radius;
                float bias = zoomingIn ? pullTowardLook : returnToCenter;
                goal = Vector3.Lerp(goal, zoomingIn ? lookPoint : pivotCenter.position, bias);
                ClampGoalToTank();
            }
        }

        Vector2 pan = panAction.action.ReadValue<Vector2>();
        if (UnityEngine.InputSystem.Mouse.current.middleButton.isPressed)
        {
            if (pan.sqrMagnitude > 0.000001f)
            {
                // If we’re locked onto a fish, release so user can pan freely
                if (focused != null) focused = null;

                // Convert screen delta to world delta aligned to camera right/up
                // Negative so dragging right moves scene left (natural feel); flip if you prefer
                Vector3 right = mainCam.transform.right;
                Vector3 up = mainCam.transform.up;

                // Scale by current radius so pan feels consistent at different zooms
                float scale = panDistanceScale * orbital.Radius * panSpeed;

                Vector3 worldDelta = (-pan.x * right + -pan.y * up) * scale;

                goal += worldDelta;
                ClampGoalToTank();
            }

            // Smooth radius & focus target
            orbital.Radius = Mathf.SmoothDamp(orbital.Radius, desiredRadius, ref radiusVel, radiusSmoothTime);
            float k = 1f - Mathf.Exp(-Time.deltaTime * (focused ? snapDamping : focusLerp));
            focusTarget.position = Vector3.Lerp(focusTarget.position, goal, k);

            // Focus mode maintenance (unchanged)
            HandleFocusMode();
        }
    }


    void HandleFocusMode()
    {
        float r = orbital.Radius;

        if (focused == null)
        {
            // Candidate snap if we're close enough AND a good target is near screen center.
            if (r <= focusRadiusThreshold)
            {
                var best = FindBestCandidateInView();
                if (best != null)
                {
                    focused = best.transform;
                    // snap goal to target immediately (with its local offset)
                    goal = TargetWorldPoint(best);
                    ClampGoalToTank();
                }
            }
        }
        else
        {
            // Maintain goal on focused target (with offset)
            goal = TargetWorldPoint(focused.GetComponent<FocusTarget>());
            ClampGoalToTank();

            // Release focus if user zooms out or target drifts off-center
            if (r >= unsnapRadiusHysteresis || TooFarFromCenter(focused.position, unsnapCenterWindow))
            {
                focused = null;
                // drift goal back toward center over time (Update will lerp)
            }
        }
    }

    FocusTarget FindBestCandidateInView()
    {
        // refresh pool if empty (cheap; you can also keep a registry)
        if (pool.Count == 0) RebuildPool();

        FocusTarget best = null;
        float bestScore = float.NegativeInfinity;
        Vector2 screenCenter = new(0.5f, 0.5f);

        foreach (var f in pool)
        {
            if (f == null) continue;
            Vector3 wp = TargetWorldPoint(f);
            Vector3 vp = mainCam.WorldToViewportPoint(wp);
            if (vp.z < 0f) continue; // behind camera

            // distance from center in viewport
            Vector2 v2 = new Vector2(vp.x, vp.y);
            float off = Vector2.Distance(v2, screenCenter);

            // Accept only if inside tight window
            if (off > centerWindow) continue;

            // Also reject if ridiculously far in world
            float worldDist = Vector3.Distance(mainCam.transform.position, wp);
            if (worldDist > maxSnapDistance) continue;

            // Higher weight & closer-to-center = better
            float score = -off * 0.2f;
            if (score > bestScore)
            {
                // Optional: line of sight check (ignore glass)
                if (Physics.Linecast(mainCam.transform.position, wp, out var hit, tankMask))
                {
                    // ok if we hit the target's collider or something in tank mask
                    // (tune as needed; can skip for perf)
                }
                bestScore = score;
                best = f;
            }
        }
        return best;
    }

    bool TooFarFromCenter(Vector3 worldPos, float window)
    {
        var vp = mainCam.WorldToViewportPoint(worldPos);
        if (vp.z < 0) return true;
        float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
        return off > window;
    }

    Vector3 TargetWorldPoint(FocusTarget f)
    {
        if (f == null) return pivotCenter.position;
        return f.transform.TransformPoint(f.focusOffset);
    }

    void ClampGoalToTank()
    {
        if (!tankBounds) return;
        Bounds b = tankBounds.bounds;
        goal = new Vector3(
            Mathf.Clamp(goal.x, b.min.x, b.max.x),
            Mathf.Clamp(goal.y, b.min.y, b.max.y),
            Mathf.Clamp(goal.z, b.min.z, b.max.z)
        );
    }

    void RebuildPool()
    {
        pool.Clear();
        // Finds all active focusables once; cheap for a tank scene
        var found = FindObjectsByType<FocusTarget>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        pool.AddRange(found);
    }
}