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
                break;
            case NpcBehaviorState.MovingToTarget:
                HandleMovingToTargetState();
                break;
            case NpcBehaviorState.ApproachingInteraction:
                HandleApproachingInteractionState();
                break;
            case NpcBehaviorState.Interacting:
                break;
            case NpcBehaviorState.PostInteractionPause:
                break;
        }
    }

    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState && newState != NpcBehaviorState.RequestingDecision) return;
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {_currentState} -> {newState}</color>");
        _currentState = newState;

        if (newState == NpcBehaviorState.Idle ||
            newState == NpcBehaviorState.ApproachingInteraction ||
            newState == NpcBehaviorState.Interacting ||
            newState == NpcBehaviorState.PostInteractionPause ||
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
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null;
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false;
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false;
                _decisionTimer = 0f;
                break;
            case NpcBehaviorState.PostInteractionPause:
                StartCoroutine(PostInteractionPauseCoroutine());
                break;
            case NpcBehaviorState.RequestingDecision:
                break;
        }
    }

    void HandleIdleState()
    {
        _decisionTimer += Time.deltaTime;
        if (_decisionTimer >= decisionInterval && !_isApiCallInProgress)
        {
            _decisionTimer = 0f;
            RequestMovementDecisionAsync();
        }
    }

    void HandleMovingToTargetState()
    {
        if (!_hasMovementTarget) {
            ChangeState(NpcBehaviorState.Idle); return;
        }
        if (_npcMovement != null && !_npcMovement.IsMoving())
        {
            Vector3 currentPosition = transform.position;
            Vector3 targetOnPlane = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, currentPosition.z);
            if (Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)) <= _npcMovement.arrivalDistance * 1.1f)
            {
                Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target: ({targetOnPlane.x:F1}, {targetOnPlane.y:F1}) via NpcMovement.</color>");
                _hasMovementTarget = false;
                UpdateLandmarkStatusOnArrivalOrDeparture(_currentMovementTargetWorld, true);
                ChangeState(NpcBehaviorState.Idle);
            }
            else
            {
                Debug.LogWarning($"[{_characterData.characterName}] NpcMovement stopped but not at arrival threshold for LLM target. Pos: {currentPosition}, Target: {targetOnPlane}. Dist: {Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y))}. Returning to Idle.");
                _hasMovementTarget = false;
                ChangeState(NpcBehaviorState.Idle);
            }
        }
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) {
            Debug.LogWarning($"[{_characterData.characterName}] Target for interaction is null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle); return;
        }

        Vector3 targetCharPos = _currentTargetInteractionCharacter.transform.position;
        Vector3 directionToChar = (targetCharPos - transform.position).normalized;
        Vector3 pointToApproach = targetCharPos - directionToChar * (dialogueInitiationDistance * 0.9f); // Approach slightly closer than exact distance
        pointToApproach.z = transform.position.z;

        float distanceToCharActual = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(targetCharPos.x, targetCharPos.y));

        if (distanceToCharActual <= dialogueInitiationDistance)
        {
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
            Debug.Log($"<color=orange>[{_characterData.characterName}] Already within/reached dialogue range of '{_currentTargetInteractionCharacter.characterName}'. Actual dist: {distanceToCharActual:F1}</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter);
        }
        // If not already moving towards this specific approach point, or target changed significantly
        else if (_npcMovement != null && (!_npcMovement.IsMoving() || Vector3.Distance(_npcMovement.CurrentTargetPosition, pointToApproach) > 0.5f) )
        {
             _npcMovement.SetMoveTarget(pointToApproach, () => {
                // This onArrival callback for NpcMovement approaching another character
                float finalDist = Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(_currentTargetInteractionCharacter.transform.position.x, _currentTargetInteractionCharacter.transform.position.y));
                Debug.Log($"<color=orange>[{_characterData.characterName}] Approached '{_currentTargetInteractionCharacter.characterName}' (NpcMovement arrival). Final dist: {finalDist:F1}. Starting dialogue.</color>");
                StartDialogueAsync(_currentTargetInteractionCharacter);
            });
        }
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
        // Update status of current zone BEFORE deciding to move (departure logic)
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

        Debug.Log($"<color=cyan>[{_characterData.characterName}] Requesting MOVEMENT. Re-eval: {isReEvaluationDueToBlock}. Context: '{requestPayload.recent_dialogue_summary_for_movement?.Substring(0, Mathf.Min(requestPayload.recent_dialogue_summary_for_movement?.Length ?? 0, 100))}...'</color>");
        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);
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
                    targetLocationMeta.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && // Check if any occupancy status exists
                    targetLocationMeta.HasDynamicStatus(OccupancyStatusOccupied) &&         // Check if specifically "occupied"
                    !targetLocationMeta.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf())) // And not occupied by self
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
                RequestMovementDecisionAsync(true);
                return;
            }

            _currentMovementTargetWorld = potentialTargetWorld;
            _hasMovementTarget = true;

            if (_npcMovement != null) {
                 _npcMovement.SetMoveTarget(_currentMovementTargetWorld);
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
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, based on our chat, I've decided to: {response.chosen_action_summary}. Sound good, {(_lastInteractedCharacter.characterName ?? "friend")}?", true);
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

        _isApiCallInProgress = false;
        ChangeState(NpcBehaviorState.Interacting);

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
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
        if (dialogueBubblePrefab_TMP == null) {
            if (dialogueUIManager != null && dialogueUIManager.gameObject.activeInHierarchy) {
                dialogueUIManager.ShowDialogue(_characterData.characterName, message, duration);
            } else {
                Debug.LogError($"[{_characterData.characterName}] Dialogue Bubble Prefab (TMP) not assigned and no fallback UI! Msg: {message}", this);
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
            Debug.LogError($"[{_characterData.characterName}] Dialogue Bubble Prefab (TMP) missing TextMeshProUGUI.", this);
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
            if (encounteredCharacter == _characterData) return;
            Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}'. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            if (_npcMovement != null && _npcMovement.IsMoving()) _npcMovement.StopMovement();
            _hasMovementTarget = false;
            ChangeState(NpcBehaviorState.ApproachingInteraction);
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            if (_currentState == NpcBehaviorState.ApproachingInteraction) {
                Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left while approaching. Returning to Idle.</color>");
                _currentTargetInteractionCharacter = null;
                ChangeState(NpcBehaviorState.Idle);
            }
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
                    if (!eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()) && // Not already occupied by self
                        !eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix)) // And not occupied by anyone else
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
        }
        else // Is Departure Event (called before new movement decision)
        {
            // If current position is no longer within the _currentActiveLandmarkZone, NPC has departed it.
            // Or if current logic is "before deciding to move, update status of where I *am* now as departing".
            if (_currentActiveLandmarkZone != null)
            {
                // A simple check: if eventLandmark (at current position) is NOT _currentActiveLandmarkZone, means we left.
                // This logic could be more robust by checking if npcCurrentPosition is outside _currentActiveLandmarkZone's bounds.
                if (eventLandmark != _currentActiveLandmarkZone)
                {
                    NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                    _currentActiveLandmarkZone = eventLandmark; // Update to new zone, or null if in a hallway
                }
            }
        }
    }

    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark)
    {
        if (departedLandmark == null) return;

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
    }

    private LandmarkDataComponent FindTargetLandmark(Vector3 targetPosition)
    {
        if (sceneContextManager == null)
        {
            Debug.LogWarning("SceneContextManager not available in NpcController.FindTargetLandmark");
            return null;
        }
        // Use the method from SceneContextManager which now returns ALL LandmarkDataComponents
        List<LandmarkDataComponent> allLandmarks = sceneContextManager.GetAllIndividualLandmarkDataComponents();
        List<RoomDataComponent> allRooms = sceneContextManager.GetAllRoomDataComponents();


        LandmarkDataComponent foundLandmark = null;
        float minDistanceSqToItem = 0.25f * 0.25f; // Small radius for specific items

        // Priority 1: Check if inside a Room's bounds
        if (allRooms != null)
        {
            foreach (var room in allRooms)
            {
                if (room.roomBoundsCollider != null && room.roomBoundsCollider.OverlapPoint(new Vector2(targetPosition.x, targetPosition.y)))
                {
                    // If the room itself has a LandmarkDataComponent (e.g. for general room info)
                    LandmarkDataComponent roomAsLandmark = room.GetComponent<LandmarkDataComponent>();
                    if (roomAsLandmark != null) return roomAsLandmark;
                    // If not, we consider the "Room" landmark concept handled by RoomDataComponent itself.
                    // For NpcController's purpose here (checking status of a point), we might need
                    // to decide if a "RoomDataComponent" should be returned or if we only care about
                    // LandmarkDataComponents. Let's assume we need LandmarkDataComponent for status notes.
                    // If RoomDataComponent itself handles occupancy/owner status, then this method needs to be smarter.
                    // For now, if target is inside a room, check if the room GameObject itself has a LandmarkDataComponent.
                }
            }
        }

        // Priority 2: Check for specific items (LandmarkDataComponent) near the target position
        if (allLandmarks != null)
        {
            foreach (var landmark in allLandmarks)
            {
                if (landmark == null) continue;
                float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;
                if (distSq < minDistanceSqToItem)
                {
                    minDistanceSqToItem = distSq;
                    foundLandmark = landmark;
                }
            }
        }
        return foundLandmark;
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
        }
    }
}