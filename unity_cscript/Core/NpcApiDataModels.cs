// NpcApiDataModels.cs
// 放置路徑建議: Assets/Scripts/Core/NpcApiDataModels.cs
// 務必確保您的 Unity 專案中已整合 Newtonsoft.Json (Json.NET)

// 如果您已安裝 Newtonsoft.Json, 請在 Player Settings -> Scripting Define Symbols 中添加 NEWTONSOFT_JSON_AVAILABLE
// 或者直接取消下面這行的註解 (如果只在此檔案中控制)
// #define NEWTONSOFT_JSON_AVAILABLE

#if NEWTONSOFT_JSON_AVAILABLE
using Newtonsoft.Json;
// using Newtonsoft.Json.Converters; // For StringEnumConverter if you use C# enums for roles
#endif

using System;
using System.Collections.Generic;

namespace NpcApiModels
{
    // --- 通用基礎模型 (Common Base Models) ---
    [System.Serializable]
    public class Position
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("x")]
#endif
        public float x;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("y")]
#endif
        public float y;
    }

    [System.Serializable]
    public class GameTime
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("current_timestamp")]
#endif
        public string current_timestamp; // ISO 8601 format string (e.g., "2025-05-19T13:30:00Z")

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("time_of_day")]
#endif
        public string time_of_day; // e.g., "morning", "afternoon"

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("day_of_week")]
#endif
        public string day_of_week; // Optional, e.g., "Monday"
    }

    [System.Serializable]
    public class NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("npc_id")]
#endif
        public string npc_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("name")]
#endif
        public string name; // Optional
    }

    // --- 對話相關模型 (Dialogue Related Models) ---
    [System.Serializable]
    public class Message
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("role")]
#endif
        public string role; // "system", "user", "assistant", "tool"

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("content")]
#endif
        public string content;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("name")]
#endif
        public string name; // Optional
    }
    
    [System.Serializable]
    public class OllamaChatOptions
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("temperature")]
#endif
        public float? temperature;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("num_ctx")]
#endif
        public int? num_ctx;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("top_k")]
#endif
        public int? top_k;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("top_p")]
#endif
        public float? top_p;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("stop")]
#endif
        public List<string> stop;
    }

    [System.Serializable]
    public class ChatRequestBase
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("model")]
#endif
        public string model;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("options")]
#endif
        public OllamaChatOptions options;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("stream")]
#endif
        public bool stream = false;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("conversation_id")]
#endif
        public string conversation_id;
    }

    [System.Serializable]
    public class StandardChatRequest : ChatRequestBase
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("messages")]
#endif
        public List<Message> messages = new List<Message>();
    }

    [System.Serializable]
    public class SimpleChatRequest : ChatRequestBase
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("system_prompt")]
#endif
        public string system_prompt;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("user_prompt")]
#endif
        public string user_prompt;
    }

    [System.Serializable]
    public class ChatResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("response")]
#endif
        public string response;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("model_used")]
#endif
        public string model_used;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("created_at")]
#endif
        public string created_at; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("api_processing_time_ms")]
#endif
        public float api_processing_time_ms;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("ollama_processing_duration_ns")]
#endif
        public long? ollama_processing_duration_ns;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("conversation_id")]
#endif
        public string conversation_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("done_reason")]
#endif
        public string done_reason;
    }

    [System.Serializable]
    public class InteractingObjectInfo : NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("model_override")]
#endif
        public string model_override;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("initial_prompt_to_llm")]
#endif
        public string initial_prompt_to_llm;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("emotional_state_input")]
#endif
        public string emotional_state_input;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("dialogue_mode_tag")]
#endif
        public string dialogue_mode_tag;
    }

    [System.Serializable]
    public class GameInteractionRequest
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("interacting_objects")]
#endif
        public List<InteractingObjectInfo> interacting_objects = new List<InteractingObjectInfo>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("scene_context_description")]
#endif
        public string scene_context_description;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("game_time_context")]
#endif
        public GameTime game_time_context;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("max_turns_per_object")]
#endif
        public int max_turns_per_object = 1;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("interaction_session_id_to_continue")]
#endif
        public string interaction_session_id_to_continue;
    }

    [System.Serializable]
    public class DialogueTurn : NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("message_original_language")]
#endif
        public string message_original_language;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("message_translated_zh_tw")]
#endif
        public string message_translated_zh_tw;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("model_used")]
#endif
        public string model_used;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("timestamp_api_generated")]
#endif
        public string timestamp_api_generated; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("llm_generated_emotional_tone")]
#endif
        public string llm_generated_emotional_tone;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("turn_processing_time_ms")]
#endif
        public float? turn_processing_time_ms;
    }

    [System.Serializable]
    public class GameInteractionResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("interaction_session_id")]
#endif
        public string interaction_session_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("dialogue_history")]
#endif
        public List<DialogueTurn> dialogue_history = new List<DialogueTurn>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("total_api_processing_time_ms")]
#endif
        public float total_api_processing_time_ms;
    }

    [System.Serializable]
    public class NpcEmotionalState
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("primary_emotion")]
#endif
        public string primary_emotion = "neutral";

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("intensity")]
#endif
        public float intensity = 0.5f;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("mood_tags")]
#endif
        public List<string> mood_tags = new List<string>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("last_significant_change_at")]
#endif
        public string last_significant_change_at; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("reason_for_last_change")]
#endif
        public string reason_for_last_change;
    }

    // ***新增的 NPC 記憶檔案核心結構模型***
    [System.Serializable]
    public class NPCScheduleRule
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("rule_id")]
#endif
        public string rule_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("time_period_tag")]
#endif
        public string time_period_tag;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("activity_description")]
#endif
        public string activity_description;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("target_location_name_or_area")]
#endif
        public string target_location_name_or_area;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("target_position")]
#endif
        public Position target_position; // Can be null

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("is_mandatory")]
#endif
        public bool is_mandatory;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("override_conditions")]
#endif
        public List<string> override_conditions = new List<string>();
    }

    [System.Serializable]
    public class VisitedLocationEntry : Position // Inherits x, y from Position
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("timestamp_visited")]
#endif
        public string timestamp_visited; // DateTime as ISO string
    }

    [System.Serializable]
    public class LongTermMemoryEntry
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("memory_id")]
#endif
        public string memory_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("timestamp_created")]
#endif
        public string timestamp_created; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("content_text")]
#endif
        public string content_text;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("memory_type_tag")]
#endif
        public string memory_type_tag;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("keywords")]
#endif
        public List<string> keywords = new List<string>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("related_npc_ids")]
#endif
        public List<string> related_npc_ids = new List<string>();
    }

    [System.Serializable]
    public class NPCMemoryFile : NpcIdentifier // Inherits npc_id, name
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("personality_description")]
#endif
        public string personality_description;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("last_saved_at")]
#endif
        public string last_saved_at; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("last_known_game_time")]
#endif
        public GameTime last_known_game_time; // Can be null

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("last_known_position")]
#endif
        public Position last_known_position; // Can be null

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("current_emotional_state")]
#endif
        public NpcEmotionalState current_emotional_state = new NpcEmotionalState();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("active_schedule_rules")]
#endif
        public List<NPCScheduleRule> active_schedule_rules = new List<NPCScheduleRule>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("short_term_location_history")]
#endif
        public List<VisitedLocationEntry> short_term_location_history = new List<VisitedLocationEntry>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("long_term_event_memories")]
#endif
        public List<LongTermMemoryEntry> long_term_event_memories = new List<LongTermMemoryEntry>();
    }
    // ***結束新增的 NPC 記憶檔案核心結構模型***


    [System.Serializable]
    public class EntityContextInfo : NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("x")]
#endif
        public float x;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("y")]
#endif
        public float y;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("entity_type")]
#endif
        public string entity_type;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("is_significant_to_npc")]
#endif
        public bool? is_significant_to_npc;
    }

    [System.Serializable]
    public class LandmarkContextInfo
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("landmark_name")]
#endif
        public string landmark_name;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("position")]
#endif
        public Position position;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("landmark_type_tag")]
#endif
        public string landmark_type_tag;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("owner_id")]
#endif
        public string owner_id;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("current_status_notes")]
#endif
        public List<string> current_status_notes = new List<string>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("entrance_positions")] // *** 新增欄位 ***
#endif
        public List<Position> entrancePositions = new List<Position>(); // *** 新增欄位 ***
    }

    [System.Serializable]
    public class SceneBoundaryInfo
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("min_x")]
#endif
        public float min_x;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("max_x")]
#endif
        public float max_x;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("min_y")]
#endif
        public float min_y;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("max_y")]
#endif
        public float max_y;
    }
    
    [System.Serializable]
    public class NpcMovementRequest : NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("model_override")]
#endif
        public string model_override;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("current_npc_position")]
#endif
        public Position current_npc_position;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("current_game_time")]
#endif
        public GameTime current_game_time;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("nearby_entities")]
#endif
        public List<EntityContextInfo> nearby_entities = new List<EntityContextInfo>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("visible_landmarks")]
#endif
        public List<LandmarkContextInfo> visible_landmarks = new List<LandmarkContextInfo>();

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("scene_boundaries")]
#endif
        public SceneBoundaryInfo scene_boundaries;
        
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("recent_dialogue_summary_for_movement")]
#endif
        public string recent_dialogue_summary_for_movement;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("explicit_player_movement_request")]
#endif
        public Position explicit_player_movement_request;
    }

    [System.Serializable]
    public class NpcMovementResponse : NpcIdentifier
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("llm_full_reasoning_text")]
#endif
        public string llm_full_reasoning_text;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("chosen_action_summary")]
#endif
        public string chosen_action_summary;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("target_destination")]
#endif
        public Position target_destination;
        
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("primary_decision_drivers")]
#endif
        public Dictionary<string, bool> primary_decision_drivers = new Dictionary<string, bool>();
        
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("updated_emotional_state_snapshot")]
#endif
        public NpcEmotionalState updated_emotional_state_snapshot;
        
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("api_processing_time_ms")]
#endif
        public float api_processing_time_ms;
    }

    [System.Serializable]
    public class HealthStatusResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("status")]
#endif
        public string status = "ok";

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("api_version")]
#endif
        public string api_version;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("service_name")]
#endif
        public string service_name;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("ollama_connection_status")]
#endif
        public string ollama_connection_status;
    }

    [System.Serializable]
    public class OllamaModelInfo
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("name")]
#endif
        public string name;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("modified_at")]
#endif
        public string modified_at; // DateTime as ISO string

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("size")]
#endif
        public long size;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("digest")]
#endif
        public string digest;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("details")]
#endif
        public Dictionary<string, object> details = new Dictionary<string, object>();
    }

    [System.Serializable]
    public class ListOllamaModelsResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("models")]
#endif
        public List<OllamaModelInfo> models;
    }

    [System.Serializable]
    public class ClearMemoryAdministrativelyResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("status")]
#endif
        public string status;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("message")]
#endif
        public string message;
    }

    [System.Serializable]
    public class ApiErrorDetail
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("error_code")]
#endif
        public string error_code;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("message")]
#endif
        public string message;

#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("context_info")]
#endif
        public Dictionary<string, object> context_info;
    }

    [System.Serializable]
    public class ApiErrorResponse
    {
#if NEWTONSOFT_JSON_AVAILABLE
        [JsonProperty("error")]
#endif
        public ApiErrorDetail error;
    }
}