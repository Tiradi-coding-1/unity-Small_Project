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
    GameTime, Message, MessageRole, OllamaChatOptions, NPCMemoryFile, default_aware_utcnow
)
from app.llm.ollama_client import OllamaService
from app.llm.prompt_builder import build_npc_movement_decision_prompt
from app.services.npc_memory_service import NPCMemoryService # To interact with NPC's memory
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.utils.helpers import parse_coordinates_from_text, clamp_position_to_bounds # Assuming these exist

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
        """
        parsed_data: Dict[str, Any] = { # Initialize with defaults
            "priority_analysis": {},
            "reasoning": "LLM did not provide clear reasoning.",
            "chosen_action": "No specific action determined by LLM.",
            "target_coordinates": None,
            "resulting_emotion_tag": "no_change"
        }
        
        try:
            # Attempt to load the YAML-like block.
            # The prompt instructs LLM to output ONLY the YAML block.
            # We might need to clean up potential markdown backticks if LLM adds them.
            cleaned_response = llm_text_response.strip()
            if cleaned_response.startswith("```yaml"):
                cleaned_response = cleaned_response[len("```yaml"):]
            if cleaned_response.startswith("```"): # Simpler ``` case
                 cleaned_response = cleaned_response[len("```"):]
            if cleaned_response.endswith("```"):
                cleaned_response = cleaned_response[:-len("```")]
            cleaned_response = cleaned_response.strip()

            # YAML parsers can be strict. If LLM output is not perfect YAML,
            # regex or line-by-line parsing might be more robust as a fallback.
            # For now, trying PyYAML
            try:
                data = yaml.safe_load(cleaned_response)
                if isinstance(data, dict):
                    parsed_data["priority_analysis"] = data.get("priority_analysis", {})
                    parsed_data["reasoning"] = data.get("reasoning", parsed_data["reasoning"])
                    parsed_data["chosen_action"] = data.get("chosen_action", parsed_data["chosen_action"])
                    parsed_data["target_coordinates"] = data.get("target_coordinates") # String like "x=1.0, y=2.0"
                    parsed_data["resulting_emotion_tag"] = data.get("resulting_emotion_tag", parsed_data["resulting_emotion_tag"])
                    logger.debug(f"Successfully parsed LLM YAML output: {parsed_data}")
                    return parsed_data
                else:
                    logger.warning(f"LLM output was not a valid YAML dictionary after cleaning: '{cleaned_response[:200]}...'")

            except yaml.YAMLError as ye:
                logger.warning(f"YAML parsing failed for LLM output: {ye}. Falling back to regex. Output: '{cleaned_response[:200]}...'")

            # Fallback to Regex if YAML parsing fails (more brittle)
            pa_block_match = re.search(r"priority_analysis:(.*?)(reasoning:)", cleaned_response, re.DOTALL | re.IGNORECASE)
            if pa_block_match:
                pa_text = pa_block_match.group(1)
                parsed_data["priority_analysis"] = {
                    "dialogue_driven": "Yes" if re.search(r"dialogue_driven:\s*Yes", pa_text, re.IGNORECASE) else "No",
                    "schedule_driven": "Yes" if re.search(r"schedule_driven:\s*Yes", pa_text, re.IGNORECASE) else "No",
                    "emotion_driven": "Yes" if re.search(r"emotion_driven:\s*Yes", pa_text, re.IGNORECASE) else "No",
                    "memory_driven": "Yes" if re.search(r"memory_driven:\s*Yes", pa_text, re.IGNORECASE) else "No",
                    "exploration_driven": "Yes" if re.search(r"exploration_driven:\s*Yes", pa_text, re.IGNORECASE) else "No",
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
        npc_id_str: str, # For logging
        current_pos: Position,
        landmarks: List[LandmarkContextInfo],
        # other_entities: List[EntityContextInfo], # Can be used to influence exploration
        bounds: SceneBoundaryInfo,
        memory_service: NPCMemoryService, # To check recently visited
        current_game_time: GameTime
    ) -> Tuple[float, float]:
        """
        Fallback strategy to find a less visited, somewhat random exploration target.
        Adapted from the original Move_Gemimi_02.py's find_least_visited_area logic.
        """
        logger.info(f"NPC '{npc_id_str}': Executing fallback exploration strategy from ({current_pos.x:.1f}, {current_pos.y:.1f}).")
        potential_targets: List[Dict[str, Any]] = []

        # 1. Consider landmarks not recently visited as high-value exploration points
        for lm in landmarks:
            dist_to_lm = current_pos.distance_to(lm.position)
            if settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT <= dist_to_lm <= settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT:
                potential_targets.append({"x": lm.position.x, "y": lm.position.y, "type": f"landmark:{lm.landmark_name}", "base_score": 75})
        
        # 2. Generate random exploration points within a reasonable annulus
        num_random_points = 15
        for i in range(num_random_points):
            angle = random.uniform(0, 2 * math.pi)
            distance = random.uniform(
                settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 1.1, # Slightly further than min
                settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT * 0.9  # Slightly less than max
            )
            x_rand = current_pos.x + distance * math.cos(angle)
            y_rand = current_pos.y + distance * math.sin(angle)
            potential_targets.append({"x": x_rand, "y": y_rand, "type": f"random_explore_{i}", "base_score": 40})
        
        # (Optional: Midpoints between landmarks, as in original code, can be added here too)

        scored_targets: List[Dict[str, Any]] = []
        if not potential_targets:
            logger.warning(f"NPC '{npc_id_str}': No potential fallback targets generated by strategies. Using emergency random within bounds.")
            # This case should be rare if random points are always generated.
            x_emergency = random.uniform(bounds.min_x + settings.SCENE_BOUNDARY_BUFFER, bounds.max_x - settings.SCENE_BOUNDARY_BUFFER)
            y_emergency = random.uniform(bounds.min_y + settings.SCENE_BOUNDARY_BUFFER, bounds.max_y - settings.SCENE_BOUNDARY_BUFFER)
            return x_emergency, y_emergency

        for pt in potential_targets:
            # Clamp to bounds first
            x_clamped, y_clamped = clamp_position_to_bounds(pt["x"], pt["y"], bounds, settings.SCENE_BOUNDARY_BUFFER)
            
            dist_from_current = current_pos.distance_to(Position(x=x_clamped, y=y_clamped))
            # Ensure the fallback point is not trivially close to current position
            if dist_from_current < settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT / 1.5: # Must be some meaningful distance away
                continue
            
            score = float(pt["base_score"])
            if await memory_service.has_been_visited_recently(x_clamped, y_clamped, current_game_time):
                score -= 250  # Heavily penalize recently visited for exploration
            else:
                score += 150  # Reward not recently visited
            
            # Prefer mid-range distances for exploration
            if dist_from_current < settings.MIN_SEARCH_DISTANCE_FOR_NEW_POINT * 2: score -= 20
            elif dist_from_current > settings.MAX_SEARCH_DISTANCE_FOR_NEW_POINT * 0.7: score -= 10
            else: score += 15
            
            score += random.uniform(-10, 10) # Add some random jitter to scores

            scored_targets.append({"x": x_clamped, "y": y_clamped, "type": pt["type"], "score": score, "dist": dist_from_current})
        
        if scored_targets:
            best_target = sorted(scored_targets, key=lambda t: t["score"], reverse=True)[0]
            logger.info(f"NPC '{npc_id_str}': Fallback strategy selected target: Type='{best_target['type']}', Coords=({best_target['x']:.1f}, {best_target['y']:.1f}), Score={best_target['score']:.1f}")
            return best_target["x"], best_target["y"]
        else:
            # If all scored targets were filtered (e.g., all too close or somehow invalid after clamping)
            logger.warning(f"NPC '{npc_id_str}': All scored fallback targets were filtered. Using emergency random point within bounds.")
            x_emergency = random.uniform(bounds.min_x + settings.SCENE_BOUNDARY_BUFFER, bounds.max_x - settings.SCENE_BOUNDARY_BUFFER)
            y_emergency = random.uniform(bounds.min_y + settings.SCENE_BOUNDARY_BUFFER, bounds.max_y - settings.SCENE_BOUNDARY_BUFFER)
            return x_emergency, y_emergency

    async def decide_npc_movement(
        self,
        request_data: NPCMovementRequest, # Contains current state from game engine
        memory_service: NPCMemoryService  # Service to access this NPC's persisted memory
    ) -> NPCMovementResponse:
        """
        Main method to determine an NPC's next movement.
        It fetches NPC's full memory, builds a prompt, calls LLM, parses response,
        applies game rules (like avoiding recent visits), and uses fallback if needed.
        """
        request_start_time = time.perf_counter()
        
        npc_id_obj = NPCIdentifier(npc_id=request_data.npc_id, name=request_data.name)
        logger.info(f"Movement decision process started for NPC '{npc_id_obj.npc_id}'.")

        # 1. Load NPC's full memory state
        npc_memory: NPCMemoryFile = await memory_service.get_memory_data()
        # Update snapshots in memory if provided in request (latest from game engine)
        npc_memory.last_known_position = request_data.current_npc_position
        npc_memory.last_known_game_time = request_data.current_game_time
        # (Personality is already in npc_memory.personality_description)

        # 2. Prepare context for the prompt builder
        # Short-term history is already part of npc_memory
        # Relevant long-term memories (example: get latest N, or implement keyword search)
        relevant_ltm = await memory_service.get_relevant_long_term_memories(limit=5) # Get up to 5 most recent LTM

        # 3. Build the prompt
        system_prompt_for_llm = build_npc_movement_decision_prompt(
            npc_info=npc_id_obj,
            personality_description=npc_memory.personality_description,
            current_position=request_data.current_npc_position,
            game_time=request_data.current_game_time,
            emotional_state=npc_memory.current_emotional_state, # Use state from memory
            active_schedule_rules=npc_memory.active_schedule_rules, # Use schedule from memory
            other_entities_nearby=request_data.nearby_entities,
            visible_landmarks=request_data.visible_landmarks,
            scene_boundaries=request_data.scene_boundaries,
            short_term_location_history=npc_memory.short_term_location_history,
            relevant_long_term_memories=relevant_ltm,
            recent_dialogue_summary=request_data.recent_dialogue_summary_for_movement,
            explicit_player_movement_request=request_data.explicit_player_movement_request
        )
        
        llm_raw_response_text: str = ""
        parsed_llm_output: Dict[str, Any] = {}
        llm_chosen_target_pos: Optional[Position] = None
        
        final_target_position: Position
        decision_context_summary: str = "Decision process initiated." # Will be updated

        try:
            effective_model = request_data.model_override or settings.DEFAULT_OLLAMA_MODEL
            llm_options = OllamaChatOptions(temperature=0.6, num_ctx=settings.DEFAULT_MAX_TOKENS, top_k=40, top_p=0.9) # Example options

            ollama_response_data = await self.ollama_service.generate_chat_completion(
                model=effective_model,
                messages=[Message(role=MessageRole.SYSTEM, content=system_prompt_for_llm)],
                stream=False,
                options=llm_options
            )
            llm_raw_response_text = ollama_response_data.get('message', {}).get('content', "")
            logger.debug(f"NPC '{npc_id_obj.npc_id}' LLM Raw Response: '{llm_raw_response_text[:300]}...'")

            parsed_llm_output = self._parse_structured_llm_movement_output(llm_raw_response_text)
            coords_str = parsed_llm_output.get("target_coordinates")
            if coords_str:
                x, y = parse_coordinates_from_text(coords_str) # From utils.helpers
                if x is not None and y is not None:
                    llm_chosen_target_pos = Position(x=x, y=y)
        
        except Exception as e: # Covers Ollama errors, connection errors, parsing errors
            logger.error(f"Error during LLM interaction or parsing for NPC '{npc_id_obj.npc_id}': {e}", exc_info=True)
            decision_context_summary = f"Error during LLM phase: {type(e).__name__}. Fallback will be used."
            # llm_chosen_target_pos remains None, fallback will be triggered

        # 4. Apply Game Logic & Fallbacks
        if llm_chosen_target_pos:
            # Clamp LLM target to bounds first
            clamped_x, clamped_y = clamp_position_to_bounds(
                llm_chosen_target_pos.x, llm_chosen_target_pos.y,
                request_data.scene_boundaries, settings.SCENE_BOUNDARY_BUFFER
            )
            llm_clamped_target_pos = Position(x=clamped_x, y=clamped_y)

            if llm_clamped_target_pos.distance_to(llm_chosen_target_pos) > 0.1: # If clamping changed it significantly
                logger.warning(f"NPC '{npc_id_obj.npc_id}': LLM target ({llm_chosen_target_pos.x:.1f},{llm_chosen_target_pos.y:.1f}) was out of bounds, clamped to ({clamped_x:.1f},{clamped_y:.1f}).")
                decision_context_summary = f"LLM target out of bounds, clamped. Reasoning: {parsed_llm_output.get('reasoning', 'N/A')}"
            
            # Check if recently visited, unless it's a high-priority move
            is_dialogue_driven = parsed_llm_output.get("priority_analysis", {}).get("dialogue_driven", "No").lower() == "yes"
            is_schedule_driven = parsed_llm_output.get("priority_analysis", {}).get("schedule_driven", "No").lower() == "yes"
            is_high_priority_llm_move = is_dialogue_driven or is_schedule_driven # Add other high-priority flags if needed

            has_visited_recently = await memory_service.has_been_visited_recently(
                llm_clamped_target_pos.x, llm_clamped_target_pos.y, request_data.current_game_time
            )

            if not has_visited_recently or is_high_priority_llm_move:
                final_target_position = llm_clamped_target_pos
                decision_context_summary = parsed_llm_output.get('reasoning', 'LLM provided target.')
                if has_visited_recently and is_high_priority_llm_move:
                     decision_context_summary += " (Note: Overriding recent visit due to high priority)."
                logger.info(f"NPC '{npc_id_obj.npc_id}': Using LLM target {final_target_position}. High Priority: {is_high_priority_llm_move}, Visited Recently: {has_visited_recently}.")
            else: # Visited recently and not high-priority
                logger.warning(f"NPC '{npc_id_obj.npc_id}': LLM target {llm_clamped_target_pos} was visited recently and not high priority. Triggering fallback exploration.")
                decision_context_summary = f"LLM target recently visited. Fallback. Original reasoning: {parsed_llm_output.get('reasoning', 'N/A')}"
                fx, fy = await self._find_fallback_exploration_target(
                    npc_id_obj.npc_id, request_data.current_npc_position, request_data.visible_landmarks,
                    request_data.scene_boundaries, memory_service, request_data.current_game_time
                )
                final_target_position = Position(x=fx, y=fy)
        else: # No valid target from LLM (error or parse failure)
            logger.warning(f"NPC '{npc_id_obj.npc_id}': No valid target from LLM. Triggering fallback exploration. Summary: {decision_context_summary}")
            fx, fy = await self._find_fallback_exploration_target(
                npc_id_obj.npc_id, request_data.current_npc_position, request_data.visible_landmarks,
                request_data.scene_boundaries, memory_service, request_data.current_game_time
            )
            final_target_position = Position(x=fx, y=fy)
            # Ensure action description reflects fallback
            parsed_llm_output["chosen_action"] = f"Fallback exploration towards ({fx:.1f}, {fy:.1f})."
            if "LLM did not provide" in decision_context_summary: # if it's still the default
                 decision_context_summary = "Fallback exploration triggered due to LLM failure to provide coordinates."


        # 5. Update NPC Memory with new state
        # Visited location (the target of this move will be "visited" when NPC arrives,
        # but we add it to short-term history now to influence immediate re-decisions if any)
        await memory_service.add_visited_location(final_target_position.x, final_target_position.y, request_data.current_game_time.current_timestamp)
        
        # Update emotional state based on LLM's suggestion
        new_emotion_tag = parsed_llm_output.get("resulting_emotion_tag", "no_change")
        updated_emotional_state_snapshot = npc_memory.current_emotional_state # Default to old state
        if new_emotion_tag and new_emotion_tag.lower() != "no_change" and new_emotion_tag.lower() != "same":
            # Assume intensity and mood_tags might also be part of LLM output in a more complex schema
            await memory_service.update_emotional_state(
                new_primary_emotion=new_emotion_tag,
                intensity=npc_memory.current_emotional_state.intensity, # Or LLM suggests new intensity
                reason=f"Decision: {parsed_llm_output.get('chosen_action', 'Unknown')}"
            )
            updated_emotional_state_snapshot = (await memory_service.get_memory_data()).emotional_state

        # Persist all changes to NPC memory (if using write-through cache or explicit save)
        # With our current NPCMemoryService._mark_dirty(), a save will be triggered on shutdown
        # or by a periodic saver if implemented. If immediate save is needed for some operations:
        # await memory_service.save_memory_to_file()


        # 6. Log detailed interaction (can be a separate method)
        self._log_movement_decision(request_data, system_prompt_for_llm, llm_raw_response_text, final_target_position, decision_context_summary)

        api_processing_time_ms = (time.perf_counter() - request_start_time) * 1000
        
        return NPCMovementResponse(
            npc_id=npc_id_obj.npc_id,
            name=npc_id_obj.name,
            llm_full_reasoning_text=parsed_llm_output.get('reasoning', llm_raw_response_text), # Provide full reasoning or raw if parsing fails
            chosen_action_summary=parsed_llm_output.get('chosen_action', "Action determined by fallback."),
            target_destination=final_target_position,
            primary_decision_drivers=parsed_llm_output.get("priority_analysis", {}),
            updated_emotional_state_snapshot=updated_emotional_state_snapshot,
            api_processing_time_ms=round(api_processing_time_ms, 2)
        )

    def _log_movement_decision(
        self, request: NPCMovementRequest, prompt: str, llm_response:str,
        final_target: Position, reason: str
    ):
        # Basic logging example, can be expanded to save to a structured log or separate file
        log_entry = {
            "timestamp": default_aware_utcnow().isoformat(),
            "npc_id": request.npc_id,
            "request_summary": {
                "current_pos": request.current_npc_position.model_dump(),
                "game_time": request.current_game_time.time_of_day,
                "dialogue_summary": request.recent_dialogue_summary_for_movement,
                "player_request": request.explicit_player_movement_request.model_dump() if request.explicit_player_movement_request else None
            },
            # "prompt_sent_to_llm": prompt, # Can be very verbose, log selectively
            "llm_raw_response": llm_response[:500] + "..." if len(llm_response) > 500 else llm_response, # Log snippet
            "decision_reason_summary": reason,
            "final_target_chosen": final_target.model_dump(),
        }
        logger.info(f"NPC Movement Log for '{request.npc_id}': Target=({final_target.x:.1f},{final_target.y:.1f}). Reason: {reason[:100]}...")
        # For more detailed debugging, could write log_entry to a dedicated JSON log file
        # logger.debug(json.dumps(log_entry, indent=2))