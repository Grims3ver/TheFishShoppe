using UnityEngine;
using UnityEngine.InputSystem;

public class FocusLockFollow : MonoBehaviour
{
    [Header("Refs")]
    public Camera mainCam;                 // Main Camera
    public Transform focusTarget;          // vCam's Tracking Target (movable empty)
    public Transform pivotCenter;          // tank center (fallback)
    public Collider tankBounds;            // inner water volume (BoxCollider)
    [Tooltip("Layers that can be focused (fish/props). Exclude glass/room/inner box.")]
    public LayerMask focusMask;

    [Header("Input")]
    public bool useMouseWheel = true;      // reads Mouse.scroll.y for in/out
    public float scrollDeadzone = 0.01f;

    [Header("Snap/Follow")]
    public float centerWindow = 0.18f;     // how close to screen center (0..~0.5)
    public float snapDwell = 0.35f;        // time the candidate must stay centered
    public float followRate = 16f;         // exp smoothing while locked
    public float idleRate = 12f;           // exp smoothing when free
    public float quietAfterPan = 0.5f;     // block snapping right after a pan gesture

    [Header("Release conditions")]
    public float releaseWindow = 0.38f;    // if target drifts this far off-center
    public float releaseOnZoomOutThreshold = 0.0f; // set >0 if you want wheel-down to force release
    public float resnapCooldown = 0.4f;    // delay before re-snap after release/manual pan

    // Hook this from your pan script: call BeginManualPan() when RMB pan starts.
    public void BeginManualPan() { _focused = null; _cooldown = resnapCooldown; _quiet = quietAfterPan; }

    Transform _focused;
    Vector3 _goal;
    float _dwell, _cooldown, _quiet;

    void Awake()
    {
        if (focusTarget) _goal = focusTarget.position;
    }

    void Update()
    {
        if (!mainCam || !focusTarget) return;

        // Optional: steer goal a bit on scroll (positive = zoom in)
        float scroll = 0f;
        if (useMouseWheel && Mouse.current != null)
        {
            scroll = Mathf.Clamp(Mouse.current.scroll.ReadValue().y, -120f, 120f) / 120f;
            if (Mathf.Abs(scroll) <= scrollDeadzone) scroll = 0f;
        }

        // Decide snap/unsnap
        if (_cooldown > 0f) _cooldown -= Time.deltaTime;
        if (_quiet > 0f) _quiet -= Time.deltaTime;

        if (_focused == null)
        {
            if (_cooldown <= 0f && _quiet <= 0f && scroll > 0f) // only consider while zooming in
            {
                var cand = FindCenteredFocusable(centerWindow);
                if (cand != null)
                {
                    _dwell += Time.deltaTime;
                    if (_dwell >= snapDwell)
                    {
                        _focused = cand.transform;
                        _dwell = 0f;
                    }
                }
                else _dwell = 0f;
            }
            else _dwell = 0f;
        }
        else
        {
            // Release rules
            if (releaseOnZoomOutThreshold > 0f && scroll < -releaseOnZoomOutThreshold)
                Release();
            else if (TooFarFromCenter(_focused.position, releaseWindow))
                Release();
        }

        // Drive goal
        if (_focused != null)
        {
            var f = _focused.GetComponent<FocusTarget>();
            var targetPos = _focused.TransformPoint(f ? f.focusOffset : Vector3.zero);
            _goal = targetPos;
            ClampToTank(ref _goal);
        }
        else
        {
            // When free, don't force center. Let your pan/zoom-steer script adjust _goal.
            // If you do want a gentle recenter when wheel-down: uncomment below
            // if (scroll < -scrollDeadzone && pivotCenter)
            // {
            //     _goal = Vector3.Lerp(_goal, pivotCenter.position, 0.15f);
            //     ClampToTank(ref _goal);
            // }
        }

        // Apply smoothing
        float rate = (_focused != null) ? followRate : idleRate;
        float k = 1f - Mathf.Exp(-rate * Time.deltaTime);
        focusTarget.position = Vector3.Lerp(focusTarget.position, _goal, k);
    }

    FocusTarget FindCenteredFocusable(float window)
    {
        Ray ray = mainCam.ScreenPointToRay(Mouse.current != null
            ? Mouse.current.position.ReadValue()
            : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

        if (Physics.Raycast(ray, out var hit, 500f, focusMask))
        {
            if (hit.transform.TryGetComponent(out FocusTarget ft))
            {
                var vp = mainCam.WorldToViewportPoint(hit.transform.position);
                if (vp.z > 0f)
                {
                    float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
                    if (off <= window) return ft;
                }
            }
        }
        return null;
    }

    bool TooFarFromCenter(Vector3 worldPos, float window)
    {
        var vp = mainCam.WorldToViewportPoint(worldPos);
        if (vp.z < 0f) return true;
        float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
        return off > window;
    }

    void ClampToTank(ref Vector3 p)
    {
        if (!tankBounds) return;
        var b = tankBounds.bounds;
        p = new Vector3(
            Mathf.Clamp(p.x, b.min.x, b.max.x),
            Mathf.Clamp(p.y, b.min.y, b.max.y),
            Mathf.Clamp(p.z, b.min.z, b.max.z)
        );
    }

    void Release()
    {
        _focused = null;
        _dwell = 0f;
        _cooldown = resnapCooldown;
    }
}