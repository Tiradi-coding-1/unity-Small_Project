// NpcMovement.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/NpcMovement.cs

using UnityEngine;
using System.Collections; // For coroutines if needed for complex movement sequences

/// <summary>
/// Handles the physical movement of an NPC towards a target position.
/// This component is responsible for updating the GameObject's transform.
/// It can be controlled by NpcController or other AI scripts.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))] // Assuming 2D, adjust if 3D
public class NpcMovement : MonoBehaviour
{
    [Header("Movement Settings")]
    [Tooltip("The speed at which the NPC moves towards its target, in units per second.")]
    public float moveSpeed = 2.0f;

    [Tooltip("The speed at which the NPC rotates to face its movement direction, in degrees per second. 0 for instant rotation.")]
    public float rotationSpeed = 360f; // Degrees per second

    [Tooltip("The distance threshold to consider the NPC has 'arrived' at its target position.")]
    public float arrivalDistance = 0.1f;

    [Tooltip("Optional: Transform of the visual model to rotate (e.g., a child Sprite GameObject). If null, rotates this GameObject's transform.")]
    public Transform visualModelToRotate;

    private Rigidbody2D _rb;
    private Vector3 _currentTargetPositionWorld;
    private bool _isMoving = false;
    private System.Action _onArrivalCallback; // Optional callback when destination is reached

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        if (_rb == null)
        {
            Debug.LogError($"[{gameObject.name}] NpcMovement requires a Rigidbody2D component. Please add one.", this);
            enabled = false;
            return;
        }
        _rb.isKinematic = true; // Common for character controllers that set position directly
                               // If you use Rigidbody.MovePosition or forces, isKinematic might be false.
                               // For simplicity, direct transform manipulation or MovePosition is often used with kinematic.
        
        if (visualModelToRotate == null)
        {
            visualModelToRotate = transform; // Default to rotating the main transform if no specific model is set
        }
    }

    /// <summary>
    /// Starts moving the NPC towards the specified target position.
    /// </summary>
    /// <param name="targetPosition">The world position to move towards.</param>
    /// <param name="onArrival">Optional: An action to call when the NPC arrives at the target.</param>
    public void SetMoveTarget(Vector3 targetPosition, System.Action onArrival = null)
    {
        // Ensure Z is consistent if this is primarily 2D movement on XY plane
        // Or handle 3D movement appropriately. For now, assuming Z might be fixed.
        _currentTargetPositionWorld = new Vector3(targetPosition.x, targetPosition.y, transform.position.z);
        _isMoving = true;
        _onArrivalCallback = onArrival;
        // Debug.Log($"[{gameObject.name}] NpcMovement: New target set to {_currentTargetPositionWorld}");
    }

    /// <summary>
    /// Stops the NPC's current movement immediately.
    /// </summary>
    public void StopMovement()
    {
        _isMoving = false;
        _onArrivalCallback = null; // Clear callback
        // Debug.Log($"[{gameObject.name}] NpcMovement: Movement stopped.");
        // If using Rigidbody velocity/forces for movement, you'd set velocity to zero here.
        // if (!_rb.isKinematic) _rb.velocity = Vector2.zero;
    }

    public bool IsMoving()
    {
        return _isMoving;
    }

    void Update()
    {
        if (!_isMoving)
        {
            // If using Rigidbody physics for movement and not kinematic, ensure velocity is zeroed when not moving.
            // if (!_rb.isKinematic && _rb.velocity.sqrMagnitude > 0.01f) _rb.velocity = Vector2.Lerp(_rb.velocity, Vector2.zero, Time.deltaTime * 10f);
            return;
        }

        MoveTowardsTarget();
        CheckForArrival();
    }

    private void MoveTowardsTarget()
    {
        Vector3 currentPosition = transform.position;
        // Ensure movement happens on the intended plane (e.g., keeping Z fixed for 2D-like movement)
        Vector3 targetPositionOnPlane = new Vector3(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y, currentPosition.z);
        Vector3 directionToTarget = (targetPositionOnPlane - currentPosition).normalized;

        // Movement
        if (directionToTarget.sqrMagnitude > 0.001f) // Only move if there's a direction
        {
            // Using transform.position for kinematic movement.
            // For physics-based movement with a non-kinematic Rigidbody2D, you'd use _rb.MovePosition or add forces.
            transform.position = Vector3.MoveTowards(currentPosition, targetPositionOnPlane, moveSpeed * Time.deltaTime);
        }


        // Rotation (simple 2D top-down look-at)
        if (visualModelToRotate != null && directionToTarget.sqrMagnitude > 0.01f && rotationSpeed > 0)
        {
            // Calculate the angle in degrees for 2D top-down (Y-up in 2D space, Z-forward for sprite facing)
            float targetAngle = Mathf.Atan2(directionToTarget.y, directionToTarget.x) * Mathf.Rad2Deg;
            // Adjust angle if your sprite's 'forward' is not to its right (e.g., if it's 'up', subtract 90 degrees)
            targetAngle -= 90f; 

            Quaternion targetRotation = Quaternion.Euler(0, 0, targetAngle); // Rotate around Z-axis for 2D
            visualModelToRotate.rotation = Quaternion.RotateTowards(visualModelToRotate.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
        else if (visualModelToRotate != null && directionToTarget.sqrMagnitude > 0.01f && rotationSpeed <= 0) // Instant rotation
        {
             float targetAngle = Mathf.Atan2(directionToTarget.y, directionToTarget.x) * Mathf.Rad2Deg - 90f;
             visualModelToRotate.rotation = Quaternion.Euler(0, 0, targetAngle);
        }
    }

    private void CheckForArrival()
    {
        // Using Vector2 for distance check on the XY plane for 2D movement
        if (Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y)) < arrivalDistance)
        {
            // Debug.Log($"[{gameObject.name}] NpcMovement: Arrived at target {_currentTargetPositionWorld}");
            transform.position = new Vector3(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y, transform.position.z); // Snap to exact target
            _isMoving = false;
            
            _onArrivalCallback?.Invoke(); // Invoke the callback if one was provided
            _onArrivalCallback = null;    // Clear callback after invoking
        }
    }

    // Optional: Public property to get current target (read-only from outside)
    public Vector3 CurrentTargetPosition => _isMoving ? _currentTargetPositionWorld : transform.position;


    // Gizmos for visualizing the target in the editor (optional)
    void OnDrawGizmosSelected()
    {
        if (_isMoving)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, _currentTargetPositionWorld);
            Gizmos.DrawWireSphere(_currentTargetPositionWorld, arrivalDistance);
        }
    }
}