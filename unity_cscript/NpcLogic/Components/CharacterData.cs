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
    public string npcId = "";

    [Tooltip("角色名稱")]
    public string characterName = "";

    [Header("AI Configuration (for LLM-driven NPCs)")]
    [Tooltip("是否為NPC，是請打勾反之請取消勾選")]
    public bool isLLMNpc = true;

    [TextArea(3, 10)]
    [Tooltip("NPC性格描述")]
    public string defaultPersonality = "A standard villager, curious about their surroundings and generally helpful. Tends to follow a daily routine unless something interesting happens.";

    [Tooltip("NPC的預設情感狀態標籤（例如'neutral'、'happy'、'curious'）。")]
    public string initialPrimaryEmotion = "neutral";

    [Range(0f, 1f)]
    [Tooltip("情緒強度(0.0 to 1.0).")]
    public float initialEmotionIntensity = 0.5f;

    [Header("Social Configuration")]
    [Tooltip("朋友的ID列表")]
    public List<string> friendNpcIds = new List<string>();

    // Example of how you might define default schedule rules in Inspector
    // You would need to make NPCScheduleRule [System.Serializable] and have its fields also marked appropriately.
    // For simplicity, we initialize an empty list in CreateDefaultMemoryFile for now.
    // public List<NpcApiModels.NPCScheduleRule> defaultScheduleRulesForInitialization = new List<NpcApiModels.NPCScheduleRule>();


    void OnValidate()
    {
        if (string.IsNullOrEmpty(characterName))
        {
            characterName = gameObject.name;
        }

        if (isLLMNpc && string.IsNullOrEmpty(npcId))
        {
            Debug.LogWarning($"CharacterData on GameObject '{gameObject.name}' is configured as an LLM NPC but has an EMPTY 'Npc Id'. " +
                             "A unique Npc Id is CRUCIAL for the AI to function correctly. Please assign a unique ID.", this);
        }
        else if (string.IsNullOrEmpty(npcId) && !gameObject.CompareTag("Player"))
        {
             Debug.Log($"CharacterData on GameObject '{gameObject.name}' has an empty 'Npc Id'. " +
                             "Consider assigning a unique ID if this character needs to be uniquely identified by other game systems.", this);
        }
    }

    public NpcIdentifier GetNpcIdentifier()
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to GetNpcIdentifier for '{characterName}' but npcId is empty!", this);
            return new NpcIdentifier { npc_id = $"UNSET_ID_{gameObject.GetInstanceID()}", name = this.characterName };
        }
        return new NpcIdentifier { npc_id = this.npcId, name = this.characterName };
    }

    public EntityContextInfo ToEntityContextInfo(bool isSignificantToDecisionMaker = false)
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to create EntityContextInfo for '{characterName}' but npcId is empty!", this);
            // Return a placeholder or handle error as appropriate for your game
            return new EntityContextInfo {
                npc_id = $"ERROR_UNSET_ID_{gameObject.GetInstanceID()}",
                name = this.characterName,
                x = transform.position.x,
                y = transform.position.y,
                entity_type = gameObject.CompareTag("Player") ? "player" : "npc",
                is_significant_to_npc = isSignificantToDecisionMaker
            };
        }
        return new EntityContextInfo
        {
            npc_id = this.npcId,
            name = this.characterName,
            x = transform.position.x,
            y = transform.position.y,
            entity_type = gameObject.CompareTag("Player") ? "player" : "npc",
            is_significant_to_npc = isSignificantToDecisionMaker
        };
    }

    public InteractingObjectInfo ToInteractingObjectInfo(
        string initialLlMPrompt = null,
        string dialogueMode = null,
        string currentEmotionalState = null,
        string llmModelOverride = null)
    {
        if (string.IsNullOrEmpty(npcId))
        {
            Debug.LogError($"Attempted to create InteractingObjectInfo for '{characterName}' but npcId is empty!", this);
        }
        return new InteractingObjectInfo
        {
            npc_id = this.npcId,
            name = this.characterName,
            initial_prompt_to_llm = initialLlMPrompt,
            dialogue_mode_tag = dialogueMode,
            emotional_state_input = currentEmotionalState,
            model_override = llmModelOverride
        };
    }

    public NPCMemoryFile CreateDefaultMemoryFile()
    {
        if (!isLLMNpc)
        {
            Debug.LogWarning($"CreateDefaultMemoryFile called on '{characterName}' which is not marked as an LLM NPC. Returning a minimal structure.", this);
             return new NPCMemoryFile { // Return a minimal valid structure if not LLM NPC
                npc_id = this.npcId,
                name = this.characterName,
                personality_description = "Not an LLM-driven character.",
                last_saved_at = DateTime.UtcNow.ToString("o"),
                current_emotional_state = new NpcEmotionalState { primary_emotion = "n_a" }
            };
        }
        
        // Helper to get current UTC time in ISO 8601 round-trip format
        string currentIsoTime = DateTime.UtcNow.ToString("o");

        return new NPCMemoryFile
        {
            npc_id = this.npcId,
            name = this.characterName,
            personality_description = this.defaultPersonality,
            last_saved_at = currentIsoTime,
            last_known_game_time = null, // GameTimeManager should provide this
            last_known_position = new NpcApiModels.Position { x = transform.position.x, y = transform.position.y },
            current_emotional_state = new NpcEmotionalState {
                primary_emotion = this.initialPrimaryEmotion,
                intensity = this.initialEmotionIntensity,
                mood_tags = new List<string>(),
                last_significant_change_at = currentIsoTime,
                reason_for_last_change = "Initial state upon memory creation."
            },
            // Initialize with empty lists or add default rules from Inspector if you set that up
            active_schedule_rules = new List<NPCScheduleRule>(),
            short_term_location_history = new List<VisitedLocationEntry>(),
            long_term_event_memories = new List<LongTermMemoryEntry>()
        };
    }
}