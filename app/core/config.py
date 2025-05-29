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
    # Default model names; it's STRONGLY recommended to set these via your .env file
    # to match the exact models you have pulled and intend to use (e.g., "llama3.1:8b", "gemma:2b")
    DEFAULT_OLLAMA_MODEL: str = "llama3" 
    DEFAULT_TRANSLATION_MODEL: str = "llama3" # Or a smaller model like "gemma:2b" if preferred for translation
    OLLAMA_REQUEST_TIMEOUT: int = 60 # MODIFIED: Default timeout. Configure a higher value in .env for your performance target.
    DEFAULT_MAX_TOKENS: int = 4096 # Desired context window size from settings, actual may be model-dependent.

    # --- CORS Settings ---
    ENABLE_CORS: bool = True
    ALLOWED_ORIGINS: Union[str, List[str]] = "*"

    # --- Logging Settings ---
    LOG_LEVEL: str = "INFO" # Recommended: "DEBUG" for development, "INFO" for production.
    LOG_TO_FILE: bool = True
    LOG_FILE_PATH: Path = Path("npc_logs/npc_api_suite_apartment.log") # MODIFIED
    LOG_ROTATION_SIZE: str = "10 MB" 
    LOG_RETENTION_COUNT: int = 5

    # --- NPC Movement & Memory Settings (Adjusted for Apartment Scene) ---
    NPC_MEMORY_DIR: Path = Path("npc_memory_apartment") # MODIFIED
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
    DEBUG_RELOAD: bool = False # Set to True for development if Uvicorn should auto-reload on code changes.

    # --- NPC Memory Cache Settings ---
    NPC_MEMORY_SAVE_ON_SHUTDOWN: bool = True
    NPC_MEMORY_AUTO_SAVE_INTERVAL_SECONDS: Optional[int] = 300 # e.g., every 5 minutes

    model_config = SettingsConfigDict(
        env_file=".env", 
        env_file_encoding='utf-8',
        extra='ignore', # Ignore extra fields from .env
        case_sensitive=False # Environment variable names are case-insensitive
    )

# Create settings instance for other modules to use
settings_instance = Settings()

# Ensure necessary directories exist
def ensure_directories_exist():
    """Ensures that directories specified in settings (like log and memory dirs) exist."""
    # Ensure NPC memory file directory exists
    settings_instance.NPC_MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    
    # If file logging is enabled, ensure the log file's parent directory exists
    if settings_instance.LOG_TO_FILE:
        log_file_parent_dir = settings_instance.LOG_FILE_PATH.parent
        log_file_parent_dir.mkdir(parents=True, exist_ok=True)

# Execute directory check and creation when the module is loaded
ensure_directories_exist()