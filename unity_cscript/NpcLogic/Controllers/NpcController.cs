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
    ApproachingInteraction,
    Interacting,
    PostInteractionPause
}

[RequireComponent(typeof(CharacterData))]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class NpcController : MonoBehaviour
{
    [Header("NPC Core Configuration")]
    [Tooltip("Time in seconds between LLM movement decisions when idle or after completing a task.")]
    public float decisionInterval = 15.0f;
    [Tooltip("Movement speed of the NPC in Unity units per second.")]
    public float moveSpeed = 2.0f;
    [Tooltip("Distance threshold to consider the NPC has arrived at its target.")]
    public float arrivalThreshold = 0.3f;
    [Tooltip("Transform of the visual model to rotate towards movement direction. Optional.")]
    public Transform visualModelTransform;


    [Header("Interaction Settings")]
    [Tooltip("Collider2D (set to IsTrigger=true) attached to this NPC, used to detect other characters for interaction.")]
    public Collider2D interactionTrigger;
    [Tooltip("Distance at which NPC stops to engage in dialogue if approaching an interaction target.")]
    public float dialogueInitiationDistance = 1.5f;
    [Tooltip("Duration in seconds NPC might pause after a dialogue before re-evaluating.")]
    public float postDialoguePauseDuration = 2.0f;


    [Header("Context Providers (Assign from Scene Managers)")]
    [Tooltip("Reference to the GameTimeManager for current game time.")]
    public GameTimeManager gameTimeManager;
    [Tooltip("Reference to the SceneContextManager for lists of landmarks and other characters.")]
    public SceneContextManager sceneContextManager;
    [Tooltip("Reference to the DialogueUIManager for displaying dialogue text.")]
    public DialogueUIManager dialogueUIManager;

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb;
    private NpcBehaviorState _currentState = NpcBehaviorState.Idle;
    private float _decisionTimer = 0f;
    private Vector3 _currentMovementTargetWorld;
    private bool _hasMovementTarget = false;
    private bool _isApiCallInProgress = false;
    private CharacterData _currentTargetInteractionCharacter = null;
    private CharacterData _lastInteractedCharacter = null;

    private List<DialogueTurn> _shortTermDialogueHistory = new List<DialogueTurn>();
    private const int MaxDialogueHistoryForMovementContext = 6;

    // Store emotional state, potentially loaded from memory or updated by API responses
    private NpcApiModels.NpcEmotionalState _currentNpcEmotionalState;


    // --- 初始化 ---
    void Awake()
    {
        _characterData = GetComponent<CharacterData>();
        _rb = GetComponent<Rigidbody2D>();
        if (_rb != null) _rb.isKinematic = true;


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

        // Initialize emotional state from CharacterData defaults
        // This would be overwritten if/when memory is loaded from server via NPCMemoryService
        if (_characterData != null && _characterData.isLLMNpc)
        {
            // *** 使用 CharacterData.cs 中定義的正確方法名 CreateDefaultMemoryFile() ***
            NPCMemoryFile defaultMemory = _characterData.CreateDefaultMemoryFile();
            if (defaultMemory != null && defaultMemory.current_emotional_state != null) // 確保 CreateDefaultMemoryFile 和其內部狀態返回有效對象
            {
                _currentNpcEmotionalState = defaultMemory.current_emotional_state;
            }
            else 
            {
                Debug.LogWarning($"[{_characterData.characterName}] CreateDefaultMemoryFile returned null or incomplete data. Initializing default emotional state for NpcController.");
                _currentNpcEmotionalState = new NpcEmotionalState {
                    primary_emotion = "neutral", // Fallback default
                    intensity = 0.5f,
                    last_significant_change_at = DateTime.UtcNow.ToString("o"),
                    reason_for_last_change = "Fallback initial state in NpcController."
                };
            }
        }
        else if (_characterData != null) // If not an LLM NPC, give a default non-applicable emotion state
        {
            _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "n_a", // Not Applicable
                intensity = 0f,
                last_significant_change_at = DateTime.UtcNow.ToString("o"),
                reason_for_last_change = "Non-LLM character initial state."
            };
        }
        else // Fallback if _characterData itself was null (should be caught by earlier check in Awake)
        {
             _currentNpcEmotionalState = new NpcEmotionalState { primary_emotion = "error_no_character_data", intensity = 0f, last_significant_change_at = DateTime.UtcNow.ToString("o") };
        }
    }

    void Start()
    {
        _decisionTimer = UnityEngine.Random.Range(decisionInterval * 0.1f, decisionInterval * 0.75f);
        ChangeState(NpcBehaviorState.Idle);
        // TODO: Consider an initial memory sync with the server here,
        // which might update _currentNpcEmotionalState from persisted data.
        // For example: LoadPersistedMemoryAsync();
    }

    // --- 主更新循環 (狀態機) ---
    void Update()
    {
        if (!_characterData.isLLMNpc || !enabled) return;

        switch (_currentState)
        {
            case NpcBehaviorState.Idle:
                HandleIdleState();
                break;
            case NpcBehaviorState.RequestingDecision:
                // Waiting for API
                break;
            case NpcBehaviorState.MovingToTarget:
                HandleMovingToTargetState();
                break;
            case NpcBehaviorState.ApproachingInteraction:
                HandleApproachingInteractionState();
                break;
            case NpcBehaviorState.Interacting:
                // Waiting for dialogue UI / player
                break;
            case NpcBehaviorState.PostInteractionPause:
                // Coroutine handles this state's duration
                break;
        }
    }

    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState && newState != NpcBehaviorState.RequestingDecision) return; // Allow re-entering RequestingDecision if needed
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {_currentState} -> {newState}</color>");
        _currentState = newState;

        switch (newState)
        {
            case NpcBehaviorState.Idle:
                _hasMovementTarget = false; // When idle, no specific LLM movement target
                // _currentTargetInteractionCharacter = null; // Consider if this should be cleared here or after PostInteractionPause
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null; // If moving to LLM target, not approaching a character
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false; // Stop current LLM movement target to approach character
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false; // Not moving while actively in dialogue UI phase
                _decisionTimer = 0f; // Reset decision timer to allow quick re-evaluation after interaction
                break;
            case NpcBehaviorState.PostInteractionPause:
                StartCoroutine(PostInteractionPauseCoroutine());
                break;
            case NpcBehaviorState.RequestingDecision:
                // This state is usually brief, just indicating an API call is active.
                break;
        }
    }

    void HandleIdleState()
    {
        _decisionTimer += Time.deltaTime;
        if (_decisionTimer >= decisionInterval && !_isApiCallInProgress)
        {
            _decisionTimer = 0f;
            RequestMovementDecisionAsync(); // Default movement decision request
        }
    }

    void HandleMovingToTargetState()
    {
        if (!_hasMovementTarget) {
            ChangeState(NpcBehaviorState.Idle); return;
        }
        Vector3 currentPosition = transform.position;
        Vector3 targetOnPlane = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, currentPosition.z);
        if (Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)) > arrivalThreshold) {
            transform.position = Vector3.MoveTowards(currentPosition, targetOnPlane, moveSpeed * Time.deltaTime);
            if (visualModelTransform != null && (targetOnPlane - currentPosition).sqrMagnitude > 0.01f) {
                Vector3 direction = (targetOnPlane - currentPosition).normalized;
                float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                visualModelTransform.rotation = Quaternion.AngleAxis(angle - 90, Vector3.forward); // Adjust -90 if sprite faces up
            }
        } else { // Arrived
            Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target: ({targetOnPlane.x:F1}, {targetOnPlane.y:F1})</color>");
            transform.position = targetOnPlane; // Snap to exact target
            _hasMovementTarget = false;
            ChangeState(NpcBehaviorState.Idle); // Return to Idle to decide next action
        }
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) { // Target might have been destroyed or moved out of range
            Debug.LogWarning($"[{_characterData.characterName}] Target for interaction is null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle); return;
        }
        Vector3 targetCharPos = _currentTargetInteractionCharacter.transform.position;
        Vector3 targetPosOnPlane = new Vector3(targetCharPos.x, targetCharPos.y, transform.position.z);
        if (Vector2.Distance(new Vector2(transform.position.x, transform.position.y), new Vector2(targetPosOnPlane.x, targetPosOnPlane.y)) > dialogueInitiationDistance) {
            transform.position = Vector3.MoveTowards(transform.position, targetPosOnPlane, moveSpeed * Time.deltaTime);
             if (visualModelTransform != null && (targetPosOnPlane - transform.position).sqrMagnitude > 0.01f) {
                Vector3 direction = (targetPosOnPlane - transform.position).normalized;
                float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                visualModelTransform.rotation = Quaternion.AngleAxis(angle - 90, Vector3.forward);
            }
        } else { // Reached interaction distance
            Debug.Log($"<color=orange>[{_characterData.characterName}] Reached '{_currentTargetInteractionCharacter.characterName}' for dialogue.</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter); // Initiate the dialogue
        }
    }

    private IEnumerator PostInteractionPauseCoroutine()
    {
        // Debug.Log($"<color=grey>[{_characterData.characterName}] PostInteractionPause started.</color>");
        yield return new WaitForSeconds(postDialoguePauseDuration);
        // Debug.Log($"<color=grey>[{_characterData.characterName}] PostInteractionPause finished. Returning to Idle.</color>");
        ChangeState(NpcBehaviorState.Idle); // After pause, re-evaluate (will trigger movement decision considering recent dialogue)
    }

    async void RequestMovementDecisionAsync(bool isFollowUpToDialogueWithSpecificTarget = false)
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
        SceneBoundaryInfo sceneBounds = sceneContextManager?.GetCurrentSceneBoundaries() ?? new SceneBoundaryInfo { min_x = -1000, max_x = 1000, min_y = -1000, max_y = 1000 }; // Fallback

        string dialogueSummary = "";
        if (_shortTermDialogueHistory.Count > 0) {
            var recentTurns = _shortTermDialogueHistory.TakeLast(MaxDialogueHistoryForMovementContext);
            dialogueSummary = string.Join("\n", recentTurns.Select(t => $"{(t.name ?? t.npc_id)}: \"{t.message_original_language}\""));
        }

        NpcMovementRequest requestPayload = new NpcMovementRequest {
            npc_id = selfIdentifier.npc_id, name = selfIdentifier.name,
            current_npc_position = currentNpcPos, current_game_time = currentGameTime,
            // current_npc_emotional_state is loaded by the server via NPCMemoryService, not sent in this request.
            // The prompt builder will use the NPC's personality_description and emotional_state from its memory.
            nearby_entities = nearbyEntities, visible_landmarks = visibleLandmarks,
            scene_boundaries = sceneBounds,
            recent_dialogue_summary_for_movement = string.IsNullOrEmpty(dialogueSummary) ? null : dialogueSummary
            // explicit_player_movement_request could be populated here if game logic detects such a request
        };

        Debug.Log($"<color=cyan>[{_characterData.characterName}] Requesting MOVEMENT decision. IsFollowUpToDialogue: {isFollowUpToDialogueWithSpecificTarget}. Dialogue context snippet: '{dialogueSummary?.Substring(0, Mathf.Min(dialogueSummary?.Length ?? 0, 70))}...'</color>");
        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);
        _isApiCallInProgress = false; // Reset API call flag

        if (response != null && response.target_destination != null) {
            Debug.Log($"<color=#90EE90>[{_characterData.characterName}] Movement decision received. Action: '{response.chosen_action_summary}'. Target: ({response.target_destination.x:F1}, {response.target_destination.y:F1})</color>");
            _currentMovementTargetWorld = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);
            _hasMovementTarget = true;

            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot; // Update internal emotional state
                Debug.Log($"<color=grey>[{_characterData.characterName}] Emotional state updated from API to: {_currentNpcEmotionalState.primary_emotion} (Intensity: {_currentNpcEmotionalState.intensity:F1})</color>");
            }

            bool wasDialogueDriven = response.primary_decision_drivers.GetValueOrDefault("dialogue_driven", false);

            // FLOWCHART: "跟改目的地" (destination set) -> "對話" (if dialogue_driven and was a follow-up)
            if (isFollowUpToDialogueWithSpecificTarget && wasDialogueDriven && _lastInteractedCharacter != null) {
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination '{response.chosen_action_summary}' was driven by dialogue with '{_lastInteractedCharacter.characterName}'. Initiating follow-up confirmation dialogue.</color>");
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, I've decided: {response.chosen_action_summary}. Does that sound good to you, {(_lastInteractedCharacter.characterName ?? "there")}?", true);
            }
            // If dialogue driven but not a specific follow-up, or no action summary to vocalize, just move.
            else if (wasDialogueDriven && !string.IsNullOrEmpty(response.chosen_action_summary) && _lastInteractedCharacter == null) { // Dialogue driven, but no specific partner (e.g. monologue or general observation)
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination decision influenced by general dialogue/thought. Thinking aloud: '{response.chosen_action_summary}'.</color>");
                if (dialogueUIManager != null) {
                    dialogueUIManager.ShowDialogue(_characterData.characterName, $"Hmm, I think I will {response.chosen_action_summary.ToLower()}.", 3f);
                }
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
            else { // Not dialogue driven, or no clear action to announce for a follow-up
                ChangeState(NpcBehaviorState.MovingToTarget);
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Failed to get valid movement decision from API or target was null. Returning to Idle.");
            ChangeState(NpcBehaviorState.Idle);
        }
    }

    async void StartDialogueAsync(CharacterData otherCharacter, string initialNpcUtteranceSeed = null, bool isFollowUpDialogue = false)
    {
        // More robust check to prevent dialogue stacking or interruption
        if (_isApiCallInProgress) {
             Debug.LogWarning($"[{_characterData.characterName}] Dialogue with '{otherCharacter.characterName}' skipped: An API call is already in progress.");
            return;
        }
        if (!isFollowUpDialogue && (_currentState == NpcBehaviorState.Interacting || _currentState == NpcBehaviorState.RequestingDecision || _currentState == NpcBehaviorState.PostInteractionPause)) {
             Debug.LogWarning($"[{_characterData.characterName}] Initial dialogue with '{otherCharacter.characterName}' skipped: NPC busy (State: {_currentState}).");
            if (_currentState == NpcBehaviorState.ApproachingInteraction && _currentTargetInteractionCharacter != otherCharacter) {
                _currentTargetInteractionCharacter = otherCharacter; // Switch approach target if a new one is prioritized
                 Debug.Log($"[{_characterData.characterName}] Switched interaction approach target to '{otherCharacter.characterName}'.");
            }
            return;
        }


        ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;
        _hasMovementTarget = false;
        _lastInteractedCharacter = otherCharacter; // Crucial for follow-up context

        string npcInitialPromptForLLM;
        if (!string.IsNullOrEmpty(initialNpcUtteranceSeed)) {
            npcInitialPromptForLLM = initialNpcUtteranceSeed;
        } else {
            npcInitialPromptForLLM = $"You, '{_characterData.characterName}', have just encountered '{otherCharacter.characterName}'. Initiate a natural, contextually appropriate conversation.";
        }

        string selfEmotionStringForPrompt = $"{_currentNpcEmotionalState.primary_emotion} (intensity: {_currentNpcEmotionalState.intensity:F1})";

        Debug.Log($"<color=orange>[{_characterData.characterName}] Initiating dialogue with '{otherCharacter.characterName}'. Follow-up: {isFollowUpDialogue}. LLM Seed: '{npcInitialPromptForLLM}'. Emotion: {selfEmotionStringForPrompt}</color>");

        var interactionRequest = new GameInteractionRequest {
            interacting_objects = new List<InteractingObjectInfo> {
                _characterData.ToInteractingObjectInfo(
                    initialLlMPrompt: npcInitialPromptForLLM,
                    dialogueMode: null, // TODO: Fetch preferred dialogue mode from CharacterData or state
                    currentEmotionalState: selfEmotionStringForPrompt, // *** ENSURE THIS MATCHES CharacterData.ToInteractingObjectInfo PARAMETER NAME ***
                    llmModelOverride: null // TODO: Fetch from CharacterData or state
                ),
                // If otherCharacter is also an LLM NPC and should respond in this same API call:
                // (This makes the request more complex, assumes server handles multi-turn for different objects)
                // otherCharacter.ToInteractingObjectInfo(
                //    initialLlMPrompt: $"Respond to what '{_characterData.characterName}' just said or initiated.",
                //    currentEmotionalState: otherCharCurrentEmotionString // Need to get this
                // )
            },
            scene_context_description = sceneContextManager?.GetGeneralSceneDescription() ?? "A generic location.",
            game_time_context = gameTimeManager?.GetCurrentGameTime() ?? new GameTime{current_timestamp=DateTime.UtcNow.ToString("o"), time_of_day="unknown_time"},
            max_turns_per_object = 1 // Each specified object gets to speak once per this API call
        };

        GameInteractionResponse response = await ApiService.PostAsync<GameInteractionRequest, GameInteractionResponse>("/dialogue/game-interaction", interactionRequest);
        
        _isApiCallInProgress = false; 
        ChangeState(NpcBehaviorState.Interacting); // Transition to Interacting to show dialogue

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
            foreach (var turn in response.dialogue_history) {
                string messageToDisplay = !string.IsNullOrEmpty(turn.message_translated_zh_tw) ? turn.message_translated_zh_tw : turn.message_original_language;
                if (dialogueUIManager != null) {
                    dialogueUIManager.ShowDialogue(turn.name ?? turn.npc_id, messageToDisplay, 5f); // Display for 5s example
                } else {
                    Debug.LogWarning($"[{_characterData.characterName}] DialogueUIManager is null. Cannot display dialogue: {turn.name ?? turn.npc_id}: {messageToDisplay}");
                }
                
                _shortTermDialogueHistory.Add(turn); // Add to history for next movement decision
                if (_shortTermDialogueHistory.Count > MaxDialogueHistoryForMovementContext * 2) {
                    _shortTermDialogueHistory.RemoveRange(0, _shortTermDialogueHistory.Count - MaxDialogueHistoryForMovementContext * 2);
                }
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Dialogue interaction with '{otherCharacter.characterName}' failed or returned no history.");
            if(dialogueUIManager != null) dialogueUIManager.ShowDialogue(_characterData.characterName, "[NPC seems unresponsive or an error occurred.]", 3f);
        }
        
        // After ANY dialogue (initial or follow-up from movement decision), NPC goes to pause, then idle.
        // The next RequestMovementDecisionAsync will be called from Idle state and will use the updated _shortTermDialogueHistory.
        // The 'isFollowUpToDialogue' parameter for RequestMovementDecisionAsync will be true IF that specific RequestMovementDecisionAsync call
        // was made immediately after a dialogue that changed destination. This is now handled by the internal logic
        // of RequestMovementDecisionAsync itself checking the 'dialogue_driven' flag from LLM response.
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
            // FLOWCHART: "遇到玩家" -> Yes
            Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}'. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            _hasMovementTarget = false; // Stop current LLM-driven movement
            ChangeState(NpcBehaviorState.ApproachingInteraction); // Move towards character before starting dialogue
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            if (_currentState == NpcBehaviorState.ApproachingInteraction) {
                Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left area while NPC was approaching. Returning to Idle.</color>");
                _currentTargetInteractionCharacter = null;
                ChangeState(NpcBehaviorState.Idle);
            } else if (_currentState == NpcBehaviorState.Interacting || _currentState == NpcBehaviorState.PostInteractionPause) {
                 Debug.Log($"<color=grey>[{_characterData.characterName}] Interaction target '{_currentTargetInteractionCharacter.characterName}' left area during/after dialogue. Ending interaction sequence.</color>");
                _currentTargetInteractionCharacter = null;
                _lastInteractedCharacter = null; // Clear last interacted as well if they leave
                if(dialogueUIManager != null) dialogueUIManager.HideDialogue();
                ChangeState(NpcBehaviorState.Idle); // Go back to idle to re-evaluate
            }
        }
    }
}