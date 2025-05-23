using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;

[RequireComponent(typeof(LineRenderer), typeof(PlayerInput))]
public class PlayerBombHandler : NetworkBehaviour
{
    // ───────────────────────── Fields ────────────────────────────
    [Header("Trajectory")]
    [SerializeField, Range(10,300)] int   maxPoints = 100;
    [SerializeField] LayerMask collisionMask;

    Bomb         currentBomb;
    public Bomb CurrentBomb => currentBomb;
    LineRenderer lr;
    bool         isAiming;
    List<Vector3> points = new();
    float        timeStep;

    // Input
    InputAction swapAct, throwAct;
    PlayerInput playerInput;
    bool inputSubscribed;

    // Animation
    private PlayerAnimator playerAnimator;
    private Animator animator;

    // ───────────────────────── Unity ─────────────────────────────
    void Awake()
    {
        lr = GetComponent<LineRenderer>();
        lr.positionCount = 0;
        lr.startWidth = lr.endWidth = 0.1f;
        lr.material = new Material(Shader.Find("Sprites/Default"));

        playerInput = GetComponent<PlayerInput>();
        playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;

        swapAct  = playerInput.actions["SwapBomb"];
        throwAct = playerInput.actions["Throw"];
        Debug.Log($"Awake: throwAct bound to {throwAct?.name}, controls: {string.Join(", ", throwAct?.controls.ToArray().Select(c => c.name))}", this);

        // Get animation components
        playerAnimator = GetComponent<PlayerAnimator>();
        animator = GetComponent<Animator>();

        timeStep = Time.fixedDeltaTime;
    }

    void Start()
    {
        StartCoroutine(SubscribeToInput());
    }

    IEnumerator SubscribeToInput()
    {
        // Wait until isLocalPlayer is stable
        while (!isLocalPlayer)
        {
            Debug.Log($"SubscribeToInput: Waiting for isLocalPlayer to be true for {gameObject.name}", this);
            yield return new WaitForSeconds(0.1f);
        }

        if (inputSubscribed)
        {
            Debug.Log($"SubscribeToInput: Input already subscribed for {gameObject.name}", this);
            yield break;
        }

        swapAct.performed += Swap;
        throwAct.started  += StartAim;
        throwAct.canceled += ReleaseThrow;
        inputSubscribed = true;

        Debug.Log($"SubscribeToInput: Input subscribed for {gameObject.name}, isLocalPlayer={isLocalPlayer}, PlayerInput enabled={playerInput.enabled}", this);
    }

    void OnDisable()
    {
        if (!isLocalPlayer || !inputSubscribed) return;
        swapAct.performed -= Swap;
        throwAct.started  -= StartAim;
        throwAct.canceled -= ReleaseThrow;
        inputSubscribed = false;
        
        Debug.Log($"PlayerBombHandler disabled for {gameObject.name}, input unsubscribed", this);
    }

    void Update()
    {
        if (!isLocalPlayer)
        {
            Debug.LogWarning($"Update: Not local player for {gameObject.name}", this);
            return;
        }
        
        Debug.Log($"Update: isLocalPlayer={isLocalPlayer}, currentBomb={currentBomb}, isAiming={isAiming}", this);

        // Fallback manual input check for debugging
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            Debug.Log("Manual check: Space key pressed", this);
            StartAim(new InputAction.CallbackContext());
        }
        if (Keyboard.current.spaceKey.wasReleasedThisFrame)
        {
            Debug.Log("Manual check: Space key released", this);
            ReleaseThrow(new InputAction.CallbackContext());
        }
        
        // Automatically hide trajectory if no bomb is held while aiming
        if (currentBomb == null && isAiming)
        {
            HideTrajectory();
            isAiming = false;
        }
    }

    // ───────────────────────── Public hooks from Bomb ────────────
    public void SetBomb(Bomb b)
    {
        currentBomb = b;
        Debug.Log($"SetBomb called for {gameObject.name}, bomb: {b}, IsHeld={b?.IsHeld}", this);

        // Update animation state for bomb in hand
        UpdateHandAnimationState();
    }

    public void ClearBomb()
    {
        if (isAiming)
        {
            HideTrajectory();
            isAiming = false;
        }
        
        currentBomb = null;
        Debug.Log($"ClearBomb called for {gameObject.name}", this);

        // Reset hand animation state
        UpdateHandAnimationState();
    }

    // Update animation state based on which hand holds the bomb
    private void UpdateHandAnimationState()
    {
        if (animator != null)
        {
            int handState = 0; // No bomb
            
            if (currentBomb != null && currentBomb.Holder == gameObject)
            {
                handState = currentBomb.IsOnRight ? 2 : 1; // 1 = Left, 2 = Right
            }
            
            // Set animation parameter directly for immediate local feedback
            animator.SetInteger("activeHand", handState);
        }
    }

    // ───────────────────────── Input handlers (local) ────────────
    void Swap(InputAction.CallbackContext _)
    {
        if (!isLocalPlayer || currentBomb == null)
        {
            Debug.Log($"Swap failed: isLocalPlayer={isLocalPlayer}, currentBomb={currentBomb}", this);
            return;
        }
        CmdSwapBomb();
    }

    void StartAim(InputAction.CallbackContext _)
    {
        if (!isLocalPlayer || currentBomb == null)
        {
            Debug.Log($"StartAim failed: isLocalPlayer={isLocalPlayer}, currentBomb={currentBomb}", this);
            return;
        }
        
        Debug.Log($"StartAim called: isLocalPlayer={isLocalPlayer}, currentBomb={currentBomb}, IsHeld={currentBomb.IsHeld}", this);
        
        if (!currentBomb.IsHeld || currentBomb.Holder != gameObject)
        {
            Debug.LogWarning($"StartAim: Bomb is not properly held by this player.", this);
            return;
        }
        
        isAiming = true;
        StartCoroutine(AimLoop());
    }

    void ReleaseThrow(InputAction.CallbackContext _)
    {
        if (!isLocalPlayer)
        {
            Debug.Log($"ReleaseThrow failed: isLocalPlayer={isLocalPlayer}", this);
            return;
        }
        
        if (!isAiming)
        {
            Debug.Log("ReleaseThrow called but wasn't aiming", this);
            return;
        }
        
        if (currentBomb == null)
        {
            Debug.Log($"ReleaseThrow failed: currentBomb is null", this);
            isAiming = false;
            HideTrajectory();
            return;
        }
        
        Debug.Log($"ReleaseThrow called: isLocalPlayer={isLocalPlayer}, currentBomb={currentBomb}, IsHeld={currentBomb.IsHeld}", this);
        
        isAiming = false;
        HideTrajectory();
        
        // Trigger throw animation locally for immediate feedback
        if (animator != null)
        {
            animator.SetTrigger("Throw");
        }
        
        CmdThrowBomb();
    }

    IEnumerator AimLoop()
    {
        while (isAiming && currentBomb != null)
        {
            DrawTrajectory();
            yield return new WaitForSeconds(timeStep);
        }
        
        HideTrajectory();
    }

    // ───────────────────────── Commands (client→server) ──────────
    [Command]
    void CmdSwapBomb()
    {
        Debug.Log($"CmdSwapBomb called: currentBomb={currentBomb}, holder check={currentBomb?.Holder == gameObject}", this);
        
        if (currentBomb && currentBomb.Holder == gameObject)
        {
            currentBomb.SwapHoldPoint();
            
            // Update animation state on all clients
            if (playerAnimator != null)
            {
                // This will be synced through PlayerAnimator's SyncVar
                UpdateHandAnimationState();
            }
        }
        else
        {
            Debug.LogWarning($"CmdSwapBomb failed: currentBomb={currentBomb}, Holder={currentBomb?.Holder}", this);
        }
    }

    [Command]
    void CmdThrowBomb()
    {
        Debug.Log($"CmdThrowBomb called: currentBomb={currentBomb}, holder check={currentBomb?.Holder == gameObject}", this);
        
        if (currentBomb && currentBomb.Holder == gameObject)
        {
            // Notify the PlayerAnimator to trigger throw animation on all clients
            if (playerAnimator != null)
            {
                playerAnimator.OnBombThrow();
            }
            
            currentBomb.ThrowBomb();
        }
        else
        {
            Debug.LogWarning($"CmdThrowBomb failed: currentBomb={currentBomb}, Holder={currentBomb?.Holder}", this);
        }
    }

    // ───────────────────────── Trajectory helpers ────────────────
    void DrawTrajectory()
    {
        points.Clear();
        if (currentBomb == null)
        {
            Debug.Log("DrawTrajectory: currentBomb is null", this);
            HideTrajectory();
            return;
        }

        string hand = currentBomb.IsOnRight ? "RightHoldPoint" : "LeftHoldPoint";
        Transform origin = transform.Find(hand);
        if (!origin)
        {
            Debug.Log($"DrawTrajectory: {hand} not found", this);
            HideTrajectory();
            return;
        }

        float speed   = currentBomb.IsOnRight ? currentBomb.NormalThrowSpeed
                                              : currentBomb.LobThrowSpeed;
        float upward  = currentBomb.IsOnRight ? currentBomb.NormalThrowUpward
                                              : currentBomb.LobThrowUpward;

        Vector3 startPos  = origin.position;
        Vector3 velocity  = origin.forward * speed + Vector3.up * upward;
        Vector3 lastPos   = startPos;
        points.Add(lastPos);

        float t = 0f;
        for (int i = 0; i < maxPoints; ++i)
        {
            t += timeStep;
            Vector3 next = startPos + velocity * t + 0.5f * Physics.gravity * t * t;
            if (Physics.Raycast(lastPos, next - lastPos, out RaycastHit hit,
                                (next - lastPos).magnitude, collisionMask))
            {
                points.Add(hit.point);
                break;
            }
            points.Add(next);
            lastPos = next;
        }

        lr.positionCount = points.Count;
        lr.SetPositions(points.ToArray());
        lr.startColor = lr.endColor = currentBomb.IsOnRight ? Color.blue : Color.yellow;
    }

    void HideTrajectory()
    {
        if (lr != null)
        {
            lr.positionCount = 0;
        }
    }
}