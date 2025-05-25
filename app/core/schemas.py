# npc_api_suite/app/core/schemas.py

from pydantic import (
    BaseModel,
    Field,
    field_validator,
    model_validator, # Pydantic V2
    EmailStr, # Example of a specific string type
    HttpUrl,  # Example of a specific URL type
    conlist,
    RootModel # For models that are just a list or dict at the root
)
from typing import List, Dict, Any, Optional, Union
from enum import Enum
from datetime import datetime, timezone, timedelta # Ensure aware datetime
import uuid
import math # For Position.distance_to

# --- Helper function for default timestamps (UTC aware) ---
def default_aware_utcnow() -> datetime:
    return datetime.now(timezone.utc)

# --- 通用基礎模型 (Common Base Models) ---
class Position(BaseModel):
    x: float = Field(..., description="X-coordinate in the game world.")
    y: float = Field(..., description="Y-coordinate in the game world.")

    def distance_to(self, other: 'Position') -> float:
        """Calculates the Euclidean distance to another Position object."""
        return math.hypot(self.x - other.x, self.y - other.y)

class GameTime(BaseModel):
    current_timestamp: datetime = Field(
        default_factory=default_aware_utcnow,
        description="Current Coordinated Universal Time (UTC) timestamp in ISO 8601 format."
    )
    time_of_day: str = Field(
        ...,
        examples=["morning", "midday", "afternoon", "evening", "night", "late_night"],
        description="Categorical representation of the current time of day."
    )
    day_of_week: Optional[str] = Field(
        None,
        examples=["Monday", "Tuesday", "Sunday"],
        description="Current day of the week, if applicable to game logic."
    )

class NPCIdentifier(BaseModel):
    npc_id: str = Field(..., min_length=1, description="Unique identifier for the NPC.")
    name: Optional[str] = Field(None, min_length=1, description="Display name of the NPC.")

# --- 對話相關模型 (Dialogue Related Models) ---
class MessageRole(str, Enum):
    SYSTEM = "system"
    USER = "user"
    ASSISTANT = "assistant"
    TOOL = "tool" 

class Message(BaseModel):
    role: MessageRole
    content: str
    name: Optional[str] = Field(
        None,
        min_length=1,
        description="Optional name for multi-agent contexts or to identify the source of a tool message."
    )

class OllamaChatOptions(BaseModel):
    temperature: Optional[float] = Field(None, ge=0.0, le=2.0, description="Controls randomness. Lower is more deterministic.")
    num_ctx: Optional[int] = Field(None, gt=0, description="Context window size. Overrides model's default if set.")
    top_k: Optional[int] = Field(None, gt=0, description="Reduces the probability of generating nonsense. Higher value = more diversity.")
    top_p: Optional[float] = Field(None, ge=0.0, le=1.0, description="Works with top-k. Higher value = more diversity.")
    stop: Optional[List[str]] = Field(None, description="Sequences where the LLM will stop generating. e.g. ['\n', 'User:']")
    seed: Optional[int] = Field(None, description="Set a seed for reproducible outputs.")

class ChatRequestBase(BaseModel):
    model: Optional[str] = Field(None, description="Ollama model to use. Defaults to system setting if None.")
    options: Optional[OllamaChatOptions] = Field(None, description="Additional Ollama options for this request.")
    stream: bool = Field(False, description="Whether to stream the response chunk by chunk.")
    conversation_id: Optional[str] = Field(
        default_factory=lambda: str(uuid.uuid4()),
        description="ID to track a conversation session. Auto-generates if not provided."
    )

class StandardChatRequest(ChatRequestBase):
    messages: conlist(Message, min_length=1) = Field(..., description="Ordered list of messages in the conversation.")

class SimpleChatRequest(ChatRequestBase):
    system_prompt: Optional[str] = Field(None, description="System instructions for the LLM for this specific request.")
    user_prompt: str = Field(..., min_length=1, description="The user's current message.")

class ChatResponse(BaseModel):
    response: str = Field(..., description="The LLM's generated textual response.")
    model_used: str = Field(..., description="The Ollama model that generated this response.")
    created_at: datetime = Field(..., description="Timestamp (UTC) when Ollama created the response message.") 
    api_processing_time_ms: float = Field(..., description="Server-side API processing time for this request in milliseconds.")
    ollama_processing_duration_ns: Optional[int] = Field(None, description="Duration of Ollama's processing in nanoseconds, if available from response.")
    conversation_id: Optional[str] = Field(None, description="ID of the conversation session.")
    done_reason: Optional[str] = Field(None, description="Reason the generation finished (e.g., 'stop', 'length').")

# --- 遊戲物件互動模型 (Game Object Interaction Models) ---
class InteractingObjectInfo(NPCIdentifier): 
    model_override: Optional[str] = Field(None, description="Specific Ollama model for this object in this interaction, overrides default.")
    initial_prompt_to_llm: Optional[str] = Field(None, description="A specific prompt to the LLM to guide its first utterance for this object.")
    emotional_state_input: Optional[str] = Field(None, examples=["happy", "cautious", "annoyed"], description="Input emotional state to influence dialogue style.")
    dialogue_mode_tag: Optional[str] = Field(None, examples=["formal_scholar", "street_urchin_talkative"], description="Tag for a predefined dialogue mode or personality overlay.")

class GameInteractionRequest(BaseModel):
    interacting_objects: conlist(InteractingObjectInfo, min_length=1)
    scene_context_description: Optional[str] = Field(None, description="Overall context of the interaction scene (e.g., 'In the apartment living room').") # Modified example
    game_time_context: Optional[GameTime] = Field(None, description="Current game time context for this interaction.")
    max_turns_per_object: int = Field(1, gt=0, le=5, description="Maximum number of speaking turns for each object in this specific interaction request.")
    interaction_id_to_continue: Optional[str] = Field(None, description="ID of a previous interaction session to continue from.") 

class DialogueTurn(NPCIdentifier): 
    message_original_language: str = Field(..., description="The original dialogue text generated by the LLM.")
    message_translated_zh_tw: Optional[str] = Field(None, description="Traditional Chinese translation for display in Unity.")
    model_used: str
    timestamp_api_generated: datetime = Field(default_factory=default_aware_utcnow, description="Timestamp when this turn was generated by the API.")
    llm_generated_emotional_tone: Optional[str] = Field(None, description="Emotional tone inferred or explicitly stated by the LLM.")
    turn_processing_time_ms: Optional[float] = Field(None, description="Processing time for this individual turn.")

class GameInteractionResponse(BaseModel):
    interaction_session_id: str = Field(default_factory=lambda: f"gi_{uuid.uuid4().hex[:10]}")
    dialogue_history: List[DialogueTurn]
    total_api_processing_time_ms: float

# --- NPC 狀態與記憶核心模型 (NPC State & Memory Core Models) ---
class NPCEmotionalState(BaseModel):
    primary_emotion: str = Field("neutral", examples=["neutral", "happy", "sad", "angry", "fearful", "curious", "annoyed", "content"]) 
    intensity: float = Field(0.5, ge=0.0, le=1.0, description="Intensity of the primary emotion (0.0 to 1.0).")
    mood_tags: List[str] = Field(default_factory=list, examples=[["calm", "slightly_distracted"], ["energetic"]], description="Additional mood tags or nuances.")
    last_significant_change_at: datetime = Field(default_factory=default_aware_utcnow)
    reason_for_last_change: Optional[str] = Field(None, max_length=200, description="Brief summary of event causing last significant emotion change.")

class NPCScheduleRule(BaseModel):
    rule_id: str = Field(default_factory=lambda: f"sched_{uuid.uuid4().hex[:6]}")
    time_period_tag: str = Field(..., examples=["morning_kitchen_routine", "afternoon_living_room_relax", "evening_bedroom_personal_time", "late_night_quiet_activity", "anytime_bathroom_break"], description="Tag representing the applicable time period or condition within apartment life.") 
    activity_description: str = Field(..., max_length=256, description="Description of the NPC's scheduled activity. e.g., 'Preparing breakfast', 'Reading on the sofa', 'Using the computer in my room'") 
    target_location_name_or_area: Optional[str] = Field(None, description="Name of target landmark or general area for this activity. e.g., 'Kitchen Stove', 'Living Room Sofa', 'Bedroom_A_Desk', 'Bathroom'") 
    target_position: Optional[Position] = Field(None, description="Specific coordinates if the activity has a precise location within the room/area.")
    is_mandatory: bool = Field(True)
    override_conditions: List[str] = Field(default_factory=list, examples=[["urgent_visitor_at_main_entrance", "apartment_emergency_alarm"]], description="Conditions under which this rule can be overridden.")

class VisitedLocationEntry(Position): 
    timestamp_visited: datetime = Field(..., description="Timestamp (UTC) when the location was marked as visited/arrived at.")

class LongTermMemoryEntry(BaseModel):
    memory_id: str = Field(default_factory=lambda: f"ltm_{uuid.uuid4().hex[:8]}")
    timestamp_created: datetime = Field(default_factory=default_aware_utcnow)
    content_text: str = Field(..., max_length=512, description="The textual content of the memory.")
    memory_type_tag: str = Field("generic_observation", examples=["dialogue_summary_with_roommate", "learned_about_neighbor", "shared_apartment_event"], description="Categorization tag for the memory.") 
    keywords: List[str] = Field(default_factory=list, description="Keywords for easier retrieval.")
    related_npc_ids: List[str] = Field(default_factory=list)

class NPCMemoryFile(NPCIdentifier): 
    personality_description: str = Field(
        "A typical apartment resident, trying to coexist with roommates. Generally follows routines and values personal space but can be social in common areas.", 
        max_length=1024, 
        description="Core personality traits and behavioral tendencies for the LLM."
    )
    last_saved_at: datetime = Field(default_factory=default_aware_utcnow)
    last_known_game_time: Optional[GameTime] = None
    last_known_position: Optional[Position] = None
    current_emotional_state: NPCEmotionalState = Field(default_factory=NPCEmotionalState)
    active_schedule_rules: List[NPCScheduleRule] = Field(default_factory=list) 
    short_term_location_history: List[VisitedLocationEntry] = Field(default_factory=list) 
    long_term_event_memories: List[LongTermMemoryEntry] = Field(default_factory=list)

    @model_validator(mode='before')
    @classmethod
    def check_npc_id_present(cls, data: Any) -> Any:
        if isinstance(data, dict) and 'npc_id' not in data:
            raise ValueError('npc_id is essential for NPCMemoryFile')
        return data

# --- NPC 移動決策模型 (NPC Movement Decision Models) ---
class EntityContextInfo(NPCIdentifier, Position): 
    entity_type: str = Field(..., examples=["player", "npc_roommate", "npc_visitor"]) 
    is_significant_to_npc: Optional[bool] = Field(None, description="Does the decision-making NPC have a notable relationship or history with this entity?")

class LandmarkContextInfo(BaseModel):
    landmark_name: str
    position: Position
    landmark_type_tag: Optional[str] = Field(None, examples=["bedroom", "kitchen", "living_room", "bathroom", "dining_room", "room_entrance", "furniture_sofa", "furniture_table", "appliance_stove", "appliance_refrigerator", "obstacle_plant"]) 
    owner_id: Optional[str] = Field(None, description="NPC ID of the owner (e.g., for a private bedroom).") 
    current_status_notes: List[str] = Field(default_factory=list, examples=[["occupancy_occupied_by_NPC_A"], ["owner_presence_absent"], ["state_tv_on"], ["state_food_cooking_on_stove"]], description="Dynamic notes about the landmark, e.g., if a bathroom is occupied, if a room owner is present.") 

class SceneBoundaryInfo(BaseModel):
    min_x: float
    max_x: float
    min_y: float
    max_y: float

    @model_validator(mode='after') 
    def check_coordinate_logic(self) -> 'SceneBoundaryInfo':
        if self.max_x < self.min_x:
            raise ValueError('SceneBoundaryInfo: max_x must be greater than or equal to min_x')
        if self.max_y < self.min_y:
            raise ValueError('SceneBoundaryInfo: max_y must be greater than or equal to min_y')
        return self

class NPCMovementRequest(NPCIdentifier): 
    model_override: Optional[str] = None
    current_npc_position: Position
    current_game_time: GameTime
    nearby_entities: List[EntityContextInfo] = Field(default_factory=list)
    visible_landmarks: List[LandmarkContextInfo] = Field(default_factory=list) 
    scene_boundaries: SceneBoundaryInfo
    recent_dialogue_summary_for_movement: Optional[str] = Field(None, max_length=1024, description="Key takeaways from recent dialogue relevant to movement.")
    explicit_player_movement_request: Optional[Position] = Field(None, description="If player directly asked NPC to go to a specific coordinate or named landmark.")

class NPCMovementResponse(NPCIdentifier): 
    llm_full_reasoning_text: str = Field(..., description="The complete reasoning text from the LLM for transparency and debugging.")
    chosen_action_summary: str = Field(..., description="A concise, in-character summary of the NPC's chosen action.")
    target_destination: Position
    primary_decision_drivers: Dict[str, bool] = Field(
        default_factory=dict,
        examples=[{"dialogue_driven": False, "schedule_driven": True, "emotion_driven": False, "memory_driven": False, "access_rules_consideration": True, "exploration_driven": False}], 
        description="Boolean flags indicating primary drivers parsed from LLM response."
    )
    updated_emotional_state_snapshot: Optional[NPCEmotionalState] = Field(None, description="NPC's emotional state after this decision, if changed.")
    api_processing_time_ms: float

# --- Admin & Health Check Models ---
class HealthStatusResponse(BaseModel):
    status: str = "ok"
    api_version: str
    service_name: str
    ollama_connection_status: str

class OllamaModelInfo(BaseModel): 
    name: str
    modified_at: datetime 
    size: int 
    digest: str
    details: Dict[str, Any]

class ListOllamaModelsResponse(BaseModel):
    models: List[OllamaModelInfo]

class ClearMemoryAdministrativelyResponse(BaseModel): 
    status: str 
    message: str

class APIErrorDetail(BaseModel): 
    error_code: Optional[str] = Field(None, description="Optional internal error code or category.")
    message: str
    context_info: Optional[Dict[str, Any]] = Field(None, description="Additional context about the error.")

class APIErrorResponse(BaseModel): 
    error: APIErrorDetail