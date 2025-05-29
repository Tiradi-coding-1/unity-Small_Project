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
                
                available_models_list_raw = response_data if isinstance(response_data, dict) else {}
                available_models_list = available_models_list_raw.get("models", []) 
                
                model_names = []
                if isinstance(available_models_list, list) and available_models_list:
                    for i, model_obj in enumerate(available_models_list):
                        model_identifier = None
                        # Try 'model' attribute first, then 'name' as fallback
                        # Based on logs: Type: <class 'ollama._types.ListResponse.Model'>. Data: model='yi:6b'
                        if hasattr(model_obj, 'model') and model_obj.model:
                            model_identifier = model_obj.model
                        elif hasattr(model_obj, 'name') and model_obj.name: 
                            model_identifier = model_obj.name
                        
                        if model_identifier:
                            model_names.append(model_identifier)
                        else:
                            logger.warning(f"Ollama model data at index {i} missing 'model' or 'name' attribute, or it's empty. Object type: {type(model_obj)}. Data: {str(model_obj)[:200]}...")
                            model_names.append(None) 
                elif not available_models_list and isinstance(available_models_list, list):
                     logger.info("Ollama reports no models are currently available/downloaded via client.list(). This is OK if you intend to use specific model names directly.")
                else:
                    logger.warning(f"Ollama 'models' field from client.list() is not a list or is missing. Response: {response_data}")

                valid_model_names = [name for name in model_names if name is not None]
                if not valid_model_names and available_models_list: # If list was not empty but we parsed no names
                    logger.warning(
                        f"Could not parse any valid model names from client.list() response, though models were present. "
                        f"Attempted model identifiers (including None for errors): {model_names}. "
                        f"Please ensure Ollama is running, models are pulled, and OLLAMA_HOST ('{settings.OLLAMA_HOST}') is correct."
                    )
                elif valid_model_names: # If we successfully parsed some names
                    logger.info(f"Successfully connected to Ollama. Parsed available model identifiers from client.list(): {valid_model_names}")
                # If available_models_list was empty from the start, the earlier log message is sufficient.
                
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
                if hasattr(cls._client, 'aclose') and callable(cls._client.aclose):
                    await cls._client.aclose() 
                    logger.debug("Ollama AsyncClient aclosed successfully.")
                elif hasattr(cls._client, '_client') and cls._client._client is not None and \
                     hasattr(cls._client._client, 'aclose') and callable(cls._client._client.aclose): 
                    await cls._client._client.aclose() 
                    logger.debug("Underlying httpx.AsyncClient aclosed.")
                else:
                    logger.debug("No standard aclose method found on client or its _client attribute.")
            except Exception as e_aclose:
                logger.warning(f"Error trying to aclose Ollama client or underlying httpx client: {e_aclose}")
            cls._client = None
        cls._is_initialized_successfully = False

    @classmethod
    def get_client(cls) -> ollama.AsyncClient:
        if not cls._is_initialized_successfully or cls._client is None:
            logger.error("Ollama AsyncClient requested but is not available or initialization failed.")
            raise ConnectionError("Ollama service client is not available. Ensure Ollama is running and models are pulled.")
        return cls._client

    @classmethod
    def is_ready(cls) -> bool:
        return cls._is_initialized_successfully and cls._client is not None

    @classmethod
    async def list_available_models(cls, log_success: bool = False) -> List[Dict[str, Any]]:
        client = cls.get_client() 
        try:
            response_data_raw = await client.list() 
            
            if not isinstance(response_data_raw, dict):
                logger.warning(f"Ollama list models API did not return a dictionary. Got: {type(response_data_raw)}")
                return []
            
            available_models_obj_list = response_data_raw.get("models", [])
            
            if not isinstance(available_models_obj_list, list):
                logger.warning(f"Ollama list models API 'models' field is not a list. Got: {type(available_models_obj_list)}")
                return []

            models_as_dicts: List[Dict[str, Any]] = []
            model_identifiers_for_log = []

            for model_obj in available_models_obj_list:
                model_dict = {}
                model_identifier = None

                # Prefer 'model' attribute if present, then 'name'
                if hasattr(model_obj, 'model') and model_obj.model: 
                    model_identifier = model_obj.model
                    model_dict['name'] = model_obj.model # Use 'name' in our dict for consistency
                elif hasattr(model_obj, 'name') and model_obj.name: 
                    model_identifier = model_obj.name
                    model_dict['name'] = model_obj.name
                
                if hasattr(model_obj, 'modified_at'): model_dict['modified_at'] = str(model_obj.modified_at)
                if hasattr(model_obj, 'size'): model_dict['size'] = int(model_obj.size) 
                if hasattr(model_obj, 'digest'): model_dict['digest'] = str(model_obj.digest)
                
                details_attr = getattr(model_obj, 'details', None) 
                if isinstance(details_attr, dict): 
                     model_dict['details'] = details_attr
                else:
                     model_dict['details'] = {}

                if model_identifier: 
                    models_as_dicts.append(model_dict)
                    model_identifiers_for_log.append(model_identifier)
                else:
                    logger.warning(f"Could not extract 'model' or 'name' attribute from a model object: {str(model_obj)[:200]}")

            if log_success:
                if not model_identifiers_for_log and available_models_obj_list: 
                     logger.warning(f"Ollama models listed but identifiers could not be reliably extracted from all objects. Data sample: {str(available_models_obj_list)[:200]}")
                elif model_identifiers_for_log:
                     logger.info(f"Ollama models listed successfully (parsed identifiers): {model_identifiers_for_log}")
                else: 
                     logger.info("Ollama reports no models available via list API or failed to parse them.")
            return models_as_dicts
            
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
        for msg_model in messages:
            msg_dict: Dict[str, Any] = {"role": msg_model.role.value, "content": msg_model.content}
            if msg_model.name:
                msg_dict["name"] = msg_model.name
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
            elif "num_ctx" in ollama_options_dict and ollama_options_dict["num_ctx"] is not None and ollama_options_dict["num_ctx"] <= 0:
                logger.warning(f"num_ctx in Ollama options was {ollama_options_dict['num_ctx']}, which is invalid. Removing it.")
                del ollama_options_dict["num_ctx"] 
                if settings.DEFAULT_MAX_TOKENS > 0 and "num_ctx" not in ollama_options_dict:
                     ollama_options_dict["num_ctx"] = settings.DEFAULT_MAX_TOKENS
        elif settings.DEFAULT_MAX_TOKENS > 0 : 
             ollama_options_dict = {"num_ctx": settings.DEFAULT_MAX_TOKENS}

        logger.debug(f"Sending chat request to Ollama. Model: {effective_model}, Messages count: {len(formatted_messages)}, Stream: {stream}, Options: {ollama_options_dict}")
        
        try:
            raw_chat_response_object = await client.chat( 
                model=effective_model,
                messages=formatted_messages, 
                stream=stream,
                options=ollama_options_dict 
            )
            
            if stream: 
                async def stream_wrapper() -> AsyncGenerator[Dict[str, Any], None]:
                    try:
                        if not hasattr(raw_chat_response_object, '__aiter__'):
                            logger.error(f"Ollama stream response was not an async generator for model {effective_model}. Type: {type(raw_chat_response_object)}")
                            yield {
                                "model": effective_model,
                                "created_at": datetime.now(timezone.utc).isoformat(),
                                "error": "Stream initialization failed, expected async generator.",
                                "done": True
                            }
                            return
                        async for chunk_obj in raw_chat_response_object: 
                            chunk_dict = {}
                            chunk_dict['model'] = getattr(chunk_obj, 'model', effective_model)
                            created_at_dt_chunk = getattr(chunk_obj, 'created_at', datetime.now(timezone.utc))
                            chunk_dict['created_at'] = created_at_dt_chunk.isoformat() if isinstance(created_at_dt_chunk, datetime) else str(created_at_dt_chunk)
                            chunk_dict['done'] = getattr(chunk_obj, 'done', False) 
                            
                            message_attr = getattr(chunk_obj, 'message', None)
                            if message_attr and hasattr(message_attr, 'role') and hasattr(message_attr, 'content'):
                                chunk_dict['message'] = {'role': message_attr.role, 'content': message_attr.content or ""}
                            else: 
                                chunk_dict['message'] = {'role': 'assistant', 'content': ''}

                            if hasattr(chunk_obj, 'done_reason'): chunk_dict['done_reason'] = chunk_obj.done_reason
                            yield chunk_dict
                    except ollama.ResponseError as e_stream:
                        logger.error(f"Ollama API error during stream (model: {effective_model}): {e_stream.status_code} - {e_stream.error}", exc_info=True)
                        yield {
                            "model": effective_model,
                            "created_at": datetime.now(timezone.utc).isoformat(),
                            "error": f"Ollama API Error: {e_stream.error}",
                            "message": {"role": "assistant", "content": f"[Stream Error: {e_stream.error}]"},
                            "done": True,
                            "done_reason": "error"
                        }
                    except Exception as e_stream_unexpected:
                        logger.error(f"Unexpected error during Ollama stream (model: {effective_model}): {e_stream_unexpected}", exc_info=True)
                        yield {
                            "model": effective_model,
                            "created_at": datetime.now(timezone.utc).isoformat(),
                            "error": f"Unexpected stream error: {type(e_stream_unexpected).__name__}",
                            "message": {"role": "assistant", "content": f"[Unexpected Stream Error: {type(e_stream_unexpected).__name__}]"},
                            "done": True,
                            "done_reason": "error"
                        }
                return stream_wrapper()
            else: 
                response_to_return: Dict[str, Any] = {}
                response_to_return['model'] = getattr(raw_chat_response_object, 'model', effective_model)
                created_at_dt = getattr(raw_chat_response_object, 'created_at', datetime.now(timezone.utc))
                response_to_return['created_at'] = created_at_dt.isoformat() if isinstance(created_at_dt, datetime) else str(created_at_dt)
                response_to_return['done'] = getattr(raw_chat_response_object, 'done', True)
                
                done_reason_attr = getattr(raw_chat_response_object, 'done_reason', None)
                if done_reason_attr is not None: 
                    response_to_return['done_reason'] = done_reason_attr
                
                message_obj = getattr(raw_chat_response_object, 'message', None)
                msg_content = "[LLM message attribute not found or invalid]"
                msg_role = "assistant"

                if message_obj: 
                    msg_content = getattr(message_obj, 'content', "[LLM message object missing content attribute]")
                    msg_content = msg_content if msg_content is not None else "" 
                    msg_role = getattr(message_obj, 'role', "assistant")
                    if not msg_role: msg_role = "assistant" 
                else:
                     logger.error(f"Ollama non-streamed response object for model {effective_model} is missing 'message' attribute. Response obj type: {type(raw_chat_response_object)}")

                response_to_return['message'] = {"role": msg_role, "content": msg_content}

                for field_name in ['total_duration', 'load_duration', 'prompt_eval_count', 'prompt_eval_duration', 'eval_count', 'eval_duration']:
                    if hasattr(raw_chat_response_object, field_name):
                        value = getattr(raw_chat_response_object, field_name)
                        if value is not None: 
                             response_to_return[field_name] = value
                
                if not msg_content and message_obj is not None : 
                     logger.warning(f"Ollama non-streamed response for model {effective_model} resulted in empty 'content'. Message: {response_to_return['message']}")
                
                return response_to_return
        
        except ollama.ResponseError as e: 
            logger.error(f"Ollama API error (model: {effective_model}): {e.status_code} - {e.error}", exc_info=True)
            # Construct a dictionary that mimics the expected error structure if possible
            # This helps services consuming this method to handle errors more gracefully.
            return {
                "model": effective_model,
                "created_at": datetime.now(timezone.utc).isoformat(),
                "message": {"role": "assistant", "content": f"[Ollama API Error: {e.error}]"},
                "done": True,
                "done_reason": "error",
                "error": f"Ollama API Error: Status {e.status_code} - {e.error}" # Add specific error info
            }
        except ConnectionError as e_conn: 
            logger.error(f"Ollama connection error during chat (model: {effective_model}): {e_conn}", exc_info=True)
            raise # Re-raise to be caught by global handler
        except ValueError as ve: # Catch potential ValueErrors from model name issues etc.
            logger.error(f"ValueError during Ollama chat setup (model: {effective_model}): {ve}", exc_info=True)
            return { # Return an error structure
                "model": effective_model,
                "created_at": datetime.now(timezone.utc).isoformat(),
                "message": {"role": "assistant", "content": f"[Configuration Error: {ve}]"},
                "done": True,
                "done_reason": "error",
                "error": f"Configuration Error: {ve}"
            }
        except Exception as e: 
            logger.error(f"Unexpected error during Ollama chat (model: {effective_model}): {e}", exc_info=True)
            # For truly unexpected errors, also return an error structure
            return {
                "model": effective_model,
                "created_at": datetime.now(timezone.utc).isoformat(),
                "message": {"role": "assistant", "content": f"[Unexpected Server Error: {type(e).__name__}]"},
                "done": True,
                "done_reason": "error",
                "error": f"Unexpected Server Error: {type(e).__name__} - {str(e)}"
            }


async def get_ollama_service() -> OllamaService:
    if not OllamaService.is_ready():
        logger.critical("get_ollama_service dependency called but OllamaService is not ready. This indicates a critical issue with Ollama client initialization.")
        raise HTTPException(status_code=503, detail="Ollama service client is not initialized or unavailable.")
    return OllamaService