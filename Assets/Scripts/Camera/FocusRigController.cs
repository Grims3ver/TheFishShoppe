using UnityEngine;
using UnityEngine.InputSystem;

/// Pan-only controller for a Cinemachine vCam focus point (horizontal only).
public class FocusPanController : MonoBehaviour
{
    public enum PanSpace { CameraRight, WorldX }

    [Header("Refs")]
    public Camera mainCam;
    public Transform focusTarget;     // vCam Tracking Target
    public Transform pivotCenter;     // Double-click recenter target
    public Collider tankBounds;       // Clamp area (BoxCollider)

    [Header("Pan")]
    public PanSpace panSpace = PanSpace.CameraRight;
    public bool panKeyboard = true;         // A/D or arrows
    public bool panRMB = true;              // Mouse delta while RMB held
    public float kbSpeed = 1.6f;            // units/sec @ 1m
    public float mouseScale = 0.012f;       // units per pixel per meter
    public float maxPanSpeedPerM = 2.0f;    // units/sec per meter
    public bool invertX = false;

    [Header("Smoothing")]
    public float panAccelTime = 0.06f;      // ramp up
    public float panDecelTime = 0.12f;      // ramp down
    public float mouseLowpassRate = 28f;    // EMA on mouse delta
    public float followSmoothTime = 0.065f; // position smoothing (lower = less lag)

    [Header("Constraints")]
    public bool lockY = true;

    [Header("Double-click RMB recenter")]
    public float doubleClickTime = 0.28f;
    public float doubleClickMaxMove = 8f;
    public float holdAsDragTime = 0.15f;

    // --- internals ---
    Vector3 _goal, _posVel;           // SmoothDamp position velocity (world)
    Vector2 _mouseEma;                // filtered mouse delta
    float _vx, _vxRef;                // horizontal pan speed + its SmoothDamp ref (separate!)
    float _yAnchor;

    // double-click state
    double _downStart, _lastDown;
    Vector2 _downPos;
    bool _dragging;
    int _clickCount;

    const float kInputDeadzone = 0.0005f;   // shuts off tiny drift

    void Awake()
    {
        if (focusTarget)
        {
            _goal = focusTarget.position;
            _yAnchor = _goal.y;
        }
    }

    void Update()
    {
        if (!mainCam || !focusTarget) return;

        HandleRmbDoubleClick();
        HandlePan();
        ApplyPositionSmoothing();
    }

    void HandlePan()
    {
        // Desired horizontal speed (units/sec)
        float dx = 0f;

        // Keyboard A/D
        if (panKeyboard)
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) dx -= kbSpeed;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dx += kbSpeed;
            }
        }

        // Mouse X while RMB held ? speed (EMA filtered)
        var mouse = Mouse.current;
        if (panRMB && mouse != null)
        {
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _mouseEma = Vector2.zero; // reset filter so we don't inherit old deltas
            }

            if (mouse.rightButton.isPressed)
            {
                Vector2 raw = mouse.delta.ReadValue(); // px/frame
                float lp = 1f - Mathf.Exp(-mouseLowpassRate * Time.deltaTime);
                _mouseEma = Vector2.Lerp(_mouseEma, raw, lp);

                float dist = Vector3.Distance(mainCam.transform.position, focusTarget.position);
                float sx = (invertX ? -1f : 1f) * _mouseEma.x * mouseScale * dist; // units/sec
                dx += sx;
            }
            else if (mouse.rightButton.wasReleasedThisFrame)
            {
                _mouseEma = Vector2.zero; // ensure no tail ? prevents post-release creep
            }
        }

        // Cap desired speed based on distance (consistent feel)
        float distToTarget = Mathf.Max(0.1f, Vector3.Distance(mainCam.transform.position, focusTarget.position));
        float maxSpeed = maxPanSpeedPerM * distToTarget;
        float targetVx = Mathf.Clamp(dx, -maxSpeed, maxSpeed);

        // SmoothDamp speed to target (separate ref var ? no coupling with position)
        float t = (Mathf.Abs(targetVx) > kInputDeadzone) ? panAccelTime : panDecelTime;
        _vx = Mathf.SmoothDamp(_vx, targetVx, ref _vxRef, Mathf.Max(0.0001f, t));

        // Hard zero near rest to kill infinitesimal drift
        if (Mathf.Abs(targetVx) <= kInputDeadzone && Mathf.Abs(_vx) < 1e-4f)
            _vx = 0f;

        if (_vx != 0f)
        {
            Vector3 right = (panSpace == PanSpace.CameraRight) ? mainCam.transform.right : Vector3.right;
            _goal += right.normalized * (_vx * Time.deltaTime);
            if (lockY) { var g = _goal; g.y = _yAnchor; _goal = g; }
            ClampToTank(ref _goal);
        }
    }

    void ApplyPositionSmoothing()
    {
        // Position SmoothDamp (independent from speed smoothing)
        float st = Mathf.Max(0.0001f, followSmoothTime);
        focusTarget.position = Vector3.SmoothDamp(focusTarget.position, _goal, ref _posVel, st);
    }

    void HandleRmbDoubleClick()
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
            { _clickCount = 0; return; }

            double now = Time.timeAsDouble;
            _clickCount = (now - _lastDown <= doubleClickTime) ? _clickCount + 1 : 1;
            _lastDown = now;

            if (_clickCount >= 2 && pivotCenter != null)
            {
                Vector3 p = pivotCenter.position;
                if (lockY) p.y = _yAnchor;
                _goal = p;
                ClampToTank(ref _goal);

                // Stop any residual motion instantly
                _vx = 0f; _vxRef = 0f;
                _posVel = Vector3.zero;
                _mouseEma = Vector2.zero;

                _clickCount = 0;
            }
        }
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