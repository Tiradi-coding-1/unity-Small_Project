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

MAX_NEARBY_ENTITIES_TO_LIST = 5 
MAX_LANDMARKS_TO_LIST = 5
MAX_LOCATION_HISTORY_TO_LIST = 3
MAX_LTM_TO_LIST_IN_PROMPT = 3

def _format_current_time_for_prompt(game_time: GameTime) -> str:
    day_info = f" on {game_time.day_of_week}" if game_time.day_of_week else ""
    time_str = "Unknown Time"
    if game_time.current_timestamp and isinstance(game_time.current_timestamp, datetime):
        try:
            if game_time.current_timestamp.tzinfo is None:
                aware_time = game_time.current_timestamp.replace(tzinfo=timezone.utc)
                time_str = aware_time.strftime('%H:%M %Z')
            else:
                time_str = game_time.current_timestamp.strftime('%H:%M %Z')
        except Exception as e:
            logger.warning(f"Error formatting game_time.current_timestamp: {e}")
            if isinstance(game_time.current_timestamp, datetime):
                 time_str = game_time.current_timestamp.isoformat()

    return f"{game_time.time_of_day}{day_info} (Timestamp: {time_str})."


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
    # Corrected: Use 'entity' instead of 'e' in the lambda for distance calculation
    # EntityContextInfo inherits x and y from Position, so direct access is fine
    sorted_entities = sorted(entities, key=lambda entity_item: npc_current_pos.distance_to(Position(x=entity_item.x, y=entity_item.y)))
    
    for i, entity in enumerate(sorted_entities[:MAX_NEARBY_ENTITIES_TO_LIST]):
        significance = " (important to you)" if entity.is_significant_to_npc else ""
        # Corrected: Use 'entity' here as well
        dist = npc_current_pos.distance_to(Position(x=entity.x, y=entity.y))
        lines.append(f"- '{entity.name or entity.npc_id}' ({entity.entity_type}{significance}) at ({entity.x:.0f},{entity.y:.0f}), dist {dist:.0f}.")

    if len(sorted_entities) > MAX_NEARBY_ENTITIES_TO_LIST:
        lines.append(f"- (...and {len(sorted_entities) - MAX_NEARBY_ENTITIES_TO_LIST} other(s) further away.)")
    return "\n".join(lines)

def _format_landmarks_for_prompt(landmarks: List[LandmarkContextInfo], npc_current_pos: Position, npc_info: NPCIdentifier) -> str:
    if not landmarks:
        return "No significant landmarks detected nearby."

    lines = ["\n--- Nearby Landmarks (Closest First) ---"]
    sorted_landmarks = sorted(landmarks, key=lambda l: npc_current_pos.distance_to(l.position))

    for i, landmark in enumerate(sorted_landmarks[:MAX_LANDMARKS_TO_LIST]):
        type_info = f" ({landmark.landmark_type_tag})" if landmark.landmark_type_tag else ""
        owner_info_parts = []
        if landmark.owner_id:
            if landmark.owner_id == npc_info.npc_id:
                owner_info_parts.append("your private area")
            else:
                owner_info_parts.append(f"private area of {landmark.owner_id}")

        if landmark.entrance_positions and len(landmark.entrance_positions) > 0:
            owner_info_parts.append(f"{len(landmark.entrance_positions)} entrance(s)")

        owner_info = f" ({', '.join(owner_info_parts)})" if owner_info_parts else ""

        dist = npc_current_pos.distance_to(landmark.position)
        lines.append(f"- '{landmark.landmark_name}'{type_info}{owner_info} at ({landmark.position.x:.0f},{landmark.position.y:.0f}), dist {dist:.0f}.")

        critical_status_notes = []
        if landmark.current_status_notes:
            npc_id_str = npc_info.npc_id if npc_info else ""
            for note in landmark.current_status_notes:
                note_lower = note.lower()
                is_occupied_by_self = f"occupancy_occupied_by_{npc_id_str.lower()}" == note_lower if npc_id_str else False
                is_occupied_by_other = "occupancy_occupied" in note_lower and not is_occupied_by_self
                
                if is_occupied_by_other and "occupancy_occupied_by_" in note_lower : 
                    critical_status_notes.append(f"STATUS: OCCUPIED BY OTHER")
                elif is_occupied_by_self:
                     critical_status_notes.append(f"STATUS: You are using it")
                elif "owner_presence_absent" in note_lower and landmark.owner_id and (not npc_id_str or landmark.owner_id.lower() != npc_id_str.lower()):
                    critical_status_notes.append(f"STATUS: OWNER ABSENT (Private to {landmark.owner_id})")
                elif "owner_presence_present" in note_lower and landmark.owner_id and (not npc_id_str or landmark.owner_id.lower() != npc_id_str.lower()):
                     critical_status_notes.append(f"STATUS: OWNER PRESENT (Private to {landmark.owner_id})")
            if critical_status_notes:
                 lines.append(f"  Notes: {'; '.join(critical_status_notes)}")
    if len(sorted_landmarks) > MAX_LANDMARKS_TO_LIST:
        lines.append(f"- (...and {len(sorted_landmarks) - MAX_LANDMARKS_TO_LIST} other landmarks further away.)")
    return "\n".join(lines)

def _format_location_history_for_prompt(history: List[VisitedLocationEntry], current_game_time_obj: GameTime) -> str:
    if not history:
        return "No recent location visits noted."

    current_game_datetime = current_game_time_obj.current_timestamp
    if not isinstance(current_game_datetime, datetime):
        try: 
            parsed_dt = datetime.fromisoformat(str(current_game_datetime).replace("Z", "+00:00"))
            if parsed_dt.tzinfo is None or parsed_dt.tzinfo.utcoffset(parsed_dt) is None:
                current_game_datetime = parsed_dt.replace(tzinfo=timezone.utc)
            else:
                current_game_datetime = parsed_dt
        except (TypeError, ValueError) as e:
            logger.error(f"Error parsing current_game_time.current_timestamp '{current_game_time_obj.current_timestamp}': {e}")
            return "Recent location history unavailable due to time parsing issue."


    lines = ["\n--- Recent Location Visits (Newest First) ---"]
    for entry in sorted(history, key=lambda e_item: e_item.timestamp_visited, reverse=True)[:MAX_LOCATION_HISTORY_TO_LIST]: # Changed loop var to e_item
        entry_datetime = entry.timestamp_visited
        if not isinstance(entry_datetime, datetime):
            try: 
                parsed_entry_dt = datetime.fromisoformat(str(entry_datetime).replace("Z", "+00:00"))
                if parsed_entry_dt.tzinfo is None or parsed_entry_dt.tzinfo.utcoffset(parsed_entry_dt) is None:
                    entry_datetime = parsed_entry_dt.replace(tzinfo=timezone.utc)
                else:
                    entry_datetime = parsed_entry_dt
            except (TypeError, ValueError) as e_parse:
                 logger.warning(f"Could not parse entry.timestamp_visited '{entry.timestamp_visited}' as datetime: {e_parse}")
                 lines.append(f"- Visited ({entry.x:.0f},{entry.y:.0f}) at an unparsed time.")
                 continue
        
        if entry_datetime.tzinfo is None and current_game_datetime.tzinfo is not None:
            entry_datetime = entry_datetime.replace(tzinfo=current_game_datetime.tzinfo) 
        elif entry_datetime.tzinfo is not None and current_game_datetime.tzinfo is None:
             logger.warning("Comparing aware location timestamp with naive current game time.")


        time_diff_seconds = (current_game_datetime - entry_datetime).total_seconds()
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
        f"Personality: \"{personality_description[:200]}{'...' if len(personality_description) > 200 else ''}\"",
        _format_current_time_for_prompt(game_time),
        _format_emotional_state_for_prompt(emotional_state),
        _format_schedule_rules_for_prompt(active_schedule_rules),
    ]

    prompt_components.extend([
        f"\nCurrent Pos: ({current_position.x:.1f},{current_position.y:.1f}). Bounds: X({scene_boundaries.min_x:.0f} to {scene_boundaries.max_x:.0f}), Y({scene_boundaries.min_y:.0f} to {scene_boundaries.max_y:.0f}). Buffer: {settings.SCENE_BOUNDARY_BUFFER:.1f}.",
        _format_location_history_for_prompt(short_term_location_history, game_time),
        f"REVISIT RULE: AVOID targets within {settings.VISIT_THRESHOLD_DISTANCE:.1f} units of recent visits (last ~{settings.REVISIT_INTERVAL_SECONDS // 60} min) UNLESS compelling reason (schedule, player request, strong emotion, or specific social goal).",
        _format_nearby_entities_for_prompt(other_entities_nearby, current_position),
        _format_landmarks_for_prompt(visible_landmarks, current_position, npc_info),
    ])

    prompt_components.append(_format_long_term_memories_for_prompt(relevant_long_term_memories))

    if recent_dialogue_summary: 
        prompt_components.append(f"\nContext/Dialogue/Prior Failures/Intent: \"{recent_dialogue_summary[:250]}{'...' if len(recent_dialogue_summary) > 250 else ''}\"")

    if explicit_player_movement_request:
        prompt_components.append(f"Player Request: Go near ({explicit_player_movement_request.x:.0f},{explicit_player_movement_request.y:.0f}). Consider if reasonable & respects rules.")

    prompt_components.append(f"""
\n--- Social Considerations & Opportunities ---
- You are a resident in a shared apartment. Being social is natural.
- If you are in a common area (e.g., 'Living Room', 'Kitchen', 'Dining_room') and you see a character you know (especially a friend) nearby who doesn't seem busy, consider initiating a short conversation as a valid action.
- If your current emotion is positive (e.g., 'happy', 'curious', 'content') or neutral and you are not in a private task, you might be more inclined to interact.
- If choosing to interact socially:
    - Your 'chosen_action' should clearly state this (e.g., "Go chat with [NPC Name]", "Approach [NPC Name] for a quick talk").
    - Your 'target_coordinates' should be a sensible position near that NPC, respecting personal space (e.g., about {settings.VISIT_THRESHOLD_DISTANCE + 0.5:.1f} to {settings.VISIT_THRESHOLD_DISTANCE + 1.5:.1f} units away from them, in an open area).
    - Set 'social_interaction_considered' to Yes in your priority_analysis.
""")

    prompt_components.append(f"""
\n--- Apartment Access Rules (CRITICAL - MUST BE FOLLOWED) ---
1.  Toilet ('bathroom' type): Enter only if its status is NOT 'OCCUPIED BY OTHER'. If occupied and you need to use it, your action should be to 'Wait near [Bathroom Landmark Name]' and target coordinates should be a valid waiting spot (e.g., an entrance of the bathroom if known, or a nearby clear spot).
2.  Private Room ('bedroom' type): If it is NOT your room (check owner_id against your ID: {npc_info.npc_id}), you may only enter if its status is 'OWNER PRESENT'. AVOID if 'OWNER ABSENT'. If it is your own room, you can enter freely.
3.  No physical doors to open. Movement between rooms is via clear passages.
4.  Avoid targeting coordinates directly ON TOP of static furniture/obstacles. Aim for clear floor space near your intended interaction point.
""")

    prompt_components.append(f"""
\n--- YOUR TASK: Decide Action & Target (Strict YAML Output) ---
Based on ALL the information above, your personality, current state, and rules:
1.  **Analyze Primary Drivers & Constraints:** What are the key factors influencing your decision? (Dialogue/Player Req/Prior Fail/Intent? Schedule? Emotion? Social Opportunity? Memory? Access Rules for Toilet/Private Room? Revisit Rule? General Exploration?)
2.  **Reasoning & Conflict Resolution:** Briefly explain your thought process. How do you prioritize conflicting goals or rules? Access rules and the Revisit rule are high priority. Social interaction is a valid choice if conditions are appropriate.
3.  **Chosen Action:** A concise, in-character phrase describing what you will do.
4.  **Target Coordinates:** Precise (x,y) coordinates for your destination. This MUST be within scene boundaries and respect ALL access and revisit rules. If waiting for an occupied landmark, target a sensible waiting spot near an entrance if possible.
5.  **Resulting Emotion Tag:** Your likely primary emotion after this decision (e.g., 'neutral', 'content', 'slightly_curious'). Use 'no_change' if your emotion is stable.

Respond ONLY with the YAML structure below. Do NOT include any text before the `priority_analysis:` line or after the final `resulting_emotion_tag:` line. Ensure correct YAML indentation.
```yaml
priority_analysis:
  dialogue_driven: Yes/No
  schedule_driven: Yes/No
  emotion_driven: Yes/No
  memory_driven: Yes/No
  social_interaction_considered: Yes/No
  access_rules_consideration: Yes/No
  exploration_driven: Yes/No
reasoning: |
  [Your concise reasoning here. Max 2-3 sentences.]
chosen_action: "[Your short, in-character action phrase here]"
target_coordinates: "x=<float_value>, y=<float_value>"
resulting_emotion_tag: "[Your new primary emotion tag or 'no_change']"
```""")
    final_prompt = "\n".join(prompt_components)
    logger.debug(
        f"Built NPC movement prompt for {npc_info.npc_id}. "
        f"Prompt length: {len(final_prompt)} chars."
    )
    return final_prompt