using UnityEngine;
using UnityEngine.InputSystem;

// Two ways to be in this world.
//
// BUILD is the fly camera and the tools — the view that draws a castle.
// WALK stands you inside the thing you drew, on your own feet, which is
// the only way to find out whether the gate is too narrow, the hall too
// mean, or the stair too steep to want to climb twice. Everything so far
// has been about making the shapes; this is the first thing that judges
// them.
//
// One static flag, read by FreeFlyCamera, GridPlacementSystem and
// BuildMenu, rather than enabling and disabling their components: each of
// them holds state a frozen Update would strand — a half-drawn gesture, a
// ghost left standing in the air, a cutaway still slicing colliders — so
// they stand DOWN deliberately instead of merely stopping.
//
// The walker IS the camera. A CharacterController lives on the same
// object with its capsule hung below the lens (centre −0.8 of an 1.8m
// body), so the camera reads as a pair of eyes 1.7m over the feet. That
// spares the scene a player rig and spares the camera a reparenting on
// every toggle.
//
// Its keys are its own, deliberately NOT in GridPlacementSystem.
// ModifierHints: that panel reports what the tool IN HAND reads, and this
// is a mode switch like the cutaway, so it carries its own readout.
public class WalkMode : MonoBehaviour
{
    public static bool Active { get; private set; }

    [Header("Walking")]
    // Brisk, not Olympic. The whole point of the mode is to feel how big
    // the place is, and a sprint makes every courtyard small.
    public float walkSpeed = 2.8f;
    public float runSpeed = 6f;
    public float eyeHeight = 1.7f;
    public float bodyHeight = 1.8f;
    // Wide enough that the lens can never reach a wall's face and clip
    // through it, narrow enough to walk a 1.1m doorway.
    public float bodyRadius = 0.35f;
    // A foundation base steps 0.5m and a stair rises 0.22m, so the step
    // offset has to clear the former or you can't walk your own terracing.
    public float stepUp = 0.55f;
    public float slopeLimit = 55f;
    public float gravity = -18f;
    // Enough to climb out of masonry that closed around you — there are
    // no structural rules yet, so a wall can be buried in a wall — and
    // nowhere near enough to vault one.
    public float jumpSpeed = 4f;

    [Header("Look")]
    public float lookSensitivity = 0.15f;

    private CharacterController controller;
    private FreeFlyCamera fly;

    private float yaw;
    private float pitch;
    private float fallSpeed;
    // Whether the mouse is ours right now. False once the Game view loses
    // focus (Escape does that), which the readout turns into a "click to
    // capture" prompt — a click is the only thing that gives it back.
    public bool CursorHeld { get; private set; }
    // Frames of look to throw away after taking the cursor parks it.
    private int skipLookFrames;

    // The last place the walker had ground under it. Walking off the
    // 800m terrain edge is a fall with no floor at the bottom of it, and
    // "I fell forever and had to leave play mode" is not a playtest.
    private Vector3 lastFooting;

    private GUIStyle readoutStyle;

    void Awake()
    {
        fly = GetComponent<FreeFlyCamera>();

        // Added at runtime rather than authored into the scene: build
        // mode wants no capsule in the world at all, and a component the
        // scene never stored can't drift out of step with these fields.
        controller = GetComponent<CharacterController>();
        if (controller == null)
            controller = gameObject.AddComponent<CharacterController>();
        controller.height = bodyHeight;
        controller.radius = bodyRadius;
        controller.center = new Vector3(0f, bodyHeight * 0.5f - eyeHeight, 0f);
        controller.stepOffset = stepUp;
        controller.slopeLimit = slopeLimit;
        controller.skinWidth = 0.04f;
        // The default 0.001m dead zone reads as a stutter at walking pace.
        controller.minMoveDistance = 0f;
        controller.enabled = false;
    }

    // Leaving play mode (or unloading the scene) mid-walk must not leave
    // the flag set for whoever reads it next.
    void OnDisable()
    {
        if (Active)
            Leave();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null)
            return;

        // Tab and nothing else. Escape used to exit too, and that was a
        // mistake: Escape is how the Unity editor takes the cursor BACK,
        // and pressing it drops the Game view's focus. An unfocused Game
        // view has no cursor state applied to it at all — neither the
        // lock nor the hide — so leaving by Escape and returning by Tab
        // landed you in walk mode with a loose pointer every time. The
        // key belongs to the editor; don't take a second meaning for it.
        // While the Saves panel has the keyboard, Tab is a keystroke in
        // someone's typing, not a mode switch.
        if (keyboard.tabKey.wasPressedThisFrame && !BuildMenu.ModalOpen)
        {
            if (Active)
                Leave();
            else
                Enter();
            return;
        }

        if (!Active)
            return;

        // Focus is the only thing that can stop the mouse working, and
        // only a click gives an unfocused Game view its focus back — so a
        // click is also the cue to re-take the cursor, and the readout
        // asks for one in as many words. Nothing is done to the pointer
        // while the focus is away: reaching out and grabbing someone's
        // cursor while they are using the Console is not this
        // component's business.
        bool held = Application.isFocused;
        if (held && !CursorHeld)
            LockCursor(true);
        else if (!held && CursorHeld)
            Cursor.visible = true;
        CursorHeld = held;
        if (held && mouse != null && mouse.leftButton.wasPressedThisFrame)
            LockCursor(true);

        Look(mouse);
        Step(keyboard);
    }

    void Enter()
    {
        Active = true;

        // The fly camera's yaw is kept — you turn up facing whatever you
        // were looking at — but the pitch is stood upright. Arriving from
        // a bird's-eye view already staring at your own boots is no way
        // to start a walk.
        yaw = transform.eulerAngles.y;
        pitch = 0f;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // You land under the camera, wherever the camera was: in the
        // courtyard if you were looking down into it, on the roof if you
        // were hanging over the roof. Predictable beats clever — dropping
        // along the view ray would put you somewhere you weren't pointing
        // every time the camera happened to be level.
        if (TryGroundUnder(transform.position, out float groundY))
        {
            Vector3 p = transform.position;
            transform.position = new Vector3(p.x, groundY + eyeHeight + 0.05f, p.z);
        }

        fallSpeed = 0f;
        lastFooting = transform.position;

        // The controller is enabled AFTER the teleport: it owns the
        // transform while it's on, and moving a live one is how you end
        // up inside the floor.
        Physics.SyncTransforms();
        controller.enabled = true;
        LockCursor(true);
    }

    void Leave()
    {
        Active = false;
        if (controller != null)
            controller.enabled = false;
        LockCursor(false);

        // Build mode picks up exactly where the walk ended, so noticing a
        // wall is wrong and reaching for the tool to fix it happen in the
        // same place. The fly camera keeps yaw and pitch in its own
        // fields, so it has to be told what the walk did to them.
        if (fly != null)
            fly.SyncOrientation();
    }

    // The ordinary way: a locked cursor and the Input System's own mouse
    // delta. No warping, no parking the pointer by hand, no measuring
    // positions against the centre of the screen.
    //
    // Everything clever tried here made it worse. Warping the pointer and
    // ALSO reading delta double-counted and dragged the view; warping and
    // reading position instead of delta traded that for a systematic
    // round-trip error and spun the camera into a blur. The genuine bugs
    // turned out to be elsewhere and are fixed elsewhere — Escape
    // dropping the Game view's focus, and nothing telling you the mouse
    // had been let go. This part was only ever standard.
    void Look(Mouse mouse)
    {
        if (mouse == null || !CursorHeld)
            return;

        Vector2 delta = mouse.delta.ReadValue();

        // Taking the cursor parks it, and the Input System reports that
        // parking as one large delta. Not the player's hand.
        if (skipLookFrames > 0)
        {
            skipLookFrames--;
            return;
        }

        yaw += delta.x * lookSensitivity;
        pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, -89f, 89f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void Step(Keyboard keyboard)
    {
        // Movement is yaw-only: looking up at a parapet doesn't walk you
        // into the sky. Same rule the fly camera uses.
        Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
        Vector3 wish = Vector3.zero;
        if (keyboard.wKey.isPressed) wish += flat * Vector3.forward;
        if (keyboard.sKey.isPressed) wish -= flat * Vector3.forward;
        if (keyboard.dKey.isPressed) wish += flat * Vector3.right;
        if (keyboard.aKey.isPressed) wish -= flat * Vector3.right;
        wish = Vector3.ClampMagnitude(wish, 1f);

        float speed = keyboard.leftShiftKey.isPressed ? runSpeed : walkSpeed;

        if (controller.isGrounded)
        {
            lastFooting = transform.position;
            // Held at a small downward press rather than zero: a grounded
            // controller with no downward velocity skips off the nose of
            // every stair tread and every terrain break instead of
            // following them down.
            fallSpeed = -2f;
            if (keyboard.spaceKey.wasPressedThisFrame)
                fallSpeed = jumpSpeed;
        }
        else
        {
            fallSpeed += gravity * Time.deltaTime;
        }

        controller.Move((wish * speed + Vector3.up * fallSpeed) * Time.deltaTime);

        // Past the terrain edge there is nothing below to land on, so the
        // fall never ends. Put them back where they last had footing.
        if (transform.position.y < lastFooting.y - 200f)
        {
            controller.enabled = false;
            transform.position = lastFooting + Vector3.up * 0.1f;
            fallSpeed = 0f;
            Physics.SyncTransforms();
            controller.enabled = true;
        }
    }

    // The surface under a point: masonry, deck, stair or terrain,
    // whichever comes first. Started a little above the point so a camera
    // resting exactly on a surface doesn't begin its ray inside it.
    static bool TryGroundUnder(Vector3 from, out float y)
    {
        if (Physics.Raycast(from + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit,
                1000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            y = hit.point.y;
            return true;
        }

        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null)
        {
            y = terrain.transform.position.y + terrain.SampleHeight(from);
            return true;
        }

        y = 0f;
        return false;
    }

    void LockCursor(bool locked)
    {
        if (locked)
        {
            // Through None first. Unity applies the lock only when the
            // state CHANGES to Locked — assigning Locked while it already
            // believes it is Locked does nothing, which is exactly when
            // it needs re-applying.
            Cursor.lockState = CursorLockMode.None;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            skipLookFrames = 2;
            return;
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    // IMGUI for the same reason the guides use it: this is a readout, not
    // scene art, and it renders identically under any pipeline.
    void OnGUI()
    {
        if (readoutStyle == null)
        {
            readoutStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };
        }

        // When the editor has taken the mouse back there is nothing to do
        // but ask for it: no cursor state we set applies to a Game view
        // that hasn't got focus. Saying so beats letting it be discovered
        // as a view that won't turn past the edge of the screen.
        string line = !Active ? "Tab — walk"
            : CursorHeld
                ? "Walk — WASD, Shift run, Space hop, mouse look  ·  Tab to build"
                : "Click in the window to capture the mouse  ·  Tab to build";

        // Top centre, because the BuildLog owns the left margin and the
        // modifier panel the right. The cutaway's readout claimed this
        // spot first, so in build mode this line sits under it when both
        // are up; walking clears the cut, so they never collide there.
        float y = !Active && CutawayView.Active ? 36f : 12f;
        var rect = new Rect((Screen.width - 560f) * 0.5f, y, 560f, 22f);

        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), line, readoutStyle);
        GUI.color = Active ? new Color(0.72f, 0.9f, 1f, 1f) : new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(rect, line, readoutStyle);
        GUI.color = Color.white;

    }
}
