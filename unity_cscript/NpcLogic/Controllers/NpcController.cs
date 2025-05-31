// 檔案名稱: NpcController.cs (包含所有已討論的修改)
// 放置路徑建議: Assets/Scripts/NpcLogic/Controllers/NpcController.cs

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NpcApiModels;
using NpcInternalModels;
using System.Linq;
using System;
using TMPro;
using System.Text.RegularExpressions;

public enum NpcBehaviorState
{
    Idle,
    ProcessingQueue,
    RequestingDecision
}

[RequireComponent(typeof(CharacterData))]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
[RequireComponent(typeof(NpcMovement))]
public class NpcController : MonoBehaviour
{
    [Header("NPC Core Configuration")]
    public float decisionInterval = 15.0f;
    public float moveSpeed = 2.0f;
    public float arrivalThreshold = 0.3f;
    public Transform visualModelTransform;

    [Header("Interaction Settings")]
    public Collider2D interactionTrigger;
    public float dialogueInitiationDistance = 1.5f;
    public float postDialoguePauseDuration = 2.0f;

    [Header("Dialogue Bubble (TextMeshPro)")]
    public GameObject dialogueBubblePrefab_TMP;
    public float dialogueBubbleOffsetY = 1.5f;
    [Tooltip("對於分頁對話，這是【每一頁】的顯示時間（秒）。")]
    public float dialogueDisplayTime = 4.0f; // 這個現在被用作 ResponseItem.AsDialogue 的 floatPayload1，即每頁的顯示時間
    [Tooltip("每頁對話顯示的最大字符數（用於簡單分頁）。0或負數表示不進行分頁。")]
    public int maxCharsPerPage = 70;

    [Header("Social Behavior Settings")]
    [Range(0f, 1f)]
    public float proactiveSocialChance = 0.25f;
    public float idleTimeToConsiderSocializing = 60.0f;

    [Header("Context Providers (Assign from Scene Managers)")]
    public GameTimeManager gameTimeManager;
    public SceneContextManager sceneContextManager;
    public DialogueUIManager dialogueUIManager;

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb;
    private NpcMovement _npcMovement;
    private NpcBehaviorState _currentState = NpcBehaviorState.Idle;
    private float _decisionTimer = 0f;
    private bool _isApiCallInProgress = false;
    private CharacterData _currentTargetInteractionCharacter = null;
    private CharacterData _lastInteractedCharacter = null;

    private List<DialogueTurn> _shortTermDialogueHistory = new List<DialogueTurn>();
    private const int MaxDialogueHistoryForMovementContext = 6;

    private NpcApiModels.NpcEmotionalState _currentNpcEmotionalState;
    private string _lastMovementAbortReason = null;

    private TextMeshProUGUI _dialogueTextTMP;
    private GameObject _currentDialogueBubbleInstance;
    private Coroutine _dialogueDisplayCoroutine; // 用於控制整個對話（包括分頁）的協程引用
    private LandmarkDataComponent _currentActiveLandmarkZone = null;

    private LandmarkDataComponent _originalTargetLandmarkForWaiting = null;
    private const float MaxWaitTimeNearTargetFallback = 20.0f;
    private const float RecheckTargetInterval = 3.0f;
    private float _nextRecheckTime = 0f;
    private float _timeSpentIdle = 0f;

    private const string OccupancyStatusPrefix = "occupancy_";
    private const string OccupancyStatusOccupied = "occupancy_occupied";
    private string GetOccupancyStatusOccupiedBySelf() => $"{OccupancyStatusOccupied}_by_{_characterData.npcId}";
    private const string OwnerPresenceStatusPrefix = "owner_presence_";
    private const string OwnerPresencePresent = "owner_presence_present";
    private const string OwnerPresenceAbsent = "owner_presence_absent";

    private Queue<ResponseItem> _responseQueue = new Queue<ResponseItem>();
    private bool _isProcessingQueueItem = false;
    private ResponseItem _currentlyProcessingItem = null;


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
        if (_npcMovement == null) { Debug.LogError($"[{gameObject.name}] NpcMovement component missing.", this); enabled = false; return; }

        _npcMovement.moveSpeed = this.moveSpeed;
        _npcMovement.arrivalDistance = this.arrivalThreshold;
        _npcMovement.rotationSpeed = 360f;
        if (visualModelTransform != null) _npcMovement.visualModelToRotate = this.visualModelTransform;

        if (_characterData == null) { Debug.LogError($"[{gameObject.name}] CharacterData component missing.", this); enabled = false; return; }
        if (!_characterData.isLLMNpc) { Debug.Log($"[{_characterData.characterName}] Non-LLM character. NpcController disabled.", this); enabled = false; return; }
        if (string.IsNullOrEmpty(_characterData.npcId)) { Debug.LogError($"[{gameObject.name}] CharacterData NpcId empty.", this); enabled = false; return; }

        if (interactionTrigger == null) Debug.LogWarning($"[{_characterData.characterName}] Interaction Trigger not assigned.", this);
        else if (!interactionTrigger.isTrigger) Debug.LogWarning($"[{_characterData.characterName}] Interaction Trigger '{interactionTrigger.name}' not set to 'Is Trigger'.", this);

        if (gameTimeManager == null) Debug.LogError($"'{_characterData.characterName}': GameTimeManager not assigned!", this);
        if (sceneContextManager == null) Debug.LogError($"'{_characterData.characterName}': SceneContextManager not assigned!", this);
        if (dialogueBubblePrefab_TMP == null && _characterData.isLLMNpc) Debug.LogWarning($"'{_characterData.characterName}': Dialogue Bubble Prefab (TMP) not assigned.", this);

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
                _currentNpcEmotionalState = new NpcEmotionalState {
                    primary_emotion = "neutral", intensity = 0.5f,
                    last_significant_change_at = DateTime.UtcNow.ToString("o"),
                    reason_for_last_change = "Fallback initial state."
                };
            }
        }
        else if (_characterData != null)
        {
            _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "n_a", intensity = 0f,
                last_significant_change_at = DateTime.UtcNow.ToString("o"),
                reason_for_last_change = "Non-LLM character initial state."
            };
        }
        else
        {
             _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "error_no_character_data", intensity = 0f,
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

        if (!_isProcessingQueueItem && _responseQueue.Count > 0)
        {
            _currentlyProcessingItem = _responseQueue.Dequeue();
            _isProcessingQueueItem = true;
            ChangeState(NpcBehaviorState.ProcessingQueue);
            ProcessResponseItem(_currentlyProcessingItem);
            return;
        }

        if (!_isProcessingQueueItem && _responseQueue.Count == 0)
        {
            if (_currentState == NpcBehaviorState.ProcessingQueue)
            {
                ChangeState(NpcBehaviorState.Idle);
            }

            switch (_currentState)
            {
                case NpcBehaviorState.Idle:
                    HandleIdleState();
                    break;
                case NpcBehaviorState.RequestingDecision:
                    break;
            }
        }
    }


    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState) return;

        NpcBehaviorState previousState = _currentState;
        _currentState = newState;
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {previousState} -> {newState}</color>");

        switch (newState)
        {
            case NpcBehaviorState.Idle:
                _originalTargetLandmarkForWaiting = null;
                _timeSpentIdle = 0f;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement();
                break;
            case NpcBehaviorState.ProcessingQueue:
                _decisionTimer = 0f;
                _timeSpentIdle = 0f;
                break;
            case NpcBehaviorState.RequestingDecision:
                _timeSpentIdle = 0f;
                if (_npcMovement.IsMoving()) _npcMovement.StopMovement();
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


    private void ProcessResponseItem(ResponseItem item)
    {
        // Debug.Log($"<color=cyan>[{_characterData.characterName}] Processing Item: {item.ToString()}</color>");
        switch (item.itemType)
        {
            case ResponseItemType.DialogueMessage:
                ShowDialogueBubble_TMP(item.stringPayload, item.floatPayload1, MarkCurrentItemCompleted);
                break;

            case ResponseItemType.MovementCommand:
                UpdateLandmarkStatusOnArrivalOrDeparture(transform.position, false);
                _npcMovement.SetMoveTarget(item.vector3Payload, () => {
                    HandleArrivalAtMovementTarget(item.vector3Payload, true);
                });
                break;

            case ResponseItemType.WaitCommand:
                _originalTargetLandmarkForWaiting = item.objectPayload as LandmarkDataComponent;
                _nextRecheckTime = Time.time + RecheckTargetInterval;

                float waitDuration = item.floatPayload1;
                if (waitDuration <= 0 && _originalTargetLandmarkForWaiting == null) {
                     Debug.LogWarning($"[{_characterData.characterName}] WaitCommand with no duration and no target landmark. Completing immediately.");
                     MarkCurrentItemCompleted();
                     return;
                }
                // Debug.Log($"<color=yellow>[{_characterData.characterName}] Starting WaitCommand. Duration: {waitDuration}s. TargetLandmark: {_originalTargetLandmarkForWaiting?.landmarkName ?? "None"}.</color>");
                StartCoroutine(HandleWaitCommandCoroutine(waitDuration, _originalTargetLandmarkForWaiting));
                break;

            case ResponseItemType.AnimationTrigger:
                Debug.Log($"[{_characterData.characterName}] Animation Trigger: {item.stringPayload} (Not Implemented)");
                MarkCurrentItemCompleted();
                break;

            case ResponseItemType.SoundEffect:
                Debug.Log($"[{_characterData.characterName}] Sound Effect: {item.stringPayload} (Not Implemented)");
                MarkCurrentItemCompleted();
                break;

            case ResponseItemType.CustomAction:
                item.customActionPayload?.Invoke();
                MarkCurrentItemCompleted();
                break;

            default:
                Debug.LogWarning($"[{_characterData.characterName}] Unknown ResponseItemType: {item.itemType}. Marking as complete.");
                MarkCurrentItemCompleted();
                break;
        }
    }

    private IEnumerator HandleWaitCommandCoroutine(float duration, LandmarkDataComponent targetLandmark)
    {
        float elapsedTime = 0f;

        while (true)
        {
            elapsedTime += Time.deltaTime;

            if (duration > 0 && elapsedTime >= duration)
            {
                // Debug.Log($"<color=yellow>[{_characterData.characterName}] WaitCommand finished: Duration {duration}s elapsed.</color>");
                break;
            }

            if (targetLandmark != null)
            {
                if (Time.time >= _nextRecheckTime)
                {
                    _nextRecheckTime = Time.time + RecheckTargetInterval;
                    bool isAvailable = true;
                    if (targetLandmark.landmarkTypeTag == "bathroom")
                    {
                        if (targetLandmark.HasDynamicStatus(OccupancyStatusOccupied) &&
                            !targetLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
                        {
                            isAvailable = false;
                        }
                    }

                    if (isAvailable)
                    {
                        // Debug.Log($"<color=green>[{_characterData.characterName}] WaitCommand finished: Target Landmark '{targetLandmark.landmarkName}' is now available.</color>");
                        break;
                    }
                    else
                    {
                        // Debug.Log($"<color=grey>[{_characterData.characterName}] WaitCommand: Target '{targetLandmark.landmarkName}' still unavailable. Continuing wait.</color>");
                    }
                }
            }

            if (duration <= 0 && targetLandmark == null) {
                Debug.LogError($"[{_characterData.characterName}] WaitCommand has no duration and no target landmark to check. Aborting wait.");
                break;
            }
            if (duration <= 0 && targetLandmark != null && elapsedTime >= MaxWaitTimeNearTargetFallback) {
                 Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] WaitCommand for '{targetLandmark.landmarkName}' timed out after {MaxWaitTimeNearTargetFallback}s. Condition not met.</color>");
                 break;
            }
            yield return null;
        }
        _originalTargetLandmarkForWaiting = null;
        MarkCurrentItemCompleted();
    }


    public void MarkCurrentItemCompleted()
    {
        // Debug.Log($"<color=grey>[{_characterData.characterName}] Completed Item: {_currentlyProcessingItem?.ToString() ?? "N/A"}</color>");
        _isProcessingQueueItem = false;
        _currentlyProcessingItem = null;

        if (_responseQueue.Count == 0 && _currentState != NpcBehaviorState.RequestingDecision)
        {
            if (!_npcMovement.IsMoving())
            {
                 ChangeState(NpcBehaviorState.Idle);
            }
        }
    }

    private void HandleArrivalAtMovementTarget(Vector3 arrivedAtPosition, bool isNewPrimaryGoal)
    {
        // Debug.Log($"<color=#32CD32>[{_characterData.characterName}] Arrived at ({arrivedAtPosition.x:F1}, {arrivedAtPosition.y:F1}). IsNewPrimaryGoal: {isNewPrimaryGoal}. CurrentItem: {_currentlyProcessingItem?.itemType}</color>");
        UpdateLandmarkStatusOnArrivalOrDeparture(arrivedAtPosition, true);

        if (_currentlyProcessingItem != null && _currentlyProcessingItem.itemType == ResponseItemType.MovementCommand)
        {
            MarkCurrentItemCompleted();
        }
        else
        {
            Debug.LogWarning($"[{_characterData.characterName}] HandleArrival called, but current item is NOT MovementCommand ({_currentlyProcessingItem?.itemType}). This might be unexpected.");
            if (_isProcessingQueueItem && _currentlyProcessingItem == null) MarkCurrentItemCompleted();
        }
    }


    async void RequestMovementDecisionAsync(bool isReEvaluationDueToReason = false)
    {
        if (_isApiCallInProgress) return;

        if (this == null || gameObject == null || !enabled) {
            _isApiCallInProgress = false; return;
        }

        if (!isReEvaluationDueToReason) {
            _originalTargetLandmarkForWaiting = null;
            _lastMovementAbortReason = null;
        }

        UpdateLandmarkStatusOnArrivalOrDeparture(transform.position, false);

        ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;

        NpcIdentifier selfIdentifier = _characterData.GetNpcIdentifier();
        Position currentNpcPos = new Position { x = transform.position.x, y = transform.position.y };
        GameTime currentGameTime = gameTimeManager?.GetCurrentGameTime() ?? new GameTime { current_timestamp = DateTime.UtcNow.ToString("o"), time_of_day = "unknown_time" };
        List<EntityContextInfo> nearbyEntities = sceneContextManager?.GetNearbyEntities(selfIdentifier.npc_id, transform.position, 20f) ?? new List<EntityContextInfo>();
        List<LandmarkContextInfo> visibleLandmarks = sceneContextManager?.GetVisibleLandmarks(transform.position, 30f) ?? new List<LandmarkContextInfo>();
        SceneBoundaryInfo sceneBounds = sceneContextManager?.GetCurrentSceneBoundaries() ?? new SceneBoundaryInfo { min_x = -1000, max_x = 1000, min_y = -1000, max_y = 1000 };

        string dialogueSummary = "";
        if (_shortTermDialogueHistory.Count > 0) {
            var recentTurns = _shortTermDialogueHistory.TakeLast(MaxDialogueHistoryForMovementContext);
            dialogueSummary = string.Join("\n", recentTurns.Select(t => $"{(t.name ?? t.npc_id)}: \"{t.message_original_language}\""));
        }
        string augmentedContextForLLM = (isReEvaluationDueToReason && !string.IsNullOrEmpty(_lastMovementAbortReason)) ? _lastMovementAbortReason : dialogueSummary;
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

        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);

        if (this == null || gameObject == null || !enabled) {
             _isApiCallInProgress = false; return;
        }

        _isApiCallInProgress = false;

        if (response != null && response.target_destination != null)
        {
            Vector3 llmTargetWorldPos = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);

            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot;
            }

            bool wasSociallyDriven = response.primary_decision_drivers.GetValueOrDefault("social_interaction_considered", false) &&
                                     IsSocialAction(response.chosen_action_summary);
            if (wasSociallyDriven)
            {
                string targetNpcName = ParseTargetNpcNameFromSocialAction(response.chosen_action_summary);
                CharacterData socialTarget = string.IsNullOrEmpty(targetNpcName) ? null : FindCharacterByName(targetNpcName);

                if (socialTarget != null && socialTarget != _characterData)
                {
                    _currentTargetInteractionCharacter = socialTarget;
                    _responseQueue.Enqueue(ResponseItem.AsDialogue($"I think I'll go say hi to {targetNpcName}.", dialogueDisplayTime * 0.9f));
                    Vector3 approachPos = socialTarget.transform.position - (socialTarget.transform.position - transform.position).normalized * (dialogueInitiationDistance * 0.9f);
                    _responseQueue.Enqueue(ResponseItem.AsMovement(new Vector3(approachPos.x, approachPos.y, transform.position.z)));
                    _responseQueue.Enqueue(ResponseItem.AsCustomAction(() => {
                        if (_currentTargetInteractionCharacter != null) {
                            StartDialogueAsync(_currentTargetInteractionCharacter, null, false);
                        } else {
                             Debug.LogWarning($"[{_characterData.characterName}] Tried custom action for dialogue, but _currentTargetInteractionCharacter is null.");
                             MarkCurrentItemCompleted();
                        }
                    }));

                    if (_currentState == NpcBehaviorState.RequestingDecision) ChangeState(NpcBehaviorState.Idle);
                    return;
                }
            }

            LandmarkDataComponent targetLandmarkAtLLMPos = FindTargetLandmark(llmTargetWorldPos);
            bool shouldWaitForLandmark = false;
            string originalTargetNameIfWaiting = null;

            if (response.chosen_action_summary != null && response.chosen_action_summary.ToLower().Contains("wait near")) {
                originalTargetNameIfWaiting = ParseOriginalTargetFromWaitingAction(response.chosen_action_summary);
                if(!string.IsNullOrEmpty(originalTargetNameIfWaiting)) {
                    LandmarkDataComponent originalLm = FindLandmarkByName(originalTargetNameIfWaiting);
                    if (originalLm != null && IsLandmarkUnavailable(originalLm)) {
                        shouldWaitForLandmark = true;
                        targetLandmarkAtLLMPos = originalLm;
                    } else if (originalLm == null) {
                         Debug.LogWarning($"[{_characterData.characterName}] LLM said wait for '{originalTargetNameIfWaiting}', but landmark not found. Will move to coords.");
                    }
                }
            } else if (targetLandmarkAtLLMPos != null && IsLandmarkUnavailable(targetLandmarkAtLLMPos)) {
                _lastMovementAbortReason = $"Target landmark '{targetLandmarkAtLLMPos.landmarkName}' is currently unavailable.";
                shouldWaitForLandmark = true;
            }

            _responseQueue.Enqueue(ResponseItem.AsMovement(llmTargetWorldPos));

            if (shouldWaitForLandmark && targetLandmarkAtLLMPos != null)
            {
                if (string.IsNullOrEmpty(response.chosen_action_summary) || !response.chosen_action_summary.ToLower().Contains("wait near")) {
                     _responseQueue.Enqueue(ResponseItem.AsDialogue($"Looks like {targetLandmarkAtLLMPos.landmarkName} is busy. I'll wait nearby.", dialogueDisplayTime));
                }
                _responseQueue.Enqueue(ResponseItem.AsWait(0, targetLandmarkAtLLMPos));
            }
        }
        else
        {
            Debug.LogError($"[{_characterData.characterName}] Failed to get valid movement decision from API or target was null.");
            _responseQueue.Enqueue(ResponseItem.AsDialogue("[Movement decision error]", 2f));
        }

        if (_currentState == NpcBehaviorState.RequestingDecision)
        {
            if (_responseQueue.Count == 0) ChangeState(NpcBehaviorState.Idle);
        }
    }

    async void StartDialogueAsync(CharacterData otherCharacter, string initialNpcUtteranceSeed = null, bool isFollowUpDialogue = false)
    {
        if (_isApiCallInProgress && !isFollowUpDialogue) return;

        if (!isFollowUpDialogue && _currentState == NpcBehaviorState.ProcessingQueue && _responseQueue.Count > 0) {
             Debug.LogWarning($"[{_characterData.characterName}] Initial dialogue with '{otherCharacter.characterName}' skipped: NPC busy processing queue (count: {_responseQueue.Count}).");
            return;
        }

        if (this == null || gameObject == null || !enabled) {
            _isApiCallInProgress = false; return;
        }

        if(!_isProcessingQueueItem) ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;

        _lastInteractedCharacter = otherCharacter;

        string npcInitialPromptForLLM = initialNpcUtteranceSeed ?? $"You, '{_characterData.characterName}', are interacting with '{otherCharacter.characterName}'.";
        string selfEmotionStringForPrompt = $"{_currentNpcEmotionalState.primary_emotion} (intensity: {_currentNpcEmotionalState.intensity:F1})";

        var interactionRequest = new GameInteractionRequest {
            interacting_objects = new List<InteractingObjectInfo> {
                _characterData.ToInteractingObjectInfo(
                    initialLlMPrompt: npcInitialPromptForLLM,
                    currentEmotionalState: selfEmotionStringForPrompt
                )
            },
            scene_context_description = sceneContextManager?.GetGeneralSceneDescription(),
            game_time_context = gameTimeManager?.GetCurrentGameTime(),
            max_turns_per_object = 1
        };

        GameInteractionResponse response = await ApiService.PostAsync<GameInteractionRequest, GameInteractionResponse>("/dialogue/game-interaction", interactionRequest);

        if (this == null || gameObject == null || !enabled) {
             _isApiCallInProgress = false; return;
        }
        _isApiCallInProgress = false;

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0)
        {
            foreach (var turn in response.dialogue_history)
            {
                if (turn.npc_id == _characterData.npcId || turn.name == _characterData.characterName)
                {
                    string messageToDisplay = !string.IsNullOrEmpty(turn.message_translated_zh_tw) ? turn.message_translated_zh_tw : turn.message_original_language;
                    _responseQueue.Enqueue(ResponseItem.AsDialogue(messageToDisplay, dialogueDisplayTime));
                    _shortTermDialogueHistory.Add(turn);
                }
            }
            _responseQueue.Enqueue(ResponseItem.AsWait(postDialoguePauseDuration));
        }
        else
        {
            _responseQueue.Enqueue(ResponseItem.AsDialogue("[Dialogue API error or no response]", dialogueDisplayTime));
            _responseQueue.Enqueue(ResponseItem.AsWait(postDialoguePauseDuration));
        }

        if (!_isProcessingQueueItem && _currentState == NpcBehaviorState.RequestingDecision) {
            if (_responseQueue.Count == 0) ChangeState(NpcBehaviorState.Idle);
        }
    }

    public void ShowDialogueBubble_TMP(string message, float durationPerPage, System.Action onComplete = null)
    {
        if (string.IsNullOrEmpty(message)) { onComplete?.Invoke(); return; }
        if (_characterData == null) { onComplete?.Invoke(); return; }

        if (dialogueBubblePrefab_TMP == null)
        {
            if (dialogueUIManager != null && dialogueUIManager.gameObject.activeInHierarchy) {
                float totalEstimatedDuration = durationPerPage;
                if (maxCharsPerPage > 0 && message.Length > maxCharsPerPage) {
                    totalEstimatedDuration = durationPerPage * (Mathf.CeilToInt((float)message.Length / maxCharsPerPage));
                }
                dialogueUIManager.ShowDialogue(_characterData.characterName, message, totalEstimatedDuration);
            } else {
                Debug.LogError($"[{_characterData.characterName}] No bubble prefab & no UIManager. Msg: {message}", this);
            }
            onComplete?.Invoke();
            return;
        }

        if (_currentDialogueBubbleInstance == null)
        {
            Vector3 bubblePosition = transform.position + Vector3.up * dialogueBubbleOffsetY;
            _currentDialogueBubbleInstance = Instantiate(dialogueBubblePrefab_TMP, bubblePosition, Quaternion.identity, transform);
            _dialogueTextTMP = _currentDialogueBubbleInstance.GetComponentInChildren<TextMeshProUGUI>() ?? _currentDialogueBubbleInstance.GetComponent<TextMeshProUGUI>();
        }
        if (_dialogueTextTMP == null) {
            Debug.LogError($"[{_characterData.characterName}] Missing TextMeshProUGUI in bubble prefab!", this);
            onComplete?.Invoke();
            return;
        }

        if (_dialogueDisplayCoroutine != null) StopCoroutine(_dialogueDisplayCoroutine);

        List<string> pages = SplitMessageIntoPages(message, maxCharsPerPage);

        if (pages.Count > 0)
        {
            _currentDialogueBubbleInstance.SetActive(true);
            _dialogueDisplayCoroutine = StartCoroutine(ShowPaginatedTextCoroutine(pages, durationPerPage, onComplete));
        }
        else
        {
            if (_currentDialogueBubbleInstance != null) _currentDialogueBubbleInstance.SetActive(false);
            onComplete?.Invoke();
        }
    }

    private List<string> SplitMessageIntoPages(string message, int maxCharsThisPage)
    {
        List<string> pages = new List<string>();
        if (string.IsNullOrEmpty(message)) return pages;
        if (maxCharsThisPage <= 0)
        {
            pages.Add(message);
            return pages;
        }

        int currentIndex = 0;
        while (currentIndex < message.Length)
        {
            int length = Mathf.Min(maxCharsThisPage, message.Length - currentIndex);

            if (currentIndex + length < message.Length)
            {
                int potentialSplitPoint = currentIndex + length -1;
                for (int i = potentialSplitPoint; i > currentIndex && i > currentIndex + length - (maxCharsThisPage / 2); i--)
                {
                    // 斷在空白或標點符號前（如果標點符號不是該行的最後一個有效字符）
                    if (char.IsWhiteSpace(message[i]) || (char.IsPunctuation(message[i]) && i < potentialSplitPoint) )
                    {
                        length = i - currentIndex + 1; // 包含該斷點字符
                        break;
                    }
                }
            }
            string currentPageContent = message.Substring(currentIndex, length).Trim();
            if (!string.IsNullOrEmpty(currentPageContent))
            {
                pages.Add(currentPageContent);
            }
            currentIndex += length;
        }
        if (pages.Count == 0 && !string.IsNullOrEmpty(message)) pages.Add(message);
        return pages;
    }

    private IEnumerator ShowPaginatedTextCoroutine(List<string> pages, float durationPerPage, System.Action onCompleteAllPages)
    {
        if (_dialogueTextTMP == null)
        {
            Debug.LogError($"[{_characterData.characterName}] DialogueTextTMP is null in ShowPaginatedTextCoroutine.");
            onCompleteAllPages?.Invoke();
            yield break;
        }

        for (int i = 0; i < pages.Count; i++)
        {
            _dialogueTextTMP.text = pages[i];
            // Debug.Log($"Displaying page {i + 1}/{pages.Count}: {pages[i]}");

            float currentPageDuration = durationPerPage;
            if (currentPageDuration <= 0.01f) 
            {
                if (i < pages.Count - 1) { 
                    yield return null;
                    continue;
                } else { 
                    yield return null; 
                }
            }
            else
            {
                yield return new WaitForSeconds(currentPageDuration);
            }
        }

        if (_currentDialogueBubbleInstance != null)
        {
            _currentDialogueBubbleInstance.SetActive(false);
        }
        _dialogueDisplayCoroutine = null;
        onCompleteAllPages?.Invoke();
    }


    void OnTriggerEnter2D(Collider2D otherCollider)
    {
        if (!_characterData.isLLMNpc || !enabled) return;

        if (_isProcessingQueueItem && (_currentlyProcessingItem?.itemType == ResponseItemType.MovementCommand || _currentlyProcessingItem?.itemType == ResponseItemType.WaitCommand) ) {
            return;
        }
        if (_isApiCallInProgress) return;

        if (interactionTrigger == null || !interactionTrigger.IsTouching(otherCollider) || otherCollider.gameObject == gameObject) return;

        if (otherCollider.TryGetComponent<CharacterData>(out CharacterData encounteredCharacter))
        {
            if (encounteredCharacter == _characterData) return;
            if (_currentTargetInteractionCharacter != null && _currentTargetInteractionCharacter == encounteredCharacter) return;

            _currentTargetInteractionCharacter = encounteredCharacter;

            if (_responseQueue.Count == 0 && !_isProcessingQueueItem)
            {
                _responseQueue.Enqueue(ResponseItem.AsDialogue($"Oh, it's {encounteredCharacter.characterName}. Maybe I'll say hi.", dialogueDisplayTime));
                Vector3 approachPos = encounteredCharacter.transform.position - (encounteredCharacter.transform.position - transform.position).normalized * (dialogueInitiationDistance * 0.9f);
                _responseQueue.Enqueue(ResponseItem.AsMovement(new Vector3(approachPos.x, approachPos.y, transform.position.z)));
                _responseQueue.Enqueue(ResponseItem.AsCustomAction(() => {
                    if (_currentTargetInteractionCharacter != null) StartDialogueAsync(_currentTargetInteractionCharacter);
                    else MarkCurrentItemCompleted();
                }));
            }
        }
    }

    void OnTriggerExit2D(Collider2D otherCollider) {
        if (_currentTargetInteractionCharacter != null && otherCollider.gameObject == _currentTargetInteractionCharacter.gameObject) {
            _currentTargetInteractionCharacter = null;
        }
    }

    private void UpdateLandmarkStatusOnArrivalOrDeparture(Vector3 npcCurrentPosition, bool isArrivalEvent)
    {
        LandmarkDataComponent eventLandmark = FindTargetLandmark(npcCurrentPosition);

        if (isArrivalEvent) {
            if (eventLandmark != null && eventLandmark != _currentActiveLandmarkZone) {
                NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                _currentActiveLandmarkZone = eventLandmark;

                if (eventLandmark.landmarkTypeTag == "bedroom" && eventLandmark.ownerNpcId == _characterData.npcId) {
                    eventLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresencePresent);
                } else if (eventLandmark.landmarkTypeTag == "bathroom") {
                    if (!eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) || eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf())) {
                         eventLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, GetOccupancyStatusOccupiedBySelf());
                    } else if (eventLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && !eventLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()) ){
                        // Debug.LogWarning($"[{_characterData.characterName}] Arrived at bathroom '{eventLandmark.landmarkName}' but it's already occupied by someone else.");
                    }
                }
                // Debug.Log($"<color=#E0BBE4>[{_characterData.characterName}] Entered zone: '{eventLandmark.landmarkName}' ({eventLandmark.landmarkTypeTag}).</color>");
            } else if (eventLandmark == null && _currentActiveLandmarkZone != null) {
                 NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                 _currentActiveLandmarkZone = null;
            }
        } else {
            if (_currentActiveLandmarkZone != null) {
                bool hasLeftCurrentZone = (eventLandmark != _currentActiveLandmarkZone);
                if (hasLeftCurrentZone) {
                    NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
                    _currentActiveLandmarkZone = eventLandmark;
                }
            } else if (eventLandmark != null) {
                _currentActiveLandmarkZone = eventLandmark;
                 // Debug.Log($"<color=#E0BBE4>[{_characterData.characterName}] Now in unpredicted zone: '{eventLandmark.landmarkName}' ({eventLandmark.landmarkTypeTag}).</color>");
            }
        }
    }

    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark)
    {
        if (departedLandmark == null || _characterData == null) return;
        // Debug.Log($"<color=#D8BFD8>[{_characterData.characterName}] Departing zone: '{departedLandmark.landmarkName}' ({departedLandmark.landmarkTypeTag}).</color>");
        if (departedLandmark.landmarkTypeTag == "bedroom" && departedLandmark.ownerNpcId == _characterData.npcId) {
            departedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresenceAbsent);
        } else if (departedLandmark.landmarkTypeTag == "bathroom") {
            if(departedLandmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf())) {
                departedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, null);
            }
        }
    }

    private LandmarkDataComponent FindTargetLandmark(Vector3 targetPosition)
    {
        if (sceneContextManager == null) return null;
        List<RoomDataComponent> allRooms = sceneContextManager.GetAllRoomDataComponents();
        if (allRooms != null) {
            foreach (var room in allRooms) {
                if (room.roomBoundsCollider != null && room.roomBoundsCollider.OverlapPoint(new Vector2(targetPosition.x, targetPosition.y))) {
                    LandmarkDataComponent roomAsLandmark = room.GetComponent<LandmarkDataComponent>();
                    if (roomAsLandmark != null) return roomAsLandmark;
                }
            }
        }
        List<LandmarkDataComponent> allLandmarks = sceneContextManager.GetAllIndividualLandmarkDataComponents();
        LandmarkDataComponent foundLandmark = null;
        float minDistanceSqToItemCenter = arrivalThreshold * arrivalThreshold * 2.25f;
        float closestDistSq = float.MaxValue;
        if (allLandmarks != null) {
            foreach (var landmark in allLandmarks) {
                if (landmark == null) continue;
                float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;
                if (distSq < minDistanceSqToItemCenter && distSq < closestDistSq) {
                    closestDistSq = distSq;
                    foundLandmark = landmark;
                }
            }
        }
        return foundLandmark;
    }
     private LandmarkDataComponent FindLandmarkByName(string name)
    {
        if (string.IsNullOrEmpty(name) || sceneContextManager == null) return null;
        var allLandmarks = sceneContextManager.GetAllIndividualLandmarkDataComponents();
        foreach(var lm in allLandmarks) {
            if (lm.landmarkName.Equals(name, StringComparison.OrdinalIgnoreCase)) return lm;
        }
        var allRooms = sceneContextManager.GetAllRoomDataComponents();
         foreach(var roomComp in allRooms) {
            if (roomComp.roomName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                return roomComp.GetComponent<LandmarkDataComponent>();
            }
        }
        return null;
    }

    private bool IsLandmarkUnavailable(LandmarkDataComponent landmark)
    {
        if (landmark == null) return false;
        if (landmark.landmarkTypeTag == "bathroom" &&
            landmark.HasDynamicStatus(OccupancyStatusOccupied) &&
            !landmark.HasDynamicStatus(GetOccupancyStatusOccupiedBySelf()))
        {
            return true;
        }
        if (landmark.landmarkTypeTag == "bedroom" &&
            !string.IsNullOrEmpty(landmark.ownerNpcId) &&
            landmark.ownerNpcId != _characterData.npcId &&
            landmark.HasDynamicStatus(OwnerPresenceAbsent))
        {
            return true;
        }
        return false;
    }

    private bool IsSocialAction(string actionSummary) {
        if (string.IsNullOrEmpty(actionSummary)) return false;
        string lowerAction = actionSummary.ToLower();
        return lowerAction.Contains("chat with") || lowerAction.Contains("talk to") ||
               lowerAction.Contains("approach") || lowerAction.Contains("greet") ||
               lowerAction.Contains("say hi to") || lowerAction.Contains("socialize with");
    }
    private bool IsGenericAction(string actionSummary) {
        if (string.IsNullOrEmpty(actionSummary)) return true;
        string lowerAction = actionSummary.ToLower();
        return lowerAction.Contains("explore") || lowerAction.Contains("wander") ||
               lowerAction.Contains("idle") || lowerAction.Contains("nothing special") ||
               lowerAction.Contains("no specific action") || lowerAction.Contains("stay put");
    }

    private string ParseOriginalTargetFromWaitingAction(string actionSummary)
    {
        if (string.IsNullOrEmpty(actionSummary)) return null;
        string pattern = @"wait near\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)";
        Match match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1) return match.Groups[1].Value.Trim();
        pattern = @"wait by the\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)";
        match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1) {
            string potentialTarget = match.Groups[1].Value.Trim();
            if (potentialTarget.EndsWith("Door", StringComparison.OrdinalIgnoreCase))
                potentialTarget = potentialTarget.Substring(0, potentialTarget.Length - "Door".Length).Trim();
            return potentialTarget;
        }
        return null;
    }
    private string ParseTargetNpcNameFromSocialAction(string actionSummary)
    {
        if (string.IsNullOrEmpty(actionSummary)) return null;
        string pattern = @"(?:chat with|talk to|approach|greet|say hi to|socialize with)\s+([A-Za-z0-9_'-]+(?:\s+[A-Za-z0-9_'-]+)*)";
        Match match = Regex.Match(actionSummary, pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1) return match.Groups[1].Value.Trim();
        return null;
    }
    private CharacterData FindCharacterByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        CharacterData[] allCharactersInScene = FindObjectsOfType<CharacterData>();
        foreach (CharacterData character in allCharactersInScene) {
            if (character.characterName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(character.npcId) && character.npcId.Equals(name, StringComparison.OrdinalIgnoreCase))) {
                return character;
            }
        }
        return null;
    }

    void OnDestroy()
    {
        if (_currentDialogueBubbleInstance != null) Destroy(_currentDialogueBubbleInstance);
        if (_currentActiveLandmarkZone != null) NotifyDepartureFromLandmark(_currentActiveLandmarkZone);
        if(_dialogueDisplayCoroutine != null) StopCoroutine(_dialogueDisplayCoroutine);
        StopAllCoroutines();
    }
}