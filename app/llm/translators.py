# npc_api_suite/app/llm/translators.py

from typing import Optional, Dict, Any 
from app.llm.ollama_client import OllamaService 
from app.core.schemas import Message, OllamaChatOptions, default_aware_utcnow 
from app.llm.prompt_builder import build_translation_prompt_messages 
from app.core.config import settings_instance as settings 
from app.core.logging_config import setup_logging 
import json 
import aiofiles 
import asyncio 
from datetime import datetime 

logger = setup_logging(__name__)

_TRANSLATION_LOG_LOCK = asyncio.Lock()

class TextTranslatorService:
    """
    A service class for handling text translation, primarily using an LLM via Ollama.
    Now includes functionality to log translation results.
    """

    def __init__(self, ollama_service: OllamaService):
        """
        Initializes the TextTranslatorService with an instance of OllamaService.
        """
        self.ollama_service = ollama_service

    async def _log_translation_attempt(
        self,
        original_text: str,
        translated_text: Optional[str],
        source_lang: Optional[str],
        target_lang: str,
        model_used: str,
        success: bool,
        error_message: Optional[str] = None
    ):
        """
        Asynchronously logs the details of a translation attempt to a file.
        """
        if not settings.ENABLE_TRANSLATION_LOGGING:
            return

        log_entry: Dict[str, Any] = {
            "timestamp_utc": default_aware_utcnow().isoformat(),
            "original_text": original_text,
            "translated_text": translated_text, # This will be None if success is False and there's an error
            "source_language": source_lang,
            "target_language": target_lang,
            "model_used": model_used,
            "success": success,
        }
        if error_message:
            log_entry["error_message"] = error_message

        try:
            async with _TRANSLATION_LOG_LOCK:
                # Ensure the directory exists (config.py should handle this on startup, but as a safeguard)
                settings.TRANSLATION_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
                async with aiofiles.open(settings.TRANSLATION_LOG_FILE, mode='a', encoding='utf-8') as f:
                    await f.write(json.dumps(log_entry, ensure_ascii=False) + "\n")
        except Exception as e:
            logger.error(f"Failed to write to translation log file '{settings.TRANSLATION_LOG_FILE}': {e}", exc_info=True)


    async def translate_text(
        self,
        text_to_translate: str,
        target_language: str = "Traditional Chinese", 
        source_language: Optional[str] = "English",   
        translation_model_override: Optional[str] = None 
    ) -> Optional[str]:
        """
        Translates the given text to the target language using an LLM.
        Logs the translation attempt if enabled in settings.

        Args:
            text_to_translate: The text string to be translated.
            target_language: The desired target language (e.g., "Traditional Chinese").
            source_language: The source language of the text (e.g., "English").
            translation_model_override: Specific Ollama model to use for this translation.
                                         If None, uses DEFAULT_TRANSLATION_MODEL from settings.

        Returns:
            The translated text as a string, or a placeholder string indicating the error if translation fails.
            Returns None if the input text is empty or whitespace-only (and logs nothing).
        """
        if not text_to_translate or not text_to_translate.strip():
            logger.debug("translate_text called with empty or whitespace-only input. Returning None.")
            return None

        effective_translation_model = translation_model_override or settings.DEFAULT_TRANSLATION_MODEL
        
        # Prepare variables for logging
        translated_text_for_log: Optional[str] = None
        log_success_status: bool = False
        log_error_message: Optional[str] = None
        final_output_text: str # This will be returned

        if not effective_translation_model:
            warning_msg = (
                f"No translation model specified (override or default) for target '{target_language}'. "
                "Translation cannot proceed."
            )
            logger.warning(warning_msg)
            log_error_message = "Translation unavailable: No model configured"
            # Do not assign to translated_text_for_log here, it should remain None for error cases
            final_output_text = f"[{log_error_message}] Original: {text_to_translate[:50]}..."
            # Log this attempt
            await self._log_translation_attempt(
                original_text=text_to_translate,
                translated_text=None, 
                source_lang=source_language,
                target_lang=target_language,
                model_used="N/A", 
                success=False,
                error_message=log_error_message 
            )
            return final_output_text

        translation_messages = build_translation_prompt_messages(
            text_to_translate=text_to_translate,
            target_language=target_language,
            source_language=source_language
        )
        
        try:
            logger.debug(f"Attempting translation from '{source_language}' to '{target_language}' "
                         f"for text: '{text_to_translate[:70]}...' using model '{effective_translation_model}'")
            
            chat_options = OllamaChatOptions(temperature=0.2, num_ctx=1024) # Example options
            
            ollama_response_data = await self.ollama_service.generate_chat_completion(
                model=effective_translation_model,
                messages=translation_messages,
                stream=False,
                options=chat_options 
            )
            
            if isinstance(ollama_response_data, dict) and ollama_response_data.get("error"):
                llm_error = ollama_response_data.get("message", {}).get("content", ollama_response_data["error"])
                logger.error(f"LLM client returned error for translation with model '{effective_translation_model}': {llm_error}")
                log_error_message = f"LLM Client Error: {llm_error}"
                final_output_text = f"[Translation Error: {log_error_message}] Original: {text_to_translate[:50]}..."
            elif isinstance(ollama_response_data, dict) and "message" in ollama_response_data and isinstance(ollama_response_data["message"], dict):
                translated_content = ollama_response_data.get('message', {}).get('content')
                if translated_content:
                    final_output_text = translated_content.strip()
                    translated_text_for_log = final_output_text # Store for logging successful translation
                    log_success_status = True
                    logger.info(f"Successfully translated to '{target_language}': '{final_output_text[:70]}...' (Original: '{text_to_translate[:50]}...')")
                else:
                    log_error_message = "Empty Response from LLM"
                    logger.warning(
                        f"Translation attempt for '{text_to_translate[:50]}...' to '{target_language}' "
                        f"using model '{effective_translation_model}' returned empty or no content. "
                        f"Full Ollama response: {str(ollama_response_data)[:200]}..."
                    )
                    final_output_text = f"[Translation Error: {log_error_message}] Original: {text_to_translate[:50]}..."
            else:
                log_error_message = "Invalid response structure from LLM client"
                logger.error(f"Unexpected response structure from ollama_service for translation: {str(ollama_response_data)[:300]}")
                final_output_text = f"[Translation Error: {log_error_message}] Original: {text_to_translate[:50]}..."

        except ConnectionError as e: 
            log_error_message = f"Ollama Connection Error: {str(e)}"
            logger.error(f"Ollama connection error during translation: {e}", exc_info=True)
            final_output_text = f"[Translation Failed: Ollama Connection Error] Original: {text_to_translate[:50]}..."
        except ValueError as ve: # Catch ValueErrors, e.g. from model name issue in ollama_client
            log_error_message = f"ValueError during translation: {str(ve)}"
            logger.error(f"ValueError during translation process for model '{effective_translation_model}': {ve}", exc_info=True)
            final_output_text = f"[Translation Failed: Configuration or Value Error] Original: {text_to_translate[:50]}..."
        except Exception as e:
            log_error_message = f"Unexpected error: {type(e).__name__} - {str(e)}"
            logger.error(
                f"An unexpected error occurred during LLM translation of '{text_to_translate[:50]}...' "
                f"to '{target_language}' using model '{effective_translation_model}': {e}",
                exc_info=True
            )
            final_output_text = f"[Translation Failed: {type(e).__name__}] Original: {text_to_translate[:50]}..."
        
        finally:
            # Log the translation attempt using the status determined within the try block
            await self._log_translation_attempt(
                original_text=text_to_translate,
                translated_text=translated_text_for_log, # This is None if translation failed
                source_lang=source_language,
                target_lang=target_language,
                model_used=effective_translation_model, # Log the model we attempted to use
                success=log_success_status,
                error_message=log_error_message # This will have the error if not successful
            )

        return final_output_text