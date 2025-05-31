// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/NpcLogic/Components/NpcMovement.cs
// NpcMovement.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/NpcMovement.cs

using UnityEngine;
using System.Collections; // For coroutines if needed for complex movement sequences

/// <summary>
/// Handles the physical movement of an NPC towards a target position.
/// This component is responsible for updating the GameObject's transform.
/// It is controlled by NpcController, which provides the target position.
/// Assumes that for complex pathfinding or obstacle avoidance beyond simple collision,
/// a more advanced system (like NavMeshAgent) would replace or augment this script's logic.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class NpcMovement : MonoBehaviour
{
    [Header("Movement Settings")]
    [Tooltip("NPC 朝目標移動的速度，單位：Unity 單位/秒。")]
    public float moveSpeed = 2.0f;

    [Tooltip("NPC 旋轉以面向其移動方向的速度，單位：度/秒。設為 0 表示瞬時旋轉。")]
    public float rotationSpeed = 360f; // Degrees per second

    [Tooltip("認為 NPC 已「到達」其目標位置的距離閾值。")]
    public float arrivalDistance = 0.1f;

    [Tooltip("可選：要旋轉的視覺模型的 Transform（例如，子 Sprite GameObject）。如果為 null，則旋轉此 GameObject 的 Transform。")]
    public Transform visualModelToRotate; // NpcController 亦可控制此項

    private Rigidbody2D _rb;
    private Vector3 _currentTargetPositionWorld;
    private bool _isMoving = false;
    private System.Action _onArrivalCallback; // 到達目的地時的可選回呼

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        if (_rb == null)
        {
            Debug.LogError($"[{gameObject.name}] NpcMovement requires a Rigidbody2D component. Please add one.", this);
            enabled = false;
            return;
        }
        // 為了讓 NpcController 或此腳本能透過 MovePosition 精確控制運動，
        // 通常將 Rigidbody2D 設為 Kinematic，使其不受外部物理力的直接影響（除非您有意設計）。
        _rb.isKinematic = true;
        _rb.freezeRotation = true; // 通常 NPC 的旋轉由腳本控制，而非物理

        if (visualModelToRotate == null)
        {
            // 如果未在 Inspector 中指定 visualModelToRotate，
            // 可以選擇預設旋轉此 GameObject 本身，或者讓 NpcController 負責視覺旋轉。
            // visualModelToRotate = transform; // 取消註解此行以預設旋轉自身
        }
    }

    /// <summary>
    /// 設定 NPC 的移動目標。通常由 NpcController 調用。
    /// </summary>
    /// <param name="targetPosition">要移動到的世界座標位置。</param>
    /// <param name="onArrival">可選：NPC 到達目標時要調用的動作。</param>
    public void SetMoveTarget(Vector3 targetPosition, System.Action onArrival = null)
    {
        // 確保 Z 軸與當前 NPC 的 Z 軸一致（主要用於 XY 平面上的 2D 移動）
        _currentTargetPositionWorld = new Vector3(targetPosition.x, targetPosition.y, transform.position.z);
        _isMoving = true;
        _onArrivalCallback = onArrival;
        // Debug.Log($"[{gameObject.name}] NpcMovement: New target set to {_currentTargetPositionWorld}");
    }

    /// <summary>
    /// 立即停止 NPC 當前的移動。通常由 NpcController 調用。
    /// </summary>
    public void StopMovement()
    {
        _isMoving = false;
        _onArrivalCallback = null; // 清除回呼
        // 如果使用的是非 Kinematic Rigidbody 並透過速度/力來移動，則在此處將速度設為零。
        // if (!_rb.isKinematic) _rb.velocity = Vector2.zero;
        // Debug.Log($"[{gameObject.name}] NpcMovement: Movement stopped.");
    }

    /// <summary>
    /// NPC 是否正在移動。
    /// </summary>
    public bool IsMoving()
    {
        return _isMoving;
    }

    /// <summary>
    /// 在 FixedUpdate 中處理物理相關的移動，以獲得更一致的行為。
    /// </summary>
    void FixedUpdate()
    {
        if (!_isMoving)
        {
            // 如果 Rigidbody 不是 Kinematic 且未使用 MovePosition，可能需要在此處處理速度衰減
            // if (!_rb.isKinematic && _rb.velocity.sqrMagnitude > 0.01f) 
            //     _rb.velocity = Vector2.Lerp(_rb.velocity, Vector2.zero, Time.fixedDeltaTime * 10f);
            return;
        }

        MoveTowardsTargetInternal(); // 執行實際的位置更新
    }
    
    /// <summary>
    /// 在 Update 中處理非物理關鍵的邏輯，如到達檢測和視覺更新。
    /// </summary>
    void Update()
    {
        if(_isMoving)
        {
            CheckForArrivalInternal();   // 檢查是否到達目的地
            HandleVisualRotationInternal(); // 處理視覺模型的旋轉
        }
    }

    /// <summary>
    /// 內部方法，實際執行向目標移動的邏輯。
    /// </summary>
    private void MoveTowardsTargetInternal()
    {
        Vector3 currentPosition = _rb.position; // 使用 Rigidbody 的位置
        // 確保目標點在 NPC 的 Z 平面上 (對於 2D 遊戲，Z 通常是固定的或用於排序)
        Vector3 targetPositionOnPlane = new Vector3(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y, currentPosition.z);
        
        // 計算移動方向和距離
        Vector3 newPosition = Vector3.MoveTowards(currentPosition, targetPositionOnPlane, moveSpeed * Time.fixedDeltaTime);

        // 使用 Rigidbody2D.MovePosition() 進行運動學移動，這有助於正確的物理交互和插值。
        _rb.MovePosition(newPosition);
    }
    
    /// <summary>
    /// 內部方法，處理視覺模型的旋轉。
    /// </summary>
    private void HandleVisualRotationInternal()
    {
        if (visualModelToRotate == null) return; // 如果沒有指定視覺模型，則不執行

        Vector3 currentPosition = transform.position; // 可以使用 transform.position 或 _rb.position
        Vector3 targetPositionOnPlane = new Vector3(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y, currentPosition.z);
        Vector3 directionToTarget = (targetPositionOnPlane - currentPosition);

        // 僅當有明確的移動方向時才進行旋轉
        if (directionToTarget.sqrMagnitude > 0.001f) // 使用平方量級比較以提高效率
        {
            // 計算2D俯視角下的目標角度（Y軸在2D空間中向上，Z軸為Sprite的視覺前方）
            float targetAngle = Mathf.Atan2(directionToTarget.y, directionToTarget.x) * Mathf.Rad2Deg;
            // 如果您的 Sprite 的「前方」不是其右方（例如，如果是「上方」），則可能需要調整角度，例如減去90度。
            // 這裡假設 NpcController 或 visualModelTransform 本身的初始朝向已考慮。
            // NpcController 中也有旋轉邏輯，需確保不衝突。
            // 通常，如果 visualModelTransform 是 transform 的子物件，讓父物件的 NpcController 控制整體朝向，
            // 或者讓此處的 NpcMovement 控制 visualModelToRotate 的局部旋轉。
            // 範例：假設 Sprite 向上是前方，所以 targetAngle 需要 -90 度。
            // (NpcController 中的旋轉是 angle - 90，如果這裡也做，可能會重複或衝突)
            // 這裡的旋轉應該基於實際的移動方向（即速度方向），或者 _currentTargetPositionWorld。
            // 我們採用 _currentTargetPositionWorld。

            // 以下旋轉邏輯與 NpcController 中的 visualModelTransform.rotation 邏輯類似。
            // 需確定由誰主導旋轉。如果 NpcController 在 HandleMovingToTargetState 中已更新 visualModelTransform.rotation，
            // 則此處的邏輯可能多餘或應被禁用/協調。
            // 為了演示，保留此處的旋轉邏輯，但實際應用中需謹慎。
            // targetAngle -= 90f; // 取決於 Sprite 預設朝向，若 NpcController 已處理，此處可省略

            Quaternion currentVisualRotation = visualModelToRotate.rotation;
            Quaternion desiredVisualRotation = Quaternion.AngleAxis(targetAngle - 90f, Vector3.forward); // 假設 Sprite 向上是前方

            if (rotationSpeed > 0)
            {
                visualModelToRotate.rotation = Quaternion.RotateTowards(currentVisualRotation, desiredVisualRotation, rotationSpeed * Time.deltaTime);
            }
            else // 瞬時旋轉
            {
                visualModelToRotate.rotation = desiredVisualRotation;
            }
        }
    }

    /// <summary>
    /// 內部方法，檢查 NPC 是否已到達目標位置。
    /// </summary>
    private void CheckForArrivalInternal()
    {
        // 在 XY 平面上使用 Vector2 進行距離檢查（適用於 2D 移動）
        if (Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y)) < arrivalDistance)
        {
            // Debug.Log($"[{gameObject.name}] NpcMovement: Arrived at target {_currentTargetPositionWorld}");
            
            // 精確移動到目標點（可選，因為 MovePosition 可能已經非常接近）
            _rb.MovePosition(new Vector3(_currentTargetPositionWorld.x, _currentTargetPositionWorld.y, transform.position.z));
            
            _isMoving = false; // 停止移動狀態
            
            _onArrivalCallback?.Invoke(); // 調用到達時的回呼（如果已提供）
            _onArrivalCallback = null;    // 調用後清除回呼，防止重複執行
        }
    }

    /// <summary>
    /// 公開屬性，獲取當前的移動目標位置（如果正在移動）。
    /// </summary>
    public Vector3 CurrentTargetPosition => _isMoving ? _currentTargetPositionWorld : transform.position;


    /// <summary>
    /// 在編輯器中選中物件時繪製 Gizmos，用於可視化目標（可選）。
    /// </summary>
    void OnDrawGizmosSelected()
    {
        if (_isMoving)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, _currentTargetPositionWorld); // 從當前位置到目標畫線
            Gizmos.DrawWireSphere(_currentTargetPositionWorld, arrivalDistance); // 在目標位置繪製到達閾值範圍
        }
    }
}