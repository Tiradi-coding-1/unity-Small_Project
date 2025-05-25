# npc_api_suite/app/services/dialogue_service.py

from typing import List, Optional, Tuple, AsyncGenerator, Dict, Any
from app.core.schemas import (
    GameInteractionRequest, DialogueTurn, InteractingObjectInfo, Message, MessageRole,
    NPCIdentifier, OllamaChatOptions, StandardChatRequest, SimpleChatRequest, ChatResponse,
    default_aware_utcnow # For timestamps
)
from app.llm.ollama_client import OllamaService
from app.llm.prompt_builder import build_dialogue_system_prompt, build_translation_prompt_messages
from app.llm.translators import TextTranslatorService # 我們之前設計的翻譯服務
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
import uuid
from datetime import datetime, timezone
import time
import ollama # For ollama.ResponseError

logger = setup_logging(__name__)

class DialogueService:
    """
    Service class for managing dialogue generation, including multi-object interactions
    and single NPC chat sessions. It uses OllamaService for LLM calls and
    TextTranslatorService for translations.
    """

    def __init__(self, ollama_s: OllamaService, translator_s: TextTranslatorService):
        """
        Initializes the DialogueService.

        Args:
            ollama_s: An instance of OllamaService for LLM interactions.
            translator_s: An instance of TextTranslatorService for text translation.
        """
        self.ollama_service = ollama_s
        self.translator_service = translator_s

    async def _generate_and_translate_turn(
        self,
        npc_info: InteractingObjectInfo,
        effective_model: str,
        messages_for_llm: List[Message],
        ollama_options: Optional[OllamaChatOptions] = None
    ) -> DialogueTurn:
        """
        Helper to generate a single turn's response from LLM and translate it.
        """
        turn_timestamp = default_aware_utcnow()
        llm_response_data: Optional[Dict[str, Any]] = None
        generated_message_original = "[Error: LLM response not obtained]"
        model_actually_used = effective_model
        ollama_created_at = turn_timestamp # Fallback
        llm_generated_emotional_tone = None # Placeholder

        try:
            ollama_options_dict = ollama_options.model_dump(exclude_none=True) if ollama_options else None
            llm_response_data = await self.ollama_service.generate_chat_completion(
                model=effective_model,
                messages=messages_for_llm,
                stream=False, # Game interactions are typically non-streamed per turn
                options=ollama_options_dict
            )
            
            generated_message_original = llm_response_data.get('message', {}).get('content', "[LLM provided no content]").strip()
            model_actually_used = llm_response_data.get('model', effective_model)
            created_at_str = llm_response_data.get('created_at')
            if created_at_str:
                try:
                    ollama_created_at = datetime.fromisoformat(created_at_str.replace("Z", "+00:00"))
                    if ollama_created_at.tzinfo is None: # Ensure tz_aware
                         ollama_created_at = ollama_created_at.replace(tzinfo=timezone.utc)
                except ValueError:
                    logger.warning(f"Could not parse Ollama 'created_at' timestamp: {created_at_str}")

            # TODO: Extract emotional tone if LLM is prompted to provide it
            # For example, if the LLM's response is structured (e.g., JSON or YAML)
            # llm_generated_emotional_tone = parsed_llm_response.get("emotional_tone")

        except ollama.ResponseError as e:
            logger.error(f"Ollama API error for NPC '{npc_info.npc_id}' using model '{effective_model}': {e.status_code} - {e.error}", exc_info=True)
            generated_message_original = f"[LLM Error: {e.error[:100] if e.error else 'Unknown Ollama Error'}]" # Truncate long errors
        except ConnectionError as e:
            logger.error(f"Ollama connection error for NPC '{npc_info.npc_id}': {e}", exc_info=True)
            generated_message_original = "[LLM Error: Connection Failed]"
        except Exception as e:
            logger.error(f"Unexpected error generating turn for NPC '{npc_info.npc_id}': {e}", exc_info=True)
            generated_message_original = f"[LLM Error: Unexpected {type(e).__name__}]"

        # Translate the original message
        translated_message_zh_tw = await self.translator_service.translate_text(
            text_to_translate=generated_message_original,
            # source_language, target_language, translation_model_override can be added if needed
        )

        return DialogueTurn(
            npc_id=npc_info.npc_id,
            name=npc_info.name,
            message_original_language=generated_message_original,
            message_translated_zh_tw=translated_message_zh_tw,
            model_used=model_actually_used,
            timestamp_api_generated=turn_timestamp, # When this DialogueTurn object was created by API
            # llm_generated_emotional_tone=llm_generated_emotional_tone # Add if implemented
        )

    async def generate_interactive_dialogue(
        self,
        request: GameInteractionRequest
    ) -> Tuple[str, List[DialogueTurn]]:
        """
        Handles dialogue generation for multi-object interactions.
        Each object speaks `max_turns_per_object` times, in sequence.
        The conversation history accumulates.
        """
        interaction_session_id = request.interaction_id_to_continue or f"gi_{uuid.uuid4().hex[:10]}"
        logger.info(f"Game interaction session '{interaction_session_id}' for {len(request.interacting_objects)} objects, max {request.max_turns_per_object} turns each.")

        dialogue_turns_history: List[DialogueTurn] = []
        
        # This will hold the history of messages as Pydantic Message objects for LLM context
        # It should only contain original language messages.
        llm_context_messages: List[Message] = []

        if request.scene_context_description:
            llm_context_messages.append(Message(role=MessageRole.SYSTEM, content=f"Overall scene context: {request.scene_context_description}"))
        if request.game_time_context:
             time_str = request.game_time_context.current_timestamp.strftime('%Y-%m-%d %H:%M %Z')
             llm_context_messages.append(Message(role=MessageRole.SYSTEM, content=f"Current game time: {request.game_time_context.time_of_day} ({time_str})."))


        for turn_num in range(request.max_turns_per_object):
            for current_object_config in request.interacting_objects:
                turn_processing_start_time = time.perf_counter()
                
                # Prepare system prompt for the current object
                # Interacting_with_entities should be all *other* objects in this interaction
                other_objects_for_prompt = [
                    NPCIdentifier(npc_id=obj.npc_id, name=obj.name)
                    for obj in request.interacting_objects if obj.npc_id != current_object_config.npc_id
                ]
                system_prompt_content = build_dialogue_system_prompt(
                    npc_info=NPCIdentifier(npc_id=current_object_config.npc_id, name=current_object_config.name),
                    interacting_with_entities=other_objects_for_prompt,
                    # scene_context_description is part of llm_context_messages now
                    dialogue_mode_tag=current_object_config.dialogue_mode_tag,
                    npc_emotional_state_input=current_object_config.emotional_state_input,
                    additional_dialogue_goal=None # Can be parameterized if needed
                )
                
                # Messages for this specific LLM call: system prompt + current history + user prompt for this turn
                messages_for_this_llm_call: List[Message] = [Message(role=MessageRole.SYSTEM, content=system_prompt_content)]
                messages_for_this_llm_call.extend(llm_context_messages) # Add the accumulated history

                # Determine the "user" prompt for the LLM for this object's turn
                # This could be the object's initial_prompt_to_llm, or a generic "What do you do/say?"
                user_prompt_for_this_turn = current_object_config.initial_prompt_to_llm
                if not user_prompt_for_this_turn:
                    if not llm_context_messages: # If it's the very first utterance in the interaction
                        user_prompt_for_this_turn = f"You are '{current_object_config.name or current_object_config.npc_id}'. The interaction begins. What do you say or do?"
                    else:
                        last_speaker_msg = llm_context_messages[-1] if llm_context_messages else None
                        if last_speaker_msg and last_speaker_msg.role == MessageRole.ASSISTANT : # Responding to another assistant
                             last_speaker_name = last_speaker_msg.name or "The previous character"
                             user_prompt_for_this_turn = f"{last_speaker_name} just spoke. Now it's your turn, '{current_object_config.name or current_object_config.npc_id}'. What is your response or action?"
                        else: # Generic prompt if last was system or user (player)
                             user_prompt_for_this_turn = f"It's your turn, '{current_object_config.name or current_object_config.npc_id}'. What do you say or do?"

                messages_for_this_llm_call.append(Message(role=MessageRole.USER, content=user_prompt_for_this_turn))

                effective_model = current_object_config.model_override or settings.DEFAULT_OLLAMA_MODEL
                
                # Generate and translate the turn
                generated_turn: DialogueTurn = await self._generate_and_translate_turn(
                    npc_info=current_object_config, # Pass the full InteractingObjectInfo
                    effective_model=effective_model,
                    messages_for_llm=messages_for_this_llm_call
                    # ollama_options can be passed from current_object_config if defined in its schema
                )
                
                turn_processing_time_ms = (time.perf_counter() - turn_processing_start_time) * 1000
                generated_turn.turn_processing_time_ms = round(turn_processing_time_ms, 2)
                dialogue_turns_history.append(generated_turn)

                # Add this turn's actual outcome to the LLM context for the *next* turn/object
                # Important: Use the *original language* message for the LLM context.
                # We represent the "user" prompt that led to this turn, and then the "assistant" (this NPC's) response.
                # This helps the next LLM understand what this NPC was responding to.
                if not generated_message_original.startswith("[LLM Error:") and not generated_message_original.startswith("[Translation"): # Only add valid responses to history
                    llm_context_messages.append(Message(
                        role=MessageRole.USER, # Or System, to indicate what the NPC was prompted with
                        content=f"({current_object_config.name or current_object_config.npc_id} was prompted with: '{user_prompt_for_this_turn}')"
                    ))
                    llm_context_messages.append(Message(
                        role=MessageRole.ASSISTANT,
                        content=generated_turn.message_original_language,
                        name=current_object_config.name or current_object_config.npc_id # Name the assistant for multi-agent
                    ))
                else: # If there was an error, add a placeholder to history to maintain turn structure
                    llm_context_messages.append(Message(role=MessageRole.SYSTEM, content=f"({current_object_config.name or current_object_config.npc_id} encountered an error and could not respond meaningfully.)"))


                # Context window management: If llm_context_messages gets too long, truncate older messages
                # This is a simple truncation, more sophisticated summarization could be used.
                # A good num_ctx for LLM might be settings.DEFAULT_MAX_TOKENS.
                # We need to estimate token count or message count. For simplicity, let's use message count.
                # Keep, e.g., last 20 messages (10 turns of user/assistant) + initial system messages.
                MAX_CONTEXT_MESSAGES = 30 # Example
                if len(llm_context_messages) > MAX_CONTEXT_MESSAGES:
                    initial_system_count = 0
                    if request.scene_context_description: initial_system_count +=1
                    if request.game_time_context: initial_system_count+=1
                    
                    messages_to_keep = llm_context_messages[:initial_system_count] + \
                                       llm_context_messages[-(MAX_CONTEXT_MESSAGES - initial_system_count):]
                    llm_context_messages = messages_to_keep
                    logger.debug(f"LLM context history truncated to {len(llm_context_messages)} messages for session '{interaction_session_id}'.")


        return interaction_session_id, dialogue_turns_history

    async def generate_standard_chat_response(
        self,
        request: StandardChatRequest,
    ) -> ChatResponse:
        """
        Handles a standard chat request with a list of messages.
        """
        start_time = time.perf_counter()
        logger.info(f"Standard chat request (ID: {request.conversation_id}) for model: {request.model or settings.DEFAULT_OLLAMA_MODEL}")

        if request.stream:
            # Streaming for this endpoint needs to be handled by the router,
            # which would iterate over an async generator returned by OllamaService.
            # This service method might return the generator directly.
            # For now, this example focuses on non-streamed for simplicity in service layer.
            raise NotImplementedError("Streaming for /chat/standard not fully implemented in DialogueService yet.")

        try:
            ollama_options_dict = request.options.model_dump(exclude_none=True) if request.options else None
            response_data = await self.ollama_service.generate_chat_completion(
                model=request.model or settings.DEFAULT_OLLAMA_MODEL,
                messages=request.messages,
                stream=False, # Explicitly non-streamed for this path
                options=ollama_options_dict
            )
            
            api_processing_time_ms = (time.perf_counter() - start_time) * 1000
            
            created_at_dt = default_aware_utcnow()
            if response_data.get('created_at'):
                 try:
                     created_at_dt = datetime.fromisoformat(response_data['created_at'].replace("Z", "+00:00"))
                     if created_at_dt.tzinfo is None: created_at_dt = created_at_dt.replace(tzinfo=timezone.utc)
                 except ValueError:
                     logger.warning(f"Could not parse Ollama 'created_at' from standard chat: {response_data.get('created_at')}")


            return ChatResponse(
                response=response_data.get('message', {}).get('content', "[No content from LLM]"),
                model_used=response_data.get('model', request.model or settings.DEFAULT_OLLAMA_MODEL),
                created_at=created_at_dt,
                api_processing_time_ms=round(api_processing_time_ms, 2),
                ollama_processing_duration_ns=response_data.get('total_duration'), # total_duration is often in ns
                conversation_id=request.conversation_id,
                done_reason=response_data.get('done_reason')
            )
        except Exception as e: # Catch specific errors if needed (OllamaResponseError, ConnectionError)
            logger.error(f"Error in generate_standard_chat_response: {e}", exc_info=True)
            # Re-raise or return a custom error response object if your API spec defines one
            raise # Let global exception handlers in main.py catch this for now


    # Similar method for SimpleChatRequest can be added if needed.