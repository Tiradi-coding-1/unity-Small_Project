# npc_api_suite/app/llm/ollama_client.py

import ollama # type: ignore # mypy 可能會抱怨 ollama 沒有類型提示，可以忽略或尋找 type stubs
from typing import Optional, List, Dict, Any, AsyncGenerator, Union
from app.core.config import settings_instance as settings # 統一從 config 讀取設定
from app.core.logging_config import setup_logging # 使用統一的日誌
from app.core.schemas import Message, OllamaChatOptions # 從 schemas 導入 Message 模型

logger = setup_logging(__name__)

class OllamaService:
    _client: Optional[ollama.AsyncClient] = None
    _is_initialized_successfully: bool = False # True ONLY after successful client creation AND first successful API call

    @classmethod
    async def initialize_client(cls) -> None:
        if cls._is_initialized_successfully: # Check this flag first
            logger.info("Ollama AsyncClient already initialized and connection verified.")
            return

        # Reset flags before attempting initialization
        cls._client = None
        cls._is_initialized_successfully = False

        try:
            logger.info(f"Attempting to initialize Ollama AsyncClient for host: {settings.OLLAMA_HOST} with timeout {settings.OLLAMA_REQUEST_TIMEOUT}s")
            # Step 1: Create the client instance
            temp_client = ollama.AsyncClient(
                host=settings.OLLAMA_HOST,
                timeout=settings.OLLAMA_REQUEST_TIMEOUT
            )
            logger.debug("Ollama AsyncClient instance created.")

            # Step 2: Perform an actual API call to test connectivity and functionality
            try:
                logger.debug("Attempting to list models to verify Ollama connection...")
                response = await temp_client.list() # Use the temporary client instance directly
                available_models_data = response.get("models", [])
                model_names = [m.get('name') for m in available_models_data]
                logger.info(f"Successfully connected to Ollama. Available models: {model_names}")
                
                # If successful, assign to class variable and set flags
                cls._client = temp_client
                cls._is_initialized_successfully = True
                logger.info("Ollama AsyncClient initialization and connection verification SUCCEEDED.")

            except ollama.ResponseError as e_api: # Specific Ollama API error during list()
                logger.error(f"Ollama API error during initial model list (host: {settings.OLLAMA_HOST}): {e_api.status_code} - {e_api.error}", exc_info=True)
                # temp_client was created but communication failed. Ensure it's cleaned up if it holds resources.
                # ollama-python's AsyncClient uses httpx, which should manage its own cleanup.
            except Exception as e_connect: # Other errors during list() (e.g., network issues before ResponseError)
                logger.error(f"Connection or other error during initial model list (host: {settings.OLLAMA_HOST}): {e_connect}", exc_info=True)
        
        except Exception as e_create: # Error during ollama.AsyncClient() creation itself
            logger.error(f"Failed to create Ollama AsyncClient instance (host: {settings.OLLAMA_HOST}): {e_create}", exc_info=True)
        
        # If any step above failed, _is_initialized_successfully remains False and _client might be None or the temp_client (which is fine as it won't be used if flag is False)

    @classmethod
    async def close_client(cls) -> None:
        # ... (close_client logic can remain largely the same) ...
        if cls._client:
            logger.info("Ollama AsyncClient is being 'closed' (client instance will be reset).")
            # If ollama.AsyncClient().aclose() becomes available, call it here.
            cls._client = None
        cls._is_initialized_successfully = False # Always set to false on close

    @classmethod
    def get_client(cls) -> ollama.AsyncClient:
        if not cls._is_initialized_successfully or cls._client is None: # Check both
            logger.error("Ollama AsyncClient requested but is not available or initialization failed.")
            raise ConnectionError("Ollama service client is not available. Please ensure it's initialized and successfully connected.")
        return cls._client

    @classmethod
    def is_ready(cls) -> bool:
        return cls._is_initialized_successfully and cls._client is not None

    @classmethod
    async def list_available_models(cls, log_success: bool = False) -> List[Dict[str, Any]]:
        # This method now assumes get_client() will provide a working client or raise error
        client = cls.get_client() # This will raise if not ready
        try:
            response = await client.list()
            available_models_data = response.get("models", [])
            if log_success: # Usually, success is logged during initialize_client's test
                model_names = [m.get('name') for m in available_models_data]
                logger.info(f"Ollama models listed successfully: {model_names}")
            return available_models_data
        except ollama.ResponseError as e: # Specific Ollama API error
            logger.error(f"Ollama API error when listing models: {e.status_code} - {e.error}", exc_info=True)
            # Consider if this specific error should also set _is_initialized_successfully to False.
            # If initialize_client succeeded, a later list_models failing might be temporary.
            # However, for robustness, if any API call fails catastrophically, re-evaluating readiness is good.
            # For now, let initialize_client handle the initial readiness.
            raise
        except Exception as e:
            logger.error(f"Unexpected error listing Ollama models: {e}", exc_info=True)
            raise
            
    # --- generate_chat_completion method remains the same as previously provided ---
    # Make sure it uses self.get_client()
    @classmethod
    async def generate_chat_completion(
        cls,
        model: Optional[str],
        messages: List[Message],
        stream: bool = False,
        options: Optional[OllamaChatOptions] = None
    ) -> Union[Dict[str, Any], AsyncGenerator[Dict[str, Any], None]]:
        client = cls.get_client() # Ensures client is ready or raises ConnectionError
        
        formatted_messages: List[Dict[str, Any]] = [] # Changed to List[Dict[str, Any]] to accommodate potential images
        for msg in messages:
            msg_dict: Dict[str, Any] = {"role": msg.role.value, "content": msg.content}
            if msg.name:
                msg_dict["name"] = msg.name
            # Example for adding images if your Message schema supports it:
            # if hasattr(msg, 'images') and msg.images:
            #     msg_dict["images"] = msg.images # Assuming images are base64 strings
            formatted_messages.append(msg_dict)

        effective_model = model if model else settings.DEFAULT_OLLAMA_MODEL
        if not effective_model:
             logger.error("No Ollama model specified for chat completion and no default model is configured.")
             raise ValueError("Ollama model name must be provided or a default set in configuration.")

        ollama_options_dict: Optional[Dict[str, Any]] = None
        if options:
            ollama_options_dict = options.model_dump(exclude_none=True)
            if "num_ctx" not in ollama_options_dict and settings.DEFAULT_MAX_TOKENS > 0 : # Check > 0
                 ollama_options_dict["num_ctx"] = settings.DEFAULT_MAX_TOKENS
        elif settings.DEFAULT_MAX_TOKENS > 0 : # Apply default num_ctx if no options given
             ollama_options_dict= {"num_ctx": settings.DEFAULT_MAX_TOKENS}


        logger.debug(f"Sending chat request to Ollama. Model: {effective_model}, Messages count: {len(formatted_messages)}, Stream: {stream}, Options: {ollama_options_dict}")
        
        try:
            response_generator_or_dict = await client.chat(
                model=effective_model,
                messages=formatted_messages, # type: ignore
                stream=stream,
                options=ollama_options_dict
            )
            
            if stream:
                # ... (streaming wrapper logic from previous version) ...
                async def stream_wrapper() -> AsyncGenerator[Dict[str, Any], None]:
                    try:
                        async for chunk in response_generator_or_dict: # type: ignore
                            yield chunk
                    except ollama.ResponseError as e_stream:
                        logger.error(f"Ollama API error during stream (model: {effective_model}): {e_stream.status_code} - {e_stream.error}", exc_info=True)
                        raise 
                    except Exception as e_stream_unexpected:
                        logger.error(f"Unexpected error during Ollama stream (model: {effective_model}): {e_stream_unexpected}", exc_info=True)
                        raise
                return stream_wrapper()
            else:
                if not isinstance(response_generator_or_dict, dict):
                     logger.error(f"Ollama non-streamed response was not a dict for model {effective_model}. Got: {type(response_generator_or_dict)}")
                     raise TypeError("Ollama non-streamed response format unexpected.")
                return response_generator_or_dict
        except ollama.ResponseError as e: # Error from client.chat() itself
            logger.error(f"Ollama API error (model: {effective_model}): {e.status_code} - {e.error}", exc_info=True)
            # No need to set _is_initialized_successfully = False here for individual chat failures,
            # unless the error indicates a persistent connection problem (e.g., specific status codes).
            raise
        except ConnectionError: # From get_client()
            raise
        except Exception as e:
            logger.error(f"Unexpected error during Ollama chat (model: {effective_model}): {e}", exc_info=True)
            raise

# --- FastAPI Dependency Injection (remains the same) ---
async def get_ollama_service() -> OllamaService:
    if not OllamaService.is_ready():
        logger.warning("get_ollama_service dependency called when OllamaService is not ready. Lifespan may have failed to initialize client properly.")
        # get_client() will be called by service methods and will raise ConnectionError if not ready
    return OllamaService