using UnityEngine;
using UnityEngine.InputSystem;

public class FocusPointSlider : MonoBehaviour
{
    public enum HorizontalMode { CameraRight, WorldX }

    [Header("Refs")]
    public Camera mainCam;
    public Transform focusTarget; //vCam's Tracking Target
    public Transform pivotCenter;  //where "recenter" should go (tank center)
    public Collider tankBounds;

    [Header("Controls")]
    public HorizontalMode mode = HorizontalMode.CameraRight;
    public bool useKeyboard = true;
    public bool useMouseRMB = true;
    public float keyboardSpeed = 1.5f; //units/sec at 1m
    public float mouseScale = 0.002f; //units per pixel per meter
    public bool invert = false;

    [Header("Smoothing")]
    public float maxStepPerSec = 0.35f;
    public float followRate = 12f; //smooth

    [Header("Double-click Recenter")]
    public float doubleClickTime; // seconds between clicks
    public float doubleClickMaxMove; //max cursor px between clicks
    public float holdAsDragTime; //if RMB held longer, treat as drag (not click)
    public float recenterBoostRate; //temporarily speed up smoothing on recenter
    public float recenterBoostDuration;

    //double click stuff
    Vector3 _goal;
    private float _recenterBoostT;
    private int _clickCount;
    private double _lastDownTime;
    private Vector2 _lastDownPos;
    private double _downStartTime;
    private bool _dragging;

    void Awake()
    {
        if (focusTarget) _goal = focusTarget.position;
    }

    void Update()
    {
        if (!mainCam || !focusTarget) return;

        HandleRightMouseClickLogic();   //detects double-click and sets _goal to pivot

        float inputX = 0f;

        //Keyboard A/D or arrows
        if (useKeyboard)
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) inputX -= keyboardSpeed;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) inputX += keyboardSpeed;
            }
        }

        //Mouse delta X while RMB held (pans focus point horizontally)
        if (useMouseRMB && Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            if (Mathf.Abs(delta.x) > Mathf.Epsilon)
            {
                float dist = Vector3.Distance(mainCam.transform.position, focusTarget.position);
                float scale = mouseScale * dist * (invert ? -1f : 1f);
                inputX += delta.x * scale;
            }
        }

        //Convert scalar input to world move
        if (Mathf.Abs(inputX) > 0.0001f)
        {
            Vector3 axis = (mode == HorizontalMode.CameraRight) ? mainCam.transform.right : Vector3.right;
            Vector3 worldDelta = axis.normalized * inputX * Time.deltaTime;

            float maxStep = maxStepPerSec * Time.deltaTime;
            if (worldDelta.magnitude > maxStep) worldDelta = worldDelta.normalized * maxStep;

            _goal += worldDelta;
            ClampToTank(ref _goal);
        }

        //Smoothly move focus target; boost briefly after recenter
        float rate = (_recenterBoostT > 0f) ? recenterBoostRate : followRate;
        float k = 1f - Mathf.Exp(-rate * Time.deltaTime);
        focusTarget.position = Vector3.Lerp(focusTarget.position, _goal, k);

        if (_recenterBoostT > 0f)
        {
            _recenterBoostT -= Time.deltaTime;
            if (_recenterBoostT < 0f) _recenterBoostT = 0f;
        }
    }

    void HandleRightMouseClickLogic()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        //RMB down: start click/drag measurement
        if (mouse.rightButton.wasPressedThisFrame)
        {
            _downStartTime = Time.timeAsDouble;
            _lastDownPos = mouse.position.ReadValue();
            _dragging = false;
        }

        //During hold: detect drag if held long enough or moved far
        if (mouse.rightButton.isPressed)
        {
            Vector2 cur = mouse.position.ReadValue();
            if (Time.timeAsDouble - _downStartTime > holdAsDragTime ||
                (cur - _lastDownPos).sqrMagnitude > (doubleClickMaxMove * doubleClickMaxMove))
            {
                _dragging = true;
            }
        }

        //RMB up: evaluate click/double-click (ignore if dragging)
        if (mouse.rightButton.wasReleasedThisFrame)
        {
            if (_dragging) { _clickCount = 0; return; }

            double now = Time.timeAsDouble;
            Vector2 upPos = mouse.position.ReadValue();

            //too much movement between down and up? treat as drag
            if ((upPos - _lastDownPos).sqrMagnitude > (doubleClickMaxMove * doubleClickMaxMove))
            {
                _clickCount = 0;
                return;
            }

            //click accepted
            if (now - _lastDownTime <= doubleClickTime)
            {
                _clickCount++;
            }
            else
            {
                _clickCount = 1;
            }
            _lastDownTime = now;

            if (_clickCount >= 2)
            {
                //Double-click detected ? recenter
                if (pivotCenter != null)
                {
                    _goal = pivotCenter.position;
                    ClampToTank(ref _goal);
                    _recenterBoostT = recenterBoostDuration; // make the recenter feel snappy
                }
                _clickCount = 0; //reset
            }
        }
    }

    //clamp to bounds
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
}