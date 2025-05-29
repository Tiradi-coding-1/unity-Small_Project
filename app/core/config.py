# npc_api_suite/app/core/config.py

from pydantic_settings import BaseSettings, SettingsConfigDict
from typing import List, Union, Optional
from pathlib import Path
import os

class Settings(BaseSettings):
    # --- API Metadata ---
    API_TITLE: str = "NPC API Suite for Unity Game"
    API_DESCRIPTION: str = "Integrated API for NPC dialogue, movement, and advanced behaviors within an apartment setting."
    API_VERSION: str = "2.0.0"

    # --- Ollama Settings ---
    OLLAMA_HOST: str = "http://localhost:11434"
    DEFAULT_OLLAMA_MODEL: str = "llama3.2:1b" # 您已更新為此模型
    DEFAULT_TRANSLATION_MODEL: str = "gemma3:4b" # 或者您為翻譯設定的其他模型
    OLLAMA_REQUEST_TIMEOUT: int = 600 
    DEFAULT_MAX_TOKENS: int = 2048 

    # --- CORS Settings ---
    ENABLE_CORS: bool = True
    ALLOWED_ORIGINS: Union[str, List[str]] = "*"

    # --- Logging Settings ---
    LOG_LEVEL: str = "DEBUG" 
    LOG_TO_FILE: bool = True
    LOG_FILE_PATH: Path = Path("npc_logs/npc_api_suite_apartment.log") 
    LOG_ROTATION_SIZE: str = "10 MB" 
    LOG_RETENTION_COUNT: int = 5

    # --- NPC Movement & Memory Settings (Adjusted for Apartment Scene) ---
    NPC_MEMORY_DIR: Path = Path("npc_memory_apartment") 
    MAX_LOCATIONS_IN_MEMORY: int = 15
    MAX_LONG_TERM_MEMORY_ENTRIES: int = 50
    
    VISIT_THRESHOLD_DISTANCE: float = 1.5
    MAX_SEARCH_DISTANCE_FOR_NEW_POINT: float = 15.0
    MIN_SEARCH_DISTANCE_FOR_NEW_POINT: float = 2.0
    REVISIT_INTERVAL_SECONDS: int = 300
    SCENE_BOUNDARY_BUFFER: float = 0.5

    # --- API Server Settings ---
    SERVER_HOST: str = "0.0.0.0"
    SERVER_PORT: int = 8000
    DEBUG_RELOAD: bool = False 

    # --- NPC Memory Cache Settings ---
    NPC_MEMORY_SAVE_ON_SHUTDOWN: bool = True
    NPC_MEMORY_AUTO_SAVE_INTERVAL_SECONDS: Optional[int] = 300 

    # --- Translation Logging Settings (新增) ---
    ENABLE_TRANSLATION_LOGGING: bool = True
    TRANSLATION_LOG_FILE: Path = Path("translation_logs/translations.jsonl") # 使用 .jsonl 副檔名

    model_config = SettingsConfigDict(
        env_file=".env", 
        env_file_encoding='utf-8',
        extra='ignore', 
        case_sensitive=False 
    )

settings_instance = Settings()

def ensure_directories_exist():
    """Ensures that directories specified in settings (like log and memory dirs) exist."""
    settings_instance.NPC_MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    
    if settings_instance.LOG_TO_FILE:
        log_file_parent_dir = settings_instance.LOG_FILE_PATH.parent
        log_file_parent_dir.mkdir(parents=True, exist_ok=True)
    
    # --- 新增：確保翻譯記錄目錄存在 ---
    if settings_instance.ENABLE_TRANSLATION_LOGGING:
        translation_log_parent_dir = settings_instance.TRANSLATION_LOG_FILE.parent
        translation_log_parent_dir.mkdir(parents=True, exist_ok=True)

ensure_directories_exist()