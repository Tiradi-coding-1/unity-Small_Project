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
using System.Text.RegularExpressions; // For Regex matching

// 輔助列舉來管理 NPC 的主要狀態
public enum NpcBehaviorState
{
    Idle,                   // 空閒狀態，等待或計時以觸發下一個決策
    RequestingDecision,     // 正在向後端 API 請求決策 (移動或對話)
    MovingToTarget,         // 正在向 LLM 決定的目標點移動
    ApproachingInteraction, // NPC 主動接近其他角色以發起對話
    Interacting,            // 正在進行對話 (UI 顯示，等待玩家或其他 NPC 回應)
    PostInteractionPause,   // 對話結束後的短暫停頓
    WaitingNearTarget       // 在目標附近等待資源可用
}

[RequireComponent(typeof(CharacterData))]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(NpcMovement))]
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

    [Header("Social Behavior Settings")]
    [Tooltip("NPC 在長時間空閒後，主動尋求社交的機率 (0-1)。0 表示從不。")]
    [Range(0f, 1f)]
    public float proactiveSocialChance = 0.25f;
    [Tooltip("NPC 認為空閒了多久可以考慮主動社交（秒）。")]
    public float idleTimeToConsiderSocializing = 60.0f;


    [Header("Context Providers (Assign from Scene Managers)")]
    [Tooltip("對 GameTimeManager 的引用，用於獲取當前遊戲時間。")]
    public GameTimeManager gameTimeManager;
    [Tooltip("對 SceneContextManager 的引用，用於獲取地標和其他角色列表。")]
    public SceneContextManager sceneContextManager;
    [Tooltip("對全域 DialogueUIManager 的引用（如果仍需用於非 NPC 系統訊息）。可選。")]
    public DialogueUIManager dialogueUIManager;

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb;
    private NpcMovement _npcMovement;
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
    private LandmarkDataComponent _currentActiveLandmarkZone = null;

    // 等待狀態相關變數
    private LandmarkDataComponent _originalTargetLandmarkForWaiting = null;
    private float _waitingTimer = 0f;
    private const float MaxWaitTimeNearTarget = 20.0f;
    private const float RecheckTargetInterval = 3.0f;
    private float _nextRecheckTime = 0f;
    private float _timeSpentIdle = 0f;


    // Constants for dynamic status notes
    private const string OccupancyStatusPrefix = "occupancy_";
    private const string OccupancyStatusOccupied = "occupancy_occupied";
    private string GetOccupancyStatusOccupiedBySelf() => $"{OccupancyStatusOccupied}_by_{_characterData.npcId}";


    private const string OwnerPresenceStatusPrefix = "owner_presence_";
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

        _npcMovement.moveSpeed = this.moveSpeed;
        _npcMovement.arrivalDistance = this.arrivalThreshold;
        _npcMovement.rotationSpeed = 360f;
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
        _timeSpentIdle = 0f;
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
                // Logic is event-driven by API response
                break;
            case NpcBehaviorState.MovingToTarget:
                HandleMovingToTargetState();
                break;
            case NpcBehaviorState.ApproachingInteraction:
                HandleApproachingInteractionState();
                break;
            case NpcBehaviorState.Interacting:
                // Logic is event-driven
                break;
            case NpcBehaviorState.PostInteractionPause:
                // Logic handled by coroutine
                break;
            case NpcBehaviorState.WaitingNearTarget:
                HandleWaitingNearTargetState();
                break;
        }
    }

    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState && newState != NpcBehaviorState.RequestingDecision) return;
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {_currentState} -> {newState}</color>");
        NpcBehaviorState previousState = _currentState;
        _currentState = newState;

        if (newState == NpcBehaviorState.Idle ||
            newState == NpcBehaviorState.ApproachingInteraction ||
            newState == NpcBehaviorState.Interacting ||
            newState == NpcBehaviorState.PostInteractionPause ||
            newState == NpcBehaviorState.WaitingNearTarget ||
            newState == NpcBehaviorState.RequestingDecision)
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
                _originalTargetLandmarkForWaiting = null;
                _waitingTimer = 0f;
                _timeSpentIdle = 0f;
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null;
                 if (previousState != NpcBehaviorState.WaitingNearTarget) {
                    _originalTargetLandmarkForWaiting = null;
                 }
                _timeSpentIdle = 0f;
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false;
                _originalTargetLandmarkForWaiting = null;
                _timeSpentIdle = 0f;
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false;
                _originalTargetLandmarkForWaiting = null;
                _decisionTimer = 0f;
                _timeSpentIdle = 0f;
                break;
            case NpcBehaviorState.PostInteractionPause:
                _originalTargetLandmarkForWaiting = null;
                _timeSpentIdle = 0f;
                StartCoroutine(PostInteractionPauseCoroutine());
                break;
            case NpcBehaviorState.RequestingDecision:
                _timeSpentIdle = 0f;
                break;
            case NpcBehaviorState.WaitingNearTarget:
                _hasMovementTarget = false;
                _decisionTimer = 0f;
                _waitingTimer = 0f;
                _nextRecheckTime = Time.time + RecheckTargetInterval;
                _timeSpentIdle = 0f;
                if(_originalTargetLandmarkForWaiting != null) {
                    // Debug.Log($"<color=#FFFF00>[{_characterData.characterName}] State: Now WaitingNearTarget for '{_originalTargetLandmarkForWaiting.landmarkName}'</color>");
                } else {
                     Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] State: Entered WaitingNearTarget but _originalTargetLandmarkForWaiting is NULL!</color>");
                }
                break;
        }
    }

    void HandleIdleState()
    {
        _decisionTimer += Time.deltaTime;
        _timeSpentIdle += Time.deltaTime;

        bool shouldTriggerDecision = false;
        string decisionReasonContext = null;

        if (_decisionTimer >= decisionInterval)
        {
            shouldTriggerDecision = true;
            decisionReasonContext = "Regular decision interval reached.";
        }
        else if (_timeSpentIdle >= idleTimeToConsiderSocializing && proactiveSocialChance > 0f && UnityEngine.Random.value < proactiveSocialChance)
        {
            // Debug.Log($"<color=cyan>[{_characterData.characterName}] Considering proactive social interaction after idle time.</color>");
            decisionReasonContext = "Feeling social after being idle, looking for interaction or a common area to hang out.";
            shouldTriggerDecision = true;
        }

        if (shouldTriggerDecision && !_isApiCallInProgress)
        {
            _decisionTimer = 0f;
            _timeSpentIdle = 0f;
            _lastMovementAbortReason = decisionReasonContext;
            RequestMovementDecisionAsync(true);
        }
    }

    void HandleMovingToTargetState()
    {
        if (!_hasMovementTarget) {
            // Debug.LogWarning($"[{_characterData.characterName}] In MovingToTarget state but no movement target. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle);
            return;
        }

        if (_npcMovement != null && !_npcMovement.IsMoving())
        {
            Vector3 currentPosition = transform.position;
            Vector3 targetOnPlane = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, currentPosition.z);

            if (Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)) <= _npcMovement.arrivalDistance * 1.2f)
            {
                // Debug.LogWarning($"[{_characterData.characterName}] NpcMovement stopped AND target reached (Update check). Invoking HandleArrival.");
                HandleArrivalAtMovementTarget(_currentMovementTargetWorld, _originalTargetLandmarkForWaiting == null);
            }
            else
            {
                Debug.LogWarning($"[{_characterData.characterName}] NpcMovement stopped but NOT at target in Update. Pos: {currentPosition}, Target: {targetOnPlane}. Dist: {Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)):F2}. Returning to Idle.");
                _hasMovementTarget = false;
                ChangeState(NpcBehaviorState.Idle);
            }
        }
    }

    private void HandleArrivalAtMovementTarget(Vector3 arrivedAtPosition, bool wasFromLLMDecisionPipelineOrFreshTarget)
    {
        string source = wasFromLLMDecisionPipelineOrFreshTarget ? "LLM Decision Pipeline/Fresh Target" : "Post-Waiting Original Target";
        // Debug.Log($"<color=#32CD32>[{_characterData.characterName}] HandleArrivalAtMovementTarget: Arrived at ({arrivedAtPosition.x:F1}, {arrivedAtPosition.y:F1}). Source: {source}. CurrentState: {_currentState}</color>");
        _hasMovementTarget = false;

        if (_originalTargetLandmarkForWaiting != null && _currentState != NpcBehaviorState.WaitingNearTarget)
        {
            bool originalTargetIsStillUnavailable = false;
            if (_originalTargetLandmarkForWaiting.landmarkTypeTag == "bathroom" &&
                _originalTargetLandmarkForWaiting.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) &&
                _originalTargetLandmarkForWaiting.HasDynamicStatus(OccupancyStatusOccupied) &&
                !_originalTargetLandmarkForWaiting.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
            {
                originalTargetIsStillUnavailable = true;
            }

            if (originalTargetIsStillUnavailable) {
                // Debug.Log($"<color=yellow>[{_characterData.characterName}] Arrived at waiting spot for '{_originalTargetLandmarkForWaiting.landmarkName}' which is still unavailable. Transitioning to WaitingNearTarget state.</color>");
                UpdateLandmarkStatusOnArrivalOrDeparture(arrivedAtPosition, true);
                ChangeState(NpcBehaviorState.WaitingNearTarget);
                return;
            } else {
                if (wasFromLLMDecisionPipelineOrFreshTarget) {
                    // Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at suggested waiting spot, but original target '{_originalTargetLandmarkForWaiting.landmarkName}' is now available or no longer needs waiting. Will attempt to go to original target.</color>");
                    Vector3 originalTargetPos = _originalTargetLandmarkForWaiting.transform.position;
                     _currentMovementTargetWorld = new Vector3(originalTargetPos.x, originalTargetPos.y, transform.position.z);
                    _hasMovementTarget = true;
                    if (_npcMovement != null) {
                        _npcMovement.SetMoveTarget(_currentMovementTargetWorld, () => {
                            HandleArrivalAtMovementTarget(_currentMovementTargetWorld, false);
                        });
                    }
                    ChangeState(NpcBehaviorState.MovingToTarget);
                    return;
                } else {
                    // Debug.Log($"<color=green>[{_characterData.characterName}] Successfully arrived at original target '{_originalTargetLandmarkForWaiting.landmarkName}' after waiting.</color>");
                    _originalTargetLandmarkForWaiting = null;
                }
            }
        }

        UpdateLandmarkStatusOnArrivalOrDeparture(arrivedAtPosition, true);
        ChangeState(NpcBehaviorState.Idle);
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) {
            Debug.LogWarning($"[{_characterData.characterName}] Target for interaction is null. Returning to Idle.");
            _originalTargetLandmarkForWaiting = null;
            ChangeState(NpcBehaviorState.Idle); return;
        }

        Vector3 targetCharPos = _currentTargetInteractionCharacter.transform.position;
        Vector3 directionToChar = (targetCharPos - transform.position).normalized;
        Vector3 pointToApproach = targetCharPos - directionToChar * (dialogueInitiationDistance * 0.9f);
        pointToApproach.z = transform.position.z;

        float distanceToCharActual = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(targetCharPos.x, targetCharPos.y));

        if (distanceToCharActual <= dialogueInitiationDistance)
        {
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
            // Debug.Log($"<color=orange>[{_characterData.characterName}] Reached/within dialogue range of '{_currentTargetInteractionCharacter.characterName}'. Actual dist: {distanceToCharActual:F1}. Starting dialogue.</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter);
        }
        else if (_npcMovement != null && (!_npcMovement.IsMoving() || Vector3.Distance(_npcMovement.CurrentTargetPosition, pointToApproach) > 0.5f) )
        {
             _npcMovement.SetMoveTarget(pointToApproach, () => {
                float finalDist = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(_currentTargetInteractionCharacter.transform.position.x, _currentTargetInteractionCharacter.transform.position.y));
                // Debug.Log($"<color=orange>[{_characterData.characterName}] NpcMovement arrived at approach point for '{_currentTargetInteractionCharacter.characterName}'. Final dist to char: {finalDist:F1}.</color>");
                if (finalDist <= dialogueInitiationDistance * 1.1f)
                {
                    StartDialogueAsync(_currentTargetInteractionCharacter);
                }
                else
                {
                    Debug.LogWarning($"[{_characterData.characterName}] Approached point for '{_currentTargetInteractionCharacter.characterName}', but character moved out of dialogue range ({finalDist:F1} > {dialogueInitiationDistance * 1.1f}). Re-evaluating (Idle).");
                     _originalTargetLandmarkForWaiting = null;
                    ChangeState(NpcBehaviorState.Idle);
                }
            });
        }
    }

    private IEnumerator PostInteractionPauseCoroutine()
    {
        yield return new WaitForSeconds(postDialoguePauseDuration);
        ChangeState(NpcBehaviorState.Idle);
    }

    async void RequestMovementDecisionAsync(bool isReEvaluationDueToReason = false)
    {
        if (_isApiCallInProgress) {
            Debug.LogWarning($"[{_characterData.characterName}] Movement decision request skipped: Another API call in progress.");
            return;
        }

        if (this == null || gameObject == null || !enabled) {
             Debug.LogWarning($"[NpcController RequestMovementDecisionAsync Start] Instance or GameObject was destroyed/disabled before starting. Aborting.");
            _isApiCallInProgress = false;
            return;
        }

        if (!isReEvaluationDueToReason)
        {
            _originalTargetLandmarkForWaiting = null;
            _lastMovementAbortReason = null;
        }

        UpdateLandmarkStatusOnArrivalOrDeparture(transform.position, false);

        ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;

        NpcIdentifier selfIdentifier = _characterData.GetNpcIdentifier();
        Position currentNpcPos = new Position { x = transform.position.x, y = transform.position.y };
        GameTime currentGameTime = gameTimeManager != null ? gameTimeManager.GetCurrentGameTime() : new GameTime { current_timestamp = DateTime.UtcNow.ToString("o"), time_of_day = "unknown_time" };

        List<EntityContextInfo> nearbyEntities = sceneContextManager?.GetNearbyEntities(selfIdentifier.npc_id, transform.position, 20f) ?? new List<EntityContextInfo>();
        List<LandmarkContextInfo> visibleLandmarks = sceneContextManager?.GetVisibleLandmarks(transform.position, 30f) ?? new List<LandmarkContextInfo>();

        SceneBoundaryInfo sceneBounds = sceneContextManager?.GetCurrentSceneBoundaries() ?? new SceneBoundaryInfo { min_x = -1000, max_x = 1000, min_y = -1000, max_y = 1000 };

        string dialogueSummary = "";
        if (_shortTermDialogueHistory.Count > 0) {
            var recentTurns = _shortTermDialogueHistory.TakeLast(MaxDialogueHistoryForMovementContext);
            dialogueSummary = string.Join("\n", recentTurns.Select(t => $"{(t.name ?? t.npc_id)}: \"{t.message_original_language}\""));
        }

        string augmentedContextForLLM = (isReEvaluationDueToReason && !string.IsNullOrEmpty(_lastMovementAbortReason))
                                        ? _lastMovementAbortReason
                                        : dialogueSummary;
        if(isReEvaluationDueToReason && !string.IsNullOrEmpty(_lastMovementAbortReason) && !string.IsNullOrEmpty(dialogueSummary) && augmentedContextForLLM != _lastMovementAbortReason){
            augmentedContextForLLM = $"Reason for re-evaluation/current intent: '{_lastMovementAbortReason}'. Recent dialogue: {dialogueSummary}";
        }


        NpcMovementRequest requestPayload = new NpcMovementRequest {
            npc_id = selfIdentifier.npc_id, name = selfIdentifier.name,
            current_npc_position = currentNpcPos, current_game_time = currentGameTime,
            nearby_entities = nearbyEntities, visible_landmarks = visibleLandmarks,
            scene_boundaries = sceneBounds,
            recent_dialogue_summary_for_movement = string.IsNullOrEmpty(augmentedContextForLLM) ? null : augmentedContextForLLM
        };

        string charNameForLog = _characterData != null ? _characterData.characterName : (selfIdentifier != null ? selfIdentifier.name : "NpcController (unknown)");
        // Debug.Log($"<color=cyan>[{charNameForLog}] Requesting MOVEMENT. Re-eval Reason: {isReEvaluationDueToReason}. Context: '{requestPayload.recent_dialogue_summary_for_movement?.Substring(0, Mathf.Min(requestPayload.recent_dialogue_summary_for_movement?.Length ?? 0, 100))}...'</color>");

        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);

        if (this == null || gameObject == null || !enabled)
        {
            string npcNameForLogAfterAwait = _characterData != null ? _characterData.characterName : (selfIdentifier != null ? selfIdentifier.name : "NpcController (unknown)");
            Debug.LogWarning($"[{npcNameForLogAfterAwait}] NpcController or its GameObject was destroyed or disabled during API call to /npc/think. Aborting further processing.");
            _isApiCallInProgress = false;
            return;
        }

        _isApiCallInProgress = false;

        if (response != null && response.target_destination != null) {
            // Debug.Log($"<color=#90EE90>[{_characterData.characterName}] Movement decision RX. Action: '{response.chosen_action_summary}'. Target: ({response.target_destination.x:F1}, {response.target_destination.y:F1})</color>");
            Vector3 potentialTargetWorld = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);

            bool canProceedToTargetDirectly = true;
            string currentAbortReasonForDirectMove = "";
            LandmarkDataComponent targetLocationMetaAtPotentialTarget = FindTargetLandmark(potentialTargetWorld);

            bool isBackendSuggestedWait = response.chosen_action_summary != null &&
                                          response.chosen_action_summary.ToLower().Contains("wait near");

            if (isBackendSuggestedWait)
            {
                string originalTargetNameFromAction = ParseOriginalTargetFromWaitingAction(response.chosen_action_summary);
                if (!string.IsNullOrEmpty(originalTargetNameFromAction)) {
                    _originalTargetLandmarkForWaiting = sceneContextManager?.GetAllIndividualLandmarkDataComponents()
                                                                     .FirstOrDefault(lm => lm.landmarkName.Equals(originalTargetNameFromAction, StringComparison.OrdinalIgnoreCase));
                    if (_originalTargetLandmarkForWaiting == null) {
                         var roomComp = sceneContextManager?.GetAllRoomDataComponents()
                                                            .FirstOrDefault(r => r.roomName.Equals(originalTargetNameFromAction, StringComparison.OrdinalIgnoreCase));
                         if (roomComp != null) _originalTargetLandmarkForWaiting = roomComp.GetComponent<LandmarkDataComponent>();
                    }

                     if (_originalTargetLandmarkForWaiting != null) {
                        // Debug.Log($"<color=#FFDEAD>[{_characterData.characterName}] Backend suggested moving to a waiting spot for original target '{originalTargetNameFromAction}'. Waiting spot is new target.</color>");
                     } else {
                        Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Backend suggested waiting for '{originalTargetNameFromAction}', but landmark not found in scene. Proceeding with backend target as is.</color>");
                        _originalTargetLandmarkForWaiting = null;
                     }
                } else {
                     Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Backend action summary '{response.chosen_action_summary}' implies waiting, but could not parse original target name.</color>");
                     _originalTargetLandmarkForWaiting = null;
                }
            }
            else
            {
                if (!isReEvaluationDueToReason)
                {
                    _originalTargetLandmarkForWaiting = null;
                }
                if (targetLocationMetaAtPotentialTarget != null)
                {
                    if (targetLocationMetaAtPotentialTarget.landmarkTypeTag == "bathroom" &&
                        targetLocationMetaAtPotentialTarget.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) &&
                        targetLocationMetaAtPotentialTarget.HasDynamicStatus(OccupancyStatusOccupied) &&
                        !targetLocationMetaAtPotentialTarget.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
                    {
                        canProceedToTargetDirectly = false;
                        currentAbortReasonForDirectMove = $"Toilet '{targetLocationMetaAtPotentialTarget.landmarkName}' is occupied by someone else.";
                    }
                    else if (targetLocationMetaAtPotentialTarget.landmarkTypeTag == "bedroom" &&
                             !string.IsNullOrEmpty(targetLocationMetaAtPotentialTarget.ownerNpcId) &&
                             targetLocationMetaAtPotentialTarget.ownerNpcId != _characterData.npcId &&
                             targetLocationMetaAtPotentialTarget.HasDynamicStatus(OwnerPresenceAbsent))
                    {
                        canProceedToTargetDirectly = false;
                        currentAbortReasonForDirectMove = $"Room '{targetLocationMetaAtPotentialTarget.landmarkName}' is private and owner '{targetLocationMetaAtPotentialTarget.ownerNpcId}' is absent.";
                    }
                }
            }

            if (!canProceedToTargetDirectly)
            {
                // Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Cannot proceed directly to LLM target ({potentialTargetWorld.x:F1}, {potentialTargetWorld.y:F1}). Reason: {currentAbortReasonForDirectMove}. Re-requesting with block info.</color>");
                _lastMovementAbortReason = currentAbortReasonForDirectMove;
                if (this == null || gameObject == null || !enabled) {
                     Debug.LogWarning($"[{_characterData.characterName}] NPC destroyed before re-requesting movement due to block. Aborting re-request.");
                     ChangeState(NpcBehaviorState.Idle);
                     return;
                }
                RequestMovementDecisionAsync(true);
                return;
            }

            _currentMovementTargetWorld = potentialTargetWorld;
            _hasMovementTarget = true;

            if (_npcMovement != null) {
                 _npcMovement.SetMoveTarget(_currentMovementTargetWorld, () => {
                    HandleArrivalAtMovementTarget(_currentMovementTargetWorld, true);
                 });
            } else {
                Debug.LogError($"[{_characterData.characterName}] NpcMovement component is missing, cannot execute move!", this);
                ChangeState(NpcBehaviorState.Idle); return;
            }

            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot;
                // Debug.Log($"<color=grey>[{_characterData.characterName}] Emotional state updated from API to: {_currentNpcEmotionalState.primary_emotion} (Intensity: {_currentNpcEmotionalState.intensity:F1})</color>");
            }

            bool wasDialogueDriven = response.primary_decision_drivers.GetValueOrDefault("dialogue_driven", false);
            bool wasSociallyDriven = response.primary_decision_drivers.GetValueOrDefault("social_interaction_considered", false) &&
                                      (response.chosen_action_summary.ToLower().Contains("chat with") ||
                                       response.chosen_action_summary.ToLower().Contains("talk to") ||
                                       response.chosen_action_summary.ToLower().Contains("approach") ||
                                       response.chosen_action_summary.ToLower().Contains("greet") ||
                                       response.chosen_action_summary.ToLower().Contains("say hi to"));


            string finalActionSummary = response.chosen_action_summary;

            if (wasDialogueDriven && _lastInteractedCharacter != null && !isReEvaluationDueToReason && !isBackendSuggestedWait) {
                // Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination '{finalActionSummary}' driven by dialogue with '{_lastInteractedCharacter.characterName}'. Confirming.</color>");
                if (this == null || gameObject == null || !enabled) { ChangeState(NpcBehaviorState.MovingToTarget); return; }
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, based on our chat, I've decided to: {finalActionSummary}. Sound good, {(_lastInteractedCharacter.characterName ?? "friend")}?", true);
            }
            else if (wasSociallyDriven && !isReEvaluationDueToReason)
            {
                string targetNpcName = ParseTargetNpcNameFromSocialAction(finalActionSummary); // *** Method call that caused error ***
                if (!string.IsNullOrEmpty(targetNpcName))
                {
                    CharacterData socialTarget = FindCharacterByName(targetNpcName);
                    if (socialTarget != null && socialTarget != _characterData)
                    {
                        // Debug.Log($"<color=cyan>[{_characterData.characterName}] LLM decided to socialize with '{targetNpcName}'. Approaching for interaction.</color>");
                        _currentTargetInteractionCharacter = socialTarget;
                         _originalTargetLandmarkForWaiting = null;
                        ChangeState(NpcBehaviorState.ApproachingInteraction);
                        ShowDialogueBubble_TMP($"I think I'll go say hi to {targetNpcName}.", dialogueDisplayTime * 0.9f);
                        return; 
                    }
                    else
                    {
                        Debug.LogWarning($"[{_characterData.characterName}] LLM wanted to socialize with '{targetNpcName}', but character not found or is self. Will move to target coordinates if any.");
                         ShowDialogueBubble_TMP($"Hmm... I will {finalActionSummary.ToLower()}.", dialogueDisplayTime * 0.9f);
                         if (_currentState != NpcBehaviorState.Interacting && _currentState != NpcBehaviorState.WaitingNearTarget && _currentState != NpcBehaviorState.ApproachingInteraction) {
                            ChangeState(NpcBehaviorState.MovingToTarget);
                        }
                    }
                } else {
                     Debug.LogWarning($"[{_characterData.characterName}] LLM indicated social interaction but target NPC name could not be parsed from '{finalActionSummary}'. Moving to coordinates if specified.");
                     ShowDialogueBubble_TMP($"Hmm... I will {finalActionSummary.ToLower()}.", dialogueDisplayTime * 0.9f);
                     if (_currentState != NpcBehaviorState.Interacting && _currentState != NpcBehaviorState.WaitingNearTarget && _currentState != NpcBehaviorState.ApproachingInteraction) {
                        ChangeState(NpcBehaviorState.MovingToTarget);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(finalActionSummary) && finalActionSummary.Length > 3 && !isReEvaluationDueToReason)
            {
                bool isGenericAction = finalActionSummary.ToLower().Contains("explore") ||
                                       finalActionSummary.ToLower().Contains("wander") ||
                                       finalActionSummary.ToLower().Contains("idle") ||
                                       finalActionSummary.ToLower().Contains("nothing special");
                if (!isGenericAction)
                {
                    ShowDialogueBubble_TMP($"Hmm... I will {finalActionSummary.ToLower()}.", dialogueDisplayTime * (isBackendSuggestedWait ? 1.0f : 0.75f) );
                }

                if (_currentState != NpcBehaviorState.Interacting && _currentState != NpcBehaviorState.WaitingNearTarget && _currentState != NpcBehaviorState.ApproachingInteraction) {
                    ChangeState(NpcBehaviorState.MovingToTarget);
                }
            }
            else if (_currentState != NpcBehaviorState.Interacting && _currentState != NpcBehaviorState.WaitingNearTarget && _currentState != NpcBehaviorState.ApproachingInteraction) {
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Failed to get valid movement decision from API or target was null. Returning to Idle.");
            _originalTargetLandmarkForWaiting = null;
            ChangeState(NpcBehaviorState.Idle);
        }
    }

    private void HandleWaitingNearTargetState()
    {
        _waitingTimer += Time.deltaTime;

        if (_originalTargetLandmarkForWaiting == null) {
            Debug.LogWarning($"[{_characterData.characterName}] In WaitingNearTarget state but _originalTargetLandmarkForWaiting is null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle);
            return;
        }

        if (_waitingTimer > MaxWaitTimeNearTarget)
        {
            // Debug.Log($"<color=yellow>[{_characterData.characterName}] Waited too long for '{_originalTargetLandmarkForWaiting.landmarkName}'. Max wait time {MaxWaitTimeNearTarget}s exceeded. Re-evaluating (Idle).</color>");
            string reason = $"Waited too long for '{_originalTargetLandmarkForWaiting.landmarkName}' to become available.";
            _originalTargetLandmarkForWaiting = null;
            _lastMovementAbortReason = reason;
            ChangeState(NpcBehaviorState.Idle);
            return;
        }

        if (Time.time >= _nextRecheckTime)
        {
            _nextRecheckTime = Time.time + RecheckTargetInterval;

            // Debug.Log($"<color=grey>[{_characterData.characterName}] Re-checking availability of '{_originalTargetLandmarkForWaiting.landmarkName}' while waiting...</color>");
            bool originalTargetNowAvailable = true;

            if (_originalTargetLandmarkForWaiting.landmarkTypeTag == "bathroom")
            {
                if (_originalTargetLandmarkForWaiting.HasDynamicStatus(OccupancyStatusOccupied) &&
                    !_originalTargetLandmarkForWaiting.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
                {
                    originalTargetNowAvailable = false;
                }
            }
            // Add checks for other types of resources if necessary

            if (originalTargetNowAvailable)
            {
                // Debug.Log($"<color=green>[{_characterData.characterName}] Original target '{_originalTargetLandmarkForWaiting.landmarkName}' is now available! Attempting to move to it.</color>");

                Vector3 targetPos = _originalTargetLandmarkForWaiting.transform.position;

                _currentMovementTargetWorld = new Vector3(targetPos.x, targetPos.y, transform.position.z);
                _hasMovementTarget = true;
                // _originalTargetLandmarkForWaiting is not cleared here yet.
                // It will be cleared in HandleArrivalAtMovementTarget if arrival at the original target is successful.

                if (_npcMovement != null) {
                     _npcMovement.SetMoveTarget(_currentMovementTargetWorld, () => {
                        HandleArrivalAtMovementTarget(_currentMovementTargetWorld, false); 
                     });
                }
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
            else
            {
                // Debug.Log($"<color=grey>[{_characterData.characterName}] Original target '{_originalTargetLandmarkForWaiting.landmarkName}' still unavailable. Continuing to wait.</color>");
            }
        }
    }

    async void StartDialogueAsync(CharacterData otherCharacter, string initialNpcUtteranceSeed = null, bool isFollowUpDialogue = false)
    {
        if (_isApiCallInProgress && !isFollowUpDialogue) {
             Debug.LogWarning($"[{_characterData.characterName}] Dialogue with '{otherCharacter.characterName}' skipped: API call in progress.");
            return;
        }
        if (!isFollowUpDialogue && (
            _currentState == NpcBehaviorState.Interacting ||
            _currentState == NpcBehaviorState.PostInteractionPause ||
            _currentState == NpcBehaviorState.RequestingDecision ||
            _currentState == NpcBehaviorState.WaitingNearTarget
           )) {
             Debug.LogWarning($"[{_characterData.characterName}] Initial dialogue with '{otherCharacter.characterName}' skipped: NPC busy (State: {_currentState}).");
            return;
        }

        if (this == null || gameObject == null || !enabled) {
            string npcNameForLog = _characterData != null ? _characterData.characterName : "NpcController (unknown)";
            Debug.LogWarning($"[{npcNameForLog}] NpcController or its GameObject was destroyed/disabled before starting StartDialogueAsync. Aborting.");
            _isApiCallInProgress = false;
            return;
        }

        _originalTargetLandmarkForWaiting = null; 

        ChangeState(NpcBehaviorState.RequestingDecision);
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
        // Debug.Log($"<color=orange>[{_characterData.characterName}] Initiating DIALOGUE with '{otherCharacter.characterName}'. FollowUp: {isFollowUpDialogue}. LLM Seed: '{npcInitialPromptForLLM}'. Emotion: {selfEmotionStringForPrompt}</color>");

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

        if (this == null || gameObject == null || !enabled)
        {
            string npcNameForLogAfterAwait = _characterData != null ? _characterData.characterName : "NpcController (unknown)";
            string otherCharNameForLog = otherCharacter != null ? otherCharacter.characterName : "other character (unknown)";
            Debug.LogWarning($"[{npcNameForLogAfterAwait}] NpcController or its GameObject was destroyed or disabled during API call for dialogue with '{otherCharNameForLog}'. Aborting processing.");
            _isApiCallInProgress = false;
            if(_currentState == NpcBehaviorState.RequestingDecision) ChangeState(NpcBehaviorState.Idle);
            return;
        }

        _isApiCallInProgress = false;

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
            ChangeState(NpcBehaviorState.Interacting);
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
        }
        ChangeState(NpcBehaviorState.PostInteractionPause);
    }

public void ShowDialogueBubble_TMP(string message, float duration)
{
    Debug.Log($"[Bubble DEBUG] NPC '{_characterData.characterName}' 嘗試顯示氣泡內容：{message}");

    if (dialogueBubblePrefab_TMP == null)
    {
        Debug.LogWarning($"[Bubble WARNING] NPC '{_characterData.characterName}' 沒有指定 dialogueBubblePrefab_TMP！");
        
        if (dialogueUIManager != null && dialogueUIManager.gameObject.activeInHierarchy)
        {
            dialogueUIManager.ShowDialogue(_characterData.characterName, message, duration);
        }
        else
        {
            Debug.LogError($"[Bubble ERROR] '{_characterData.characterName}' 缺少 prefab 且無法 fallback 至 UIManager，訊息如下：{message}", this);
        }
        return;
    }

    if (_hideBubbleCoroutine != null)
    {
        StopCoroutine(_hideBubbleCoroutine);
        _hideBubbleCoroutine = null;
    }

    if (_currentDialogueBubbleInstance == null)
    {
        Vector3 bubblePosition = transform.position + Vector3.up * dialogueBubbleOffsetY;
        _currentDialogueBubbleInstance = Instantiate(dialogueBubblePrefab_TMP, bubblePosition, Quaternion.identity, transform);
        Debug.Log($"[Bubble DEBUG] 已實例化對話氣泡於位置：{bubblePosition}");

        _dialogueTextTMP = _currentDialogueBubbleInstance.GetComponentInChildren<TextMeshProUGUI>();
        if (_dialogueTextTMP == null)
        {
            _dialogueTextTMP = _currentDialogueBubbleInstance.GetComponent<TextMeshProUGUI>();
        }
    }

    if (_dialogueTextTMP != null)
    {
        _dialogueTextTMP.text = message;
        Debug.Log($"[Bubble DEBUG] 設定 TextMeshProUGUI 為：{message}");
        _currentDialogueBubbleInstance.SetActive(true);

        if (duration > 0)
        {
            _hideBubbleCoroutine = StartCoroutine(HideDialogueBubbleAfterDelay_TMP(duration));
        }
    }
    else
    {
        Debug.LogError($"[Bubble ERROR] '{_characterData.characterName}' 的 Dialogue Bubble Prefab 中沒有找到 TextMeshProUGUI 元件！", this);
        if (_currentDialogueBubbleInstance != null)
        {
            Destroy(_currentDialogueBubbleInstance);
        }
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
            _currentState == NpcBehaviorState.WaitingNearTarget ||
            _currentState == NpcBehaviorState.RequestingDecision) return;

        if (interactionTrigger == null || !interactionTrigger.IsTouching(otherCollider) || otherCollider.gameObject == gameObject) return;

        if (otherCollider.TryGetComponent<CharacterData>(out CharacterData encounteredCharacter))
        {
            if (encounteredCharacter == _characterData) return;
            if (_currentTargetInteractionCharacter != null && _currentTargetInteractionCharacter == encounteredCharacter) return;

            // Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}'. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
            _hasMovementTarget = false;
            _originalTargetLandmarkForWaiting = null;
            ChangeState(NpcBehaviorState.ApproachingInteraction);
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            if (_currentState == NpcBehaviorState.ApproachingInteraction) {
                // Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left trigger while approaching. Returning to Idle.</color>");
                _currentTargetInteractionCharacter = null;
                if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
                _originalTargetLandmarkForWaiting = null;
                ChangeState(NpcBehaviorState.Idle);
            }
        }
    }

    private void UpdateLandmarkStatusOnArrivalOrDeparture(Vector3 npcCurrentPosition, bool isArrivalEvent)
    {
        LandmarkDataComponent eventLandmark = FindTargetLandmark(npcCurrentPosition);

        if (isArrivalEvent)
        {
            if (eventLandmark != null && eventLandmark != _currentActiveLandmarkZone)
            {
                NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                _currentActiveLandmarkZone = eventLandmark;

                if (eventLandmark.landmarkTypeTag == "bedroom" && eventLandmark.ownerNpcId == _characterData.npcId)
                {
                    eventLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresencePresent);
                }
                else if (eventLandmark.landmarkTypeTag == "bathroom")
                {
                    if (!eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) ||
                        eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
                    {
                         eventLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, GetOccupancyStatusOccupiedBySelf());
                    } else if (eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && !eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()) ){
                        Debug.LogWarning($"[{_characterData.characterName}] Arrived at bathroom '{eventLandmark.landmarkName}' but it's already occupied by someone else. Status was not pre-checked or wait logic failed?");
                    }
                }
                // Debug.Log($"[{_characterData.characterName}] Entered zone: '{eventLandmark.landmarkName}' ({eventLandmark.landmarkTypeTag}).");
            }
            else if (eventLandmark == null && _currentActiveLandmarkZone != null)
            {
                 NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                 _currentActiveLandmarkZone = null;
            }
        }
        else // Is Departure Event
        {
            if (_currentActiveLandmarkZone != null)
            {
                bool hasLeftCurrentZone = true;
                if (eventLandmark == _currentActiveLandmarkZone)
                {
                    hasLeftCurrentZone = false;
                }
                else if (eventLandmark != null && eventLandmark != _currentActiveLandmarkZone)
                {
                    hasLeftCurrentZone = true;
                }
                else if (eventLandmark == null)
                {
                     hasLeftCurrentZone = true;
                }

                if (hasLeftCurrentZone)
                {
                    NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                    _currentActiveLandmarkZone = eventLandmark;
                }
            }
            else if (eventLandmark != null)
            {
                // This case is more like an "initial entry" rather than departure, handled by arrival logic.
            }
        }
    }

    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark)
    {
        if (departedLandmark == null || _characterData == null) return;

        // Debug.Log($"[{_characterData.characterName}] Notifying departure from: '{departedLandmark.landmarkName}' ({departedLandmark.landmarkTypeTag}).");
        if (departedLandmark.landmarkTypeTag == "bedroom" && departedLandmark.ownerNpcId == _characterData.npcId)
        {
            departedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresenceAbsent);
        }
        else if (departedLandmark.landmarkTypeTag == "bathroom")
        {
            if(departedLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
            {
                departedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, null);
            }
        }
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

        if (allRooms != null)
        {
            foreach (var room in allRooms)
            {
                if (room.roomBoundsCollider != null && room.roomBoundsCollider.OverlapPoint(new Vector2(targetPosition.x, targetPosition.y)))
                {
                    LandmarkDataComponent roomAsLandmark = room.GetComponent<LandmarkDataComponent>();
                    if (roomAsLandmark != null) return roomAsLandmark;
                }
            }
        }

        float minDistanceSqToItemCenter = arrivalThreshold * arrivalThreshold * 1.5f;
        float closestDistSq = float.MaxValue;
        if (allLandmarks != null)
        {
            foreach (var landmark in allLandmarks)
            {
                if (landmark == null) continue;
                float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;

                if (distSq < minDistanceSqToItemCenter)
                {
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        foundLandmark = landmark;
                    }
                }
            }
        }
        return foundLandmark;
    }

    // *** 此處定義了 ParseOriginalTargetFromWaitingAction ***
    private string ParseOriginalTargetFromWaitingAction(string actionSummary)
    {
        if (string.IsNullOrEmpty(actionSummary)) return null;
        
        // Pattern to match "wait near Landmark_Name" or "wait near Landmark Name" (possibly with parentheses after)
        // It tries to capture a sequence of words after "wait near " that doesn't start with '('
        string pattern = @"wait near\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)"; 
        Match match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
        {
            string potentialTarget = match.Groups[1].Value.Trim();
            // Debug.Log($"Parsed original target from '{actionSummary}': '{potentialTarget}'");
            return potentialTarget;
        }

        // Fallback for slightly different phrasing, e.g., "wait by the X"
        pattern = @"wait by the\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)";
        match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
        {
            string potentialTarget = match.Groups[1].Value.Trim();
            // Attempt to clean common additions like "Door" if it's a general instruction
            if (potentialTarget.EndsWith("Door", StringComparison.OrdinalIgnoreCase)) {
                potentialTarget = potentialTarget.Substring(0, potentialTarget.Length - "Door".Length).Trim();
            }
            // Debug.Log($"Parsed original target (fallback) from '{actionSummary}': '{potentialTarget}'");
            return potentialTarget;
        }
        // Debug.LogWarning($"Could not parse original target from action: '{actionSummary}'");
        return null;
    }
    
    // *** 此處定義了 ParseTargetNpcNameFromSocialAction ***
    private string ParseTargetNpcNameFromSocialAction(string actionSummary)
    {
        if (string.IsNullOrEmpty(actionSummary)) return null;
        // Examples: "Approach Lin Yao-Yu for a chat", "Go chat with Tsai Szu-Yen", "Say hi to M01"
        // Pattern to match common phrases indicating social interaction with a named target
        string pattern = @"(?:chat with|talk to|approach|greet|say hi to)\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)";
        Match match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value.Trim();
        }
        return null;
    }

    // *** 此處定義了 FindCharacterByName ***
    private CharacterData FindCharacterByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // Ideal way: if SceneContextManager caches and provides a list of all CharacterData
        // List<CharacterData> allCharacters = sceneContextManager?.GetAllCharacterDataInstances(); 
        // if (allCharacters != null) { ... }

        // Fallback: FindObjectsOfType - use with caution if called very frequently or in large scenes
        CharacterData[] allCharactersInScene = FindObjectsOfType<CharacterData>();
        foreach (CharacterData character in allCharactersInScene)
        {
            if (character.characterName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(character.npcId) && character.npcId.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return character;
            }
        }
        // Debug.LogWarning($"FindCharacterByName: Character '{name}' not found.");
        return null;
    }

    void OnDestroy()
    {
        if (_currentDialogueBubbleInstance != null)
        {
            Destroy(_currentDialogueBubbleInstance);
        }
        if (_currentActiveLandmarkZone != null)
        {
            NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
            _currentActiveLandmarkZone = null;
        }
        if(_hideBubbleCoroutine != null) StopCoroutine(_hideBubbleCoroutine);
        StopAllCoroutines();
    }
}