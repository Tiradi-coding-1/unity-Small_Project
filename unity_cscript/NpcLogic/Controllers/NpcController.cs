// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/NpcLogic/Controllers/NpcController.cs
// NpcController.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Controllers/NpcController.cs

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NpcApiModels;
using System.Linq; // For LINQ operations like TakeLast
using System; // For DateTime
using TMPro; // 引用 TextMeshPro

// 輔助列舉來管理 NPC 的主要狀態
public enum NpcBehaviorState
{
    Idle,                   // 空閒狀態，等待或計時以觸發下一個決策
    RequestingDecision,     // 正在向後端 API 請求決策 (移動或對話)
    MovingToTarget,         // 正在向 LLM 決定的目標點移動
    ApproachingInteraction, // NPC 主動接近其他角色以發起對話
    Interacting,            // 正在進行對話 (UI 顯示，等待玩家或其他 NPC 回應)
    PostInteractionPause    // 對話結束後的短暫停頓
}

[RequireComponent(typeof(CharacterData))]
[RequireComponent(typeof(Rigidbody2D))] // 用於物理交互和觸發器檢測
[RequireComponent(typeof(Collider2D))]  // NPC 自身的主要碰撞體或互動觸發器的根碰撞體
[RequireComponent(typeof(NpcMovement))] // 現在明確要求 NpcMovement 組件
public class NpcController : MonoBehaviour
{
    [Header("NPC Core Configuration")]
    [Tooltip("當 NPC 空閒或完成任務後，進行 LLM 移動決策之間的時間（秒）。")]
    public float decisionInterval = 15.0f;
    [Tooltip("NPC 的移動速度（Unity 單位/秒）。將傳遞給 NpcMovement 組件。")]
    public float moveSpeed = 2.0f;
    [Tooltip("認為 NPC 已到達目標的距離閾值。將傳遞給 NpcMovement 組件。")]
    public float arrivalThreshold = 0.3f;
    [Tooltip("可選：視覺模型的 Transform，用於朝向移動方向旋轉。")]
    public Transform visualModelTransform;


    [Header("Interaction Settings")]
    [Tooltip("附加到此 NPC 的 Collider2D（設定為 IsTrigger=true），用於偵測其他角色以進行互動。")]
    public Collider2D interactionTrigger;
    [Tooltip("如果 NPC 正在接近互動目標，它會在此距離停下來發起對話。")]
    public float dialogueInitiationDistance = 1.5f;
    [Tooltip("NPC 在對話後可能暫停的持續時間（秒），然後重新評估。")]
    public float postDialoguePauseDuration = 2.0f;

    [Header("Dialogue Bubble (TextMeshPro)")]
    [Tooltip("NPC 頭頂對話氣泡的預製件，應包含一個 TextMeshProUGUI 組件。")]
    public GameObject dialogueBubblePrefab_TMP;
    [Tooltip("對話氣泡相對於 NPC 位置的垂直偏移量。")]
    public float dialogueBubbleOffsetY = 1.5f;
    [Tooltip("對話氣泡預設顯示時間（秒）。")]
    public float dialogueDisplayTime = 4.0f;


    [Header("Context Providers (Assign from Scene Managers)")]
    [Tooltip("對 GameTimeManager 的引用，用於獲取當前遊戲時間。")]
    public GameTimeManager gameTimeManager;
    [Tooltip("對 SceneContextManager 的引用，用於獲取地標和其他角色列表。")]
    public SceneContextManager sceneContextManager;
    [Tooltip("對全域 DialogueUIManager 的引用（如果仍需用於非 NPC 系統訊息）。可選。")]
    public DialogueUIManager dialogueUIManager; // 保留可選的全域 UI 管理器

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb;
    private NpcMovement _npcMovement; // 引用 NpcMovement 組件
    private NpcBehaviorState _currentState = NpcBehaviorState.Idle;
    private float _decisionTimer = 0f;
    private Vector3 _currentMovementTargetWorld;
    private bool _hasMovementTarget = false;
    private bool _isApiCallInProgress = false;
    private CharacterData _currentTargetInteractionCharacter = null;
    private CharacterData _lastInteractedCharacter = null;

    private List<DialogueTurn> _shortTermDialogueHistory = new List<DialogueTurn>();
    private const int MaxDialogueHistoryForMovementContext = 6;

    private NpcApiModels.NpcEmotionalState _currentNpcEmotionalState;
    private string _lastMovementAbortReason = null;

    // Dialogue Bubble related
    private TextMeshProUGUI _dialogueTextTMP;
    private GameObject _currentDialogueBubbleInstance;
    private Coroutine _hideBubbleCoroutine;
    private LandmarkDataComponent _currentActiveLandmarkZone = null; // Track the zone NPC is currently in


    // Constants for dynamic status notes (與 LandmarkDataComponent.cs 中的定義匹配)
    private const string OccupancyStatusPrefix = "occupancy_"; // 例如 "occupancy_occupied", "occupancy_vacant"
    private const string OccupancyStatusOccupied = "occupancy_occupied"; // 泛指被佔用
    private string GetOccupancyStatusOccupiedBySelf() => $"{OccupancyStatusOccupied}_by_{_characterData.npcId}";


    private const string OwnerPresenceStatusPrefix = "owner_presence_"; // 例如 "owner_presence_present", "owner_presence_absent"
    private const string OwnerPresencePresent = "owner_presence_present";
    private const string OwnerPresenceAbsent = "owner_presence_absent";


    // --- 初始化 ---
    void Awake()
    {
        _characterData = GetComponent<CharacterData>();
        _rb = GetComponent<Rigidbody2D>();
        _npcMovement = GetComponent<NpcMovement>();

        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.freezeRotation = true;
        }
        if (_npcMovement == null)
        {
            Debug.LogError($"[{gameObject.name}] NpcController requires an NpcMovement component. Please add one.", this);
            enabled = false; return;
        }
        // 將 NpcController 的設定傳遞給 NpcMovement 組件
        _npcMovement.moveSpeed = this.moveSpeed;
        _npcMovement.arrivalDistance = this.arrivalThreshold;
        _npcMovement.rotationSpeed = 360f; // 可以考慮也作為 NpcController 的公開欄位
        if (visualModelTransform != null)
        {
            _npcMovement.visualModelToRotate = this.visualModelTransform;
        }


        if (_characterData == null) {
            Debug.LogError($"[{gameObject.name}] NpcController requires a CharacterData component. Disabling.", this);
            enabled = false; return;
        }
        if (!_characterData.isLLMNpc) {
            Debug.Log($"[{_characterData.characterName}] NpcController is on a non-LLM character. Disabling.", this);
            enabled = false; return;
        }
        if (string.IsNullOrEmpty(_characterData.npcId)) {
            Debug.LogError($"[{gameObject.name}] NpcController's CharacterData has an empty NpcId. Disabling.", this);
            enabled = false; return;
        }
        if (interactionTrigger == null) {
            Debug.LogWarning($"[{_characterData.characterName}] Interaction Trigger not assigned to NpcController. NPC might not detect others for dialogue.", this);
        } else if (!interactionTrigger.isTrigger) {
             Debug.LogWarning($"[{_characterData.characterName}] Interaction Trigger on '{interactionTrigger.name}' is not set to 'Is Trigger'. Please enable it.", this);
        }

        if (gameTimeManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': GameTimeManager not assigned!", this);
        if (sceneContextManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': SceneContextManager not assigned!", this);
        if (dialogueBubblePrefab_TMP == null && _characterData.isLLMNpc)
        {
            Debug.LogWarning($"NpcController on '{_characterData.characterName}': Dialogue Bubble Prefab (TMP) is not assigned! NPC will not be able to display its own dialogue bubbles.", this);
        }

        InitializeEmotionalState();
    }

    void InitializeEmotionalState()
    {
        if (_characterData != null && _characterData.isLLMNpc)
        {
            NPCMemoryFile defaultMemory = _characterData.CreateDefaultMemoryFile();
            if (defaultMemory != null && defaultMemory.current_emotional_state != null)
            {
                _currentNpcEmotionalState = defaultMemory.current_emotional_state;
            }
            else
            {
                Debug.LogWarning($"[{_characterData.characterName}] CreateDefaultMemoryFile returned null or incomplete data. Initializing default emotional state for NpcController.");
                _currentNpcEmotionalState = new NpcEmotionalState {
                    primary_emotion = "neutral",
                    intensity = 0.5f,
                    last_significant_change_at = DateTime.UtcNow.ToString("o"),
                    reason_for_last_change = "Fallback initial state in NpcController."
                };
            }
        }
        else if (_characterData != null)
        {
            _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "n_a",
                intensity = 0f,
                last_significant_change_at = DateTime.UtcNow.ToString("o"),
                reason_for_last_change = "Non-LLM character initial state."
            };
        }
        else
        {
             _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "error_no_character_data",
                intensity = 0f,
                last_significant_change_at = DateTime.UtcNow.ToString("o")
            };
        }
    }

    void Start()
    {
        _decisionTimer = UnityEngine.Random.Range(decisionInterval * 0.1f, decisionInterval * 0.75f);
        ChangeState(NpcBehaviorState.Idle);
    }

    void Update()
    {
        if (!_characterData.isLLMNpc || !enabled) return;

        switch (_currentState)
        {
            case NpcBehaviorState.Idle:
                HandleIdleState();
                break;
            case NpcBehaviorState.RequestingDecision:
                // No continuous update logic here, it's event-driven by API response
                break;
            case NpcBehaviorState.MovingToTarget:
                HandleMovingToTargetState();
                break;
            case NpcBehaviorState.ApproachingInteraction:
                HandleApproachingInteractionState();
                break;
            case NpcBehaviorState.Interacting:
                // No continuous update logic here, it's event-driven by API response or timer
                break;
            case NpcBehaviorState.PostInteractionPause:
                // Logic handled by coroutine
                break;
        }
    }

    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState && newState != NpcBehaviorState.RequestingDecision) return; // Avoid redundant state changes, allow re-requesting
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {_currentState} -> {newState}</color>");
        _currentState = newState;

        // Stop movement if entering a state that shouldn't have movement by default
        if (newState == NpcBehaviorState.Idle ||
            newState == NpcBehaviorState.ApproachingInteraction || // Movement is set specifically in HandleApproachingInteractionState
            newState == NpcBehaviorState.Interacting ||
            newState == NpcBehaviorState.PostInteractionPause ||
            newState == NpcBehaviorState.RequestingDecision) // Stop any prior movement before new decision
        {
            if (_npcMovement != null && _npcMovement.IsMoving())
            {
                _npcMovement.StopMovement();
            }
        }

        switch (newState)
        {
            case NpcBehaviorState.Idle:
                _hasMovementTarget = false;
                // _decisionTimer is handled by HandleIdleState
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null; // No longer approaching a specific character for dialogue
                // Movement target is set by RequestMovementDecisionAsync
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false; // Specific approach logic handles movement
                // Target character is set before changing to this state
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false; // Not moving while interacting
                _decisionTimer = 0f; // Reset decision timer, will evaluate after PostInteractionPause
                break;
            case NpcBehaviorState.PostInteractionPause:
                StartCoroutine(PostInteractionPauseCoroutine());
                break;
            case NpcBehaviorState.RequestingDecision:
                // State where NPC is waiting for API response. No specific actions here yet.
                break;
        }
    }

    void HandleIdleState()
    {
        _decisionTimer += Time.deltaTime;
        if (_decisionTimer >= decisionInterval && !_isApiCallInProgress)
        {
            _decisionTimer = 0f; // Reset before new request
            RequestMovementDecisionAsync();
        }
    }

    void HandleMovingToTargetState()
    {
        if (!_hasMovementTarget) { // Should have a target if in this state
            ChangeState(NpcBehaviorState.Idle); return;
        }
        // NpcMovement component handles arrival detection via its callback or by IsMoving() becoming false.
        // If NpcMovement.SetMoveTarget was given a callback, that callback would handle arrival.
        // If not, we check if NpcMovement has stopped.
        if (_npcMovement != null && !_npcMovement.IsMoving())
        {
            // This block executes if NpcMovement reports it's no longer moving.
            // This implies it has reached its target (or was stopped externally).
            // NpcMovement should ideally handle precise arrival internally.
            // We double-check distance here as a safeguard or if no callback was used with NpcMovement.
            Vector3 currentPosition = transform.position;
            Vector3 targetOnPlane = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, currentPosition.z);

            // Use NpcMovement's arrivalDistance for consistency
            if (Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)) <= _npcMovement.arrivalDistance * 1.1f) // Allow slight overshoot
            {
                Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target: ({targetOnPlane.x:F1}, {targetOnPlane.y:F1}) as NpcMovement stopped near target.</color>");
                _hasMovementTarget = false;
                UpdateLandmarkStatusOnArrivalOrDeparture(_currentMovementTargetWorld, true); // Notify arrival at target
                ChangeState(NpcBehaviorState.Idle);
            }
            else
            {
                // This case means NpcMovement stopped, but we are not at the target.
                // Could be due to external StopMovement() call or an issue with NpcMovement.
                Debug.LogWarning($"[{_characterData.characterName}] NpcMovement stopped but not at arrival threshold for LLM target. Pos: {currentPosition}, Target: {targetOnPlane}. Dist: {Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)):F2}. Returning to Idle.");
                _hasMovementTarget = false;
                // Do not call UpdateLandmarkStatusOnArrivalOrDeparture if not actually arrived at the intended target landmark
                ChangeState(NpcBehaviorState.Idle);
            }
        }
        // Visual rotation can be handled by NpcMovement itself if visualModelToRotate is assigned to it.
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) {
            Debug.LogWarning($"[{_characterData.characterName}] Target for interaction is null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle); return;
        }

        Vector3 targetCharPos = _currentTargetInteractionCharacter.transform.position;
        Vector3 directionToChar = (targetCharPos - transform.position).normalized;
        // Approach a point slightly offset from the character to avoid overlapping, respecting dialogueInitiationDistance
        Vector3 pointToApproach = targetCharPos - directionToChar * (dialogueInitiationDistance * 0.9f); 
        pointToApproach.z = transform.position.z; // Ensure Z is correct for 2D

        float distanceToCharActual = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(targetCharPos.x, targetCharPos.y));

        if (distanceToCharActual <= dialogueInitiationDistance)
        {
            // Already within dialogue range or have reached it
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop if was moving
            Debug.Log($"<color=orange>[{_characterData.characterName}] Reached/within dialogue range of '{_currentTargetInteractionCharacter.characterName}'. Actual dist: {distanceToCharActual:F1}. Starting dialogue.</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter);
        }
        // If not already moving towards this specific approach point, or target changed significantly
        else if (_npcMovement != null && (!_npcMovement.IsMoving() || Vector3.Distance(_npcMovement.CurrentTargetPosition, pointToApproach) > 0.5f) ) // 0.5f is a threshold to re-evaluate if target shifted much
        {
             _npcMovement.SetMoveTarget(pointToApproach, () => {
                // This onArrival callback is for NpcMovement successfully reaching the approach point
                float finalDist = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(_currentTargetInteractionCharacter.transform.position.x, _currentTargetInteractionCharacter.transform.position.y));
                Debug.Log($"<color=orange>[{_characterData.characterName}] NpcMovement arrived at approach point for '{_currentTargetInteractionCharacter.characterName}'. Final dist to char: {finalDist:F1}. Starting dialogue.</color>");
                // Ensure we are truly in range now (character might have moved slightly)
                if (finalDist <= dialogueInitiationDistance * 1.1f) // Allow slight tolerance
                {
                    StartDialogueAsync(_currentTargetInteractionCharacter);
                }
                else
                {
                    Debug.LogWarning($"[{_characterData.characterName}] Approached point for '{_currentTargetInteractionCharacter.characterName}', but character moved out of dialogue range ({finalDist:F1} > {dialogueInitiationDistance * 1.1f}). Re-evaluating (Idle).");
                    ChangeState(NpcBehaviorState.Idle);
                }
            });
        }
        // If NpcMovement is already moving towards a valid approach point, let it continue. Its callback will handle arrival.
    }


    private IEnumerator PostInteractionPauseCoroutine()
    {
        yield return new WaitForSeconds(postDialoguePauseDuration);
        ChangeState(NpcBehaviorState.Idle);
    }

    async void RequestMovementDecisionAsync(bool isReEvaluationDueToBlock = false)
    {
        if (_isApiCallInProgress) {
            Debug.LogWarning($"[{_characterData.characterName}] Movement decision request skipped: Another API call in progress.");
            return;
        }
        
        // It's crucial that _characterData is valid here.
        // If _characterData could become null due to destruction, this method shouldn't be called or should guard earlier.
        // The MissingReferenceException happened *after* the await, so _characterData was likely valid here.

        // Update status of current zone BEFORE deciding to move (departure logic)
        // This needs to happen before any `await` if `transform.position` is used and the object might be destroyed.
        // However, if `this` is null later, this call might have been on a destroyed object too.
        // For safety, we ensure 'this' is valid before this call.
        if (this == null || gameObject == null || !enabled) {
             Debug.LogWarning($"[NpcController RequestMovementDecisionAsync Start] Instance or GameObject was destroyed/disabled before starting. Aborting.");
            _isApiCallInProgress = false; // Ensure this is reset if we bail early
            return;
        }
        UpdateLandmarkStatusOnArrivalOrDeparture(transform.position, false);


        ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;

        NpcIdentifier selfIdentifier = _characterData.GetNpcIdentifier(); // Potential NRE if _characterData is null
        Position currentNpcPos = new Position { x = transform.position.x, y = transform.position.y }; // Potential NRE if transform is gone
        GameTime currentGameTime = gameTimeManager != null ? gameTimeManager.GetCurrentGameTime() : new GameTime { current_timestamp = DateTime.UtcNow.ToString("o"), time_of_day = "unknown_time" };

        List<EntityContextInfo> nearbyEntities = sceneContextManager?.GetNearbyEntities(selfIdentifier.npc_id, transform.position, 20f) ?? new List<EntityContextInfo>();
        List<LandmarkContextInfo> visibleLandmarks = sceneContextManager?.GetVisibleLandmarks(transform.position, 30f) ?? new List<LandmarkContextInfo>();

        SceneBoundaryInfo sceneBounds = sceneContextManager?.GetCurrentSceneBoundaries() ?? new SceneBoundaryInfo { min_x = -1000, max_x = 1000, min_y = -1000, max_y = 1000 };

        string dialogueSummary = "";
        if (_shortTermDialogueHistory.Count > 0) {
            var recentTurns = _shortTermDialogueHistory.TakeLast(MaxDialogueHistoryForMovementContext);
            dialogueSummary = string.Join("\n", recentTurns.Select(t => $"{(t.name ?? t.npc_id)}: \"{t.message_original_language}\""));
        }

        string augmentedContextForLLM = dialogueSummary;
        if(isReEvaluationDueToBlock && !string.IsNullOrEmpty(_lastMovementAbortReason)){
            augmentedContextForLLM = $"My previous attempt to move was blocked. Reason: '{_lastMovementAbortReason}'. I need to decide on a new action or destination considering this. " +
                                     (string.IsNullOrEmpty(dialogueSummary) ? "There was no other recent dialogue." : $"Recent dialogue was: {dialogueSummary}");
        }

        NpcMovementRequest requestPayload = new NpcMovementRequest {
            npc_id = selfIdentifier.npc_id, name = selfIdentifier.name,
            current_npc_position = currentNpcPos, current_game_time = currentGameTime,
            nearby_entities = nearbyEntities, visible_landmarks = visibleLandmarks,
            scene_boundaries = sceneBounds,
            recent_dialogue_summary_for_movement = string.IsNullOrEmpty(augmentedContextForLLM) ? null : augmentedContextForLLM
        };

        string charNameForLog = _characterData != null ? _characterData.characterName : (selfIdentifier != null ? selfIdentifier.name : "NpcController (unknown)");
        Debug.Log($"<color=cyan>[{charNameForLog}] Requesting MOVEMENT. Re-eval: {isReEvaluationDueToBlock}. Context: '{requestPayload.recent_dialogue_summary_for_movement?.Substring(0, Mathf.Min(requestPayload.recent_dialogue_summary_for_movement?.Length ?? 0, 100))}...'</color>");
        
        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);

        // *** ADDED CHECK for MissingReferenceException ***
        if (this == null || gameObject == null || !enabled)
        {
            string npcNameForLogAfterAwait = _characterData != null ? _characterData.characterName : (selfIdentifier != null ? selfIdentifier.name : "NpcController (unknown)");
            Debug.LogWarning($"[{npcNameForLogAfterAwait}] NpcController or its GameObject was destroyed or disabled during API call to /npc/think. Aborting further processing in RequestMovementDecisionAsync.");
            _isApiCallInProgress = false; // Attempt to reset state
            return; 
        }
        // *** END ADDED CHECK ***

        _isApiCallInProgress = false; 
        _lastMovementAbortReason = null;

        if (response != null && response.target_destination != null) {
            Debug.Log($"<color=#90EE90>[{_characterData.characterName}] Movement decision RX. Action: '{response.chosen_action_summary}'. Target: ({response.target_destination.x:F1}, {response.target_destination.y:F1})</color>");
            Vector3 potentialTargetWorld = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);

            bool canProceedToTarget = true;
            string currentAbortReason = "";
            LandmarkDataComponent targetLocationMeta = FindTargetLandmark(potentialTargetWorld);

            if (targetLocationMeta != null)
            {
                if (targetLocationMeta.landmarkTypeTag == "bathroom" &&
                    targetLocationMeta.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && 
                    targetLocationMeta.HasDynamicStatus(OccupancyStatusOccupied) &&        
                    !targetLocationMeta.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf())) 
                {
                    canProceedToTarget = false;
                    currentAbortReason = "Toilet is occupied by someone else.";
                }
                else if (targetLocationMeta.landmarkTypeTag == "bedroom" &&
                         !string.IsNullOrEmpty(targetLocationMeta.ownerNpcId) &&
                         targetLocationMeta.ownerNpcId != _characterData.npcId &&
                         targetLocationMeta.HasDynamicStatus(OwnerPresenceAbsent))
                {
                    canProceedToTarget = false;
                    currentAbortReason = $"Room '{targetLocationMeta.landmarkName}' is private and owner '{targetLocationMeta.ownerNpcId}' is absent.";
                }
            }

            if (!canProceedToTarget)
            {
                Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Cannot proceed to LLM target ({potentialTargetWorld.x:F1}, {potentialTargetWorld.y:F1}). Reason: {currentAbortReason}. Re-requesting.</color>");
                _lastMovementAbortReason = currentAbortReason;
                // Before re-requesting, ensure NPC is still valid to prevent infinite loop if destroyed.
                if (this == null || gameObject == null || !enabled) {
                     Debug.LogWarning($"[{_characterData.characterName}] NPC destroyed before re-requesting movement due to block. Aborting re-request.");
                     ChangeState(NpcBehaviorState.Idle); // Go to idle if cannot re-request
                     return;
                }
                RequestMovementDecisionAsync(true);
                return;
            }

            _currentMovementTargetWorld = potentialTargetWorld;
            _hasMovementTarget = true;

            if (_npcMovement != null) {
                 _npcMovement.SetMoveTarget(_currentMovementTargetWorld, () => {
                    // This is NpcMovement's onArrival callback for LLM chosen target
                    Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target ({_currentMovementTargetWorld.x:F1}, {_currentMovementTargetWorld.y:F1}) via NpcMovement onArrival callback.</color>");
                    _hasMovementTarget = false; // Mark as no longer having an active movement target
                    UpdateLandmarkStatusOnArrivalOrDeparture(_currentMovementTargetWorld, true); // Notify arrival at target
                    ChangeState(NpcBehaviorState.Idle); // Transition to Idle after arrival
                 });
            } else {
                Debug.LogError($"[{_characterData.characterName}] NpcMovement component is missing, cannot execute move!", this);
                ChangeState(NpcBehaviorState.Idle); return;
            }


            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot;
                Debug.Log($"<color=grey>[{_characterData.characterName}] Emotional state updated from API to: {_currentNpcEmotionalState.primary_emotion} (Intensity: {_currentNpcEmotionalState.intensity:F1})</color>");
            }

            bool wasDialogueDriven = response.primary_decision_drivers.GetValueOrDefault("dialogue_driven", false);

            if (wasDialogueDriven && _lastInteractedCharacter != null && !isReEvaluationDueToBlock) {
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination '{response.chosen_action_summary}' driven by dialogue with '{_lastInteractedCharacter.characterName}'. Confirming.</color>");
                // Ensure NPC is still valid before starting new dialogue
                if (this == null || gameObject == null || !enabled) {
                     Debug.LogWarning($"[{_characterData.characterName}] NPC destroyed before starting follow-up dialogue. Aborting.");
                     ChangeState(NpcBehaviorState.MovingToTarget); // Or Idle, but MovingToTarget was just set
                     return;
                }
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, based on our chat, I've decided to: {response.chosen_action_summary}. Sound good, {(_lastInteractedCharacter.characterName ?? "friend")}?", true);
                // Note: StartDialogueAsync will change state, potentially overriding MovingToTarget if dialogue starts immediately.
                // This might be okay if the confirmation dialogue is brief.
            }
            else if (!string.IsNullOrEmpty(response.chosen_action_summary) && !wasDialogueDriven && response.chosen_action_summary.Length > 3 && !isReEvaluationDueToBlock)
            {
                bool isGenericAction = response.chosen_action_summary.ToLower().Contains("explore") ||
                                       response.chosen_action_summary.ToLower().Contains("wander") ||
                                       response.chosen_action_summary.ToLower().Contains("idle") ||
                                       response.chosen_action_summary.ToLower().Contains("nothing special");
                if (!isGenericAction)
                {
                    ShowDialogueBubble_TMP($"Hmm... I think I will {response.chosen_action_summary.ToLower()}.", dialogueDisplayTime * 0.75f);
                }
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
            else {
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
        } else {
            // Ensure _characterData is safe to access here if 'this' could be null due to earlier destruction
            // The check after await should prevent reaching here if 'this' is null.
            Debug.LogError($"[{_characterData.characterName}] Failed to get valid movement decision from API or target was null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle);
        }
    }

    async void StartDialogueAsync(CharacterData otherCharacter, string initialNpcUtteranceSeed = null, bool isFollowUpDialogue = false)
    {
        if (_isApiCallInProgress && !isFollowUpDialogue) {
             Debug.LogWarning($"[{_characterData.characterName}] Dialogue with '{otherCharacter.characterName}' skipped: API call in progress.");
            return;
        }
        if (!isFollowUpDialogue && (_currentState == NpcBehaviorState.Interacting || _currentState == NpcBehaviorState.PostInteractionPause || _currentState == NpcBehaviorState.RequestingDecision) ) {
             Debug.LogWarning($"[{_characterData.characterName}] Initial dialogue with '{otherCharacter.characterName}' skipped: NPC busy (State: {_currentState}).");
            return;
        }
        
        // Safety check before proceeding with async operations
        if (this == null || gameObject == null || !enabled) {
            string npcNameForLog = _characterData != null ? _characterData.characterName : "NpcController (unknown)";
            Debug.LogWarning($"[{npcNameForLog}] NpcController or its GameObject was destroyed/disabled before starting StartDialogueAsync. Aborting.");
            _isApiCallInProgress = false; // Ensure reset
            return;
        }

        ChangeState(NpcBehaviorState.RequestingDecision); // NPC is now busy requesting dialogue
        _isApiCallInProgress = true;
        if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
        _hasMovementTarget = false;
        _lastInteractedCharacter = otherCharacter;

        string npcInitialPromptForLLM;
        if (!string.IsNullOrEmpty(initialNpcUtteranceSeed)) {
            npcInitialPromptForLLM = initialNpcUtteranceSeed;
        } else {
            string sceneHint = "You are in a shared apartment. ";
            npcInitialPromptForLLM = $"{sceneHint}You, '{_characterData.characterName}', have encountered '{otherCharacter.characterName}'. Initiate a natural, contextually appropriate conversation.";
        }

        string selfEmotionStringForPrompt = $"{_currentNpcEmotionalState.primary_emotion} (intensity: {_currentNpcEmotionalState.intensity:F1})";
        Debug.Log($"<color=orange>[{_characterData.characterName}] Initiating DIALOGUE with '{otherCharacter.characterName}'. FollowUp: {isFollowUpDialogue}. LLM Seed: '{npcInitialPromptForLLM}'. Emotion: {selfEmotionStringForPrompt}</color>");

        var interactionRequest = new GameInteractionRequest {
            interacting_objects = new List<InteractingObjectInfo> {
                _characterData.ToInteractingObjectInfo(
                    initialLlMPrompt: npcInitialPromptForLLM,
                    currentEmotionalState: selfEmotionStringForPrompt
                )
            },
            scene_context_description = sceneContextManager?.GetGeneralSceneDescription() ?? "A room in the apartment.",
            game_time_context = gameTimeManager?.GetCurrentGameTime() ?? new GameTime{current_timestamp=DateTime.UtcNow.ToString("o"), time_of_day="unknown_time"},
            max_turns_per_object = 1
        };

        GameInteractionResponse response = await ApiService.PostAsync<GameInteractionRequest, GameInteractionResponse>("/dialogue/game-interaction", interactionRequest);

        // *** ADDED CHECK for MissingReferenceException ***
        if (this == null || gameObject == null || !enabled)
        {
            string npcNameForLogAfterAwait = _characterData != null ? _characterData.characterName : "NpcController (unknown)";
            string otherCharNameForLog = otherCharacter != null ? otherCharacter.characterName : "other character (unknown)";
            Debug.LogWarning($"[{npcNameForLogAfterAwait}] NpcController or its GameObject was destroyed or disabled during API call for dialogue with '{otherCharNameForLog}'. Aborting further processing in StartDialogueAsync.");
            _isApiCallInProgress = false; // Attempt to reset state
            // Potentially try to revert to Idle if the current state is RequestingDecision and no response processed
            if(_currentState == NpcBehaviorState.RequestingDecision) ChangeState(NpcBehaviorState.Idle);
            return; 
        }
        // *** END ADDED CHECK ***

        _isApiCallInProgress = false;
        // If we are here, 'this' is valid. Now determine the next state based on response.
        // ChangeState(NpcBehaviorState.Interacting); // This was a bit premature, move after response check

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
            ChangeState(NpcBehaviorState.Interacting); // Now change to Interacting if response is valid
            foreach (var turn in response.dialogue_history) {
                string messageToDisplay = !string.IsNullOrEmpty(turn.message_translated_zh_tw) ? turn.message_translated_zh_tw : turn.message_original_language;

                if (turn.npc_id == _characterData.npcId || turn.name == _characterData.characterName) {
                    ShowDialogueBubble_TMP(messageToDisplay, dialogueDisplayTime);
                } else if (otherCharacter != null && (turn.npc_id == otherCharacter.npcId || turn.name == otherCharacter.characterName)) {
                    NpcController otherNpcCtrl = otherCharacter.GetComponent<NpcController>();
                    if (otherNpcCtrl != null) otherNpcCtrl.ShowDialogueBubble_TMP(messageToDisplay, dialogueDisplayTime);
                    else Debug.LogWarning($"Other character '{otherCharacter.name}' has no NpcController for bubble.");
                } else {
                    if (dialogueUIManager != null && dialogueUIManager.gameObject.activeInHierarchy)
                        dialogueUIManager.ShowDialogue(turn.name ?? turn.npc_id, messageToDisplay, dialogueDisplayTime);
                    else
                        Debug.Log($"[System/Other Dialogue] {turn.name ?? turn.npc_id}: {messageToDisplay}");
                }

                _shortTermDialogueHistory.Add(turn);
                if (_shortTermDialogueHistory.Count > MaxDialogueHistoryForMovementContext * 2) {
                    _shortTermDialogueHistory.RemoveRange(0, _shortTermDialogueHistory.Count - MaxDialogueHistoryForMovementContext * 2);
                }
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Dialogue with '{otherCharacter.characterName}' failed or no history.");
            ShowDialogueBubble_TMP("[Dialogue Error]", 2f);
            // If dialogue failed, don't stay in RequestingDecision; go to PostInteractionPause then Idle
        }

        ChangeState(NpcBehaviorState.PostInteractionPause); // Always go to post-interaction pause after attempt
    }

    public void ShowDialogueBubble_TMP(string message, float duration)
    {
        if (dialogueBubblePrefab_TMP == null) {
            if (dialogueUIManager != null && dialogueUIManager.gameObject.activeInHierarchy) {
                dialogueUIManager.ShowDialogue(_characterData.characterName, message, duration);
            } else {
                // Check if _characterData is null before accessing its members
                string charName = _characterData != null ? _characterData.characterName : "Unknown NPC";
                Debug.LogError($"[{charName}] Dialogue Bubble Prefab (TMP) not assigned and no fallback UI! Msg: {message}", this);
            }
            return;
        }

        if (_hideBubbleCoroutine != null) {
            StopCoroutine(_hideBubbleCoroutine);
            _hideBubbleCoroutine = null;
        }
        if (_currentDialogueBubbleInstance == null) {
            Vector3 bubblePosition = transform.position + Vector3.up * dialogueBubbleOffsetY;
            _currentDialogueBubbleInstance = Instantiate(dialogueBubblePrefab_TMP, bubblePosition, Quaternion.identity, transform);
            _dialogueTextTMP = _currentDialogueBubbleInstance.GetComponentInChildren<TextMeshProUGUI>();
            if (_dialogueTextTMP == null) _dialogueTextTMP = _currentDialogueBubbleInstance.GetComponent<TextMeshProUGUI>();
        }

        if (_dialogueTextTMP != null) {
            _dialogueTextTMP.text = message;
            _currentDialogueBubbleInstance.SetActive(true);
            if (duration > 0) {
                _hideBubbleCoroutine = StartCoroutine(HideDialogueBubbleAfterDelay_TMP(duration));
            }
        } else {
            string charName = _characterData != null ? _characterData.characterName : "Unknown NPC";
            Debug.LogError($"[{charName}] Dialogue Bubble Prefab (TMP) missing TextMeshProUGUI.", this);
            if (_currentDialogueBubbleInstance != null) Destroy(_currentDialogueBubbleInstance);
        }
    }

    private IEnumerator HideDialogueBubbleAfterDelay_TMP(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_currentDialogueBubbleInstance != null) {
            _currentDialogueBubbleInstance.SetActive(false);
        }
        _hideBubbleCoroutine = null;
    }

    void OnTriggerEnter2D(Collider2D otherCollider)
    {
        if (!_characterData.isLLMNpc || !enabled || _isApiCallInProgress) return;
        if (_currentState == NpcBehaviorState.Interacting ||
            _currentState == NpcBehaviorState.ApproachingInteraction ||
            _currentState == NpcBehaviorState.PostInteractionPause ||
            _currentState == NpcBehaviorState.RequestingDecision) return;

        if (interactionTrigger == null || !interactionTrigger.IsTouching(otherCollider) || otherCollider.gameObject == gameObject) return;

        if (otherCollider.TryGetComponent<CharacterData>(out CharacterData encounteredCharacter))
        {
            if (encounteredCharacter == _characterData) return; // Don't interact with self
            
            // Prevent initiating new interaction if already targeting someone or just finished
            if (_currentTargetInteractionCharacter != null && _currentTargetInteractionCharacter == encounteredCharacter) return;


            Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}'. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop current LLM movement
            _hasMovementTarget = false; // Cancel any LLM movement goal
            ChangeState(NpcBehaviorState.ApproachingInteraction);
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            // If the character we were trying to approach for interaction leaves our trigger zone
            if (_currentState == NpcBehaviorState.ApproachingInteraction) {
                Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left trigger while approaching. Returning to Idle.</color>");
                _currentTargetInteractionCharacter = null;
                if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop the approach
                ChangeState(NpcBehaviorState.Idle);
            }
            // If we were already interacting and they leave, the dialogue might end or continue depending on design.
            // For now, we don't explicitly handle ending an active dialogue if target leaves trigger here,
            // as dialogue is short and turn-based via API. The PostInteractionPause will lead to Idle.
        }
    }

    private void UpdateLandmarkStatusOnArrivalOrDeparture(Vector3 npcCurrentPosition, bool isArrivalEvent)
    {
        LandmarkDataComponent eventLandmark = FindTargetLandmark(npcCurrentPosition); // Landmark at current/arrival position

        if (isArrivalEvent) // NPC Arrived at a new location (_currentMovementTargetWorld)
        {
            if (eventLandmark != null && eventLandmark != _currentActiveLandmarkZone)
            {
                NotifyDepartureFromLandmark(_currentActiveLandmarkZone); // Depart from old zone if it was different
                _currentActiveLandmarkZone = eventLandmark;               // Set new current zone

                if (eventLandmark.landmarkTypeTag == "bedroom" && eventLandmark.ownerNpcId == _characterData.npcId)
                {
                    eventLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresencePresent);
                }
                else if (eventLandmark.landmarkTypeTag == "bathroom")
                {
                    if (!eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()) && 
                        !eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix)) 
                    {
                         eventLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, GetOccupancyStatusOccupiedBySelf());
                    }
                }
                Debug.Log($"[{_characterData.characterName}] Entered zone: '{eventLandmark.landmarkName}' ({eventLandmark.landmarkTypeTag}).");
            }
            else if (eventLandmark == null && _currentActiveLandmarkZone != null) // Arrived at a point not in a zone, but was in a zone
            {
                 NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                 _currentActiveLandmarkZone = null;
            }
            // If eventLandmark is null and _currentActiveLandmarkZone is also null, nothing to do.
            // If eventLandmark is the same as _currentActiveLandmarkZone, already in this zone, nothing to do for arrival.
        }
        else // Is Departure Event (called before new movement decision, from current npcCurrentPosition)
        {
            // Logic: if NPC is currently inside _currentActiveLandmarkZone, but is about to decide to move
            // (which means it's "departing" its current spot), update status.
            // Or, if the NPC is no longer physically within the bounds of _currentActiveLandmarkZone.
            if (_currentActiveLandmarkZone != null)
            {
                bool hasLeftCurrentZone = true; // Assume left unless proven otherwise
                if (eventLandmark == _currentActiveLandmarkZone) // Still in the same landmark zone based on current position
                {
                    hasLeftCurrentZone = false;
                }
                else if (eventLandmark != null && eventLandmark != _currentActiveLandmarkZone) // Clearly in a new zone
                {
                    hasLeftCurrentZone = true;
                }
                else if (eventLandmark == null) // Currently not in any specific landmark zone
                {
                     hasLeftCurrentZone = true;
                }


                if (hasLeftCurrentZone)
                {
                    NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                    _currentActiveLandmarkZone = eventLandmark; // Update to new zone (which might be null if in hallway)
                }
            }
            else if (eventLandmark != null) // Was not in a zone, but current position IS in a zone (e.g. game start)
            {
                // This case is more like an "initial entry" rather than departure, handled by arrival logic.
                // For departure logic, if _currentActiveLandmarkZone is null, nothing to depart from.
            }
        }
    }

    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark)
    {
        if (departedLandmark == null || _characterData == null) return; // Safety check

        Debug.Log($"[{_characterData.characterName}] Notifying departure from: '{departedLandmark.landmarkName}' ({departedLandmark.landmarkTypeTag}).");
        if (departedLandmark.landmarkTypeTag == "bedroom" && departedLandmark.ownerNpcId == _characterData.npcId)
        {
            departedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresenceAbsent);
        }
        else if (departedLandmark.landmarkTypeTag == "bathroom")
        {
            if(departedLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
            {
                departedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, null); // Effectively makes it vacant from this NPC
            }
        }
        // Potentially other landmark types could have departure logic here
    }

    private LandmarkDataComponent FindTargetLandmark(Vector3 targetPosition)
    {
        if (sceneContextManager == null)
        {
            Debug.LogWarning("SceneContextManager not available in NpcController.FindTargetLandmark");
            return null;
        }
        
        List<LandmarkDataComponent> allLandmarks = sceneContextManager.GetAllIndividualLandmarkDataComponents();
        List<RoomDataComponent> allRooms = sceneContextManager.GetAllRoomDataComponents();


        LandmarkDataComponent foundLandmark = null;
        // For specific items, a small radius might be okay.
        // For rooms, we need to check if the targetPosition is *inside* the room's bounds.
        float minDistanceSqToItemCenter = 0.5f * 0.5f; // Increased slightly from 0.25 for small items

        // Priority 1: Check if targetPosition is inside any Room's bounds (RoomDataComponent)
        if (allRooms != null)
        {
            foreach (var room in allRooms)
            {
                if (room.roomBoundsCollider != null && room.roomBoundsCollider.OverlapPoint(new Vector2(targetPosition.x, targetPosition.y)))
                {
                    // If the room GameObject itself has a LandmarkDataComponent, prioritize that.
                    LandmarkDataComponent roomAsLandmark = room.GetComponent<LandmarkDataComponent>();
                    if (roomAsLandmark != null) return roomAsLandmark;
                    
                    // If not, but we need a LandmarkDataComponent (e.g. for specific status notes not on RoomDataComponent),
                    // we might need to search for a 'representative' landmark within that room near the target point,
                    // or decide that the RoomDataComponent itself (if it had more general landmark-like properties) is enough.
                    // For now, if a room has a LandmarkDataComponent, we use it. Otherwise, we fall through to item check.
                    // This implies rooms that are *just* RoomDataComponent without an associated LandmarkDataComponent
                    // on the same GameObject won't be returned directly by this function for "zone" purposes,
                    // unless they contain individual items that are LandmarkDataComponents.
                }
            }
        }

        // Priority 2: Check for specific items (LandmarkDataComponent) near the target position
        // This is useful if the target is an item within a room, or an item in an open area.
        float closestDistSq = float.MaxValue;
        if (allLandmarks != null)
        {
            foreach (var landmark in allLandmarks)
            {
                if (landmark == null) continue;
                // Check distance to landmark's transform position
                float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;
                
                // A landmark is considered "at" the target position if:
                // 1. It's a very small item and the target is very close to its center.
                // 2. OR, if the landmark has its own collider (e.g., a zone trigger for a bed or chair)
                //    and the target point is inside that collider. (More complex, not implemented here directly, relies on Room for larger zones)

                if (distSq < minDistanceSqToItemCenter) // For very small, point-like landmarks
                {
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        foundLandmark = landmark;
                    }
                }
                // If the landmark has a collider that defines its "area" and it's not a room collider already checked:
                // Collider2D landmarkCollider = landmark.GetComponent<Collider2D>();
                // if (landmarkCollider != null && landmark.GetComponent<RoomDataComponent>() == null && landmarkCollider.OverlapPoint(new Vector2(targetPosition.x, targetPosition.y)))
                // {
                //    return landmark; // Found a non-room landmark whose area contains the target
                // }
            }
        }
        
        // If a specific item was found very close, return it.
        // Otherwise, if no room with a LandmarkDataComponent was found, and no item was super close,
        // it means the target is likely in an open area of a room, or a hallway.
        // In such cases, we might return null, indicating no specific "landmark zone" for that point,
        // or the RoomDataComponent itself if we adapted this function to return it.
        // Current logic prioritizes LandmarkDataComponent.
        return foundLandmark; 
    }

    void OnDestroy()
    {
        if (_currentDialogueBubbleInstance != null)
        {
            Destroy(_currentDialogueBubbleInstance);
        }
        // Ensure NPC "departs" its current zone if the object is destroyed
        if (_currentActiveLandmarkZone != null)
        {
            NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
            _currentActiveLandmarkZone = null;
        }
        // Stop any running coroutines manually if needed, though Unity usually handles this on destroy
        if(_hideBubbleCoroutine != null) StopCoroutine(_hideBubbleCoroutine);
        // If in PostInteractionPause, stop that too
        StopCoroutine(nameof(PostInteractionPauseCoroutine)); // Stop by name to be safe
    }
}