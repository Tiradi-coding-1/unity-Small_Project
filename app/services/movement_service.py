# npc_api_suite/app/services/movement_service.py

import math
import random
import re 
import time 
from typing import Tuple, Optional, List, Dict, Any, Union 
from datetime import datetime, timezone 
import yaml 

from app.core.schemas import (
    NPCMovementRequest, NPCMovementResponse, Position, NPCIdentifier,
    LandmarkContextInfo, EntityContextInfo, SceneBoundaryInfo, NPCEmotionalState,
    GameTime, Message, MessageRole,
    OllamaChatOptions, NPCMemoryFile, default_aware_utcnow
)
from app.llm.ollama_client import OllamaService
from app.llm.prompt_builder import build_npc_movement_decision_prompt
from app.services.npc_memory_service import NPCMemoryService 
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.utils.helpers import parse_coordinates_from_text, clamp_position_to_bounds 

logger = setup_logging(__name__)

class MovementService:
    def __init__(self, ollama_s: OllamaService):
        self.ollama_service = ollama_s

    def _clean_llm_response_for_yaml(self, llm_text_response: str) -> str:
        if not llm_text_response:
            return ""
        
        text = llm_text_response.strip()
        
        yaml_block_match = re.search(r"```yaml\s*([\s\S]+?)\s*```", text, re.IGNORECASE)
        if yaml_block_match:
            return yaml_block_match.group(1).strip()

        prefixes_to_remove = [
            "<|start_header_id|>assistant<|end_header_id|>",
            "<|start_header_id|>system<|end_header_id|>",
            "<|start_header_id|>user<|end_header_id|>",
            "<|eot_id|>", 
        ]
        suffixes_to_remove = [
            "<|eot_id|>",
        ]

        cleaned_text = text
        for prefix in prefixes_to_remove:
            if cleaned_text.lower().startswith(prefix.lower()):
                cleaned_text = cleaned_text[len(prefix):].lstrip()
        
        for suffix in suffixes_to_remove:
            if cleaned_text.lower().endswith(suffix.lower()):
                cleaned_text = cleaned_text[:-len(suffix)].rstrip()
        
        lines = cleaned_text.splitlines()
        processed_lines = []
        in_yaml_content = False
        
        temp_lines = list(lines)
        while temp_lines and temp_lines[0].strip() == "---":
            temp_lines.pop(0)
        
        while temp_lines and temp_lines[-1].strip() == "---":
            temp_lines.pop()
            
        cleaned_text = "\n".join(temp_lines)

        return cleaned_text.strip()


    def _parse_structured_llm_movement_output(self, llm_text_response: str) -> Dict[str, Any]:
        parsed_data: Dict[str, Any] = { 
            "priority_analysis": {
                "dialogue_driven": False,
                "schedule_driven": False,
                "emotion_driven": False,
                "memory_driven": False,
                "social_interaction_considered": False, 
                "access_rules_consideration": False,
                "exploration_driven": False
            },
            "reasoning": "LLM did not provide clear reasoning or response was unparsable.", 
            "chosen_action": "No specific action determined by LLM.",
            "target_coordinates": None,
            "resulting_emotion_tag": "no_change"
        }
        
        if not llm_text_response or not llm_text_response.strip():
            logger.warning("LLM response was empty or whitespace only. Cannot parse YAML.")
            return parsed_data

        cleaned_response_for_yaml = self._clean_llm_response_for_yaml(llm_text_response)
        
        if not cleaned_response_for_yaml:
            logger.warning("LLM response became empty after cleaning. Cannot parse YAML.")
            return parsed_data

        try:
            data = yaml.safe_load(cleaned_response_for_yaml)
            if isinstance(data, dict):
                pa_data = data.get("priority_analysis", {})
                if isinstance(pa_data, dict): 
                    parsed_data["priority_analysis"]["dialogue_driven"] = str(pa_data.get("dialogue_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["schedule_driven"] = str(pa_data.get("schedule_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["emotion_driven"] = str(pa_data.get("emotion_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["memory_driven"] = str(pa_data.get("memory_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["social_interaction_considered"] = str(pa_data.get("social_interaction_considered", "No")).lower() == "yes" 
                    parsed_data["priority_analysis"]["access_rules_consideration"] = str(pa_data.get("access_rules_consideration", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["exploration_driven"] = str(pa_data.get("exploration_driven", "No")).lower() == "yes"
                else:
                    logger.warning("YAML 'priority_analysis' field was not a dictionary. Using defaults.")

                parsed_data["reasoning"] = data.get("reasoning", parsed_data["reasoning"])
                parsed_data["chosen_action"] = data.get("chosen_action", parsed_data["chosen_action"])
                parsed_data["target_coordinates"] = data.get("target_coordinates") 
                parsed_data["resulting_emotion_tag"] = data.get("resulting_emotion_tag", parsed_data["resulting_emotion_tag"])
                logger.debug(f"Successfully parsed LLM YAML output: {parsed_data}")
                return parsed_data
            else:
                logger.warning(f"LLM output (after cleaning: '{cleaned_response_for_yaml[:200]}...') was not a valid YAML dictionary. Will attempt regex.")

        except yaml.YAMLError as ye:
            logger.warning(f"YAML parsing failed for LLM output (after cleaning: '{cleaned_response_for_yaml[:200]}...'): {ye}. Falling back to regex.")
            
        original_llm_text_for_regex = llm_text_response 

        pa_block_match = re.search(r"priority_analysis:(.*?)(reasoning:)", original_llm_text_for_regex, re.DOTALL | re.IGNORECASE)
        if pa_block_match:
            pa_text = pa_block_match.group(1)
            if "priority_analysis" not in parsed_data or not isinstance(parsed_data["priority_analysis"], dict):
                parsed_data["priority_analysis"] = {} 

            parsed_data["priority_analysis"].update({
                "dialogue_driven": bool(re.search(r"dialogue_driven:\s*Yes", pa_text, re.IGNORECASE)),
                "schedule_driven": bool(re.search(r"schedule_driven:\s*Yes", pa_text, re.IGNORECASE)),
                "emotion_driven": bool(re.search(r"emotion_driven:\s*Yes", pa_text, re.IGNORECASE)),
                "memory_driven": bool(re.search(r"memory_driven:\s*Yes", pa_text, re.IGNORECASE)),
                "social_interaction_considered": bool(re.search(r"social_interaction_considered:\s*Yes", pa_text, re.IGNORECASE)), 
                "access_rules_consideration": bool(re.search(r"access_rules_consideration:\s*Yes", pa_text, re.IGNORECASE)),
                "exploration_driven": bool(re.search(r"exploration_driven:\s*Yes", pa_text, re.IGNORECASE)),
            })

        reasoning_match = re.search(r"reasoning:\s*\|?\s*(.*?)(?=chosen_action:|target_coordinates:|$)", original_llm_text_for_regex, re.DOTALL | re.IGNORECASE)
        if reasoning_match: parsed_data["reasoning"] = reasoning_match.group(1).strip()
        
        action_match = re.search(r"chosen_action:\s*\"?(.*?)\"?\s*(?=target_coordinates:|$)", original_llm_text_for_regex, re.DOTALL | re.IGNORECASE)
        if action_match: parsed_data["chosen_action"] = action_match.group(1).strip().strip('"')

        coords_match = re.search(r"target_coordinates:\s*\"?(.*?)\"?\s*(?=resulting_emotion_tag:|$)", original_llm_text_for_regex, re.DOTALL | re.IGNORECASE)
        if coords_match: parsed_data["target_coordinates"] = coords_match.group(1).strip().strip('"')

        emotion_match = re.search(r"resulting_emotion_tag:\s*\"?(.*?)\"?\s*$", original_llm_text_for_regex, re.DOTALL | re.IGNORECASE)
        if emotion_match: parsed_data["resulting_emotion_tag"] = emotion_match.group(1).strip().strip('"')
        
        logger.debug(f"Parsed LLM output using regex fallback: {parsed_data}")
        
        return parsed_data

    async def _find_fallback_exploration_target(
        self,
        npc_id_str: str, 
        current_pos: Position,
        landmarks: List[LandmarkContextInfo],
        bounds: SceneBoundaryInfo,
        memory_service: NPCMemoryService, 
        current_game_time: GameTime,
        other_entities: Optional[List[EntityContextInfo]] = None 
    ) -> Tuple[float, float]:
        logger.info(f"NPC '{npc_id_str}': Executing FALLBACK exploration strategy for apartment from ({current_pos.x:.1f}, {current_pos.y:.1f}).")
        potential_targets: List[Dict[str, Any]] = []

        for lm in landmarks:
            is_accessible = True
            if lm.landmark_type_tag == "bathroom":
                if any("occupancy_occupied" in note.lower() and npc_id_str.lower() not in note.lower() for note in lm.current_status_notes):
                    is_accessible = False
            elif lm.landmark_type_tag == "bedroom" and lm.owner_id and lm.owner_id.lower() != npc_id_str.lower():
                if any("owner_presence_absent" in note.lower() for note in lm.current_status_notes):
                    is_accessible = False
            
            if not is_accessible:
                logger.debug(f"Fallback: Landmark '{lm.landmark_name}' is not accessible, skipping for exploration target.")
                continue

            dist_to_lm = current_pos.distance_to(lm.position)
            if settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 0.5 <= dist_to_lm <= settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT * 0.7:
                score = 50 
                if lm.landmark_type_tag in ["living_room", "kitchen", "dining_room"]:
                    score += 20
                elif lm.landmark_type_tag == "bedroom" and lm.owner_id and lm.owner_id.lower() == npc_id_str.lower():
                    score += 15
                potential_targets.append({"x": lm.position.x, "y": lm.position.y, "type": f"landmark:{lm.landmark_name}", "base_score": score})
        
        num_random_points = 10
        for i in range(num_random_points):
            angle = random.uniform(0, 2 * math.pi)
            distance = random.uniform(
                settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 0.7, 
                settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT * 0.5 
            )
            x_rand = current_pos.x + distance * math.cos(angle)
            y_rand = current_pos.y + distance * math.sin(angle)
            potential_targets.append({"x": x_rand, "y": y_rand, "type": f"random_explore_{i}", "base_score": 30})
        
        scored_targets: List[Dict[str, Any]] = []
        if not potential_targets:
            logger.warning(f"NPC '{npc_id_str}': No potential fallback targets generated. Using emergency random within bounds.")
            x_emergency = random.uniform(bounds.min_x + settings.SCENE_BOUNDARY_BUFFER, bounds.max_x - settings.SCENE_BOUNDARY_BUFFER)
            y_emergency = random.uniform(bounds.min_y + settings.SCENE_BOUNDARY_BUFFER, bounds.max_y - settings.SCENE_BOUNDARY_BUFFER)
            return x_emergency, y_emergency

        for pt in potential_targets:
            x_clamped, y_clamped = clamp_position_to_bounds(pt["x"], pt["y"], bounds, settings.SCENE_BOUNDARY_BUFFER)
            
            dist_from_current = current_pos.distance_to(Position(x=x_clamped, y=y_clamped))
            if dist_from_current < settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 0.3:
                continue
            
            score = float(pt["base_score"])
            if current_game_time and isinstance(current_game_time.current_timestamp, datetime):
                 if await memory_service.has_been_visited_recently(x_clamped, y_clamped, current_game_time.current_timestamp):
                    score -= 300 
                 else:
                    score += 100 
            else:
                logger.warning(f"Fallback exploration for {npc_id_str}: current_game_time or timestamp invalid, skipping revisit check.")
            
            if other_entities:
                for entity_info in other_entities:
                    if Position(x=x_clamped,y=y_clamped).distance_to(Position(x=entity_info.x,y=entity_info.y)) < 2.0: 
                        score -= 25

            score += random.uniform(-15, 15) 

            scored_targets.append({"x": x_clamped, "y": y_clamped, "type": pt["type"], "score": score, "dist": dist_from_current})
        
        if scored_targets:
            best_target = sorted(scored_targets, key=lambda t: t["score"], reverse=True)[0]
            logger.info(f"NPC '{npc_id_str}': Fallback strategy selected target: Type='{best_target['type']}', Coords=({best_target['x']:.1f}, {best_target['y']:.1f}), Score={best_target['score']:.1f}")
            return best_target["x"], best_target["y"]
        else:
            logger.warning(f"NPC '{npc_id_str}': All scored fallback targets were filtered. Using emergency random point.")
            x_emergency = random.uniform(bounds.min_x + settings.SCENE_BOUNDARY_BUFFER, bounds.max_x - settings.SCENE_BOUNDARY_BUFFER)
            y_emergency = random.uniform(bounds.min_y + settings.SCENE_BOUNDARY_BUFFER, bounds.max_y - settings.SCENE_BOUNDARY_BUFFER)
            return x_emergency, y_emergency

    def _get_landmark_by_name_or_pos(
        self,
        target_name_or_pos: Union[str, Position],
        visible_landmarks: List[LandmarkContextInfo],
        current_npc_pos: Position, 
        proximity_threshold: float = 2.0 
    ) -> Optional[LandmarkContextInfo]:
        if isinstance(target_name_or_pos, str) and target_name_or_pos:
            target_name_lower = target_name_or_pos.lower()
            for lm in visible_landmarks:
                if lm.landmark_name.lower() == target_name_lower or \
                   (lm.landmark_type_tag and lm.landmark_type_tag.lower() in target_name_lower): 
                    return lm
        
        if isinstance(target_name_or_pos, Position):
            sorted_landmarks = sorted(
                visible_landmarks,
                key=lambda lm_i: lm_i.position.distance_to(target_name_or_pos)
            )
            if sorted_landmarks and sorted_landmarks[0].position.distance_to(target_name_or_pos) < proximity_threshold:
                logger.debug(f"LLM target coords ({target_name_or_pos.x:.1f}, {target_name_or_pos.y:.1f}) matched to landmark '{sorted_landmarks[0].landmark_name}' by proximity.")
                return sorted_landmarks[0]
        
        logger.debug(f"Could not reliably match target '{str(target_name_or_pos)}' to a specific visible landmark via name or proximity to LLM coords.")
        return None

    def _extract_entrance_positions_from_landmark(self, landmark: LandmarkContextInfo) -> List[Position]:
        if landmark.entrance_positions and isinstance(landmark.entrance_positions, list):
            valid_entrances: List[Position] = []
            for ep_data in landmark.entrance_positions:
                if isinstance(ep_data, Position):
                    valid_entrances.append(ep_data)
                elif isinstance(ep_data, dict): 
                    try:
                        valid_entrances.append(Position(**ep_data))
                    except Exception as e: 
                        logger.warning(f"Could not parse entrance position dict for landmark '{landmark.landmark_name}': {ep_data}, Error: {e}")
                else:
                    logger.warning(f"Invalid data type for entrance position in landmark '{landmark.landmark_name}': {type(ep_data)}. Expected Position or dict.")
            return valid_entrances
        return [] 

    async def decide_npc_movement(
        self,
        request_data: NPCMovementRequest,
        memory_service: NPCMemoryService
    ) -> NPCMovementResponse:
        request_start_time = time.perf_counter()
        npc_id_obj = NPCIdentifier(npc_id=request_data.npc_id, name=request_data.name)
        logger.info(f"Movement decision process started for NPC '{npc_id_obj.npc_id}'.")

        npc_memory: NPCMemoryFile = await memory_service.get_memory_data()
        npc_memory.last_known_position = request_data.current_npc_position
        npc_memory.last_known_game_time = request_data.current_game_time

        system_prompt_for_llm = build_npc_movement_decision_prompt(
            npc_info=npc_id_obj,
            personality_description=npc_memory.personality_description,
            current_position=request_data.current_npc_position,
            game_time=request_data.current_game_time,
            emotional_state=npc_memory.current_emotional_state,
            active_schedule_rules=npc_memory.active_schedule_rules,
            other_entities_nearby=request_data.nearby_entities,
            visible_landmarks=request_data.visible_landmarks,
            scene_boundaries=request_data.scene_boundaries,
            short_term_location_history=npc_memory.short_term_location_history,
            relevant_long_term_memories=await memory_service.get_relevant_long_term_memories(limit=5),
            recent_dialogue_summary=request_data.recent_dialogue_summary_for_movement,
            explicit_player_movement_request=request_data.explicit_player_movement_request
        )

        llm_response_raw: str = "" # Renamed from llm_raw_response_text for clarity with the parameter in _log_movement_decision_details
        parsed_llm_output: Dict[str, Any]
        llm_chosen_target_pos: Optional[Position] = None
        final_target_position: Position
        decision_context_summary: str = "Decision process initiated."
        chosen_action_summary_override: Optional[str] = None 

        try:
            effective_model = request_data.model_override or settings.DEFAULT_OLLAMA_MODEL
            llm_temperature = 0.65 
            llm_options = OllamaChatOptions(
                temperature=llm_temperature, 
                num_ctx=settings.DEFAULT_MAX_TOKENS if settings.DEFAULT_MAX_TOKENS > 0 else None, 
                top_k=45, 
                top_p=0.92
            )

            messages_for_llm = [Message(role=MessageRole.SYSTEM, content=system_prompt_for_llm)]
            ollama_response_data = await self.ollama_service.generate_chat_completion(
                model=effective_model,
                messages=messages_for_llm,
                stream=False,
                options=llm_options
            )
            
            if isinstance(ollama_response_data, dict) and "error" in ollama_response_data:
                error_message_from_client = ollama_response_data.get("message", {}).get("content", ollama_response_data["error"])
                logger.error(f"LLM client returned an error structure for NPC '{npc_id_obj.npc_id}': {error_message_from_client}")
                llm_response_raw = error_message_from_client 
            elif isinstance(ollama_response_data, dict) and "message" in ollama_response_data and isinstance(ollama_response_data["message"], dict):
                 llm_response_raw = ollama_response_data.get('message', {}).get('content', "")
            else:
                logger.error(f"Unexpected response structure from ollama_service for NPC '{npc_id_obj.npc_id}': {str(ollama_response_data)[:300]}")
                llm_response_raw = "[LLM Response Structure Invalid]"

            logger.debug(f"NPC '{npc_id_obj.npc_id}' LLM Raw Response for movement (to be parsed): '{llm_response_raw[:300]}...'")

            parsed_llm_output = self._parse_structured_llm_movement_output(llm_response_raw)
            coords_str = parsed_llm_output.get("target_coordinates")
            if coords_str:
                x_coord, y_coord = parse_coordinates_from_text(coords_str)
                if x_coord is not None and y_coord is not None:
                    llm_chosen_target_pos = Position(x=x_coord, y=y_coord)
        
        except Exception as e: 
            logger.error(f"Error during LLM interaction or initial parsing for NPC '{npc_id_obj.npc_id}': {e}", exc_info=True)
            decision_context_summary = f"Error during LLM phase: {type(e).__name__}. Fallback will be used."
            if 'parsed_llm_output' not in locals() or not isinstance(parsed_llm_output, dict):
                 parsed_llm_output = self._parse_structured_llm_movement_output("")


        if llm_chosen_target_pos:
            llm_action_summary_for_landmark_search = parsed_llm_output.get("chosen_action", "")
            
            final_targeted_landmark: Optional[LandmarkContextInfo] = self._get_landmark_by_name_or_pos(
                llm_chosen_target_pos, request_data.visible_landmarks, request_data.current_npc_position
            )
            
            if not final_targeted_landmark and llm_action_summary_for_landmark_search:
                search_term_from_action = ""
                if "bathroom" in llm_action_summary_for_landmark_search.lower() or "toilet" in llm_action_summary_for_landmark_search.lower():
                    search_term_from_action = "bathroom" 
                
                if search_term_from_action:
                    final_targeted_landmark = self._get_landmark_by_name_or_pos(
                        search_term_from_action, request_data.visible_landmarks, request_data.current_npc_position
                    )

            if final_targeted_landmark and \
               final_targeted_landmark.landmark_type_tag == "bathroom" and \
               any("occupancy_occupied" in note.lower() and request_data.npc_id.lower() not in note.lower() for note in final_targeted_landmark.current_status_notes):
                
                logger.info(f"NPC '{request_data.npc_id}' intends to go to bathroom '{final_targeted_landmark.landmark_name}', but it's occupied by another.")
                
                entrance_positions = self._extract_entrance_positions_from_landmark(final_targeted_landmark)
                best_waiting_spot: Optional[Position] = None

                if entrance_positions:
                    entrance_positions.sort(key=lambda p: request_data.current_npc_position.distance_to(p))
                    best_waiting_spot = entrance_positions[0]
                    logger.info(f"Identified closest entrance for occupied bathroom '{final_targeted_landmark.landmark_name}' at: ({best_waiting_spot.x:.2f}, {best_waiting_spot.y:.2f})")
                
                if best_waiting_spot:
                    clamped_x_wait, clamped_y_wait = clamp_position_to_bounds(
                        best_waiting_spot.x, best_waiting_spot.y,
                        request_data.scene_boundaries, settings.SCENE_BOUNDARY_BUFFER * 0.5 
                    )
                    final_target_position = Position(x=clamped_x_wait, y=clamped_y_wait)
                    chosen_action_summary_override = f"Heading to wait near {final_targeted_landmark.landmark_name} (it is occupied)."
                    decision_context_summary = chosen_action_summary_override 
                    logger.info(f"NPC '{request_data.npc_id}': LLM target was occupied bathroom. Adjusted target to waiting spot: ({final_target_position.x:.1f},{final_target_position.y:.1f}). Action: {chosen_action_summary_override}")
                else: 
                    logger.warning(f"NPC '{request_data.npc_id}': Bathroom '{final_targeted_landmark.landmark_name}' is occupied, but no entrance/waiting spot found/defined for it. Using LLM's original target directly; Unity may reject or NPC behavior might be suboptimal.")
                    clamped_x, clamped_y = clamp_position_to_bounds(
                        llm_chosen_target_pos.x, llm_chosen_target_pos.y,
                        request_data.scene_boundaries, settings.SCENE_BOUNDARY_BUFFER
                    )
                    final_target_position = Position(x=clamped_x, y=clamped_y)
                    decision_context_summary = parsed_llm_output.get('reasoning', 'LLM provided target for bathroom, but it is occupied and no waiting spot was determined by backend.')
            
            else: 
                clamped_x, clamped_y = clamp_position_to_bounds(
                    llm_chosen_target_pos.x, llm_chosen_target_pos.y,
                    request_data.scene_boundaries, settings.SCENE_BOUNDARY_BUFFER
                )
                final_target_position = Position(x=clamped_x, y=clamped_y)
                decision_context_summary = parsed_llm_output.get('reasoning', 'LLM provided target.')
                if final_target_position.distance_to(llm_chosen_target_pos) > 0.1: 
                     decision_context_summary += " (Note: LLM target was clamped to scene boundaries)."
        
        else: 
            logger.warning(f"NPC '{npc_id_obj.npc_id}': No valid target coordinates from LLM. Triggering fallback exploration. Last decision context: {decision_context_summary}")
            if 'parsed_llm_output' not in locals() or not isinstance(parsed_llm_output, dict):
                parsed_llm_output = self._parse_structured_llm_movement_output("")

            fx, fy = await self._find_fallback_exploration_target(
                npc_id_obj.npc_id, request_data.current_npc_position, request_data.visible_landmarks,
                request_data.scene_boundaries, memory_service, request_data.current_game_time, request_data.nearby_entities
            )
            final_target_position = Position(x=fx, y=fy)
            parsed_llm_output["chosen_action"] = f"Fallback exploration towards ({fx:.1f}, {fy:.1f})."
            decision_context_summary = "Fallback exploration triggered due to LLM failure to provide valid coordinates."
            if not isinstance(parsed_llm_output.get("priority_analysis"), dict):
                parsed_llm_output["priority_analysis"] = {} 
            parsed_llm_output["priority_analysis"]["exploration_driven"] = True


        if final_target_position and request_data.current_game_time and isinstance(request_data.current_game_time.current_timestamp, datetime) :
            await memory_service.add_visited_location(final_target_position.x, final_target_position.y, request_data.current_game_time.current_timestamp)
        elif final_target_position:
             logger.warning(f"Could not add visited location for {npc_id_obj.npc_id} due to invalid game_time.current_timestamp: {type(request_data.current_game_time.current_timestamp if request_data.current_game_time else None)}")


        new_emotion_tag = parsed_llm_output.get("resulting_emotion_tag", "no_change")
        updated_emotional_state_snapshot = npc_memory.current_emotional_state
        
        current_primary_emotion_lower = npc_memory.current_emotional_state.primary_emotion.lower()
        new_emotion_tag_lower = new_emotion_tag.lower() if isinstance(new_emotion_tag, str) else "no_change" 
        meaningful_change_tags = ["no_change", "same", "n/a", "none", "neutral"]
        
        if isinstance(new_emotion_tag, str):
            cleaned_new_emotion_tag = re.sub(r"[\[\]\"']", "", new_emotion_tag).strip()
            if "no change" in cleaned_new_emotion_tag.lower() or "neutral emotion" in cleaned_new_emotion_tag.lower() : 
                 potential_emotions = ["happy", "sad", "angry", "curious", "content", "annoyed", "fearful"] 
                 found_emotion = "no_change"
                 for pe in potential_emotions:
                     if pe in cleaned_new_emotion_tag.lower():
                         found_emotion = pe
                         break
                 cleaned_new_emotion_tag = found_emotion

            new_emotion_tag_lower = cleaned_new_emotion_tag.lower()

            if cleaned_new_emotion_tag and \
               new_emotion_tag_lower not in meaningful_change_tags and \
               new_emotion_tag_lower != current_primary_emotion_lower:
                await memory_service.update_emotional_state(
                    new_primary_emotion=cleaned_new_emotion_tag, 
                    intensity=npc_memory.current_emotional_state.intensity, 
                    reason=f"Movement Decision: {parsed_llm_output.get('chosen_action', 'Unknown')}"
                )
                updated_emotional_state_snapshot = (await memory_service.get_memory_data()).current_emotional_state
            elif new_emotion_tag_lower != "no_change" and new_emotion_tag_lower != current_primary_emotion_lower : 
                logger.info(f"NPC {npc_id_obj.npc_id} emotion tag from LLM '{new_emotion_tag}' resulted in no change or back to neutral.")
        else:
             logger.warning(f"NPC {npc_id_obj.npc_id} resulting_emotion_tag was not a string: {new_emotion_tag}")


        await memory_service.save_memory_to_file()

        # *** Pass llm_response_raw (which holds the text) to the logging function ***
        self._log_movement_decision_details(request_data, system_prompt_for_llm, llm_response_raw, final_target_position, decision_context_summary, parsed_llm_output)

        api_processing_time_ms = (time.perf_counter() - request_start_time) * 1000
        
        final_chosen_action_summary = chosen_action_summary_override or parsed_llm_output.get('chosen_action', "Action determined by fallback.")
        
        final_priority_drivers = parsed_llm_output.get("priority_analysis", {})
        if not isinstance(final_priority_drivers, dict):
            final_priority_drivers = {} 

        return NPCMovementResponse(
            npc_id=npc_id_obj.npc_id,
            name=npc_id_obj.name,
            llm_full_reasoning_text=parsed_llm_output.get('reasoning', llm_response_raw if llm_response_raw else "No reasoning available."), # Use llm_response_raw here
            chosen_action_summary=final_chosen_action_summary,
            target_destination=final_target_position,
            primary_decision_drivers=final_priority_drivers,
            updated_emotional_state_snapshot=updated_emotional_state_snapshot,
            api_processing_time_ms=round(api_processing_time_ms, 2)
        )

    def _log_movement_decision_details(
        self, request: NPCMovementRequest, prompt: str, llm_response_raw_text_param: str, # Changed parameter name
        final_target: Position, reason_summary: str, parsed_llm_output: Dict[str,Any]
    ):
        log_entry = {
            "timestamp": default_aware_utcnow().isoformat(),
            "npc_id": request.npc_id,
            "request_summary": {
                "current_pos": request.current_npc_position.model_dump(),
                "game_time": request.current_game_time.model_dump(mode='json') if request.current_game_time else None,
                "dialogue_summary_input": request.recent_dialogue_summary_for_movement,
                "player_request_input": request.explicit_player_movement_request.model_dump(mode='json') if request.explicit_player_movement_request else None
            },
            # *** Corrected variable name here ***
            "llm_raw_response_snippet": llm_response_raw_text_param[:500] + "..." if len(llm_response_raw_text_param) > 500 else llm_response_raw_text_param,
            "llm_parsed_output": parsed_llm_output,
            "decision_reason_summary": reason_summary,
            "final_target_chosen": final_target.model_dump() if final_target else None,
        }
        logger.debug(f"NPC Movement Trace for '{request.npc_id}': {log_entry}")
        
        action_log = parsed_llm_output.get('chosen_action', "N/A")
        if final_target:
             logger.info(f"NPC Movement Decision for '{request.npc_id}': Target=({final_target.x:.1f},{final_target.y:.1f}). Action: '{action_log}'. Reason: {reason_summary[:100]}...")
        else:
             logger.info(f"NPC Movement Decision for '{request.npc_id}': No valid final target. Action: '{action_log}'. Reason: {reason_summary[:100]}...")