# npc_api_suite/app/llm/ollama_client.py

import ollama # type: ignore
from typing import Optional, List, Dict, Any, AsyncGenerator, Union
from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.core.schemas import Message, OllamaChatOptions
from datetime import datetime, timezone

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
                response_data = await temp_client.list() 
                available_models_list = response_data.get("models", [])
                
                model_names = []
                if isinstance(available_models_list, list) and available_models_list:
                    for i, model_info_dict in enumerate(available_models_list):
                        model_name_tag = None
                        if isinstance(model_info_dict, dict):
                            # MODIFIED: Log all keys if 'name' and 'model' are not found or empty
                            model_name_tag = model_info_dict.get('name')
                            if not model_name_tag: 
                                model_name_tag = model_info_dict.get('model')
                            
                            if not model_name_tag: # If still not found, log keys
                                logger.warning(f"Ollama model data at index {i} missing 'name'/'model'. Keys: {list(model_info_dict.keys())}. Data: {str(model_info_dict)[:200]}...")
                        else:
                            logger.warning(f"Ollama model data at index {i} is not a dict. Type: {type(model_info_dict)}. Data: {str(model_info_dict)[:200]}...")

                        if model_name_tag:
                            model_names.append(model_name_tag)
                        else:
                            # Warning already logged above if keys were missing from dict
                            # or if it wasn't a dict.
                            model_names.append(None)
                elif not available_models_list and isinstance(available_models_list, list):
                     logger.info("Ollama reports no models are currently available/downloaded via client.list().")
                else:
                    logger.warning(f"Ollama 'models' field from client.list() is not a list or is missing. Response: {response_data}")

                valid_model_names = [name for name in model_names if name is not None]
                if not valid_model_names:
                    logger.warning(
                        f"No valid Ollama models found after parsing client.list() response. "
                        f"Parsed names (including None for errors): {model_names}. "
                        f"Please ensure Ollama is running, models are pulled (e.g., 'ollama pull {settings.DEFAULT_OLLAMA_MODEL}'), "
                        f"and the OLLAMA_HOST ('{settings.OLLAMA_HOST}') is correct."
                    )
                else:
                    logger.info(f"Successfully connected to Ollama. Available models from client.list(): {valid_model_names}")
                
                cls._client = temp_client
                cls._is_initialized_successfully = True 
                logger.info("Ollama AsyncClient initialization and connection verification process completed.")

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
            try:
                if hasattr(cls._client, '_client') and cls._client._client is not None: 
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
            response_data = await client.list()
            available_models_list = response_data.get("models", [])
            
            if not isinstance(available_models_list, list):
                logger.warning(f"Ollama list models API did not return a list under 'models' key. Got: {type(available_models_list)}")
                return []

            if log_success: 
                model_names_for_log = []
                for model_info in available_models_list: 
                    name_tag = None
                    if isinstance(model_info, dict):
                        name_tag = model_info.get('name') or model_info.get('model')
                    # Removed hasattr checks as they are less reliable for TypedDicts if structure is fixed.
                    # The primary check is isinstance(dict) and .get()
                    if name_tag:
                        model_names_for_log.append(name_tag)
                
                if not model_names_for_log and available_models_list:
                     logger.warning(f"Ollama models listed but names/model tags could not be extracted. Data: {available_models_list}")
                elif model_names_for_log :
                     logger.info(f"Ollama models listed successfully: {model_names_for_log}")
                else: 
                     logger.info("Ollama reports no models available via list API.")
            return available_models_list 
            
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
        messages: List[Message], 
        stream: bool = False,
        options: Optional[OllamaChatOptions] = None 
    ) -> Union[Dict[str, Any], AsyncGenerator[Dict[str, Any], None]]:
        client = cls.get_client() 
        
        formatted_messages: List[Dict[str, Any]] = []
        for msg in messages:
            msg_dict: Dict[str, Any] = {"role": msg.role.value, "content": msg.content}
            if msg.name:
                msg_dict["name"] = msg.name
            formatted_messages.append(msg_dict)

        effective_model = model if model else settings.DEFAULT_OLLAMA_MODEL
        if not effective_model:
             logger.error("No Ollama model specified for chat completion and no default model is configured.")
             raise ValueError("Ollama model name must be provided or a default set in configuration.")

        ollama_options_dict: Optional[Dict[str, Any]] = None
        if options: 
            ollama_options_dict = options.model_dump(exclude_none=True)
            if "num_ctx" not in ollama_options_dict and settings.DEFAULT_MAX_TOKENS > 0 :
                 ollama_options_dict["num_ctx"] = settings.DEFAULT_MAX_TOKENS
        elif settings.DEFAULT_MAX_TOKENS > 0 :
             ollama_options_dict= {"num_ctx": settings.DEFAULT_MAX_TOKENS}

        logger.debug(f"Sending chat request to Ollama. Model: {effective_model}, Messages count: {len(formatted_messages)}, Stream: {stream}, Options: {ollama_options_dict}")
        
        try:
            chat_response_obj = await client.chat( 
                model=effective_model,
                messages=formatted_messages, # type: ignore
                stream=stream,
                options=ollama_options_dict
            )
            
            if stream: 
                async def stream_wrapper() -> AsyncGenerator[Dict[str, Any], None]:
                    try:
                        async for chunk in chat_response_obj: # type: ignore
                            yield chunk 
                    except ollama.ResponseError as e_stream:
                        logger.error(f"Ollama API error during stream (model: {effective_model}): {e_stream.status_code} - {e_stream.error}", exc_info=True)
                        raise 
                    except Exception as e_stream_unexpected:
                        logger.error(f"Unexpected error during Ollama stream (model: {effective_model}): {e_stream_unexpected}", exc_info=True)
                        raise
                return stream_wrapper()
            else: 
                if chat_response_obj is None:
                    logger.error(f"Ollama non-streamed response was unexpectedly None for model {effective_model}.")
                    return {
                        "model": effective_model, 
                        "created_at": datetime.now(timezone.utc).isoformat(), 
                        "message": {"role": "assistant", "content": "[LLM response was None]"}, 
                        "done": True,
                        "done_reason": "error_empty_response"
                    }
                
                # MODIFIED: Treat empty content as a valid (but empty) response from LLM for now.
                # The service layer (MovementService) will then parse this empty content.
                message_field = chat_response_obj.get('message')
                if not isinstance(message_field, dict): # Check if 'message' itself is a dict
                    logger.error(f"Ollama non-streamed response for model {effective_model} has an invalid 'message' field (not a dict). Message field type: {type(message_field)}. Response dump: {str(chat_response_obj)[:300]}...")
                    return {
                        "model": effective_model,
                        "created_at": chat_response_obj.get('created_at', datetime.now(timezone.utc).isoformat()), # type: ignore
                        "message": {"role": "assistant", "content": "[LLM response: 'message' field invalid]"},
                        "done": chat_response_obj.get('done', True), # type: ignore
                        "done_reason": chat_response_obj.get('done_reason', 'error_malformed_message_field') # type: ignore
                    }
                
                # 'content' key must exist in message_field, even if its value is an empty string.
                # If 'content' key itself is missing, that's a structural problem.
                if 'content' not in message_field:
                    logger.error(f"Ollama non-streamed response for model {effective_model}, 'message' field is missing 'content' key. Message field: {message_field}. Response dump: {str(chat_response_obj)[:300]}...")
                    return {
                        "model": effective_model,
                        "created_at": chat_response_obj.get('created_at', datetime.now(timezone.utc).isoformat()), # type: ignore
                        "message": {"role": "assistant", "content": "[LLM response: 'message' missing 'content']"},
                        "done": chat_response_obj.get('done', True), # type: ignore
                        "done_reason": chat_response_obj.get('done_reason', 'error_message_missing_content_key') # type: ignore
                    }

                # If we reach here, 'message' is a dict and 'content' key exists.
                # The content might be an empty string, which is what the log showed.
                # We will now pass this chat_response_obj as is.
                # The MovementService will get chat_response_obj['message']['content'], which might be "".
                
                # Log if content is empty for debugging purposes
                if not message_field.get('content'): # Checks for None or empty string
                     logger.warning(f"Ollama non-streamed response for model {effective_model} has an empty 'content' in 'message' field. Message: {message_field}")
                
                return chat_response_obj # type: ignore 
        except ollama.ResponseError as e: 
            logger.error(f"Ollama API error (model: {effective_model}): {e.status_code} - {e.error}", exc_info=True)
            raise
        except ConnectionError: 
            raise
        except NameError as ne:
            logger.error(f"NameError during Ollama chat (model: {effective_model}): {ne}. This likely means a missing import.", exc_info=True)
            raise 
        except Exception as e: 
            logger.error(f"Unexpected error during Ollama chat (model: {effective_model}): {e}", exc_info=True)
            raise

async def get_ollama_service() -> OllamaService:
    if not OllamaService.is_ready():
        logger.warning("get_ollama_service dependency called when OllamaService is not ready. Lifespan may have failed to initialize client properly.")
    return OllamaService