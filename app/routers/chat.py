# npc_api_suite/app/routers/chat.py

from fastapi import APIRouter, Depends, HTTPException, status, Body, Query
from fastapi.responses import StreamingResponse # For streaming chat if implemented
from typing import List, Dict, Any, AsyncGenerator # For streaming
import time
import ollama # For ollama.ResponseError

from app.core.schemas import (
    StandardChatRequest, SimpleChatRequest, ChatResponse,
    GameInteractionRequest, GameInteractionResponse,
    ListOllamaModelsResponse, OllamaModelInfo, # Ensure these are defined in schemas.py
    Message, MessageRole # For potential streaming construction
)
from app.llm.ollama_client import OllamaService, get_ollama_service
from app.llm.translators import TextTranslatorService
from app.services.dialogue_service import DialogueService
from app.core.logging_config import setup_logging
from app.core.config import settings_instance as settings
from datetime import datetime, timezone # For default_aware_utcnow in ChatResponse

logger = setup_logging(__name__) # Logger for this router

router = APIRouter(
    prefix="/dialogue", # Changed prefix to be more descriptive
    tags=["Dialogue Engine & LLM Interaction"]
)

# --- Dependency Injection for DialogueService ---
async def get_dialogue_service(
    ollama_s: OllamaService = Depends(get_ollama_service),
    # translator_s can also be a separate dependency if preferred
) -> DialogueService:
    """Dependency to get an instance of DialogueService."""
    # TextTranslatorService is instantiated here, using the shared OllamaService
    translator_instance = TextTranslatorService(ollama_service=ollama_s)
    return DialogueService(ollama_s=ollama_s, translator_s=translator_instance)

# --- API Endpoints ---

@router.post("/standard-chat", response_model=ChatResponse)
async def handle_standard_chat(
    request: StandardChatRequest,
    dialogue_s: DialogueService = Depends(get_dialogue_service) # Use DialogueService
):
    """
    Handles a standard chat request with a list of messages.
    (Currently non-streaming, see DialogueService for streaming TODOs)
    """
    request_start_time = time.perf_counter()
    logger.info(f"Standard chat request (ID: {request.conversation_id}) received for model: {request.model or settings.DEFAULT_OLLAMA_MODEL}")

    if request.stream:
        # TODO: Implement actual streaming response.
        # This would involve DialogueService returning an AsyncGenerator,
        # and this router yielding from it.
        logger.warning("Streaming output for /standard-chat is not fully implemented in this example. Processing as non-streamed.")
        # For now, we will process it as non-streamed. If streaming is critical,
        # the DialogueService's generate_standard_chat_response would need modification.
        # Example of how streaming might be initiated (conceptual):
        # async def stream_ollama_response():
        #     async for chunk in dialogue_s.stream_standard_chat(request):
        #         yield f"data: {json.dumps(chunk.model_dump())}\n\n" # Server-Sent Events format
        # return StreamingResponse(stream_ollama_response(), media_type="text/event-stream")
        # For now, let's fall back to non-streaming or raise an error.
        # Fallback to non-streaming for this example:
        request.stream = False # Force non-streaming


    try:
        # Assuming DialogueService.generate_standard_chat_response handles non-streaming for now
        chat_response_obj: ChatResponse = await dialogue_s.generate_standard_chat_response(request)
        
        # The processing time is now calculated within DialogueService for more accuracy
        # but we can log the total router handling time here.
        router_handling_time_ms = (time.perf_counter() - request_start_time) * 1000
        logger.info(f"Standard chat request (ID: {request.conversation_id}) processed. Router time: {router_handling_time_ms:.2f}ms. LLM time: {chat_response_obj.api_processing_time_ms:.2f}ms.")
        
        return chat_response_obj

    except NotImplementedError as nie: # If streaming was attempted and not implemented
        logger.error(f"Feature not implemented: {nie}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_501_NOT_IMPLEMENTED, detail=str(nie))
    except ConnectionError as e:
        logger.error(f"Ollama connection error in /standard-chat: {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=f"Ollama service unavailable: {str(e)}")
    except ollama.ResponseError as e:
        logger.error(f"Ollama API error in /standard-chat (model: {request.model}): Status {e.status_code} - {e.error}", exc_info=True)
        raise HTTPException(status_code=e.status_code or status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"Ollama API error: {e.error}")
    except Exception as e:
        logger.error(f"Unexpected error in /standard-chat (model: {request.model}): {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"An unexpected error occurred: {type(e).__name__}")


@router.post("/simple-chat", response_model=ChatResponse)
async def handle_simple_chat(
    request: SimpleChatRequest,
    dialogue_s: DialogueService = Depends(get_dialogue_service) # Use DialogueService
):
    """
    Handles a simple chat request with a system prompt and a user prompt.
    (Currently non-streaming)
    """
    request_start_time = time.perf_counter()
    logger.info(f"Simple chat request (ID: {request.conversation_id}) for model: {request.model or settings.DEFAULT_OLLAMA_MODEL}")

    if request.stream:
        logger.warning("Streaming output for /simple-chat is not fully implemented. Processing as non-streamed.")
        request.stream = False # Force non-streaming

    # Convert SimpleChatRequest to a StandardChatRequest structure for the service
    messages_for_llm: List[Message] = []
    if request.system_prompt:
        messages_for_llm.append(Message(role=MessageRole.SYSTEM, content=request.system_prompt))
    messages_for_llm.append(Message(role=MessageRole.USER, content=request.user_prompt))

    standard_chat_req_equivalent = StandardChatRequest(
        model=request.model,
        options=request.options,
        stream=request.stream, # Will be False based on above
        messages=messages_for_llm,
        conversation_id=request.conversation_id
    )

    try:
        chat_response_obj: ChatResponse = await dialogue_s.generate_standard_chat_response(standard_chat_req_equivalent)
        
        router_handling_time_ms = (time.perf_counter() - request_start_time) * 1000
        logger.info(f"Simple chat request (ID: {request.conversation_id}) processed. Router time: {router_handling_time_ms:.2f}ms. LLM time: {chat_response_obj.api_processing_time_ms:.2f}ms.")
        
        return chat_response_obj
    except Exception as e: # Catch-all, specific errors handled similarly to /standard-chat
        logger.error(f"Error in /simple-chat (model: {request.model}): {e}", exc_info=True)
        if isinstance(e, ConnectionError):
             raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=f"Ollama service unavailable: {str(e)}")
        if isinstance(e, ollama.ResponseError):
             raise HTTPException(status_code=e.status_code or status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"Ollama API error: {e.error}")
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"An unexpected error occurred in simple chat: {type(e).__name__}")


@router.post("/game-interaction", response_model=GameInteractionResponse)
async def handle_game_interaction(
    request: GameInteractionRequest,
    dialogue_s: DialogueService = Depends(get_dialogue_service)
):
    """
    Handles dialogue initiation and progression between multiple game objects.
    Includes translation of responses.
    """
    request_start_time = time.perf_counter()
    logger.info(f"Game interaction request for {len(request.interacting_objects)} objects, max {request.max_turns_per_object} turns each.")
    
    try:
        interaction_id, dialogue_history = await dialogue_s.generate_interactive_dialogue(request)
        
        total_api_processing_time_ms = (time.perf_counter() - request_start_time) * 1000
        logger.info(f"Game interaction session '{interaction_id}' processed in {total_api_processing_time_ms:.0f}ms total API time.")
        
        return GameInteractionResponse(
            interaction_session_id=interaction_id,
            dialogue_history=dialogue_history,
            total_api_processing_time_ms=round(total_api_processing_time_ms, 2)
        )
    except ConnectionError as e:
        logger.error(f"Ollama connection error in /game-interaction: {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=f"Ollama service unavailable: {str(e)}")
    except Exception as e:
        logger.error(f"Unexpected error in /game-interaction: {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"Failed to process game interaction: {type(e).__name__}")


@router.get("/list-models", response_model=ListOllamaModelsResponse)
async def list_available_ollama_models(
    ollama_s: OllamaService = Depends(get_ollama_service)
):
    """
    Lists all models available to the configured Ollama service.
    """
    try:
        logger.info("Request received for /list-models")
        models_data = await ollama_s.list_available_models(log_success=False) # Success already logged on init
        
        # Convert raw data to Pydantic models
        pydantic_models: List[OllamaModelInfo] = []
        for model_dict in models_data:
            try:
                # Ollama's modified_at is ISO string, Pydantic will convert to datetime
                pydantic_models.append(OllamaModelInfo(**model_dict))
            except Exception as e_model_parse:
                logger.warning(f"Could not parse model data for '{model_dict.get('name')}': {e_model_parse}. Skipping this model.")
        
        return ListOllamaModelsResponse(models=pydantic_models)
    except ConnectionError as e:
        logger.error(f"Ollama connection error when listing models: {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=f"Ollama service unavailable: {str(e)}")
    except Exception as e:
        logger.error(f"Failed to list Ollama models: {e}", exc_info=True)
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail="Could not retrieve models from Ollama service.")