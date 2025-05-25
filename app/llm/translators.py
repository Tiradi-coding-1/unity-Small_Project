# npc_api_suite/app/llm/translators.py

from typing import Optional
from app.llm.ollama_client import OllamaService # 使用共享的 Ollama 服務
from app.core.schemas import Message # 從 schemas 導入 Message 模型 (雖然 build_translation_prompt_messages 已導入)
from app.llm.prompt_builder import build_translation_prompt_messages # 使用提示構建器
from app.core.config import settings_instance as settings # 獲取預設翻譯模型等設定
from app.core.logging_config import setup_logging # 使用日誌

logger = setup_logging(__name__)

class TextTranslatorService:
    """
    A service class for handling text translation, primarily using an LLM via Ollama.
    """

    def __init__(self, ollama_service: OllamaService):
        """
        Initializes the TextTranslatorService with an instance of OllamaService.
        """
        self.ollama_service = ollama_service

    async def translate_text(
        self,
        text_to_translate: str,
        target_language: str = "Traditional Chinese", # 預設目標語言為繁體中文
        source_language: Optional[str] = "English",   # 預設源語言為英文
        translation_model_override: Optional[str] = None # 允許覆蓋預設翻譯模型
    ) -> Optional[str]:
        """
        Translates the given text to the target language using an LLM.

        Args:
            text_to_translate: The text string to be translated.
            target_language: The desired target language (e.g., "Traditional Chinese").
            source_language: The source language of the text (e.g., "English").
            translation_model_override: Specific Ollama model to use for this translation.
                                         If None, uses DEFAULT_TRANSLATION_MODEL from settings.

        Returns:
            The translated text as a string, or None if translation fails or input is empty.
            If translation fails, a placeholder string indicating the error might be returned.
        """
        if not text_to_translate or not text_to_translate.strip():
            logger.debug("translate_text called with empty or whitespace-only input. Returning None.")
            return None

        # 確定使用的翻譯模型
        effective_translation_model = translation_model_override or settings.DEFAULT_TRANSLATION_MODEL
        if not effective_translation_model:
            logger.warning(
                f"No translation model specified (override or default) for target '{target_language}'. "
                "Translation cannot proceed."
            )
            return f"[Translation unavailable: No model configured] Original: {text_to_translate[:50]}..."

        # 構建翻譯提示
        translation_messages = build_translation_prompt_messages(
            text_to_translate=text_to_translate,
            target_language=target_language,
            source_language=source_language
        )
        
        try:
            logger.debug(f"Attempting translation from '{source_language}' to '{target_language}' "
                         f"for text: '{text_to_translate[:70]}...' using model '{effective_translation_model}'")
            
            # 調用 Ollama 服務進行聊天補全 (即翻譯)
            # 翻譯任務通常不需要流式輸出，且溫度可以設低一些以求精確
            ollama_response = await self.ollama_service.generate_chat_completion(
                model=effective_translation_model,
                messages=translation_messages,
                stream=False,
                options={"temperature": 0.2, "num_ctx": 1024} # 示例：低溫，適中上下文
            )
            
            # 從 Ollama 回應中提取翻譯後的文本
            # Ollama chat response format: {'model': '...', 'created_at': '...', 'message': {'role': 'assistant', 'content': '...'}, ...}
            translated_content = ollama_response.get('message', {}).get('content')
            
            if translated_content:
                translated_text = translated_content.strip()
                logger.info(f"Successfully translated to '{target_language}': '{translated_text[:70]}...' (Original: '{text_to_translate[:50]}...')")
                return translated_text
            else:
                logger.warning(
                    f"Translation attempt for '{text_to_translate[:50]}...' to '{target_language}' "
                    f"using model '{effective_translation_model}' returned empty or no content. "
                    f"Full Ollama response: {str(ollama_response)[:200]}..."
                )
                # 返回帶有錯誤標記的原文，以便調試和讓前端知道翻譯失敗
                return f"[Translation Error: Empty Response from LLM] Original: {text_to_translate[:50]}..."

        except ConnectionError as e: # OllamaService.get_client() 可能拋出
            logger.error(f"Ollama connection error during translation: {e}", exc_info=True)
            return f"[Translation Failed: Ollama Connection Error] Original: {text_to_translate[:50]}..."
        except Exception as e:
            logger.error(
                f"An unexpected error occurred during LLM translation of '{text_to_translate[:50]}...' "
                f"to '{target_language}' using model '{effective_translation_model}': {e}",
                exc_info=True
            )
            return f"[Translation Failed: {type(e).__name__}] Original: {text_to_translate[:50]}..."

# --- FastAPI Dependency Injection (可選) ---
# 如果你希望在路由處理器中直接注入 TextTranslatorService，可以這樣做：
# from fastapi import Depends
# from app.llm.ollama_client import get_ollama_service
#
# async def get_text_translator_service(
#     ollama_s: OllamaService = Depends(get_ollama_service)
# ) -> TextTranslatorService:
#     return TextTranslatorService(ollama_service=ollama_s)
#
# 然後在 router 中：
# translator: TextTranslatorService = Depends(get_text_translator_service)
# await translator.translate_text(...)
#
# 不過，更常見的做法是將 TextTranslatorService 作為 DialogueService 的一個組件，
# DialogueService 再被注入到 router 中。