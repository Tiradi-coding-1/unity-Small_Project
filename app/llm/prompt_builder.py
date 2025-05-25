# npc_api_suite/app/llm/prompt_builder.py

from typing import List, Optional, Dict, Union
from app.core.schemas import (
    NPCIdentifier, GameTime, NPCEmotionalState, NPCScheduleRule,
    EntityContextInfo, LandmarkContextInfo, SceneBoundaryInfo, Message, MessageRole,
    Position, VisitedLocationEntry, LongTermMemoryEntry # 確保導入所有需要的 schemas
)
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging, main_app_logger # 使用 main_app_logger 或 setup_logging(__name__)
from datetime import datetime # 用於計算時間差

logger = setup_logging(__name__) # 每個模組使用自己的 logger 實例

# --- 對話相關提示 (Dialogue Prompts) ---

# 預定義的對話模式指令，可以根據 dialogue_mode_tag 選擇
DIALOGUE_MODE_INSTRUCTIONS: Dict[str, str] = {
    "formal_scholar": "You should speak in a formal, educated, and slightly pedantic manner. You enjoy using precise language and might occasionally reference obscure facts.",
    "curious_child": "You are very curious and tend to ask many questions. Speak with youthful innocence, wonder, and simple vocabulary.",
    "grumpy_merchant": "You are a grumpy, impatient merchant. Be terse, focus on practical matters (like trade or value), and don't be overly friendly unless it serves a clear purpose for you.",
    "street_urchin_talkative": "You are a cunning but talkative street urchin. Use slang, be a bit cheeky, and perhaps try to glean information or a small advantage.",
    "wise_elder": "Speak with wisdom, patience, and a calm demeanor. You might offer advice or cryptic hints.",
    "default": "Respond naturally and appropriately based on your core personality (defined elsewhere) and the immediate situation."
}

def build_dialogue_system_prompt(
    npc_info: NPCIdentifier,
    interacting_with_entities: List[NPCIdentifier], # 參與互動的其他實體
    scene_context_description: Optional[str] = None,
    dialogue_mode_tag: Optional[str] = "default",
    npc_emotional_state_input: Optional[str] = None, # 例如 "feeling happy and energetic"
    additional_dialogue_goal: Optional[str] = None # 例如 "Try to find out where the player is going."
) -> str:
    """
    Builds a system prompt for an NPC engaging in dialogue, incorporating various contexts.
    """
    prompt_parts = [
        f"You are the game character known as '{npc_info.name or npc_info.npc_id}' (NPC ID: {npc_info.npc_id})."
    ]

    if npc_emotional_state_input:
        prompt_parts.append(f"You are currently {npc_emotional_state_input}.")

    # 選擇對話模式指令
    mode_instruction = DIALOGUE_MODE_INSTRUCTIONS.get(dialogue_mode_tag or "default", DIALOGUE_MODE_INSTRUCTIONS["default"])
    prompt_parts.append(mode_instruction)

    if scene_context_description:
        prompt_parts.append(f"The current scene is: {scene_context_description}.")

    if interacting_with_entities:
        other_names = [entity.name or entity.npc_id for entity in interacting_with_entities]
        if other_names:
            prompt_parts.append(f"You are interacting with: {', '.join(other_names)}.")
    else:
        # 如果列表為空，可能是與玩家的隱式互動或自言自語
        prompt_parts.append("You might be speaking to an unseen player or thinking aloud.")

    if additional_dialogue_goal:
        prompt_parts.append(f"Your specific goal for this conversation is: {additional_dialogue_goal}")

    prompt_parts.append("Keep your responses concise and in character. Avoid making up information not provided in the context unless it's a very minor, character-consistent detail.")

    final_prompt = " ".join(prompt_parts)
    logger.debug(f"Built dialogue system prompt for {npc_info.npc_id} (mode: {dialogue_mode_tag}): '{final_prompt[:300]}...'")
    return final_prompt


def build_translation_prompt_messages(
    text_to_translate: str,
    target_language: str = "Traditional Chinese", # 繁體中文
    source_language: Optional[str] = "English"
) -> List[Message]:
    """
    Builds a list of messages for a translation task, aiming for accuracy and naturalness.
    """
    system_content_parts = [
        f"You are an expert linguist and translator. Your task is to accurately translate the user's text from {source_language if source_language else 'the original language'} to {target_language}.",
        "Preserve the original meaning, tone, style, and nuance as faithfully as possible.",
        "If the original text contains colloquialisms or culturally specific phrases, try to find equivalent expressions in the target language.",
        "Do NOT add any commentary, explanations, or text beyond the translation itself.",
        "Output ONLY the translated text."
    ]
    system_content = " ".join(system_content_parts)
    
    return [
        Message(role=MessageRole.SYSTEM, content=system_content),
        Message(role=MessageRole.USER, content=text_to_translate)
    ]

# --- NPC 移動決策提示 (NPC Movement Decision Prompts) ---

def _format_current_time_for_prompt(game_time: GameTime) -> str:
    day_info = f" on {game_time.day_of_week}" if game_time.day_of_week else ""
    # 使用 settings 中的時區或 UTC
    return f"{game_time.time_of_day}{day_info} (Current time: {game_time.current_timestamp.strftime('%Y-%m-%d %H:%M %Z')})."

def _format_emotional_state_for_prompt(emotional_state: NPCEmotionalState) -> str:
    moods = f" Additional moods: {', '.join(emotional_state.mood_tags)}." if emotional_state.mood_tags else ""
    return f"Your current emotional state is '{emotional_state.primary_emotion}' with an intensity of {emotional_state.intensity:.1f}/1.0.{moods}"

def _format_schedule_rules_for_prompt(schedule_rules: Optional[List[NPCScheduleRule]]) -> str:
    if not schedule_rules:
        return "You have no specific schedule obligations or routines defined at this moment. You are free to act based on other factors."
    
    lines = ["\n--- Your Current Schedule & Obligations ---"]
    for rule in schedule_rules:
        loc_info_parts = []
        if rule.target_location_name_or_area:
            loc_info_parts.append(f"'{rule.target_location_name_or_area}'")
        if rule.target_position:
            loc_info_parts.append(f"around coordinates ({rule.target_position.x:.1f}, {rule.target_position.y:.1f})")
        
        loc_info_str = " at/near " + " or ".join(loc_info_parts) if loc_info_parts else " in a suitable location"

        mandatory_str = "MANDATORY:" if rule.is_mandatory else "Preferred:"
        lines.append(f"- {mandatory_str} For time period/condition '{rule.time_period_tag}', your activity is '{rule.activity_description}'{loc_info_str}.")
        if rule.override_conditions:
            lines.append(f"  (Can be overridden if: {', '.join(rule.override_conditions)})")
        # if rule.contingency_plan:
        #     lines.append(f"  (Contingency: {rule.contingency_plan})")
    return "\n".join(lines)

def _format_nearby_entities_for_prompt(entities: List[EntityContextInfo], npc_current_pos: Position) -> str:
    if not entities:
        return "No other characters are currently nearby or detected."
    lines = ["\n--- Other Characters Nearby ---"]
    for entity in sorted(entities, key=lambda e: e.distance_to(npc_current_pos)): # Sort by distance
        significance = " (significant to you)" if entity.is_significant_to_npc else ""
        dist = entity.distance_to(npc_current_pos)
        lines.append(f"- '{entity.name or entity.npc_id}' ({entity.entity_type}{significance}) is at ({entity.x:.1f}, {entity.y:.1f}), approx. {dist:.1f} units away.")
    return "\n".join(lines)

def _format_landmarks_for_prompt(landmarks: List[LandmarkContextInfo], npc_current_pos: Position) -> str:
    if not landmarks:
        return "No significant landmarks are visible or known in this immediate area."
    lines = ["\n--- Significant Landmarks in Scene ---"]
    for landmark in sorted(landmarks, key=lambda l: l.position.distance_to(npc_current_pos)): # Sort by distance
        type_info = f" (type: {landmark.landmark_type_tag})" if landmark.landmark_type_tag else ""
        owner_info = f" (owner: '{landmark.owner_id}')" if landmark.owner_id else ""
        dist = landmark.position.distance_to(npc_current_pos)
        lines.append(f"- '{landmark.landmark_name}'{type_info}{owner_info} is at ({landmark.position.x:.1f}, {landmark.position.y:.1f}), approx. {dist:.1f} units away.")
        if landmark.current_status_notes:
            for note in landmark.current_status_notes:
                lines.append(f"  Note: {note}")
    return "\n".join(lines)

def _format_location_history_for_prompt(history: List[VisitedLocationEntry], current_game_time: GameTime) -> str:
    if not history: # history is expected to be short-term, recent visits
        return "You haven't noted any specific location visits very recently."
    lines = ["\n--- Your Short-Term Location History (Newest First) ---"]
    for entry in sorted(history, key=lambda e: e.timestamp_visited, reverse=True):
        time_diff_seconds = (current_game_time.current_timestamp - entry.timestamp_visited).total_seconds()
        if time_diff_seconds < 60: time_ago_str = f"{int(time_diff_seconds)} seconds ago"
        elif time_diff_seconds < 3600: time_ago_str = f"{int(time_diff_seconds / 60)} minutes ago"
        else: time_ago_str = f"{int(time_diff_seconds / 3600)} hours ago"
        lines.append(f"- Visited ({entry.x:.1f}, {entry.y:.1f}) {time_ago_str}.")
    return "\n".join(lines)

def _format_long_term_memories_for_prompt(memories: Optional[List[LongTermMemoryEntry]]) -> str:
    if not memories:
        return "You have no specific long-term memories that seem immediately relevant."
    lines = ["\n--- Relevant Long-Term Memories Recalled ---"]
    for i, mem in enumerate(memories):
        # Format timestamp_created to be more readable if needed
        age_delta = datetime.now(timezone.utc) - mem.timestamp_created
        if age_delta.days > 1: memory_age = f"(recorded {age_delta.days} days ago)"
        elif age_delta.total_seconds() > 3600: memory_age = f"(recorded {int(age_delta.total_seconds() / 3600)} hours ago)"
        else: memory_age = "(recorded recently)"
        lines.append(f"Memory {i+1} ({mem.memory_type_tag} {memory_age}): \"{mem.content_text}\"")
        if i >= settings.MAX_LONG_TERM_MEMORY_ENTRIES // 10 : # Limit displayed memories to avoid prompt bloat, e.g., max 5 if 50 total
            lines.append("  (and possibly more...)")
            break
    return "\n".join(lines)


def build_npc_movement_decision_prompt(
    npc_info: NPCIdentifier,
    personality_description: str, # From NPCMemoryFile
    current_position: Position,
    game_time: GameTime,
    emotional_state: NPCEmotionalState, # From NPCMemoryFile
    active_schedule_rules: Optional[List[NPCScheduleRule]], # From NPCMemoryFile
    # Scene context from request
    other_entities_nearby: List[EntityContextInfo],
    visible_landmarks: List[LandmarkContextInfo],
    scene_boundaries: SceneBoundaryInfo,
    # Processed memory context from request/memory service
    short_term_location_history: List[VisitedLocationEntry], # From NPCMemoryFile, passed through service
    relevant_long_term_memories: Optional[List[LongTermMemoryEntry]], # From NPCMemoryFile, filtered by service
    # Dialogue/Interaction context from request
    recent_dialogue_summary: Optional[str],
    explicit_player_movement_request: Optional[Position]
) -> str:
    """
    Builds a comprehensive system prompt for an NPC to make a movement decision,
    instructing the LLM to provide a structured YAML-like response.
    """
    
    # --- Section 1: Core Identity and State ---
    prompt_components = [
        f"You are the game character '{npc_info.name or npc_info.npc_id}' (ID: {npc_info.npc_id}).",
        f"Your core personality is: \"{personality_description}\"",
        _format_current_time_for_prompt(game_time),
        _format_emotional_state_for_prompt(emotional_state),
        _format_schedule_rules_for_prompt(active_schedule_rules),
    ]

    # --- Section 2: Current Environment & Short-Term Context ---
    prompt_components.extend([
        f"\nYour current location is ({current_position.x:.1f}, {current_position.y:.1f}).",
        f"The visible scene boundaries are: X-axis from {scene_boundaries.min_x:.1f} to {scene_boundaries.max_x:.1f}, Y-axis from {scene_boundaries.min_y:.1f} to {scene_boundaries.max_y:.1f}. You MUST stay within these boundaries, ideally with a small buffer of {settings.SCENE_BOUNDARY_BUFFER:.1f} units from any edge.",
        _format_location_history_for_prompt(short_term_location_history, game_time),
        f"IMPORTANT RULE: Unless a very strong reason (like a direct player request you are willing to follow, an URGENT schedule item, or a compelling emotional need like fleeing danger) dictates otherwise, AVOID choosing a target destination that is within {settings.VISIT_THRESHOLD_DISTANCE:.1f} units of any location you visited in the last {settings.REVISIT_INTERVAL_SECONDS // 60} minutes.",
        _format_nearby_entities_for_prompt(other_entities_nearby, current_position),
        _format_landmarks_for_prompt(visible_landmarks, current_position),
    ])

    # --- Section 3: Long-Term Context & External Influences ---
    prompt_components.append(_format_long_term_memories_for_prompt(relevant_long_term_memories))
    
    if recent_dialogue_summary:
        prompt_components.append(f"\n--- Summary of Recent Relevant Dialogue ---")
        prompt_components.append(f"\"{recent_dialogue_summary}\"")
    
    if explicit_player_movement_request:
        prompt_components.append(f"\n--- Explicit Player Movement Request ---")
        prompt_components.append(f"A player has explicitly asked or strongly suggested you go to/near coordinates ({explicit_player_movement_request.x:.1f}, {explicit_player_movement_request.y:.1f}). You should strongly consider this if it's reasonable and doesn't severely conflict with critical duties or your safety.")

    # --- Section 4: Decision Making Instructions & Output Format ---
    prompt_components.append(f"""
\n--- YOUR DECISION TASK ---
Considering ALL the information provided above, you need to decide on your next immediate action and target destination. Follow these steps in your reasoning:
1.  **Identify Primary Drivers:** What are the strongest influences on your decision right now?
    * **Dialogue/Player Request:** Is there a clear request or suggestion from recent dialogue or the explicit player request?
    * **Schedule:** Do your active schedule rules dictate a mandatory or preferred action/location at this specific game time?
    * **Emotion:** Does your current emotional state compel you towards a particular action or place (e.g., seeking solitude if sad, seeking company if happy, fleeing if scared)?
    * **Memory:** Do any of your long-term memories suggest a goal or task (e.g., a promise, an unresolved curiosity)?
    * **Exploration/Default:** If no strong drivers above, what is a logical routine or exploratory action based on your personality and unvisited areas?
2.  **Prioritize and Resolve Conflicts:** If there are conflicting drivers (e.g., player wants you to go to the park, but your schedule says you must be at the shop), explain your prioritization based on your personality, the urgency/importance of each driver, and your emotional state.
3.  **Formulate Chosen Action:** Clearly state the action you've decided to take. This should be a concise, in-character phrase.
4.  **Determine Target Coordinates:** Select precise (x, y) coordinates for your chosen action. These coordinates MUST be within the scene boundaries and adhere to the 'avoid recent visits' rule unless overridden by a high-priority driver. If moving to a named landmark, choose coordinates on or very near it.
5.  **Reflect on Emotional Change:** Briefly note if this decision process or chosen action is likely to change your current emotional state, and what that new state might be.

Your response MUST be structured in the following YAML-like format. Provide a value for each key. Use "N/A", "none", or be descriptive as appropriate.

```yaml
priority_analysis:
  dialogue_driven: Yes/No # Did dialogue/player request primarily drive THIS SPECIFIC action?
  schedule_driven: Yes/No # Was schedule the main driver for THIS SPECIFIC action?
  emotion_driven: Yes/No  # Was emotion the main driver for THIS SPECIFIC action?
  memory_driven: Yes/No   # Did a long-term memory significantly influence THIS SPECIFIC action?
  exploration_driven: Yes/No # Is this primarily an exploration or default routine action?
reasoning: |
  [Your step-by-step thought process explaining your decision and prioritization. Be clear and concise, max 3-5 sentences. For example: "Player asked me to go to the park, which sounds pleasant. My schedule is free. I'm feeling neutral, so a walk in the park is fine. Therefore, I will go to the park coordinates."]
chosen_action: "[Short, in-character phrase describing your action. e.g., 'Alright, let's head to the Park coordinates the player suggested!', 'I should probably check on my stall at the market now.', 'Feeling a bit restless, I'll explore the area near the old mill.', 'I recall promising to visit the library today; I should do that.']"
target_coordinates: "x=<float_value>, y=<float_value>" # e.g., "x=123.4, y=56.7"
resulting_emotion_tag: "[Your primary emotional tag after this decision, e.g., 'neutral', 'content', 'slightly_anxious', 'curious', 'pleased'. Use 'no_change' if your emotion is stable or unaffected by this specific decision.]"
""")
    final_prompt = "\n".join(prompt_components)
    logger.debug(
        f"Built NPC movement prompt for {npc_info.npc_id} (Personality: {personality_description[:30]}...). "
        f"Prompt length: {len(final_prompt)} chars."
    )
    # For very long prompts, you might want to log only a portion or a hash for brevity in production logs
    # if len(final_prompt) > 3000: # Example limit
    #     logger.debug(f"Prompt Preview (first 500, last 200): {final_prompt[:500]}...{final_prompt[-200:]}")
    # else:
    #     logger.debug(f"Full Prompt: {final_prompt}")
        
    return final_prompt


