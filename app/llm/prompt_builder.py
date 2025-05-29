# npc_api_suite/app/llm/prompt_builder.py

from typing import List, Optional, Dict, Union
from app.core.schemas import (
    NPCIdentifier, GameTime, NPCEmotionalState, NPCScheduleRule,
    EntityContextInfo, LandmarkContextInfo, SceneBoundaryInfo, Message, MessageRole,
    Position, VisitedLocationEntry, LongTermMemoryEntry 
)
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from datetime import datetime, timezone

logger = setup_logging(__name__)

DIALOGUE_MODE_INSTRUCTIONS: Dict[str, str] = {
    "formal_scholar": "Speak formally, educatedly, pedantically. Use precise language, occasionally reference obscure facts.",
    "curious_child": "You are curious, ask many questions. Speak with youthful innocence, wonder, simple vocabulary.",
    "grumpy_merchant": "You are a grumpy, impatient merchant. Be terse, focus on practical matters. Not overly friendly unless it serves you.",
    "street_urchin_talkative": "You are a cunning, talkative street urchin. Use slang, be cheeky, try to glean info or advantage.",
    "wise_elder": "Speak with wisdom, patience, calm demeanor. Offer advice or cryptic hints.",
    "default": "Respond naturally and appropriately based on your core personality and the immediate situation."
}

def build_dialogue_system_prompt(
    npc_info: NPCIdentifier,
    interacting_with_entities: List[NPCIdentifier], 
    scene_context_description: Optional[str] = None,
    dialogue_mode_tag: Optional[str] = "default",
    npc_emotional_state_input: Optional[str] = None, 
    additional_dialogue_goal: Optional[str] = None 
) -> str:
    prompt_parts = [
        f"You are game character '{npc_info.name or npc_info.npc_id}' (ID: {npc_info.npc_id})."
    ]
    if npc_emotional_state_input:
        prompt_parts.append(f"Currently {npc_emotional_state_input}.")

    mode_instruction = DIALOGUE_MODE_INSTRUCTIONS.get(dialogue_mode_tag or "default", DIALOGUE_MODE_INSTRUCTIONS["default"])
    prompt_parts.append(mode_instruction)

    if scene_context_description:
        prompt_parts.append(f"Scene: {scene_context_description}.")

    if interacting_with_entities:
        other_names = [entity.name or entity.npc_id for entity in interacting_with_entities]
        if other_names:
            prompt_parts.append(f"Interacting with: {', '.join(other_names)}.")
    else:
        prompt_parts.append("You might be speaking to an unseen player or thinking aloud.")

    if additional_dialogue_goal:
        prompt_parts.append(f"Your goal: {additional_dialogue_goal}")

    prompt_parts.append("Keep responses concise, in character. Avoid unprovided info unless minor, character-consistent detail.")
    final_prompt = " ".join(prompt_parts)
    logger.debug(f"Built dialogue system prompt for {npc_info.npc_id} (mode: {dialogue_mode_tag}): '{final_prompt[:150]}...'")
    return final_prompt

def build_translation_prompt_messages(
    text_to_translate: str,
    target_language: str = "Traditional Chinese",
    source_language: Optional[str] = "English"
) -> List[Message]:
    system_content_parts = [
        f"Translate the user's text from {source_language if source_language else 'original language'} to {target_language}.",
        "Preserve meaning, tone, style, nuance. Find equivalent expressions for colloquialisms.",
        "Output ONLY the translated text. NO commentary or explanations."
    ]
    system_content = " ".join(system_content_parts)
    
    return [
        Message(role=MessageRole.SYSTEM, content=system_content),
        Message(role=MessageRole.USER, content=text_to_translate)
    ]

MAX_NEARBY_ENTITIES_TO_LIST = 3
MAX_LANDMARKS_TO_LIST = 5
MAX_LOCATION_HISTORY_TO_LIST = 3
MAX_LTM_TO_LIST_IN_PROMPT = 3

def _format_current_time_for_prompt(game_time: GameTime) -> str:
    day_info = f" on {game_time.day_of_week}" if game_time.day_of_week else ""
    return f"{game_time.time_of_day}{day_info} (Timestamp: {game_time.current_timestamp.strftime('%H:%M %Z')})."

def _format_emotional_state_for_prompt(emotional_state: NPCEmotionalState) -> str:
    moods = f" Moods: {', '.join(emotional_state.mood_tags)}." if emotional_state.mood_tags else ""
    return f"Emotion: '{emotional_state.primary_emotion}' (Intensity: {emotional_state.intensity:.1f}/1.0).{moods}"

def _format_schedule_rules_for_prompt(schedule_rules: Optional[List[NPCScheduleRule]]) -> str:
    if not schedule_rules:
        return "No specific schedule obligations currently."
    
    lines = ["\n--- Current Schedule ---"]
    for rule in schedule_rules[:2]: 
        loc_info_parts = []
        if rule.target_location_name_or_area:
            loc_info_parts.append(f"'{rule.target_location_name_or_area}'")
        if rule.target_position:
            loc_info_parts.append(f"({rule.target_position.x:.0f},{rule.target_position.y:.0f})")
        
        loc_info_str = " at/near " + " / ".join(loc_info_parts) if loc_info_parts else ""
        mandatory_str = "MANDATORY:" if rule.is_mandatory else "Preferred:"
        lines.append(f"- {mandatory_str} '{rule.time_period_tag}': '{rule.activity_description}'{loc_info_str}.")
    if len(schedule_rules) > 2:
        lines.append("- (...and more rules.)")
    return "\n".join(lines)

def _format_nearby_entities_for_prompt(entities: List[EntityContextInfo], npc_current_pos: Position) -> str:
    if not entities:
        return "No other characters nearby."
    
    lines = ["\n--- Nearby Characters (Closest First) ---"]
    sorted_entities = sorted(entities, key=lambda e: e.distance_to(npc_current_pos))
    
    for i, entity in enumerate(sorted_entities[:MAX_NEARBY_ENTITIES_TO_LIST]):
        significance = " (important)" if entity.is_significant_to_npc else ""
        dist = entity.distance_to(npc_current_pos)
        lines.append(f"- '{entity.name or entity.npc_id}' ({entity.entity_type}{significance}) at ({entity.x:.0f},{entity.y:.0f}), dist {dist:.0f}.")
    
    if len(sorted_entities) > MAX_NEARBY_ENTITIES_TO_LIST:
        lines.append(f"- (...and {len(sorted_entities) - MAX_NEARBY_ENTITIES_TO_LIST} other(s) further away.)")
    return "\n".join(lines)

# MODIFIED: Added npc_info as the third parameter
def _format_landmarks_for_prompt(landmarks: List[LandmarkContextInfo], npc_current_pos: Position, npc_info: NPCIdentifier) -> str:
    if not landmarks:
        return "No significant landmarks detected nearby."
        
    lines = ["\n--- Nearby Landmarks (Closest First) ---"]
    sorted_landmarks = sorted(landmarks, key=lambda l: l.position.distance_to(npc_current_pos))
    
    for i, landmark in enumerate(sorted_landmarks[:MAX_LANDMARKS_TO_LIST]):
        type_info = f" ({landmark.landmark_type_tag})" if landmark.landmark_type_tag else ""
        owner_info = f" (Owner: {landmark.owner_id})" if landmark.owner_id else ""
        dist = landmark.position.distance_to(npc_current_pos)
        lines.append(f"- '{landmark.landmark_name}'{type_info}{owner_info} at ({landmark.position.x:.0f},{landmark.position.y:.0f}), dist {dist:.0f}.")
        
        critical_status_notes = []
        if landmark.current_status_notes:
            # Ensure npc_info.npc_id is available; if npc_info is None, this would error.
            # However, build_npc_movement_decision_prompt always passes a valid npc_info.
            npc_id_str = npc_info.npc_id if npc_info else "" # Defensive
            for note in landmark.current_status_notes:
                if "occupancy_occupied_by_" in note and npc_id_str not in note : 
                    critical_status_notes.append(f"STATUS: OCCUPIED BY OTHER")
                elif "occupancy_occupied" in note and npc_id_str in note: # Check if the current NPC is the occupier
                     critical_status_notes.append(f"STATUS: You are using it") 
                elif "owner_presence_absent" in note and landmark.owner_id and landmark.owner_id != npc_id_str:
                    critical_status_notes.append(f"STATUS: OWNER ABSENT (Private)")
                elif "owner_presence_present" in note and landmark.owner_id and landmark.owner_id != npc_id_str:
                     critical_status_notes.append(f"STATUS: OWNER PRESENT")
            if critical_status_notes:
                 lines.append(f"  Notes: {'; '.join(critical_status_notes)}")
    if len(sorted_landmarks) > MAX_LANDMARKS_TO_LIST:
        lines.append(f"- (...and {len(sorted_landmarks) - MAX_LANDMARKS_TO_LIST} other landmarks further away.)")
    return "\n".join(lines)

def _format_location_history_for_prompt(history: List[VisitedLocationEntry], current_game_time: GameTime) -> str:
    if not history: 
        return "No recent location visits noted."
    lines = ["\n--- Recent Location Visits (Newest First) ---"]
    for entry in sorted(history, key=lambda e: e.timestamp_visited, reverse=True)[:MAX_LOCATION_HISTORY_TO_LIST]:
        time_diff_seconds = (current_game_time.current_timestamp - entry.timestamp_visited).total_seconds()
        time_diff_seconds = max(0, time_diff_seconds) 

        if time_diff_seconds < 120: time_ago_str = f"~{int(time_diff_seconds / 60)} min ago"
        elif time_diff_seconds < 3600 * 2 : time_ago_str = f"~{int(time_diff_seconds / 3600)} hr ago"
        else: time_ago_str = "earlier"
        lines.append(f"- Visited ({entry.x:.0f},{entry.y:.0f}) {time_ago_str}.")
    if len(history) > MAX_LOCATION_HISTORY_TO_LIST:
        lines.append(f"- (...and more prior visits.)")
    return "\n".join(lines)

def _format_long_term_memories_for_prompt(memories: Optional[List[LongTermMemoryEntry]]) -> str:
    if not memories:
        return "No specific long-term memories seem immediately relevant."
    lines = ["\n--- Relevant Long-Term Memories ---"]
    
    for i, mem in enumerate(memories[:MAX_LTM_TO_LIST_IN_PROMPT]):
        lines.append(f"Memory ({mem.memory_type_tag}): \"{mem.content_text[:80]}{'...' if len(mem.content_text) > 80 else ''}\"")
    
    if len(memories) > MAX_LTM_TO_LIST_IN_PROMPT:
        lines.append(f"  (...and {len(memories) - MAX_LTM_TO_LIST_IN_PROMPT} more.)")
    return "\n".join(lines)

def build_npc_movement_decision_prompt(
    npc_info: NPCIdentifier,
    personality_description: str, 
    current_position: Position,
    game_time: GameTime,
    emotional_state: NPCEmotionalState, 
    active_schedule_rules: Optional[List[NPCScheduleRule]], 
    other_entities_nearby: List[EntityContextInfo],
    visible_landmarks: List[LandmarkContextInfo], 
    scene_boundaries: SceneBoundaryInfo,
    short_term_location_history: List[VisitedLocationEntry], 
    relevant_long_term_memories: Optional[List[LongTermMemoryEntry]], 
    recent_dialogue_summary: Optional[str], 
    explicit_player_movement_request: Optional[Position]
) -> str:
    prompt_components = [
        f"You are '{npc_info.name or npc_info.npc_id}' (ID: {npc_info.npc_id}) in a shared apartment.",
        f"Personality: \"{personality_description[:150]}{'...' if len(personality_description) > 150 else ''}\"",
        _format_current_time_for_prompt(game_time),
        _format_emotional_state_for_prompt(emotional_state),
        _format_schedule_rules_for_prompt(active_schedule_rules),
    ]

    prompt_components.extend([
        f"\nCurrent Pos: ({current_position.x:.1f},{current_position.y:.1f}). Bounds: X({scene_boundaries.min_x:.0f} to {scene_boundaries.max_x:.0f}), Y({scene_boundaries.min_y:.0f} to {scene_boundaries.max_y:.0f}). Buffer: {settings.SCENE_BOUNDARY_BUFFER:.1f}.",
        _format_location_history_for_prompt(short_term_location_history, game_time),
        f"REVISIT RULE: AVOID targets within {settings.VISIT_THRESHOLD_DISTANCE:.1f} units of recent visits (last ~{settings.REVISIT_INTERVAL_SECONDS // 60} min) UNLESS compelling reason (schedule, player request, strong emotion).",
        _format_nearby_entities_for_prompt(other_entities_nearby, current_position),
        _format_landmarks_for_prompt(visible_landmarks, current_position, npc_info), # MODIFIED: npc_info is correctly passed here
    ])

    prompt_components.append(_format_long_term_memories_for_prompt(relevant_long_term_memories))
    
    if recent_dialogue_summary:
        prompt_components.append(f"\nContext/Dialogue/Prior Failures: \"{recent_dialogue_summary[:200]}{'...' if len(recent_dialogue_summary) > 200 else ''}\"")
    
    if explicit_player_movement_request:
        prompt_components.append(f"Player Request: Go near ({explicit_player_movement_request.x:.0f},{explicit_player_movement_request.y:.0f}). Consider if reasonable & respects rules.")

    prompt_components.append(f"""
\n--- Apartment Access Rules (CRITICAL) ---
1.  Toilet ('bathroom' type): Enter only if NOT 'STATUS: OCCUPIED BY OTHER'.
2.  Private Room ('bedroom' type): If NOT your room (check owner_id), enter only if 'STATUS: OWNER PRESENT'. AVOID if 'STATUS: OWNER ABSENT'.
3.  No physical doors to open. Movement between rooms is via clear passages.
4.  Avoid targeting coordinates ON TOP of static furniture/obstacles. Aim for clear floor space.
""")

    prompt_components.append(f"""
\n--- YOUR TASK: Decide Action & Target (YAML Output) ---
1.  **Primary Drivers & Constraints:** Dialogue/Player Req/Prior Fail? Schedule? Emotion? Memory? Access Rules (Toilet/Private Room)? Revisit Rule? Exploration?
2.  **Prioritize & Resolve:** Explain conflict resolution. Access & Revisit rules are high priority.
3.  **Action:** Your chosen action.
4.  **Target Coords:** Precise (x,y) within bounds, respecting ALL rules.
5.  **Emotion Change:** Likely new emotion tag.

Respond in YAML format:
```yaml
priority_analysis:
  dialogue_driven: Yes/No
  schedule_driven: Yes/No
  emotion_driven: Yes/No
  memory_driven: Yes/No
  access_rules_consideration: Yes/No # Crucial: did access rules shape your choice?
  exploration_driven: Yes/No
reasoning: |
  [Concise reasoning (1-3 sentences). State if rules forced plan change.]
chosen_action: "[Short, in-character action phrase]"
target_coordinates: "x=<float_value>, y=<float_value>"
resulting_emotion_tag: "[New primary emotion. 'no_change' if stable]"
```""")
    final_prompt = "\n".join(prompt_components)
    logger.debug(
        f"Built NPC movement prompt for {npc_info.npc_id}. "
        f"Prompt length: {len(final_prompt)} chars."
    )
    return final_prompt