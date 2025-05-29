# npc_api_suite/app/llm/ollama_client.py

import ollama # type: ignore
from typing import Optional, List, Dict, Any, AsyncGenerator, Union
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.core.schemas import Message, OllamaChatOptions

logger = setup_logging(__name__)

class OllamaService:
    _client: Optional[ollama.AsyncClient] = None
    _is_initialized_successfully: bool = False

    @classmethod
    async def initialize_client(cls) -> None:
        if cls._is_initialized_successfully:
            logger.info("Ollama AsyncClient already initialized and connection verified.")
            return

        cls._client = None
        cls._is_initialized_successfully = False

        try:
            logger.info(f"Attempting to initialize Ollama AsyncClient for host: {settings.OLLAMA_HOST} with timeout {settings.OLLAMA_REQUEST_TIMEOUT}s")
            temp_client = ollama.AsyncClient(
                host=settings.OLLAMA_HOST,
                timeout=settings.OLLAMA_REQUEST_TIMEOUT
            )
            logger.debug("Ollama AsyncClient instance created.")

            try:
                logger.debug("Attempting to list models to verify Ollama connection...")
                response = await temp_client.list() 
                available_models_data = response.get("models", [])
                # Safely get model names, handling cases where 'm' might not be a dict or 'name' is missing
                model_names = []
                if isinstance(available_models_data, list):
                    for m in available_models_data:
                        if isinstance(m, dict) and m.get('name'):
                            model_names.append(m.get('name'))
                        else:
                            model_names.append(None) # Keep the None for logging consistency if parsing fails per item

                logger.info(f"Successfully connected to Ollama. Available models: {model_names}")
                
                cls._client = temp_client
                cls._is_initialized_successfully = True
                logger.info("Ollama AsyncClient initialization and connection verification SUCCEEDED.")

            except ollama.ResponseError as e_api: 
                logger.error(f"Ollama API error during initial model list (host: {settings.OLLAMA_HOST}): {e_api.status_code} - {e_api.error}", exc_info=True)
            except Exception as e_connect: 
                logger.error(f"Connection or other error during initial model list (host: {settings.OLLAMA_HOST}): {e_connect}", exc_info=True)
        
        except Exception as e_create: 
            logger.error(f"Failed to create Ollama AsyncClient instance (host: {settings.OLLAMA_HOST}): {e_create}", exc_info=True)

    @classmethod
    async def close_client(cls) -> None:
        if cls._client:
            logger.info("Ollama AsyncClient is being 'closed' (client instance will be reset).")
            # httpx.AsyncClient (used by ollama.AsyncClient) has an aclose() method
            try:
                if hasattr(cls._client, '_client') and cls._client._client is not None: # Access underlying httpx client
                    await cls._client._client.aclose() # type: ignore
                    logger.debug("Underlying httpx.AsyncClient aclosed.")
            except Exception as e_aclose:
                logger.warning(f"Error trying to aclose underlying httpx client: {e_aclose}")
            cls._client = None
        cls._is_initialized_successfully = False

    @classmethod
    def get_client(cls) -> ollama.AsyncClient:
        if not cls._is_initialized_successfully or cls._client is None:
            logger.error("Ollama AsyncClient requested but is not available or initialization failed.")
            raise ConnectionError("Ollama service client is not available. Please ensure it's initialized and successfully connected.")
        return cls._client

    @classmethod
    def is_ready(cls) -> bool:
        return cls._is_initialized_successfully and cls._client is not None

    @classmethod
    async def list_available_models(cls, log_success: bool = False) -> List[Dict[str, Any]]:
        client = cls.get_client() 
        try:
            response = await client.list()
            available_models_data = response.get("models", [])
            if log_success: 
                model_names = [m.get('name') for m in available_models_data if isinstance(m, dict)]
                logger.info(f"Ollama models listed successfully: {model_names}")
            return available_models_data if isinstance(available_models_data, list) else []
        except ollama.ResponseError as e: 
            logger.error(f"Ollama API error when listing models: {e.status_code} - {e.error}", exc_info=True)
            raise
        except Exception as e:
            logger.error(f"Unexpected error listing Ollama models: {e}", exc_info=True)
            raise
            
    @classmethod
    async def generate_chat_completion(
        cls,
        model: Optional[str],
        messages: List[Message], # Expects Pydantic Message objects
        stream: bool = False,
        options: Optional[OllamaChatOptions] = None # Expects Pydantic OllamaChatOptions object
    ) -> Union[Dict[str, Any], AsyncGenerator[Dict[str, Any], None]]:
        client = cls.get_client() 
        
        formatted_messages: List[Dict[str, Any]] = []
        for msg in messages:
            # msg is expected to be a Pydantic Message object
            msg_dict: Dict[str, Any] = {"role": msg.role.value, "content": msg.content}
            if msg.name:
                msg_dict["name"] = msg.name
            formatted_messages.append(msg_dict)

        effective_model = model if model else settings.DEFAULT_OLLAMA_MODEL
        if not effective_model:
             logger.error("No Ollama model specified for chat completion and no default model is configured.")
             raise ValueError("Ollama model name must be provided or a default set in configuration.")

        ollama_options_dict: Optional[Dict[str, Any]] = None
        if options: # options is an OllamaChatOptions object
            ollama_options_dict = options.model_dump(exclude_none=True)
            if "num_ctx" not in ollama_options_dict and settings.DEFAULT_MAX_TOKENS > 0 :
                 ollama_options_dict["num_ctx"] = settings.DEFAULT_MAX_TOKENS
        elif settings.DEFAULT_MAX_TOKENS > 0 :
             ollama_options_dict= {"num_ctx": settings.DEFAULT_MAX_TOKENS}

        logger.debug(f"Sending chat request to Ollama. Model: {effective_model}, Messages count: {len(formatted_messages)}, Stream: {stream}, Options: {ollama_options_dict}")
        
        try:
            response_generator_or_typed_dict = await client.chat( # Renamed variable for clarity
                model=effective_model,
                messages=formatted_messages, # type: ignore # ollama library expects List[Dict]
                stream=stream,
                options=ollama_options_dict
            )
            
            if stream:
                async def stream_wrapper() -> AsyncGenerator[Dict[str, Any], None]:
                    try:
                        async for chunk in response_generator_or_typed_dict: # type: ignore
                            yield chunk # chunk is already a dict
                    except ollama.ResponseError as e_stream:
                        logger.error(f"Ollama API error during stream (model: {effective_model}): {e_stream.status_code} - {e_stream.error}", exc_info=True)
                        raise 
                    except Exception as e_stream_unexpected:
                        logger.error(f"Unexpected error during Ollama stream (model: {effective_model}): {e_stream_unexpected}", exc_info=True)
                        raise
                return stream_wrapper()
            else: # stream == False
                # The ollama.chat() function when stream=False returns an ollama._types.ChatResponse (which is a TypedDict).
                # This object behaves like a dictionary and can be returned directly.
                # The check `isinstance(response_generator_or_typed_dict, dict)` was too restrictive.
                if response_generator_or_typed_dict is None:
                    logger.error(f"Ollama non-streamed response was unexpectedly None for model {effective_model}.")
                    return {"model": effective_model, "message": {"role": "assistant", "content": "[LLM response was None]"}, "done": True} # Return a valid-like structure
                
                # Ensure it's dict-like and has the 'message' key, which callers expect.
                # Note: ollama._types.ChatResponse is a TypedDict, so direct key access is fine.
                if 'message' not in response_generator_or_typed_dict:
                    logger.error(f"Ollama non-streamed response for model {effective_model} is missing 'message' key. Got: {response_generator_or_typed_dict}")
                    return {"model": effective_model, "message": {"role": "assistant", "content": "[LLM response missing 'message' field]"}, "done": True, "error_details": "Response structure incorrect from Ollama."}

                # The variable response_generator_or_typed_dict is of type ollama._types.ChatResponse here
                # It can be returned as is because it's compatible with Dict[str, Any] for callers.
                return response_generator_or_typed_dict
        except ollama.ResponseError as e: 
            logger.error(f"Ollama API error (model: {effective_model}): {e.status_code} - {e.error}", exc_info=True)
            raise
        except ConnectionError: 
            raise
        except Exception as e:
            logger.error(f"Unexpected error during Ollama chat (model: {effective_model}): {e}", exc_info=True)
            raise

async def get_ollama_service() -> OllamaService:
    if not OllamaService.is_ready():
        logger.warning("get_ollama_service dependency called when OllamaService is not ready. Lifespan may have failed to initialize client properly.")
    return OllamaService