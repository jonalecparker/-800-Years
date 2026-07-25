using UnityEngine;
using UnityEngine.InputSystem;

public class FreeFlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 6f;
    public float boostMultiplier = 3f;
    public float unitsPerScrollStep = 1f;

    [Header("Look")]
    public float lookSensitivity = 0.15f;

    [Header("Orbit")]
    public float orbitSensitivity = 0.3f;
    public float defaultOrbitDistance = 10f;
    public float orbitDragThresholdPixels = 4f;

    // True once a right-click-drag has crossed the threshold and is actively
    // orbiting. GridPlacementSystem reads this to tell an orbit drag apart
    // from a plain right-click (which removes a placed piece).
    public static bool IsOrbiting { get; private set; }
    // Set by GridPlacementSystem when a right-click starts on a placed
    // piece, claiming the drag for deletion instead of letting it become an
    // orbit — the two features share the same button and can't both have it.
    public static bool SuppressOrbit { get; set; }

    private float yaw;
    private float pitch;

    private bool rightButtonCandidate;
    private Vector2 rightPressScreenPos;
    private Vector3 orbitPivot;
    private float orbitDistance;

    void Start()
    {
        Vector3 angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = angles.x;
    }

    void Update()
    {
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null || keyboard == null)
            return;

        HandleOrbit(mouse);

        // Free look while holding middle mouse — skipped mid-orbit.
        if (!IsOrbiting && mouse.middleButton.isPressed)
        {
            Vector2 lookDelta = mouse.delta.ReadValue();
            yaw += lookDelta.x * lookSensitivity;
            pitch -= lookDelta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        HandleMove(keyboard, mouse);
    }

void HandleOrbit(Mouse mouse)
    {
        if (mouse.rightButton.wasPressedThisFrame)
        {
            rightButtonCandidate = true;
            rightPressScreenPos = mouse.position.ReadValue();
            IsOrbiting = false;
        }

        if (!mouse.rightButton.isPressed || !rightButtonCandidate)
        {
            if (!mouse.rightButton.isPressed)
            {
                rightButtonCandidate = false;
                SuppressOrbit = false;
            }
            return;
        }

        if (!IsOrbiting)
        {
            // GridPlacementSystem claims this drag for piece deletion when it
            // started on a placed piece — don't hijack it into an orbit too.
            if (SuppressOrbit)
                return;

            Vector2 currentPos = mouse.position.ReadValue();
            if ((currentPos - rightPressScreenPos).sqrMagnitude < orbitDragThresholdPixels * orbitDragThresholdPixels)
                return;

            // Crossed the drag threshold — start orbiting. Distance/pivot are
            // fixed for the rest of the drag, like Scene View's Alt+LMB orbit.
            IsOrbiting = true;
            orbitDistance = defaultOrbitDistance;
            if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, 500f))
                orbitDistance = hit.distance;
            orbitPivot = transform.position + transform.forward * orbitDistance;
        }

        Vector2 delta = mouse.delta.ReadValue();
        yaw += delta.x * orbitSensitivity;
        pitch -= delta.y * orbitSensitivity;
        pitch = Mathf.Clamp(pitch, -89f, 89f);

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = rot;
        transform.position = orbitPivot - rot * Vector3.forward * orbitDistance;
    }

    void HandleMove(Keyboard keyboard, Mouse mouse)
    {
        // Movement stays horizontal (yaw-only) regardless of camera pitch —
        // looking up/down doesn't tilt WASD movement into the sky/ground.
        Quaternion flatYaw = Quaternion.Euler(0f, yaw, 0f);
        Vector3 flatForward = flatYaw * Vector3.forward;
        Vector3 flatRight = flatYaw * Vector3.right;

        Vector3 move = Vector3.zero;
        if (keyboard.wKey.isPressed) move += flatForward;
        if (keyboard.sKey.isPressed) move -= flatForward;
        if (keyboard.aKey.isPressed) move -= flatRight;
        if (keyboard.dKey.isPressed) move += flatRight;

        float speed = moveSpeed * (keyboard.leftShiftKey.isPressed ? boostMultiplier : 1f);
        transform.position += move.normalized * speed * Time.deltaTime;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            transform.position += transform.forward * Mathf.Sign(scroll) * unitsPerScrollStep;
    }
}
