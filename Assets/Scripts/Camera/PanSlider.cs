using UnityEngine;
using UnityEngine.InputSystem;

using UnityEngine;
using UnityEngine.InputSystem;

public class FocusRigController : MonoBehaviour
{
    public enum PanSpace { CameraPlane, WorldXZ }

    [Header("Refs")]
    public Camera mainCam;
    public Transform focusTarget;     // vCam's Tracking Target (movable empty)
    public Transform pivotCenter;     // tank center for recenter
    public Collider tankBounds;       // inner water volume (BoxCollider)
    [Tooltip("Layers that can be focused/zoomed toward (fish/props). Exclude glass/room/inner box.")]
    public LayerMask focusMask;

    [Header("Pan")]
    public PanSpace panSpace = PanSpace.CameraPlane;
    public bool panKeyboard = true;           // WASD / arrows
    public bool panRMB = true;                // mouse delta while RMB held
    public float kbSpeed = 1.5f;              // units/sec @ 1m
    public float mouseScale = 0.002f;         // units per pixel per meter
    public float maxPanStepPerSec = 0.35f;    // clamp per-frame world move
    public bool invertX = false;
    public bool invertY = true;               // natural “grab scene” feel

    [Header("Zoom steer toward cursor")]
    public bool steerOnScroll = true;
    public float zoomInBias = 0.35f;          // pull toward cursor on wheel up
    public float zoomOutBias = 0.15f;         // drift toward center on wheel down
    public float scrollDeadzone = 0.01f;
    public float quietAfterPan = 0.6f;        // block zoom steer briefly after pan

    [Header("Focus lock/follow")]
    public float centerWindow = 0.18f;        // snap window (viewport distance)
    public float releaseWindow = 0.40f;       // release if target drifts this far
    public float snapDwell = 0.35f;           // dwell to confirm intent
    public float resnapCooldown = 0.50f;

    [Header("Smoothing / anti-jitter")]
    public float followSmoothTime = 0.10f;    // SmoothDamp time for focusTarget
    public float handoffBlendTime = 0.15f;    // extra smoothing just after switching focus
    public float targetLowpassRate = 20f;     // low-pass rate on focused target motion

    [Header("Double-click RMB recenter")]
    public float doubleClickTime = 0.28f;
    public float doubleClickMaxMove = 8f;
    public float holdAsDragTime = 0.15f;
    public float recenterBoostTime = 0.25f;   // temporary faster follow after recenter
    public float recenterBoostSmooth = 0.07f; // temporarily smaller SmoothDamp time

    // --- internals ---
    Transform _focused;
    Vector3 _goal;                 // where we want focusTarget to go
    Vector3 _vel;                  // SmoothDamp velocity
    float _quietTimer, _cooldown, _dwell, _handoffT, _recenterT;

    // double-click state
    double _downStart, _lastDown;
    Vector2 _downPos;
    bool _dragging;
    int _clickCount;

    void Awake()
    {
        if (focusTarget) _goal = focusTarget.position;
    }

    void Update()
    {
        if (!mainCam || !focusTarget) return;

        HandleRmbClickDouble();      // recenter on double-click (ignores drags)
        HandleZoomSteer();           // steer goal when scrolling (toward cursor)
        HandlePan();                 // horizontal + vertical pan (RMB / keyboard)
        HandleFocusLock();           // snap/unsnap & compute goal while focused

        // --- SmoothDamp toward goal (low-jitter) ---
        float smooth = followSmoothTime;
        if (_recenterT > 0f) smooth = recenterBoostSmooth;
        if (_handoffT > 0f) smooth = Mathf.Min(smooth, Mathf.Lerp(followSmoothTime * 0.5f, followSmoothTime, 1f - _handoffT));

        focusTarget.position = Vector3.SmoothDamp(focusTarget.position, _goal, ref _vel, Mathf.Max(0.0001f, smooth));

        // timers
        if (_quietTimer > 0f) _quietTimer = Mathf.Max(0f, _quietTimer - Time.deltaTime);
        if (_cooldown > 0f) _cooldown = Mathf.Max(0f, _cooldown - Time.deltaTime);
        if (_handoffT > 0f) _handoffT = Mathf.Max(0f, _handoffT - Time.deltaTime);
        if (_recenterT > 0f) _recenterT = Mathf.Max(0f, _recenterT - Time.deltaTime);
    }

    // ---------- Pan ----------
    void HandlePan()
    {
        float dx = 0f, dy = 0f;

        // Keyboard
        if (panKeyboard)
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) dx -= kbSpeed;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dx += kbSpeed;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) dy -= kbSpeed;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) dy += kbSpeed;
            }
        }

        // RMB mouse deltas
        var mouse = Mouse.current;
        if (panRMB && mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 m = mouse.delta.ReadValue();
            float dist = Vector3.Distance(mainCam.transform.position, focusTarget.position);
            float scale = mouseScale * dist;
            dx += (invertX ? -1f : 1f) * m.x * scale;
            dy += (invertY ? -1f : 1f) * m.y * scale;
        }

        if (Mathf.Abs(dx) > 0.0001f || Mathf.Abs(dy) > 0.0001f)
        {
            _focused = null;              // manual input releases focus
            _cooldown = resnapCooldown;   // block re-snap briefly
            _quietTimer = quietAfterPan;  // block zoom-steer briefly

            Vector3 right, up;
            if (panSpace == PanSpace.CameraPlane)
            {
                right = mainCam.transform.right;
                up = mainCam.transform.up;      // vertical on screen
            }
            else // WorldXZ: horizontal = world X, vertical = world Y
            {
                right = Vector3.right;
                up = Vector3.up;
            }

            Vector3 worldDelta = (right * dx + up * dy) * Time.deltaTime;

            // cap per-frame movement to avoid “hard” jumps
            float maxStep = maxPanStepPerSec * Time.deltaTime;
            if (worldDelta.magnitude > maxStep) worldDelta = worldDelta.normalized * maxStep;

            _goal += worldDelta;
            ClampToTank(ref _goal);
        }
    }

    // ---------- Zoom steer toward cursor ----------
    void HandleZoomSteer()
    {
        if (!steerOnScroll || Mouse.current == null) return;
        float raw = Mathf.Clamp(Mouse.current.scroll.ReadValue().y, -120f, 120f) / 120f;
        if (Mathf.Abs(raw) <= scrollDeadzone) return;
        if (_quietTimer > 0f) return;

        bool zoomingIn = raw > 0f;

        if (zoomingIn)
        {
            Ray ray = mainCam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 500f, focusMask))
            {
                _goal = Vector3.Lerp(_goal, hit.point, zoomInBias);
                ClampToTank(ref _goal);
            }
            else if (pivotCenter)
            {
                _goal = Vector3.Lerp(_goal, pivotCenter.position, zoomInBias * 0.5f);
                ClampToTank(ref _goal);
            }
        }
        else
        {
            if (pivotCenter)
            {
                _goal = Vector3.Lerp(_goal, pivotCenter.position, zoomOutBias);
                ClampToTank(ref _goal);
            }
        }
    }

    // ---------- Focus lock / follow ----------
    void HandleFocusLock()
    {
        // try to acquire when zooming in (or always; change here if desired)
        bool tryingToSnap = (_cooldown <= 0f && _quietTimer <= 0f);

        if (_focused == null && tryingToSnap)
        {
            var cand = FindCenteredFocusable(centerWindow);
            if (cand != null)
            {
                _dwell += Time.deltaTime;
                if (_dwell >= snapDwell)
                {
                    _focused = cand.transform;
                    _dwell = 0f;
                    _handoffT = handoffBlendTime;    // brief extra smoothing
                    _vel = Vector3.zero;             // reset damp velocity
                }
            }
            else _dwell = 0f;
        }
        else if (_focused != null)
        {
            // low-pass filter the moving target (reduces jittery fish motion)
            Vector3 targetPos = _focused.position;
            var ft = _focused.GetComponent<FocusTarget>();
            if (ft) targetPos = _focused.TransformPoint(ft.focusOffset);

            float lpK = 1f - Mathf.Exp(-targetLowpassRate * Time.deltaTime);
            Vector3 filtered = Vector3.Lerp(_goal, targetPos, lpK);

            _goal = filtered;
            ClampToTank(ref _goal);

            // release conditions
            if (TooFarFromCenter(_focused.position, releaseWindow))
                ReleaseFocus();
        }
    }

    void ReleaseFocus()
    {
        _focused = null;
        _dwell = 0f;
        _cooldown = resnapCooldown;
        _handoffT = handoffBlendTime;
        // keep current _goal so there’s no snap-back
    }

    // ---------- Double-click RMB recenter ----------
    void HandleRmbClickDouble()
    {
        var mouse = Mouse.current; if (mouse == null) return;

        if (mouse.rightButton.wasPressedThisFrame)
        {
            _downStart = Time.timeAsDouble;
            _downPos = mouse.position.ReadValue();
            _dragging = false;
        }

        if (mouse.rightButton.isPressed)
        {
            Vector2 cur = mouse.position.ReadValue();
            if (Time.timeAsDouble - _downStart > holdAsDragTime ||
                (cur - _downPos).sqrMagnitude > (doubleClickMaxMove * doubleClickMaxMove))
            {
                _dragging = true;
            }
        }

        if (mouse.rightButton.wasReleasedThisFrame)
        {
            if (_dragging) { _clickCount = 0; return; }

            Vector2 upPos = mouse.position.ReadValue();
            if ((upPos - _downPos).sqrMagnitude > (doubleClickMaxMove * doubleClickMaxMove))
            {
                _clickCount = 0; return;
            }

            double now = Time.timeAsDouble;
            _clickCount = (now - _lastDown <= doubleClickTime) ? _clickCount + 1 : 1;
            _lastDown = now;

            if (_clickCount >= 2 && pivotCenter != null)
            {
                _focused = null;
                _goal = pivotCenter.position;
                ClampToTank(ref _goal);
                _recenterT = recenterBoostTime;
                _vel = Vector3.zero;
                _clickCount = 0;
            }
        }
    }

    // ---------- Helpers ----------
    FocusTarget FindCenteredFocusable(float window)
    {
        // Ray from mouse position; use center of screen instead by swapping to Screen.width*0.5f etc.
        Vector2 pos = (Mouse.current != null) ? Mouse.current.position.ReadValue() : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Ray ray = mainCam.ScreenPointToRay(pos);
        if (Physics.Raycast(ray, out var hit, 500f, focusMask))
        {
            if (hit.transform.TryGetComponent(out FocusTarget ft))
            {
                Vector3 vp = mainCam.WorldToViewportPoint(hit.transform.position);
                float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
                if (vp.z > 0f && off <= window) return ft;
            }
        }
        return null;
    }

    bool TooFarFromCenter(Vector3 worldPos, float window)
    {
        Vector3 vp = mainCam.WorldToViewportPoint(worldPos);
        if (vp.z < 0f) return true;
        float off = Vector2.Distance(new Vector2(vp.x, vp.y), new Vector2(0.5f, 0.5f));
        return off > window;
    }

    void ClampToTank(ref Vector3 p)
    {
        if (!tankBounds) return;
        Bounds b = tankBounds.bounds;
        p = new Vector3(
            Mathf.Clamp(p.x, b.min.x, b.max.x),
            Mathf.Clamp(p.y, b.min.y, b.max.y),
            Mathf.Clamp(p.z, b.min.z, b.max.z)
        );
    }
}
