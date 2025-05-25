// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/NpcLogic/Components/CharacterData.cs
// CharacterData.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/CharacterData.cs

using UnityEngine;
using System.Collections.Generic;
using NpcApiModels; // 引用我們在 NpcApiDataModels.cs 中定義的命名空間
using System; // For DateTime

/// <summary>
/// Stores core identification data and initial configuration for any character (NPC or Player)
/// that will interact with the LLM-driven API system.
/// This component should be attached to the character's main GameObject.
/// </summary>
public class CharacterData : MonoBehaviour
{
    [Header("Core Identification")]
    [Tooltip("NPC 的唯一識別碼，必須與後端 API 中的 NPC ID 匹配。")]
    public string npcId = ""; // 例如 "NPC_ApartmentMate_A"

    [Tooltip("角色的顯示名稱。")]
    public string characterName = ""; // 例如 "艾莉絲" 或 "公寓室友A"

    [Header("AI Configuration (for LLM-driven NPCs)")]
    [Tooltip("是否為由 LLM 驅動的 NPC。若是，請勾選；若為玩家或其他非 LLM 控制角色，請取消勾選。")]
    public bool isLLMNpc = true;

    [TextArea(3, 10)]
    [Tooltip("NPC 的預設性格描述。這將作為 LLM 理解 NPC 行為的基礎。請描述其在公寓環境中的典型行為和與人相處的風格。")]
    public string defaultPersonality = "一個普通的公寓室友，通常專注於自己的日常事務，但在公共區域（如客廳、廚房）也願意進行社交互動。可能有點潔癖，或者比較隨和，取決於具體設定。"; // Modified example for apartment setting

    [Tooltip("NPC 的預設主要情感狀態標籤（例如 'neutral', 'happy', 'curious', 'slightly_annoyed'）。")]
    public string initialPrimaryEmotion = "neutral";

    [Range(0f, 1f)]
    [Tooltip("初始主要情感的強度 (0.0 到 1.0)。")]
    public float initialEmotionIntensity = 0.5f;

    [Header("Social Configuration")]
    [Tooltip("此 NPC 的朋友列表（使用其 npcId）。用於判斷互動時的『重要性』。")]
    public List<string> friendNpcIds = new List<string>(); // 例如，公寓內的其他室友可以是朋友

    // 您可以在 Inspector 中定義預設的日程規則（如果 NPCScheduleRule 設為 [System.Serializable]）
    // 為簡單起見，我們目前在 CreateDefaultMemoryFile 中初始化一個空列表。
    // public List<NpcApiModels.NPCScheduleRule> defaultScheduleRulesForInitialization = new List<NpcApiModels.NPCScheduleRule>();


    void OnValidate()
    {
        if (string.IsNullOrEmpty(characterName))
        {
            characterName = gameObject.name; // 如果角色名稱為空，預設使用 GameObject 的名稱
        }

        if (isLLMNpc && string.IsNullOrEmpty(npcId))
        {
            Debug.LogWarning($"CharacterData on GameObject '{gameObject.name}' is configured as an LLM NPC but has an EMPTY 'Npc Id'. " +
                             "A unique Npc Id is CRUCIAL for the AI to function correctly. Please assign a unique ID (e.g., 'NPC_ApartmentMate_A').", this);
        }
        else if (string.IsNullOrEmpty(npcId) && !gameObject.CompareTag("Player")) // 如果不是玩家且 NpcId 為空
        {
             Debug.Log($"CharacterData on GameObject '{gameObject.name}' has an empty 'Npc Id'. " +
                             "Consider assigning a unique ID if this character needs to be uniquely identified by other game systems or for specific landmark ownership.", this);
        }
    }

    /// <summary>
    /// 獲取此角色的 NpcIdentifier，用於 API 請求。
    /// </summary>
    /// <returns>包含 npc_id 和 name 的 NpcIdentifier 物件。</returns>
    public NpcIdentifier GetNpcIdentifier()
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to GetNpcIdentifier for '{characterName}' but npcId is empty! This can cause issues with API communication.", this);
            // 返回一個包含錯誤標記的識別碼，以避免後續出現 null 引用錯誤，但應盡快修復 npcId
            return new NpcIdentifier { npc_id = $"ERROR_UNSET_ID_FOR_{gameObject.name.Replace(" ", "_")}_{gameObject.GetInstanceID()}", name = this.characterName };
        }
        return new NpcIdentifier { npc_id = this.npcId, name = this.characterName };
    }

    /// <summary>
    /// 將此角色的資料轉換為 EntityContextInfo，用於向 API 提供附近實體的上下文。
    /// </summary>
    /// <param name="isSignificantToDecisionMaker">此實體對於正在做決策的 NPC 是否重要（例如，是否為朋友）。</param>
    /// <returns>包含實體詳細資訊的 EntityContextInfo 物件。</returns>
    public EntityContextInfo ToEntityContextInfo(bool isSignificantToDecisionMaker = false)
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to create EntityContextInfo for '{characterName}' but npcId is empty!", this);
            return new EntityContextInfo {
                npc_id = $"ERROR_UNSET_ID_FOR_{gameObject.name.Replace(" ", "_")}_{gameObject.GetInstanceID()}",
                name = this.characterName,
                x = transform.position.x,
                y = transform.position.y,
                entity_type = gameObject.CompareTag("Player") ? "player" : (isLLMNpc ? "npc_llm_roommate" : "npc_basic_roommate"), // More specific entity types for apartment
                is_significant_to_npc = isSignificantToDecisionMaker
            };
        }
        return new EntityContextInfo
        {
            npc_id = this.npcId,
            name = this.characterName,
            x = transform.position.x,
            y = transform.position.y,
            entity_type = gameObject.CompareTag("Player") ? "player" : (isLLMNpc ? "npc_llm_roommate" : "npc_basic_roommate"), // Modified entity_type for apartment
            is_significant_to_npc = isSignificantToDecisionMaker
        };
    }

    /// <summary>
    /// 將此角色的資料轉換為 InteractingObjectInfo，用於對話 API 請求。
    /// </summary>
    /// <param name="initialLlMPrompt">給 LLM 的初始提示，用於引導其第一句話。</param>
    /// <param name="dialogueMode">對話模式標籤。</param>
    /// <param name="currentEmotionalState">當前的情緒狀態字串輸入。</param>
    /// <param name="llmModelOverride">是否覆蓋預設的 LLM 模型。</param>
    /// <returns>包含互動物件詳細資訊的 InteractingObjectInfo 物件。</returns>
    public InteractingObjectInfo ToInteractingObjectInfo(
        string initialLlMPrompt = null,
        string dialogueMode = null,
        string currentEmotionalState = null, // This string is usually prepared by NpcController
        string llmModelOverride = null)
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to create InteractingObjectInfo for '{characterName}' but npcId is empty!", this);
            // Provide a fallback to prevent null issues, but this is an error condition
             return new InteractingObjectInfo {
                npc_id = $"ERROR_UNSET_ID_FOR_{gameObject.name.Replace(" ", "_")}_{gameObject.GetInstanceID()}",
                name = this.characterName,
                initial_prompt_to_llm = initialLlMPrompt ?? "I am unsure how to respond due to an ID error.",
                dialogue_mode_tag = dialogueMode,
                emotional_state_input = currentEmotionalState ?? "confused_due_to_error",
                model_override = llmModelOverride
            };
        }
        return new InteractingObjectInfo
        {
            npc_id = this.npcId,
            name = this.characterName,
            initial_prompt_to_llm = initialLlMPrompt,
            dialogue_mode_tag = dialogueMode,
            emotional_state_input = currentEmotionalState, // Passed in from NpcController, which has the live _currentNpcEmotionalState
            model_override = llmModelOverride
        };
    }

    /// <summary>
    /// 創建一個預設的 NPC 記憶檔案物件。
    /// 這通常在 NPC 首次初始化或其記憶檔案遺失時使用。
    /// </summary>
    /// <returns>一個預填充了預設值的 NPCMemoryFile 物件。</returns>
    public NPCMemoryFile CreateDefaultMemoryFile()
    {
        if (!isLLMNpc)
        {
            Debug.LogWarning($"CreateDefaultMemoryFile called on '{characterName}' which is not marked as an LLM NPC. Returning a minimal structure.", this);
             return new NPCMemoryFile { 
                npc_id = string.IsNullOrEmpty(this.npcId) ? $"NON_LLM_ID_{gameObject.GetInstanceID()}" : this.npcId,
                name = this.characterName,
                personality_description = "Not an LLM-driven character.",
                last_saved_at = DateTime.UtcNow.ToString("o"), // ISO 8601 format
                current_emotional_state = new NpcEmotionalState { primary_emotion = "n_a", intensity = 0f } // Not applicable
            };
        }
        
        string currentIsoTime = DateTime.UtcNow.ToString("o"); // "o" for round-trip ISO 8601 format

        return new NPCMemoryFile
        {
            npc_id = this.npcId,
            name = this.characterName,
            personality_description = this.defaultPersonality, // Uses the personality set in Inspector
            last_saved_at = currentIsoTime,
            last_known_game_time = null, // GameTimeManager 應提供這個，初始為 null
            last_known_position = new NpcApiModels.Position { x = transform.position.x, y = transform.position.y },
            current_emotional_state = new NpcEmotionalState {
                primary_emotion = this.initialPrimaryEmotion,
                intensity = this.initialEmotionIntensity,
                mood_tags = new List<string>(), // 初始時沒有額外情緒標籤
                last_significant_change_at = currentIsoTime,
                reason_for_last_change = "Initial state upon memory creation for apartment living." // Modified reason
            },
            active_schedule_rules = new List<NPCScheduleRule>(), // 初始時沒有活動的日程規則，應由 NpcScheduleManager 或 API 設定
            short_term_location_history = new List<VisitedLocationEntry>(),
            long_term_event_memories = new List<LongTermMemoryEntry>()
        };
    }
}