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

// 輔助列舉來管理 NPC 的主要狀態
public enum NpcBehaviorState
{
    Idle,
    RequestingDecision,
    MovingToTarget,
    ApproachingInteraction, // NPC 主動接近其他角色以發起對話
    Interacting,            // 正在進行對話 (UI 顯示，等待玩家或其他 NPC 回應)
    PostInteractionPause    // 對話結束後的短暫停頓
}

[RequireComponent(typeof(CharacterData))]
[RequireComponent(typeof(Rigidbody2D))] // Rigidbody2D is used by NpcMovement
[RequireComponent(typeof(Collider2D))] // 通常用於檢測互動觸發器
[RequireComponent(typeof(NpcMovement))] // **** NEW: Ensure NpcMovement component is present ****
public class NpcController : MonoBehaviour
{
    [Header("NPC Core Configuration")]
    [Tooltip("當 NPC 空閒或完成任務後，進行 LLM 移動決策之間的時間（秒）。")]
    public float decisionInterval = 15.0f;
    // [Tooltip("NPC 的移動速度（Unity 單位/秒）。")] // Now handled by NpcMovement
    // public float moveSpeed = 2.0f;
    [Tooltip("認為 NPC 已到達目標的距離閾值（用於 NpcMovement）。")]
    public float arrivalThreshold = 0.3f; // This can configure NpcMovement's arrivalDistance
    [Tooltip("可選：視覺模型的 Transform，用於朝向移動方向旋轉（現在主要由 NpcMovement 控制）。")]
    public Transform visualModelTransform; // NpcMovement can also use this or its own reference


    [Header("Interaction Settings")]
    [Tooltip("附加到此 NPC 的 Collider2D（設定為 IsTrigger=true），用於偵測其他角色以進行互動。")]
    public Collider2D interactionTrigger;
    [Tooltip("如果 NPC 正在接近互動目標，它會在此距離停下來發起對話。")]
    public float dialogueInitiationDistance = 1.5f;
    [Tooltip("NPC 在對話後可能暫停的持續時間（秒），然後重新評估。")]
    public float postDialoguePauseDuration = 2.0f;


    [Header("Context Providers (Assign from Scene Managers)")]
    [Tooltip("對 GameTimeManager 的引用，用於獲取當前遊戲時間。")]
    public GameTimeManager gameTimeManager;
    [Tooltip("對 SceneContextManager 的引用，用於獲取地標和其他角色列表。")]
    public SceneContextManager sceneContextManager;
    [Tooltip("對 DialogueUIManager 的引用，用於顯示對話文本。")]
    public DialogueUIManager dialogueUIManager;

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb; // NpcMovement uses this
    private NpcMovement _npcMovement; // **** NEW: Reference to NpcMovement component ****
    private NpcBehaviorState _currentState = NpcBehaviorState.Idle;
    private float _decisionTimer = 0f;
    private Vector3 _currentMovementTargetWorld; // LLM 決定的世界座標目標點
    private bool _hasMovementTarget = false;
    private bool _isApiCallInProgress = false;
    private CharacterData _currentTargetInteractionCharacter = null; // 當前主動接近以進行互動的目標角色
    private CharacterData _lastInteractedCharacter = null; // 上一個與之互動的角色

    private List<DialogueTurn> _shortTermDialogueHistory = new List<DialogueTurn>();
    private const int MaxDialogueHistoryForMovementContext = 6;

    private NpcApiModels.NpcEmotionalState _currentNpcEmotionalState;

    private const string OccupancyStatusPrefix = "occupancy_";
    private const string OccupancyStatusOccupied = "occupancy_occupied";
    private const string OccupancyStatusVacant = "occupancy_vacant";

    private const string OwnerPresenceStatusPrefix = "owner_presence_";
    private const string OwnerPresencePresent = "owner_presence_present";
    private const string OwnerPresenceAbsent = "owner_presence_absent";

    private string _lastMovementAbortReason = null;


    // --- 初始化 ---
    void Awake()
    {
        _characterData = GetComponent<CharacterData>();
        _rb = GetComponent<Rigidbody2D>(); // NpcMovement requires and uses this
        _npcMovement = GetComponent<NpcMovement>(); // **** NEW: Get NpcMovement component ****

        // Configure NpcMovement based on NpcController settings if desired
        if (_npcMovement != null)
        {
            // _npcMovement.moveSpeed = this.moveSpeed; // If NpcController still defined moveSpeed
            _npcMovement.arrivalDistance = this.arrivalThreshold;
            if (visualModelTransform != null && _npcMovement.visualModelToRotate == null)
            {
                _npcMovement.visualModelToRotate = this.visualModelTransform;
            }
        }
        else
        {
            Debug.LogError($"[{gameObject.name}] NpcController requires an NpcMovement component but it was not found. Disabling NpcController.", this);
            enabled = false; return;
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
        if (dialogueUIManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': DialogueUIManager not assigned!", this);

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
        _currentState = newState;

        switch (newState)
        {
            case NpcBehaviorState.Idle:
                _hasMovementTarget = false;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop NpcMovement component
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null;
                // Movement target is set by RequestMovementDecisionAsync using _npcMovement.SetMoveTarget
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement();
                // Movement target is set in HandleApproachingInteractionState using _npcMovement.SetMoveTarget
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement();
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
        if (!_hasMovementTarget) { // If no LLM target was set or movement completed
            ChangeState(NpcBehaviorState.Idle); 
            return;
        }

        // NpcMovement component handles the actual movement and arrival detection.
        // We just need to check if it's still moving towards the LLM target.
        if (!_npcMovement.IsMoving()) // NpcMovement has arrived or was stopped
        {
            Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target (handled by NpcMovement): ({_currentMovementTargetWorld.x:F1}, {_currentMovementTargetWorld.y:F1})</color>");
            // Ensure final position if NpcMovement didn't snap it exactly (though it should)
            transform.position = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, transform.position.z);
            _hasMovementTarget = false;
            
            UpdateLandmarkStatusOnArrival(_currentMovementTargetWorld);
            ChangeState(NpcBehaviorState.Idle);
        }
        // Visual rotation is handled by NpcMovement if visualModelToRotate is assigned to it.
        // Or, if NpcController's visualModelTransform is still used for other visual cues,
        // that logic could remain, but it's cleaner if NpcMovement handles its own visual rotation during movement.
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) { 
            Debug.LogWarning($"[{_characterData.characterName}] Target for interaction is null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle); return;
        }
        
        Vector3 targetCharPos = _currentTargetInteractionCharacter.transform.position;
        // Calculate a point slightly offset from the character to avoid overlapping, or use dialogueInitiationDistance directly
        Vector3 directionToChar = (targetCharPos - transform.position).normalized;
        Vector3 desiredPosNearChar = targetCharPos - directionToChar * dialogueInitiationDistance;
        desiredPosNearChar.z = transform.position.z; // Keep on same Z plane

        float distanceToDesiredPos = Vector2.Distance(
            new Vector2(transform.position.x, transform.position.y), 
            new Vector2(desiredPosNearChar.x, desiredPosNearChar.y)
        );

        // If not already at the dialogue initiation spot (or very close to it)
        if (distanceToDesiredPos > _npcMovement.arrivalDistance * 1.1f) // Use NpcMovement's arrival as a threshold
        {
            if (!_npcMovement.IsMoving() || (_npcMovement.IsMoving() && Vector3.Distance(_npcMovement.CurrentTargetPosition, desiredPosNearChar) > 0.1f) )
            {
                // If not moving, or moving to a different target, set new target
                 _npcMovement.SetMoveTarget(desiredPosNearChar, () => {
                    // This callback will be invoked by NpcMovement upon arrival
                    Debug.Log($"<color=orange>[{_characterData.characterName}] Reached '{_currentTargetInteractionCharacter.characterName}' for dialogue (via NpcMovement callback).</color>");
                    StartDialogueAsync(_currentTargetInteractionCharacter);
                });
            }
        } else { // Already at or very near the interaction spot
             if (_npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop if was moving for some reason
            Debug.Log($"<color=orange>[{_characterData.characterName}] Already near '{_currentTargetInteractionCharacter.characterName}'. Initiating dialogue.</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter);
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
            Debug.LogWarning($"[{_characterData.characterName}] Movement decision request skipped: Another API call is already in progress.");
            return;
        }
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
        
        string previousAbortReasonForPrompt = null;
        if(isReEvaluationDueToBlock && !string.IsNullOrEmpty(_lastMovementAbortReason)){
            previousAbortReasonForPrompt = $"Previous movement attempt was aborted because: {_lastMovementAbortReason}. Please choose a new action or destination.";
        }

        NpcMovementRequest requestPayload = new NpcMovementRequest {
            npc_id = selfIdentifier.npc_id, name = selfIdentifier.name,
            current_npc_position = currentNpcPos, current_game_time = currentGameTime,
            nearby_entities = nearbyEntities, visible_landmarks = visibleLandmarks,
            scene_boundaries = sceneBounds,
            recent_dialogue_summary_for_movement = string.IsNullOrEmpty(dialogueSummary) ? null : dialogueSummary,
        };
        if(previousAbortReasonForPrompt != null){
             requestPayload.recent_dialogue_summary_for_movement = string.IsNullOrEmpty(requestPayload.recent_dialogue_summary_for_movement)
                ? previousAbortReasonForPrompt
                : $"{previousAbortReasonForPrompt}\nRecent dialogue was: {requestPayload.recent_dialogue_summary_for_movement}";
        }

        Debug.Log($"<color=cyan>[{_characterData.characterName}] Requesting MOVEMENT decision. Re-eval: {isReEvaluationDueToBlock}. Dialogue/Context: '{requestPayload.recent_dialogue_summary_for_movement?.Substring(0, Mathf.Min(requestPayload.recent_dialogue_summary_for_movement?.Length ?? 0, 100))}...'</color>");
        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);
        _isApiCallInProgress = false; 
        _lastMovementAbortReason = null;

        if (response != null && response.target_destination != null) {
            Debug.Log($"<color=#90EE90>[{_characterData.characterName}] Movement decision received. Action: '{response.chosen_action_summary}'. Target: ({response.target_destination.x:F1}, {response.target_destination.y:F1})</color>");
            Vector3 potentialTargetWorld = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);

            bool canProceedToTarget = true;
            string currentAbortReason = "";
            LandmarkDataComponent targetLocationMeta = FindTargetLandmark(potentialTargetWorld);

            if (targetLocationMeta != null)
            {
                if (targetLocationMeta.landmarkTypeTag == "bathroom" && targetLocationMeta.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && targetLocationMeta.HasDynamicStatus(OccupancyStatusOccupied))
                {
                    canProceedToTarget = false;
                    currentAbortReason = "Toilet is occupied.";
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
                Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Cannot proceed to LLM target ({potentialTargetWorld.x:F1}, {potentialTargetWorld.y:F1}). Reason: {currentAbortReason}. Re-requesting decision.</color>");
                _lastMovementAbortReason = currentAbortReason;
                RequestMovementDecisionAsync(true);
                return; 
            }

            _currentMovementTargetWorld = potentialTargetWorld;
            _hasMovementTarget = true;

            // **** NEW: Use NpcMovement to move to the LLM target ****
            _npcMovement.SetMoveTarget(_currentMovementTargetWorld, () => {
                // This callback is invoked by NpcMovement when it arrives
                // The HandleMovingToTargetState will also detect this if this callback wasn't used,
                // but a callback is cleaner for specific "on arrival at LLM target" logic.
                // HandleMovingToTargetState will then take over and switch to Idle.
                // We could also directly call the arrival logic from HandleMovingToTargetState here,
                // but for now, let's keep HandleMovingToTargetState as the primary checker.
            });
            // **** END NEW ****

            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot; 
                Debug.Log($"<color=grey>[{_characterData.characterName}] Emotional state updated from API to: {_currentNpcEmotionalState.primary_emotion} (Intensity: {_currentNpcEmotionalState.intensity:F1})</color>");
            }

            bool wasDialogueDriven = response.primary_decision_drivers.GetValueOrDefault("dialogue_driven", false);
            bool accessRulesConsidered = response.primary_decision_drivers.GetValueOrDefault("access_rules_consideration", false);

            if (isReEvaluationDueToBlock && accessRulesConsidered) {
                 Debug.Log($"<color=orange>[{_characterData.characterName}] Re-evaluated decision, LLM considered access rules. Proceeding with: '{response.chosen_action_summary}'</color>");
            }

            if (wasDialogueDriven && _lastInteractedCharacter != null && isReEvaluationDueToBlock == false) {
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination '{response.chosen_action_summary}' was driven by dialogue with '{_lastInteractedCharacter.characterName}'. Initiating follow-up confirmation dialogue.</color>");
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, I've decided based on our chat: {response.chosen_action_summary}. Does that make sense to you, {(_lastInteractedCharacter.characterName ?? "there")}?", true);
                // Note: StartDialogueAsync now sets state to Interacting. If moving AND talking, need careful state management.
                // For now, dialogue takes precedence if it happens. NpcMovement would be stopped by ChangeState.
            }
            else if (wasDialogueDriven && !string.IsNullOrEmpty(response.chosen_action_summary) && _lastInteractedCharacter == null && isReEvaluationDueToBlock == false) { 
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination decision influenced by general dialogue/thought. Thinking aloud: '{response.chosen_action_summary}'.</color>");
                if (dialogueUIManager != null) {
                    dialogueUIManager.ShowDialogue(_characterData.characterName, $"Hmm, I think I will {response.chosen_action_summary.ToLower()}.", 3f);
                }
                ChangeState(NpcBehaviorState.MovingToTarget); // State change will trigger movement via NpcMovement
            }
            else { 
                ChangeState(NpcBehaviorState.MovingToTarget); // State change will trigger movement via NpcMovement
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Failed to get valid movement decision from API or target was null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle);
        }
    }

    async void StartDialogueAsync(CharacterData otherCharacter, string initialNpcUtteranceSeed = null, bool isFollowUpDialogue = false)
    {
        if (_isApiCallInProgress) {
             Debug.LogWarning($"[{_characterData.characterName}] Dialogue with '{otherCharacter.characterName}' skipped: An API call is already in progress.");
            return;
        }
        if (!isFollowUpDialogue && (_currentState == NpcBehaviorState.Interacting || _currentState == NpcBehaviorState.RequestingDecision || _currentState == NpcBehaviorState.PostInteractionPause)) {
             Debug.LogWarning($"[{_characterData.characterName}] Initial dialogue with '{otherCharacter.characterName}' skipped: NPC busy (State: {_currentState}).");
            if (_currentState == NpcBehaviorState.ApproachingInteraction && _currentTargetInteractionCharacter != otherCharacter) {
                _currentTargetInteractionCharacter = otherCharacter; 
                 Debug.Log($"[{_characterData.characterName}] Switched interaction approach target to '{otherCharacter.characterName}'.");
            }
            return;
        }

        // **** NEW: Stop any current movement when dialogue starts ****
        if (_npcMovement.IsMoving())
        {
            _npcMovement.StopMovement();
        }
        // **** END NEW ****

        ChangeState(NpcBehaviorState.RequestingDecision); // This state also stops NpcMovement implicitly via ChangeState logic
        _isApiCallInProgress = true;
        _hasMovementTarget = false; // Ensure no LLM movement target while in dialogue sequence
        _lastInteractedCharacter = otherCharacter; 

        string npcInitialPromptForLLM;
        if (!string.IsNullOrEmpty(initialNpcUtteranceSeed)) {
            npcInitialPromptForLLM = initialNpcUtteranceSeed;
        } else {
            string sceneHint = "You are in a shared apartment. ";
            npcInitialPromptForLLM = $"{sceneHint}You, '{_characterData.characterName}', have encountered '{otherCharacter.characterName}'. Initiate a natural, contextually appropriate conversation based on your personality and current emotional state.";
        }

        string selfEmotionStringForPrompt = $"{_currentNpcEmotionalState.primary_emotion} (intensity: {_currentNpcEmotionalState.intensity:F1})";

        Debug.Log($"<color=orange>[{_characterData.characterName}] Initiating dialogue with '{otherCharacter.characterName}'. Follow-up: {isFollowUpDialogue}. LLM Seed: '{npcInitialPromptForLLM}'. Emotion: {selfEmotionStringForPrompt}</color>");

        var interactionRequest = new GameInteractionRequest {
            interacting_objects = new List<InteractingObjectInfo> {
                _characterData.ToInteractingObjectInfo(
                    initialLlMPrompt: npcInitialPromptForLLM,
                    dialogueMode: null, 
                    currentEmotionalState: selfEmotionStringForPrompt, 
                    llmModelOverride: null 
                ),
            },
            scene_context_description = sceneContextManager?.GetGeneralSceneDescription() ?? "A room in the apartment.",
            game_time_context = gameTimeManager?.GetCurrentGameTime() ?? new GameTime{current_timestamp=DateTime.UtcNow.ToString("o"), time_of_day="unknown_time"},
            max_turns_per_object = 1 
        };

        GameInteractionResponse response = await ApiService.PostAsync<GameInteractionRequest, GameInteractionResponse>("/dialogue/game-interaction", interactionRequest);
        
        _isApiCallInProgress = false; 
        // ChangeState(NpcBehaviorState.Interacting); // No, dialogue is shown, then PostInteractionPause

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
            foreach (var turn in response.dialogue_history) {
                string messageToDisplay = !string.IsNullOrEmpty(turn.message_translated_zh_tw) ? turn.message_translated_zh_tw : turn.message_original_language;
                if (dialogueUIManager != null) {
                    // If it's a follow-up after movement decision, it might be brief.
                    // The actual dialogue display duration can be managed by DialogueUIManager or passed here.
                    float displayDuration = isFollowUpDialogue ? 4.0f : 5.0f;
                    dialogueUIManager.ShowDialogue(turn.name ?? turn.npc_id, messageToDisplay, displayDuration); 
                } else {
                    Debug.LogWarning($"[{_characterData.characterName}] DialogueUIManager is null. Cannot display dialogue: {turn.name ?? turn.npc_id}: {messageToDisplay}");
                }
                
                _shortTermDialogueHistory.Add(turn); 
                if (_shortTermDialogueHistory.Count > MaxDialogueHistoryForMovementContext * 2) {
                    _shortTermDialogueHistory.RemoveRange(0, _shortTermDialogueHistory.Count - MaxDialogueHistoryForMovementContext * 2);
                }
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Dialogue interaction with '{otherCharacter.characterName}' failed or returned no history.");
            if(dialogueUIManager != null) dialogueUIManager.ShowDialogue(_characterData.characterName, "[NPC seems unresponsive or an error occurred.]", 3f);
        }
        
        // After showing dialogue, go to post-interaction pause.
        // NpcController's ChangeState will handle stopping NpcMovement if it was somehow active.
        ChangeState(NpcBehaviorState.PostInteractionPause);
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

            Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}' in apartment. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            // _hasMovementTarget = false; // Set by ChangeState
            ChangeState(NpcBehaviorState.ApproachingInteraction); 
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            if (_currentState == NpcBehaviorState.ApproachingInteraction) {
                Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left area while NPC was approaching. Returning to Idle.</color>");
                _currentTargetInteractionCharacter = null;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement(); // Stop approaching movement
                ChangeState(NpcBehaviorState.Idle);
            } else if (_currentState == NpcBehaviorState.Interacting || _currentState == NpcBehaviorState.PostInteractionPause) {
                 Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left area during/after dialogue. Ending interaction sequence.</color>");
                _currentTargetInteractionCharacter = null;
                _lastInteractedCharacter = null; 
                if(dialogueUIManager != null) dialogueUIManager.HideDialogue();
                ChangeState(NpcBehaviorState.Idle); 
            }
        }
    }

    private LandmarkDataComponent FindTargetLandmark(Vector3 targetPosition)
    {
        if (sceneContextManager == null) return null;
        var allLandmarks = sceneContextManager.GetAllLandmarkDataComponents();
        if (allLandmarks == null || allLandmarks.Count == 0) return null;

        LandmarkDataComponent closestLandmark = null;
        float minDistanceSq = float.MaxValue;
        float effectiveRadiusSq = 0.5f * 0.5f; 

        foreach (var landmark in allLandmarks)
        {
            if (landmark == null) continue;
            float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;
            if (distSq < effectiveRadiusSq) 
            {
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    closestLandmark = landmark;
                }
            }
        }
        return closestLandmark; 
    }
    
    private void UpdateLandmarkStatusOnArrival(Vector3 arrivalPosition)
    {
        LandmarkDataComponent arrivedLandmark = FindTargetLandmark(arrivalPosition);
        if (arrivedLandmark == null) return;

        if (arrivedLandmark.landmarkTypeTag == "bedroom" && arrivedLandmark.ownerNpcId == _characterData.npcId)
        {
            arrivedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresencePresent);
            Debug.Log($"[{_characterData.characterName}] arrived at own bedroom '{arrivedLandmark.landmarkName}', status set to owner_present.");
        }
        else if (arrivedLandmark.landmarkTypeTag == "bathroom")
        {
            if (!arrivedLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) || arrivedLandmark.HasDynamicStatus(OccupancyStatusVacant))
            {
                arrivedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, $"{OccupancyStatusOccupied}_by_{_characterData.npcId}");
                 Debug.Log($"[{_characterData.characterName}] entered bathroom '{arrivedLandmark.landmarkName}', status set to occupied_by_{_characterData.npcId}.");
            }
        }
    }

    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark)
    {
        if (departedLandmark == null) return;

        if (departedLandmark.landmarkTypeTag == "bedroom" && departedLandmark.ownerNpcId == _characterData.npcId)
        {
            departedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresenceAbsent);
             Debug.Log($"[{_characterData.characterName}] departed own bedroom '{departedLandmark.landmarkName}', status set to owner_absent.");
        }
        else if (departedLandmark.landmarkTypeTag == "bathroom")
        {
            if(departedLandmark.HasDynamicStatus($"{OccupancyStatusOccupied}_by_{_characterData.npcId}"))
            {
                departedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, OccupancyStatusVacant);
                 Debug.Log($"[{_characterData.characterName}] exited bathroom '{departedLandmark.landmarkName}', status set to vacant.");
            }
        }
    }
}