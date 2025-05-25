# npc_api_suite/app/core/config.py

from pydantic_settings import BaseSettings, SettingsConfigDict
from typing import List, Union, Optional # 新增 Optional
from pathlib import Path
import os # 雖然在這個版本中沒直接用 os.getenv，但保留 import 以防未來需要

class Settings(BaseSettings):
    # --- API Metadata ---
    API_TITLE: str = "NPC API Suite for Unity Game"
    API_DESCRIPTION: str = "Integrated API for NPC dialogue, movement, and advanced behaviors."
    API_VERSION: str = "2.0.0"

    # --- Ollama Settings ---
    OLLAMA_HOST: str = "http://localhost:11434"
    DEFAULT_OLLAMA_MODEL: str = "llama3"
    DEFAULT_TRANSLATION_MODEL: str = "llama3"
    OLLAMA_REQUEST_TIMEOUT: int = 60 # seconds
    DEFAULT_MAX_TOKENS: int = 4096 # Default context size for Ollama if not specified in options

    # --- CORS Settings ---
    ENABLE_CORS: bool = True
    ALLOWED_ORIGINS: Union[str, List[str]] = "*" # For production, specify exact domains: ["http://localhost:8000", "https://yourgame.com"]

    # --- Logging Settings ---
    LOG_LEVEL: str = "INFO" # Options: "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"
    LOG_TO_FILE: bool = True # Recommended for easier debugging and tracking
    LOG_FILE_PATH: Path = Path("npc_logs/npc_api_suite.log") # Centralized log file
    LOG_ROTATION_SIZE: str = "10 MB" # e.g., "500 KB", "10 MB", "1 GB". Parsed into bytes.
    LOG_RETENTION_COUNT: int = 5 # Number of backup log files to keep

    # --- NPC Movement & Memory Settings ---
    NPC_MEMORY_DIR: Path = Path("npc_memory")
    # NPC_LOG_DIR: Path = Path("npc_logs/npc_specific_logs") # If separate logs per NPC type needed
    MAX_LOCATIONS_IN_MEMORY: int = 20 # Max recent locations stored per NPC
    MAX_LONG_TERM_MEMORY_ENTRIES: int = 50 # Max long-term memory text entries per NPC
    VISIT_THRESHOLD_DISTANCE: float = 5.0 # Game units, for considering a location "same"
    MAX_SEARCH_DISTANCE_FOR_NEW_POINT: float = 100.0 # Fallback: Max distance to search for new point
    MIN_SEARCH_DISTANCE_FOR_NEW_POINT: float = 10.0 # Fallback: Min distance to search for new point
    REVISIT_INTERVAL_SECONDS: int = 120 # Seconds, to consider a location "recently visited"
    SCENE_BOUNDARY_BUFFER: float = 1.0 # Game units, buffer from scene edges for NPC movement

    # --- API Server Settings ---
    SERVER_HOST: str = "0.0.0.0" # Listen on all available interfaces
    SERVER_PORT: int = 8000 # Unified port for the API suite
    DEBUG_RELOAD: bool = False # For Uvicorn's auto-reload feature during development

    # --- NPC Memory Cache Settings (for write strategy B) ---
    NPC_MEMORY_SAVE_ON_SHUTDOWN: bool = True # Save all 'dirty' NPC memories on application shutdown
    # Optional: Interval for periodic saving of dirty memories (if needed beyond shutdown)
    # NPC_MEMORY_AUTO_SAVE_INTERVAL_SECONDS: Optional[int] = 300 # e.g., save every 5 minutes. Set to None to disable.

    # Pydantic-settings model_config (Pydantic V2 style)
    model_config = SettingsConfigDict(
        env_file=".env", # Specifies the .env file name
        env_file_encoding='utf-8',
        extra='ignore', # Ignore extra variables in the .env file not defined in Settings
        case_sensitive=False # Environment variable names are case-insensitive
    )

# Create a globally accessible instance of the settings
# This instance will be created when this module is first imported.
settings_instance = Settings()

# --- Function to ensure necessary directories exist upon application startup ---
def ensure_directories_exist():
    """Ensures that necessary directories for logs and NPC memory exist based on settings."""
    # Create NPC memory directory
    settings_instance.NPC_MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    
    # Create log directory if logging to file is enabled
    if settings_instance.LOG_TO_FILE:
        log_file_parent_dir = settings_instance.LOG_FILE_PATH.parent
        log_file_parent_dir.mkdir(parents=True, exist_ok=True)
    
    # If you had a separate NPC_LOG_DIR, you would create it here too.
    # For example:
    # if settings_instance.NPC_LOG_DIR:
    #     settings_instance.NPC_LOG_DIR.mkdir(parents=True, exist_ok=True)

# Call this function immediately after the settings_instance is created
# to ensure directories are ready before any other part of the app tries to use them.
ensure_directories_exist()

# To make it easier for other modules to import:
# from app.core.config import settings_instance as settings