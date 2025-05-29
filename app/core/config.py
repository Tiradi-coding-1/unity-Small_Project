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
    # 預設值，會被 .env 檔案中的設定覆蓋
    DEFAULT_OLLAMA_MODEL: str = "llama3"
    DEFAULT_TRANSLATION_MODEL: str = "llama3"
    OLLAMA_REQUEST_TIMEOUT: int = 60
    DEFAULT_MAX_TOKENS: int = 4096

    # --- CORS Settings ---
    ENABLE_CORS: bool = True
    ALLOWED_ORIGINS: Union[str, List[str]] = "*"

    # --- Logging Settings ---
    LOG_LEVEL: str = "INFO"
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
    DEBUG_RELOAD: bool = False # 生產環境中建議設為 False

    # --- NPC Memory Cache Settings ---
    NPC_MEMORY_SAVE_ON_SHUTDOWN: bool = True
    NPC_MEMORY_AUTO_SAVE_INTERVAL_SECONDS: Optional[int] = 300 # 每 5 分鐘儲存一次

    model_config = SettingsConfigDict(
        env_file=".env", # <<<--- 假設您的 .env 檔案名稱是 ".env"
                         # 如果是 ".env_apartment"，請改回 ".env_apartment"
        env_file_encoding='utf-8',
        extra='ignore',
        case_sensitive=False # Pydantic V2 預設行為，環境變數名稱不區分大小寫
    )

# 建立設定實例供其他模組使用
settings_instance = Settings()

# 確保必要的目錄存在
def ensure_directories_exist():
    """Ensures that directories specified in settings (like log and memory dirs) exist."""
    # 確保 NPC 記憶檔案目錄存在
    settings_instance.NPC_MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    
    # 如果啟用了檔案日誌，確保日誌檔案的父目錄存在
    if settings_instance.LOG_TO_FILE:
        log_file_parent_dir = settings_instance.LOG_FILE_PATH.parent
        log_file_parent_dir.mkdir(parents=True, exist_ok=True)

# 在模組加載時執行目錄檢查和創建
ensure_directories_exist()