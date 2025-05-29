# npc_api_suite/app/services/movement_service.py

import math
import random
import re # For parsing LLM output if not perfectly structured
from typing import Tuple, Optional, List, Dict, Any
import time # For processing time calculation
from datetime import datetime, timezone # For default_aware_utcnow
import yaml # For parsing YAML-like LLM output

from app.core.schemas import (
    NPCMovementRequest, NPCMovementResponse, Position, NPCIdentifier,
    LandmarkContextInfo, EntityContextInfo, SceneBoundaryInfo, NPCEmotionalState,
    GameTime, Message, MessageRole,
    OllamaChatOptions, NPCMemoryFile, default_aware_utcnow
)
from app.llm.ollama_client import OllamaService
from app.llm.prompt_builder import build_npc_movement_decision_prompt
from app.services.npc_memory_service import NPCMemoryService # To interact with NPC's memory
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.utils.helpers import parse_coordinates_from_text, clamp_position_to_bounds 

logger = setup_logging(__name__)

class MovementService:
    """
    Service class for determining NPC movement decisions based on various inputs
    including current state, environment, memory, dialogue, and LLM responses.
    """
    def __init__(self, ollama_s: OllamaService):
        """
        Initializes the MovementService.
        Args:
            ollama_s: An instance of OllamaService for LLM interactions.
        """
        self.ollama_service = ollama_s

    def _parse_structured_llm_movement_output(self, llm_text_response: str) -> Dict[str, Any]:
        """
        Parses the YAML-like structured output from the LLM's movement decision.
        Now includes 'access_rules_consideration'.
        """
        parsed_data: Dict[str, Any] = { 
            "priority_analysis": {
                "dialogue_driven": False,
                "schedule_driven": False,
                "emotion_driven": False,
                "memory_driven": False,
                "access_rules_consideration": False,
                "exploration_driven": False
            },
            "reasoning": "LLM did not provide clear reasoning.",
            "chosen_action": "No specific action determined by LLM.",
            "target_coordinates": None,
            "resulting_emotion_tag": "no_change"
        }
        
        try:
            cleaned_response = llm_text_response.strip()
            if cleaned_response.startswith("```yaml"):
                cleaned_response = cleaned_response[len("```yaml"):]
            if cleaned_response.startswith("```"): 
                 cleaned_response = cleaned_response[len("```"):]
            if cleaned_response.endswith("```"):
                cleaned_response = cleaned_response[:-len("```")]
            cleaned_response = cleaned_response.strip()

            try:
                data = yaml.safe_load(cleaned_response)
                if isinstance(data, dict):
                    pa_data = data.get("priority_analysis", {})
                    parsed_data["priority_analysis"]["dialogue_driven"] = str(pa_data.get("dialogue_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["schedule_driven"] = str(pa_data.get("schedule_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["emotion_driven"] = str(pa_data.get("emotion_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["memory_driven"] = str(pa_data.get("memory_driven", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["access_rules_consideration"] = str(pa_data.get("access_rules_consideration", "No")).lower() == "yes"
                    parsed_data["priority_analysis"]["exploration_driven"] = str(pa_data.get("exploration_driven", "No")).lower() == "yes"
                    
                    parsed_data["reasoning"] = data.get("reasoning", parsed_data["reasoning"])
                    parsed_data["chosen_action"] = data.get("chosen_action", parsed_data["chosen_action"])
                    parsed_data["target_coordinates"] = data.get("target_coordinates") 
                    parsed_data["resulting_emotion_tag"] = data.get("resulting_emotion_tag", parsed_data["resulting_emotion_tag"])
                    logger.debug(f"Successfully parsed LLM YAML output: {parsed_data}")
                    return parsed_data
                else:
                    logger.warning(f"LLM output was not a valid YAML dictionary after cleaning: '{cleaned_response[:200]}...'")

            except yaml.YAMLError as ye:
                logger.warning(f"YAML parsing failed for LLM output: {ye}. Falling back to regex. Output: '{cleaned_response[:200]}...'")

            pa_block_match = re.search(r"priority_analysis:(.*?)(reasoning:)", cleaned_response, re.DOTALL | re.IGNORECASE)
            if pa_block_match:
                pa_text = pa_block_match.group(1)
                parsed_data["priority_analysis"] = {
                    "dialogue_driven": bool(re.search(r"dialogue_driven:\s*Yes", pa_text, re.IGNORECASE)),
                    "schedule_driven": bool(re.search(r"schedule_driven:\s*Yes", pa_text, re.IGNORECASE)),
                    "emotion_driven": bool(re.search(r"emotion_driven:\s*Yes", pa_text, re.IGNORECASE)),
                    "memory_driven": bool(re.search(r"memory_driven:\s*Yes", pa_text, re.IGNORECASE)),
                    "access_rules_consideration": bool(re.search(r"access_rules_consideration:\s*Yes", pa_text, re.IGNORECASE)),
                    "exploration_driven": bool(re.search(r"exploration_driven:\s*Yes", pa_text, re.IGNORECASE)),
                }

            reasoning_match = re.search(r"reasoning:\s*\|?\s*(.*?)(?=chosen_action:|target_coordinates:|$)", cleaned_response, re.DOTALL | re.IGNORECASE)
            if reasoning_match: parsed_data["reasoning"] = reasoning_match.group(1).strip()
            
            action_match = re.search(r"chosen_action:\s*\"?(.*?)\"?\s*(?=target_coordinates:|$)", cleaned_response, re.DOTALL | re.IGNORECASE)
            if action_match: parsed_data["chosen_action"] = action_match.group(1).strip().strip('"')

            coords_match = re.search(r"target_coordinates:\s*\"?(.*?)\"?\s*(?=resulting_emotion_tag:|$)", cleaned_response, re.DOTALL | re.IGNORECASE)
            if coords_match: parsed_data["target_coordinates"] = coords_match.group(1).strip().strip('"')

            emotion_match = re.search(r"resulting_emotion_tag:\s*\"?(.*?)\"?\s*$", cleaned_response, re.DOTALL | re.IGNORECASE)
            if emotion_match: parsed_data["resulting_emotion_tag"] = emotion_match.group(1).strip().strip('"')
            
            logger.debug(f"Parsed LLM output using regex fallback: {parsed_data}")

        except Exception as e:
            logger.error(f"Error parsing LLM movement response: {e}. Raw response: '{llm_text_response[:200]}...'", exc_info=True)
        
        return parsed_data

    async def _find_fallback_exploration_target(
        self,
        npc_id_str: str, 
        current_pos: Position,
        landmarks: List[LandmarkContextInfo],
        bounds: SceneBoundaryInfo,
        memory_service: NPCMemoryService, 
        current_game_time: GameTime, # MODIFIED: This is the full GameTime object
        other_entities: Optional[List[EntityContextInfo]] = None 
    ) -> Tuple[float, float]:
        logger.info(f"NPC '{npc_id_str}': Executing FALLBACK exploration strategy for apartment from ({current_pos.x:.1f}, {current_pos.y:.1f}).")
        potential_targets: List[Dict[str, Any]] = []

        for lm in landmarks:
            is_accessible = True
            if lm.landmark_type_tag == "bathroom" and any("occupancy_occupied" in note for note in lm.current_status_notes):
                is_accessible = False
            elif lm.landmark_type_tag == "bedroom" and lm.owner_id and lm.owner_id != npc_id_str and \
                 any("owner_presence_absent" in note for note in lm.current_status_notes):
                is_accessible = False
            
            if not is_accessible:
                logger.debug(f"Fallback: Landmark '{lm.landmark_name}' is not accessible, skipping for exploration target.")
                continue

            dist_to_lm = current_pos.distance_to(lm.position)
            if settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 0.5 <= dist_to_lm <= settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT * 0.7:
                score = 50
                if lm.landmark_type_tag in ["living_room", "kitchen", "dining_room"]:
                    score += 20
                elif lm.landmark_type_tag == "bedroom" and lm.owner_id == npc_id_str:
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
            # MODIFIED: Pass current_game_time.current_timestamp (which is a datetime object)
            if await memory_service.has_been_visited_recently(x_clamped, y_clamped, current_game_time.current_timestamp):
                score -= 300
            else:
                score += 100
            
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
        
        llm_raw_response_text: str = ""
        parsed_llm_output: Dict[str, Any]
        llm_chosen_target_pos: Optional[Position] = None
        final_target_position: Position
        decision_context_summary: str = "Decision process initiated."

        try:
            effective_model = request_data.model_override or settings.DEFAULT_OLLAMA_MODEL
            llm_options = OllamaChatOptions(temperature=0.65, num_ctx=settings.DEFAULT_MAX_TOKENS, top_k=45, top_p=0.92) 

            messages_for_llm = [Message(role=MessageRole.SYSTEM, content=system_prompt_for_llm)]
            ollama_response_data = await self.ollama_service.generate_chat_completion(
                model=effective_model,
                messages=messages_for_llm,
                stream=False,
                options=llm_options
            )
            
            llm_raw_response_text = ollama_response_data.get('message', {}).get('content', "")
            logger.debug(f"NPC '{npc_id_obj.npc_id}' LLM Raw Response for movement: '{llm_raw_response_text[:300]}...'")

            parsed_llm_output = self._parse_structured_llm_movement_output(llm_raw_response_text)
            coords_str = parsed_llm_output.get("target_coordinates")
            if coords_str:
                x, y = parse_coordinates_from_text(coords_str) 
                if x is not None and y is not None:
                    llm_chosen_target_pos = Position(x=x, y=y)
        
        except Exception as e: 
            logger.error(f"Error during LLM interaction or parsing for NPC '{npc_id_obj.npc_id}': {e}", exc_info=True)
            decision_context_summary = f"Error during LLM phase: {type(e).__name__}. Fallback will be used."
            parsed_llm_output = self._parse_structured_llm_movement_output("")

        if llm_chosen_target_pos:
            clamped_x, clamped_y = clamp_position_to_bounds(
                llm_chosen_target_pos.x, llm_chosen_target_pos.y,
                request_data.scene_boundaries, settings.SCENE_BOUNDARY_BUFFER
            )
            final_target_position = Position(x=clamped_x, y=y_clamped)
            decision_context_summary = parsed_llm_output.get('reasoning', 'LLM provided target.')
            if final_target_position.distance_to(llm_chosen_target_pos) > 0.1:
                logger.warning(f"NPC '{npc_id_obj.npc_id}': LLM target ({llm_chosen_target_pos.x:.1f},{llm_chosen_target_pos.y:.1f}) was out of bounds, clamped to ({clamped_x:.1f},{clamped_y:.1f}).")
                decision_context_summary += " (Note: LLM target was clamped to scene boundaries)."
            
            logger.info(f"NPC '{npc_id_obj.npc_id}': LLM proposed target {final_target_position}. Unity-side controller will perform final access checks.")
        else: 
            logger.warning(f"NPC '{npc_id_obj.npc_id}': No valid target from LLM. Triggering fallback exploration. Summary: {decision_context_summary}")
            fx, fy = await self._find_fallback_exploration_target(
                npc_id_obj.npc_id, request_data.current_npc_position, request_data.visible_landmarks,
                request_data.scene_boundaries, memory_service, request_data.current_game_time, request_data.nearby_entities # MODIFIED: Pass full GameTime object
            )
            final_target_position = Position(x=fx, y=fy)
            parsed_llm_output["chosen_action"] = f"Fallback exploration towards ({fx:.1f}, {fy:.1f})."
            if "LLM did not provide" in decision_context_summary: 
                 decision_context_summary = "Fallback exploration triggered due to LLM failure to provide coordinates."
        
        # MODIFIED: Pass request_data.current_game_time.current_timestamp (which is a datetime object)
        await memory_service.add_visited_location(final_target_position.x, final_target_position.y, request_data.current_game_time.current_timestamp)
        
        new_emotion_tag = parsed_llm_output.get("resulting_emotion_tag", "no_change")
        updated_emotional_state_snapshot = npc_memory.current_emotional_state 
        if new_emotion_tag and new_emotion_tag.lower() not in ["no_change", "same", "n/a", "none"]:
            await memory_service.update_emotional_state(
                new_primary_emotion=new_emotion_tag,
                intensity=npc_memory.current_emotional_state.intensity, 
                reason=f"Decision: {parsed_llm_output.get('chosen_action', 'Unknown')}"
            )
            updated_emotional_state_snapshot = (await memory_service.get_memory_data()).current_emotional_state
        
        await memory_service.save_memory_to_file()

        self._log_movement_decision_details(request_data, system_prompt_for_llm, llm_raw_response_text, final_target_position, decision_context_summary, parsed_llm_output)

        api_processing_time_ms = (time.perf_counter() - request_start_time) * 1000
        
        return NPCMovementResponse(
            npc_id=npc_id_obj.npc_id,
            name=npc_id_obj.name,
            llm_full_reasoning_text=parsed_llm_output.get('reasoning', llm_raw_response_text), 
            chosen_action_summary=parsed_llm_output.get('chosen_action', "Action determined by fallback."),
            target_destination=final_target_position,
            primary_decision_drivers=parsed_llm_output.get("priority_analysis", {}),
            updated_emotional_state_snapshot=updated_emotional_state_snapshot,
            api_processing_time_ms=round(api_processing_time_ms, 2)
        )

    def _log_movement_decision_details(
        self, request: NPCMovementRequest, prompt: str, llm_response_raw:str,
        final_target: Position, reason_summary: str, parsed_llm_output: Dict[str,Any]
    ):
        log_entry = {
            "timestamp": default_aware_utcnow().isoformat(),
            "npc_id": request.npc_id,
            "request_summary": {
                "current_pos": request.current_npc_position.model_dump(),
                "game_time": request.current_game_time.model_dump(mode='json'),
                "dialogue_summary_input": request.recent_dialogue_summary_for_movement,
                "player_request_input": request.explicit_player_movement_request.model_dump(mode='json') if request.explicit_player_movement_request else None
            },
            "llm_raw_response_snippet": llm_response_raw[:500] + "..." if len(llm_response_raw) > 500 else llm_response_raw,
            "llm_parsed_output": parsed_llm_output,
            "decision_reason_summary": reason_summary,
            "final_target_chosen": final_target.model_dump(),
        }
        logger.debug(f"NPC Movement Trace for '{request.npc_id}': {log_entry}")
        logger.info(f"NPC Movement Decision for '{request.npc_id}': Target=({final_target.x:.1f},{final_target.y:.1f}). Action: '{parsed_llm_output.get('chosen_action')}'. Reason: {reason_summary[:100]}...")