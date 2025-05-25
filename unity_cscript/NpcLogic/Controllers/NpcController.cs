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
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))] // 通常用於檢測互動觸發器
public class NpcController : MonoBehaviour
{
    [Header("NPC Core Configuration")]
    [Tooltip("當 NPC 空閒或完成任務後，進行 LLM 移動決策之間的時間（秒）。")]
    public float decisionInterval = 15.0f;
    [Tooltip("NPC 的移動速度（Unity 單位/秒）。")]
    public float moveSpeed = 2.0f;
    [Tooltip("認為 NPC 已到達目標的距離閾值。")]
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


    [Header("Context Providers (Assign from Scene Managers)")]
    [Tooltip("對 GameTimeManager 的引用，用於獲取當前遊戲時間。")]
    public GameTimeManager gameTimeManager;
    [Tooltip("對 SceneContextManager 的引用，用於獲取地標和其他角色列表。")]
    public SceneContextManager sceneContextManager;
    [Tooltip("對 DialogueUIManager 的引用，用於顯示對話文本。")]
    public DialogueUIManager dialogueUIManager;

    // --- 內部狀態 ---
    private CharacterData _characterData;
    private Rigidbody2D _rb;
    private NpcBehaviorState _currentState = NpcBehaviorState.Idle;
    private float _decisionTimer = 0f;
    private Vector3 _currentMovementTargetWorld; // LLM 決定的世界座標目標點
    private bool _hasMovementTarget = false;
    private bool _isApiCallInProgress = false;
    private CharacterData _currentTargetInteractionCharacter = null; // 當前主動接近以進行互動的目標角色
    private CharacterData _lastInteractedCharacter = null; // 上一個與之互動的角色

    private List<DialogueTurn> _shortTermDialogueHistory = new List<DialogueTurn>();
    private const int MaxDialogueHistoryForMovementContext = 6; // 移動決策時考慮的最近對話輪次數量

    // 儲存情緒狀態，可能從記憶中載入或由 API 回應更新
    private NpcApiModels.NpcEmotionalState _currentNpcEmotionalState;

    // Constants for dynamic status notes prefixes and values (optional, for clarity)
    private const string OccupancyStatusPrefix = "occupancy_";
    private const string OccupancyStatusOccupied = "occupancy_occupied";
    private const string OccupancyStatusVacant = "occupancy_vacant"; // Example if needed

    private const string OwnerPresenceStatusPrefix = "owner_presence_";
    private const string OwnerPresencePresent = "owner_presence_present";
    private const string OwnerPresenceAbsent = "owner_presence_absent";


    // --- 初始化 ---
    void Awake()
    {
        _characterData = GetComponent<CharacterData>();
        _rb = GetComponent<Rigidbody2D>();
        if (_rb != null) _rb.isKinematic = true; // 通常由控制器直接設定位置


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

        // 檢查必要的管理器是否已分配
        if (gameTimeManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': GameTimeManager not assigned!", this);
        if (sceneContextManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': SceneContextManager not assigned!", this);
        if (dialogueUIManager == null) Debug.LogError($"NpcController on '{_characterData.characterName}': DialogueUIManager not assigned!", this);

        // 初始化情緒狀態
        InitializeEmotionalState();
    }

    void InitializeEmotionalState()
    {
        // 首先嘗試從 CharacterData 的預設記憶檔案中獲取情緒狀態
        // 這會在之後被從伺服器透過 NPCMemoryService 載入的記憶所覆寫（如果實現了該功能）
        if (_characterData != null && _characterData.isLLMNpc)
        {
            NPCMemoryFile defaultMemory = _characterData.CreateDefaultMemoryFile(); // 使用 CharacterData.cs 中定義的正確方法名
            if (defaultMemory != null && defaultMemory.current_emotional_state != null)
            {
                _currentNpcEmotionalState = defaultMemory.current_emotional_state;
            }
            else 
            {
                Debug.LogWarning($"[{_characterData.characterName}] CreateDefaultMemoryFile returned null or incomplete data. Initializing default emotional state for NpcController.");
                _currentNpcEmotionalState = new NpcEmotionalState {
                    primary_emotion = "neutral", // 後備預設值
                    intensity = 0.5f,
                    last_significant_change_at = DateTime.UtcNow.ToString("o"), // ISO 8601 格式
                    reason_for_last_change = "Fallback initial state in NpcController."
                };
            }
        }
        else if (_characterData != null) // 如果不是 LLM NPC，給一個預設的「不適用」情緒狀態
        {
            _currentNpcEmotionalState = new NpcEmotionalState {
                primary_emotion = "n_a", // Not Applicable
                intensity = 0f,
                last_significant_change_at = DateTime.UtcNow.ToString("o"),
                reason_for_last_change = "Non-LLM character initial state."
            };
        }
        else // 後備情況，如果 _characterData 本身為 null (理論上會被 Awake 前面的檢查捕捉到)
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
        // 初始決策計時器隨機化，避免所有 NPC 同時請求決策
        _decisionTimer = UnityEngine.Random.Range(decisionInterval * 0.1f, decisionInterval * 0.75f);
        ChangeState(NpcBehaviorState.Idle);
        // TODO: 考慮在此處與伺服器進行初始記憶體同步，
        // 這可能會從持久化資料中更新 _currentNpcEmotionalState。
        // 例如: CoroutineHelper.Run(LoadPersistedMemoryAsync());
    }

    // --- 主更新循環 (狀態機) ---
    void Update()
    {
        if (!_characterData.isLLMNpc || !enabled) return; // 如果不是 LLM NPC 或已禁用，則不執行

        switch (_currentState)
        {
            case NpcBehaviorState.Idle:
                HandleIdleState();
                break;
            case NpcBehaviorState.RequestingDecision:
                // 等待 API 回應，通常此狀態很短暫
                break;
            case NpcBehaviorState.MovingToTarget:
                HandleMovingToTargetState();
                break;
            case NpcBehaviorState.ApproachingInteraction:
                HandleApproachingInteractionState();
                break;
            case NpcBehaviorState.Interacting:
                // 等待對話 UI / 玩家回應 / 其他 NPC 回應
                break;
            case NpcBehaviorState.PostInteractionPause:
                // 協程處理此狀態的持續時間
                break;
        }
    }

    void ChangeState(NpcBehaviorState newState)
    {
        if (_currentState == newState && newState != NpcBehaviorState.RequestingDecision) return; // 允許重新進入 RequestingDecision
        // Debug.Log($"<color=#ADD8E6>[{_characterData.characterName}] State: {_currentState} -> {newState}</color>"); // 淡藍色日誌
        _currentState = newState;

        switch (newState)
        {
            case NpcBehaviorState.Idle:
                _hasMovementTarget = false; // 空閒時，沒有特定的 LLM 移動目標
                // _currentTargetInteractionCharacter = null; // 考慮是否應在此處或 PostInteractionPause 後清除
                break;
            case NpcBehaviorState.MovingToTarget:
                 _currentTargetInteractionCharacter = null; // 如果是移向 LLM 目標，則不是接近某個角色
                break;
             case NpcBehaviorState.ApproachingInteraction:
                _hasMovementTarget = false; // 停止當前的 LLM 移動目標，以接近角色
                break;
            case NpcBehaviorState.Interacting:
                _hasMovementTarget = false; // 在活躍的對話 UI 階段不移動
                _decisionTimer = 0f; // 重置決策計時器，以便在互動後快速重新評估
                break;
            case NpcBehaviorState.PostInteractionPause:
                StartCoroutine(PostInteractionPauseCoroutine());
                break;
            case NpcBehaviorState.RequestingDecision:
                // 此狀態通常很短暫，僅表示 API 呼叫正在進行中。
                break;
        }
    }

    void HandleIdleState()
    {
        _decisionTimer += Time.deltaTime;
        if (_decisionTimer >= decisionInterval && !_isApiCallInProgress)
        {
            _decisionTimer = 0f; // 重置計時器
            RequestMovementDecisionAsync(); // 預設的移動決策請求
        }
    }

    void HandleMovingToTargetState()
    {
        if (!_hasMovementTarget) {
            ChangeState(NpcBehaviorState.Idle); return;
        }
        Vector3 currentPosition = transform.position;
        // 確保目標點在 NPC 的 Z 平面上
        Vector3 targetOnPlane = new Vector3(_currentMovementTargetWorld.x, _currentMovementTargetWorld.y, currentPosition.z); 
        
        if (Vector2.Distance(new Vector2(currentPosition.x, currentPosition.y), new Vector2(targetOnPlane.x, targetOnPlane.y)) > arrivalThreshold) {
            // 使用 NpcMovement 組件（如果有的話）或直接移動
            // 假設直接移動 transform.position
            transform.position = Vector3.MoveTowards(currentPosition, targetOnPlane, moveSpeed * Time.deltaTime);
            
            // 視覺模型旋轉
            if (visualModelTransform != null && (targetOnPlane - currentPosition).sqrMagnitude > 0.01f) {
                Vector3 direction = (targetOnPlane - currentPosition).normalized;
                float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                visualModelTransform.rotation = Quaternion.AngleAxis(angle - 90, Vector3.forward); // 如果 Sprite 預設朝上，可能需要 -90 度調整
            }
        } else { // 到達目標
            Debug.Log($"<color=green>[{_characterData.characterName}] Arrived at LLM target: ({targetOnPlane.x:F1}, {targetOnPlane.y:F1})</color>");
            transform.position = targetOnPlane; // 精確到達目標點
            _hasMovementTarget = false;
            
            // TODO: 在到達後，可以觸發一個事件或通知 NPCMemoryService 記錄此次到達
            // 例如，更新此地標的 "occupancy" 或 "owner_presence" 狀態
            UpdateLandmarkStatusOnArrival(targetOnPlane);

            ChangeState(NpcBehaviorState.Idle); // 返回 Idle 狀態以決定下一步行動
        }
    }

    void HandleApproachingInteractionState()
    {
        if (_currentTargetInteractionCharacter == null) { 
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
        } else { // 到達互動距離
            Debug.Log($"<color=orange>[{_characterData.characterName}] Reached '{_currentTargetInteractionCharacter.characterName}' for dialogue.</color>");
            StartDialogueAsync(_currentTargetInteractionCharacter); // 發起對話
        }
    }

    private IEnumerator PostInteractionPauseCoroutine()
    {
        yield return new WaitForSeconds(postDialoguePauseDuration);
        ChangeState(NpcBehaviorState.Idle); 
    }

    async void RequestMovementDecisionAsync(bool isReEvaluationDueToBlock = false) // 新增標記
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
        
        // 確保傳遞最新的地標狀態 (包括廁所是否佔用，房間主人是否在場等)
        List<EntityContextInfo> nearbyEntities = sceneContextManager?.GetNearbyEntities(selfIdentifier.npc_id, transform.position, 20f) ?? new List<EntityContextInfo>();
        List<LandmarkContextInfo> visibleLandmarks = sceneContextManager?.GetVisibleLandmarks(transform.position, 30f) ?? new List<LandmarkContextInfo>(); // SceneContextManager 應提供包含最新狀態的地標
        
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
            // 新增：將之前的移動中止原因傳遞給 LLM，如果有的話
            // 需要在 NpcApiDataModels.cs 的 NpcMovementRequest 中也添加此欄位 (例如 previous_movement_failure_reason)
            // For now, we can prepend it to dialogue_summary or another text field if model doesn't have a dedicated field.
            // Or, a simpler way is to modify the prompt builder to include this context conditionally.
            // Let's assume prompt_builder.py will handle adding this context if we pass it as an argument.
            // This example will assume that the `recent_dialogue_summary_for_movement` field can be augmented or
            // the prompt builder is modified to take an additional parameter.
            // For simplicity, let's augment the dialogue summary for now if a dedicated field is not in NpcMovementRequest schema.
            // A better way is to add a field like `additional_context_for_decision` to NpcMovementRequest
        };
        if(previousAbortReasonForPrompt != null){
             requestPayload.recent_dialogue_summary_for_movement = string.IsNullOrEmpty(requestPayload.recent_dialogue_summary_for_movement)
                ? previousAbortReasonForPrompt
                : $"{previousAbortReasonForPrompt}\nRecent dialogue was: {requestPayload.recent_dialogue_summary_for_movement}";
        }


        Debug.Log($"<color=cyan>[{_characterData.characterName}] Requesting MOVEMENT decision. Re-eval: {isReEvaluationDueToBlock}. Dialogue/Context: '{requestPayload.recent_dialogue_summary_for_movement?.Substring(0, Mathf.Min(requestPayload.recent_dialogue_summary_for_movement?.Length ?? 0, 100))}...'</color>");
        NpcMovementResponse response = await ApiService.PostAsync<NpcMovementRequest, NpcMovementResponse>("/npc/think", requestPayload);
        _isApiCallInProgress = false; 
        _lastMovementAbortReason = null; // 清除上次的中止原因

        if (response != null && response.target_destination != null) {
            Debug.Log($"<color=#90EE90>[{_characterData.characterName}] Movement decision received. Action: '{response.chosen_action_summary}'. Target: ({response.target_destination.x:F1}, {response.target_destination.y:F1})</color>");
            Vector3 potentialTargetWorld = new Vector3(response.target_destination.x, response.target_destination.y, transform.position.z);

            // --- BEGIN MODIFIED ACCESSIBILITY CHECK ---
            bool canProceedToTarget = true;
            string currentAbortReason = "";

            LandmarkDataComponent targetLocationMeta = FindTargetLandmark(potentialTargetWorld);

            if (targetLocationMeta != null)
            {
                // Rule 1: Toilet occupied?
                // LandmarkDataComponent.HasDynamicStatusWithPrefix and HasDynamicStatus are new helpers
                if (targetLocationMeta.landmarkTypeTag == "bathroom" && targetLocationMeta.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) && targetLocationMeta.HasDynamicStatus(OccupancyStatusOccupied))
                {
                    // More specific check: is THIS NPC the one occupying it?
                    // For simplicity, if "occupied" by anyone else, cannot enter.
                    // We'd need a way to know WHO is occupying it.
                    // Assuming "OccupancyStatusOccupied" means someone *else* or unknown.
                    // If the note was "occupancy_occupied_by_MyNpcId", then it's fine.
                    // For now, a general "occupied" blocks.
                    canProceedToTarget = false;
                    currentAbortReason = "Toilet is occupied.";
                }
                // Rule 2: Private room and owner absent?
                else if (targetLocationMeta.landmarkTypeTag == "bedroom" &&
                         !string.IsNullOrEmpty(targetLocationMeta.ownerNpcId) &&
                         targetLocationMeta.ownerNpcId != _characterData.npcId &&
                         targetLocationMeta.HasDynamicStatus(OwnerPresenceAbsent)) // Check for specific "absent" status
                {
                    canProceedToTarget = false;
                    currentAbortReason = $"Room '{targetLocationMeta.landmarkName}' is private and owner '{targetLocationMeta.ownerNpcId}' is absent.";
                }
            }

            if (!canProceedToTarget)
            {
                Debug.LogWarning($"<color=yellow>[{_characterData.characterName}] Cannot proceed to LLM target ({potentialTargetWorld.x:F1}, {potentialTargetWorld.y:F1}). Reason: {currentAbortReason}. Re-requesting decision.</color>");
                _lastMovementAbortReason = currentAbortReason; // 儲存中止原因
                RequestMovementDecisionAsync(true); // 標記為重新評估
                return; 
            }
            // --- END MODIFIED ACCESSIBILITY CHECK ---

            _currentMovementTargetWorld = potentialTargetWorld;
            _hasMovementTarget = true;

            if (response.updated_emotional_state_snapshot != null) {
                _currentNpcEmotionalState = response.updated_emotional_state_snapshot; 
                Debug.Log($"<color=grey>[{_characterData.characterName}] Emotional state updated from API to: {_currentNpcEmotionalState.primary_emotion} (Intensity: {_currentNpcEmotionalState.intensity:F1})</color>");
            }

            bool wasDialogueDriven = response.primary_decision_drivers.GetValueOrDefault("dialogue_driven", false);
            bool accessRulesConsidered = response.primary_decision_drivers.GetValueOrDefault("access_rules_consideration", false); // Check new flag from LLM

            if (isReEvaluationDueToBlock && accessRulesConsidered) {
                 Debug.Log($"<color=orange>[{_characterData.characterName}] Re-evaluated decision, LLM considered access rules. Proceeding with: '{response.chosen_action_summary}'</color>");
            }


            if (wasDialogueDriven && _lastInteractedCharacter != null && isReEvaluationDueToBlock == false) { // Only do follow-up if not a re-evaluation from block
                // This condition might need adjustment: should a re-evaluated move also trigger follow-up?
                // For now, assume re-evaluations don't trigger new dialogues about the *same* original decision point.
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination '{response.chosen_action_summary}' was driven by dialogue with '{_lastInteractedCharacter.characterName}'. Initiating follow-up confirmation dialogue.</color>");
                StartDialogueAsync(_lastInteractedCharacter, $"Okay, I've decided based on our chat: {response.chosen_action_summary}. Does that make sense to you, {(_lastInteractedCharacter.characterName ?? "there")}?", true);
            }
            else if (wasDialogueDriven && !string.IsNullOrEmpty(response.chosen_action_summary) && _lastInteractedCharacter == null && isReEvaluationDueToBlock == false) { 
                Debug.Log($"<color=magenta>[{_characterData.characterName}] Destination decision influenced by general dialogue/thought. Thinking aloud: '{response.chosen_action_summary}'.</color>");
                if (dialogueUIManager != null) {
                    dialogueUIManager.ShowDialogue(_characterData.characterName, $"Hmm, I think I will {response.chosen_action_summary.ToLower()}.", 3f);
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
    private string _lastMovementAbortReason = null; // 新增欄位來儲存上次移動中止的原因


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

        ChangeState(NpcBehaviorState.RequestingDecision);
        _isApiCallInProgress = true;
        _hasMovementTarget = false; 
        _lastInteractedCharacter = otherCharacter; 

        string npcInitialPromptForLLM;
        if (!string.IsNullOrEmpty(initialNpcUtteranceSeed)) {
            npcInitialPromptForLLM = initialNpcUtteranceSeed;
        } else {
            // 更具體的初始提示，考慮公寓場景
            string sceneHint = "You are in a shared apartment. ";
            npcInitialPromptForLLM = $"{sceneHint}You, '{_characterData.characterName}', have encountered '{otherCharacter.characterName}'. Initiate a natural, contextually appropriate conversation based on your personality and current emotional state.";
        }

        string selfEmotionStringForPrompt = $"{_currentNpcEmotionalState.primary_emotion} (intensity: {_currentNpcEmotionalState.intensity:F1})";

        Debug.Log($"<color=orange>[{_characterData.characterName}] Initiating dialogue with '{otherCharacter.characterName}'. Follow-up: {isFollowUpDialogue}. LLM Seed: '{npcInitialPromptForLLM}'. Emotion: {selfEmotionStringForPrompt}</color>");

        var interactionRequest = new GameInteractionRequest {
            interacting_objects = new List<InteractingObjectInfo> {
                _characterData.ToInteractingObjectInfo(
                    initialLlMPrompt: npcInitialPromptForLLM,
                    dialogueMode: null, // TODO: 從 CharacterData 或狀態獲取偏好的對話模式
                    currentEmotionalState: selfEmotionStringForPrompt, 
                    llmModelOverride: null 
                ),
                // 如果 otherCharacter 也是 LLM NPC 並且應該在此同一個 API 呼叫中回應:
                // otherCharacter.ToInteractingObjectInfo( ... ) 
            },
            scene_context_description = sceneContextManager?.GetGeneralSceneDescription() ?? "A room in the apartment.", // 更新場景描述
            game_time_context = gameTimeManager?.GetCurrentGameTime() ?? new GameTime{current_timestamp=DateTime.UtcNow.ToString("o"), time_of_day="unknown_time"},
            max_turns_per_object = 1 
        };

        GameInteractionResponse response = await ApiService.PostAsync<GameInteractionRequest, GameInteractionResponse>("/dialogue/game-interaction", interactionRequest);
        
        _isApiCallInProgress = false; 
        ChangeState(NpcBehaviorState.Interacting); 

        if (response != null && response.dialogue_history != null && response.dialogue_history.Count > 0) {
            foreach (var turn in response.dialogue_history) {
                string messageToDisplay = !string.IsNullOrEmpty(turn.message_translated_zh_tw) ? turn.message_translated_zh_tw : turn.message_original_language;
                if (dialogueUIManager != null) {
                    dialogueUIManager.ShowDialogue(turn.name ?? turn.npc_id, messageToDisplay, 5f); 
                } else {
                    Debug.LogWarning($"[{_characterData.characterName}] DialogueUIManager is null. Cannot display dialogue: {turn.name ?? turn.npc_id}: {messageToDisplay}");
                }
                
                _shortTermDialogueHistory.Add(turn); 
                if (_shortTermDialogueHistory.Count > MaxDialogueHistoryForMovementContext * 2) { // 保持歷史記錄簡潔
                    _shortTermDialogueHistory.RemoveRange(0, _shortTermDialogueHistory.Count - MaxDialogueHistoryForMovementContext * 2);
                }
            }
        } else {
            Debug.LogError($"[{_characterData.characterName}] Dialogue interaction with '{otherCharacter.characterName}' failed or returned no history.");
            if(dialogueUIManager != null) dialogueUIManager.ShowDialogue(_characterData.characterName, "[NPC seems unresponsive or an error occurred.]", 3f);
        }
        
        ChangeState(NpcBehaviorState.PostInteractionPause);
    }

    void OnTriggerEnter2D(Collider2D otherCollider)
    {
        if (!_characterData.isLLMNpc || !enabled || _isApiCallInProgress) return;
        if (_currentState == NpcBehaviorState.Interacting || 
            _currentState == NpcBehaviorState.ApproachingInteraction || 
            _currentState == NpcBehaviorState.PostInteractionPause ||
            _currentState == NpcBehaviorState.RequestingDecision) return; // 如果正在忙，則不觸發新的互動

        if (interactionTrigger == null || !interactionTrigger.IsTouching(otherCollider) || otherCollider.gameObject == gameObject) return;

        if (otherCollider.TryGetComponent<CharacterData>(out CharacterData encounteredCharacter))
        {
            // 避免與自己互動，或與非 LLM NPC 進行複雜互動 (除非有特定設計)
            if (encounteredCharacter == _characterData) return;

            // 在公寓場景中，NPC 更可能頻繁遇到其他 NPC
            Debug.Log($"<color=yellow>[{_characterData.characterName}] Encountered '{encounteredCharacter.characterName}' in apartment. Preparing to interact.</color>");
            _currentTargetInteractionCharacter = encounteredCharacter;
            _hasMovementTarget = false; 
            ChangeState(NpcBehaviorState.ApproachingInteraction); 
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
                _lastInteractedCharacter = null; 
                if(dialogueUIManager != null) dialogueUIManager.HideDialogue();
                ChangeState(NpcBehaviorState.Idle); 
            }
        }
    }

    // --- Helper function to find LandmarkDataComponent at or near a position ---
    private LandmarkDataComponent FindTargetLandmark(Vector3 targetPosition)
    {
        if (sceneContextManager == null) return null;

        // sceneContextManager 應該提供一個獲取所有 LandmarkDataComponent 的方法
        // 或者，您可以讓 NpcController 自己快取一份場景中的所有 LandmarkDataComponent
        // List<LandmarkDataComponent> allLandmarks = sceneContextManager.GetAllLandmarkComponentsInScene(); // 假設 SceneContextManager 有此方法
        // 為了這個範例，我們假設 SceneContextManager 可以直接提供列表
        
        var allLandmarks = FindObjectsOfType<LandmarkDataComponent>(); // 簡單但效率稍差的查找方式，最好由 SceneContextManager 管理
        if (allLandmarks == null || allLandmarks.Length == 0) return null;

        LandmarkDataComponent closestLandmark = null;
        float minDistanceSq = float.MaxValue;
        float effectiveRadiusSq = 0.5f * 0.5f; // 認為目標點在此半徑內的地標即為目標地標

        foreach (var landmark in allLandmarks)
        {
            if (landmark == null) continue;
            float distSq = (landmark.transform.position - targetPosition).sqrMagnitude;
            if (distSq < effectiveRadiusSq) // 如果在地標的「精確」範圍內
            {
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    closestLandmark = landmark;
                }
            }
        }
        // if (closestLandmark != null) Debug.Log($"FindTargetLandmark: Found '{closestLandmark.landmarkName}' near ({targetPosition.x:F1},{targetPosition.y:F1})");
        // else Debug.Log($"FindTargetLandmark: No specific landmark found very near ({targetPosition.x:F1},{targetPosition.y:F1})");
        return closestLandmark; 
        // 注意：如果 LLM 的目標是一個開放區域而不是特定地標，此方法可能返回 null。
        // 在這種情況下，房間進入規則可能不適用，除非該區域本身就是一個 "房間" 類型的地標。
    }
    
    // --- Helper to update landmark status on arrival/departure ---
    // 這個方法應該在 NPC 實際到達一個地標或離開一個地標時被調用
    private void UpdateLandmarkStatusOnArrival(Vector3 arrivalPosition)
    {
        LandmarkDataComponent arrivedLandmark = FindTargetLandmark(arrivalPosition);
        if (arrivedLandmark == null) return;

        // 範例：如果到達的是自己的臥室，更新主人在場狀態
        if (arrivedLandmark.landmarkTypeTag == "bedroom" && arrivedLandmark.ownerNpcId == _characterData.npcId)
        {
            arrivedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresencePresent);
            Debug.Log($"[{_characterData.characterName}] arrived at own bedroom '{arrivedLandmark.landmarkName}', status set to owner_present.");
        }
        // 範例：如果到達的是廁所
        else if (arrivedLandmark.landmarkTypeTag == "bathroom")
        {
            // 假設一次只允許一個 NPC 使用廁所
            // 檢查是否已經被其他人佔用（這裡的邏輯可能需要更完善，例如通過檢查是否有 "occupied_by_OtherNPC" 狀態）
            if (!arrivedLandmark.HasDynamicStatusWithPrefix(OccupancyStatusPrefix) || arrivedLandmark.HasDynamicStatus(OccupancyStatusVacant))
            {
                arrivedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, $"{OccupancyStatusOccupied}_by_{_characterData.npcId}");
                 Debug.Log($"[{_characterData.characterName}] entered bathroom '{arrivedLandmark.landmarkName}', status set to occupied_by_{_characterData.npcId}.");
            }
        }
        // 其他地標狀態更新...
    }

    // 當 NPC 離開一個地標時，也需要類似的 UpdateLandmarkStatusOnDeparture
    // 例如：離開自己的臥室，更新為 owner_absent；離開廁所，更新為 vacant
    public void NotifyDepartureFromLandmark(LandmarkDataComponent departedLandmark) // Public if called from elsewhere
    {
        if (departedLandmark == null) return;

        if (departedLandmark.landmarkTypeTag == "bedroom" && departedLandmark.ownerNpcId == _characterData.npcId)
        {
            departedLandmark.UpdateDynamicStatusByPrefix(OwnerPresenceStatusPrefix, OwnerPresenceAbsent);
             Debug.Log($"[{_characterData.characterName}] departed own bedroom '{departedLandmark.landmarkName}', status set to owner_absent.");
        }
        else if (departedLandmark.landmarkTypeTag == "bathroom")
        {
            // 只有當是這個 NPC 佔用時才將其設為 vacant
            if(departedLandmark.HasDynamicStatus($"{OccupancyStatusOccupied}_by_{_characterData.npcId}"))
            {
                departedLandmark.UpdateDynamicStatusByPrefix(OccupancyStatusPrefix, OccupancyStatusVacant); // Or just remove the specific occupied_by note
                 Debug.Log($"[{_characterData.characterName}] exited bathroom '{departedLandmark.landmarkName}', status set to vacant.");
            }
        }
    }
    // 您需要在 HandleMovingToTargetState 的 ChangeState(NpcBehaviorState.Idle) 之前，
    // 或在其他狀態轉換時，判斷 NPC 是否正從一個重要地標離開，並調用 NotifyDepartureFromLandmark。
    // 這需要追蹤 NPC 目前 "所在" 的地標。
}