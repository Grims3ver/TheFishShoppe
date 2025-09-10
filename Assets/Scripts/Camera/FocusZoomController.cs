using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[RequireComponent(typeof(CinemachineCamera))]
public class FocusZoomController : MonoBehaviour
{
    [Header("Refs")]
    public Camera mainCam;
    public Transform focusTarget;          // vCam tracks this
    public Transform pivotCenter;          // tank center
    public Collider tankBounds;            // inner water volume
    public LayerMask tankMask;             // fish/plants (no glass)

    [Header("Input (Input System)")]
    public InputActionReference lookAction;    // same one driving CM3 X/Y
    public InputActionReference panAction;     // Vector2 (Mouse Delta); gated by middle mouse or modifier in asset/code
    public InputActionReference zoomAction;    // Axis (mouse scroll Y)

    [Header("Radius")]
    public float minRadius = 3f;
    public float maxRadius = 10f;
    public float radiusStep = 0.75f;           // amount per wheel tick
    public float radiusSmoothTime = 0.18f;     // SmoothDamp

    [Header("Pan")]
    public float panSpeed = 1.0f;
    public float panDistanceScale = 0.002f;    // units per pixel per meter
    public float maxPanStepPerSec = 0.25f;     // clamp world delta per second
    public bool requireMiddleMouse = true;     // set false if using modifier composite in asset

    [Header("General Zoom Bias")]
    public float pullTowardLook = 0.35f;       // when zooming in
    public float returnToCenter = 0.15f;       // when zooming out
    public float focusSmoothRate = 12f;        // exp smoothing toward goal

    [Header("Focus Snap")]
    public float focusRadiusThreshold = 4.2f;  // start snapping when closer than this
    public float unsnapRadiusHysteresis = 4.6f;
    public float centerWindow = 0.18f;         // tight snap window (viewport distance from center)
    public float releaseWindow = 0.33f;        // looser to release
    public float snapDelay = 0.30f;            // dwell time to confirm intent
    public float resnapCooldown = 0.40f;       // cooldown after manual pan
    public float snapFollowRate = 16f;         // faster smoothing while focused

    // --- internals ---
    CinemachineCamera vcam;
    CinemachineOrbitalFollow orbital;
    float desiredRadius, radiusVel;
    Vector3 goal;
    Transform focused;                         // current “locked” target
    float dwellTimer, resnapTimer;

    void Awake()
    {
        vcam = GetComponent<CinemachineCamera>();
        orbital = GetComponent<CinemachineOrbitalFollow>();
        if (focusTarget) goal = focusTarget.position;
    }

    void OnEnable()
    {
        lookAction?.action.Enable();
        panAction?.action.Enable();
        zoomAction?.action.Enable();
        if (orbital) desiredRadius = orbital.Radius;
    }

    void OnDisable()
    {
        lookAction?.action.Disable();
        panAction?.action.Disable();
        zoomAction?.action.Disable();
    }

    void Update()
    {
        if (!mainCam || !focusTarget || !pivotCenter || orbital == null) return;

        HandleZoom();
        HandlePan();

        // Smooth radius
        orbital.Radius = Mathf.SmoothDamp(orbital.Radius, desiredRadius, ref radiusVel, radiusSmoothTime);

        // Maintain focus/snap logic
        HandleFocusMode();

        // Smoothly move focus target
        float rate = focused ? snapFollowRate : focusSmoothRate;
        float k = 1f - Mathf.Exp(-Time.deltaTime * Mathf.Max(0.0001f, rate));
        focusTarget.position = Vector3.Lerp(focusTarget.position, goal, k);
    }

    void HandleZoom()
    {
        float dz = zoomAction?.action.ReadValue<float>() ?? 0f;
        if (Mathf.Abs(dz) < 0.01f) dz = 0f;

        if (dz == 0f) return;

        float prev = desiredRadius;
        desiredRadius = Mathf.Clamp(desiredRadius - dz * radiusStep, minRadius, maxRadius);

        bool zoomingIn = desiredRadius < prev;

        if (focused == null)
        {
            // compute look point (center ray)
            Vector3 lookPoint = pivotCenter.position;
            Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
            if (Physics.Raycast(ray, out var hit, 200f, tankMask)) lookPoint = hit.point;

            float bias = zoomingIn ? pullTowardLook : returnToCenter;
            goal = Vector3.Lerp(goal, zoomingIn ? lookPoint : pivotCenter.position, bias);
            ClampGoalToTank();
        }

        // Unsnap on zoom-out with hysteresis
        if (!zoomingIn && orbital.Radius >= unsnapRadiusHysteresis) { focused = null; dwellTimer = 0f; }
    }

    void HandlePan()
    {
        Vector2 pan = panAction?.action.ReadValue<Vector2>() ?? Vector2.zero;
        bool allow = true;

        if (requireMiddleMouse)
        {
            var mouse = Mouse.current;
            allow = mouse != null && mouse.middleButton.isPressed;
        }

        if (allow && pan.sqrMagnitude > 0.000001f)
        {
            focused = null;                     // manual input releases focus
            resnapTimer = resnapCooldown;       // block re-snap for a moment

            Vector3 right = mainCam.transform.right;
            Vector3 up = mainCam.transform.up;

            float scale = panDistanceScale * orbital.Radius * panSpeed;
            Vector3 worldDelta = (-pan.x * right + -pan.y * up) * scale;

            // clamp per-frame to avoid “hard” jumps
            float maxStep = maxPanStepPerSec * Time.deltaTime;
            if (worldDelta.magnitude > maxStep) worldDelta = worldDelta.normalized * maxStep;

            goal += worldDelta;
            ClampGoalToTank();
        }

        if (resnapTimer > 0f) resnapTimer -= Time.deltaTime;
    }

    void HandleFocusMode()
    {
        float r = orbital.Radius;

        if (focused == null)
        {
            if (resnapTimer <= 0f && r <= focusRadiusThreshold)
            {
                var candidate = FindBestCandidateInView(centerWindow);
                if (candidate != null)
                {
                    dwellTimer += Time.deltaTime;
                    if (dwellTimer >= snapDelay)
                    {
                        focused = candidate.transform;
                        goal = candidate.transform.TransformPoint(candidate.focusOffset);
                        ClampGoalToTank();
                        dwellTimer = 0f;
                    }
                }
                else dwellTimer = 0f;
            }
            else
            {
                // follow the focused point (assume its transform is the anchor)
                goal = focused.position;
                ClampGoalToTank();

                // release if it drifts off center or we zoomed out earlier
                if (TooFarFromCenter(focused.position, releaseWindow))
                {
                    focused = null;
                    dwellTimer = 0f;
                }
            }
        }
    }

    FocusTarget FindBestCandidateInView(float window)
    {
        // Raycast from screen center and only accept objects with FocusableTarget
        Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, 200f, tankMask))
        {
            if (hit.transform.TryGetComponent<FocusTarget>(out var f))
            {
                var vp = mainCam.WorldToViewportPoint(f.transform.position);
                if (vp.z > 0f)
                {
                    float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
                    if (off <= window) return f;
                }
            }
        }
        return null;
    }

    bool TooFarFromCenter(Vector3 worldPos, float window)
    {
        var vp = mainCam.WorldToViewportPoint(worldPos);
        if (vp.z < 0) return true;
        float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
        return off > window;
    }

    void ClampGoalToTank()
    {
        if (!tankBounds) return;
        var b = tankBounds.bounds;
        goal = new Vector3(
            Mathf.Clamp(goal.x, b.min.x, b.max.x),
            Mathf.Clamp(goal.y, b.min.y, b.max.y),
            Mathf.Clamp(goal.z, b.min.z, b.max.z)
        );
    }
}
