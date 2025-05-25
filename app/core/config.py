# npc_api_suite/app/core/config.py

from pydantic_settings import BaseSettings, SettingsConfigDict
from typing import List, Union, Optional 
from pathlib import Path
import os 

class Settings(BaseSettings):
    # --- API Metadata ---
    API_TITLE: str = "NPC API Suite for Unity Game"
    API_DESCRIPTION: str = "Integrated API for NPC dialogue, movement, and advanced behaviors within an apartment setting." # Updated description
    API_VERSION: str = "2.0.0" # Assuming version remains the same unless specified

    # --- Ollama Settings ---
    OLLAMA_HOST: str = "http://localhost:11434"
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
    LOG_FILE_PATH: Path = Path("npc_logs/npc_api_suite_apartment.log") # Potentially a different log for this scene config
    LOG_ROTATION_SIZE: str = "10 MB" 
    LOG_RETENTION_COUNT: int = 5 

    # --- NPC Movement & Memory Settings (Adjusted for Apartment Scene) ---
    NPC_MEMORY_DIR: Path = Path("npc_memory_apartment") # Potentially separate memory dir for this scene config
    MAX_LOCATIONS_IN_MEMORY: int = 15 # Reduced slightly for smaller, more frequently visited areas
    MAX_LONG_TERM_MEMORY_ENTRIES: int = 50 
    
    VISIT_THRESHOLD_DISTANCE: float = 1.5 # Game units, for considering a location "same" within a room or small area. Was 5.0.
    MAX_SEARCH_DISTANCE_FOR_NEW_POINT: float = 15.0 # Fallback: Max distance to search for new point within apartment. Was 100.0.
    MIN_SEARCH_DISTANCE_FOR_NEW_POINT: float = 2.0  # Fallback: Min distance for a new distinct point. Was 10.0.
    REVISIT_INTERVAL_SECONDS: int = 300 # Seconds (5 minutes), to consider a location "recently visited". Was 120. Might increase if NPCs have fewer unique spots.
    SCENE_BOUNDARY_BUFFER: float = 0.5 # Game units, buffer from scene/room edges. Was 1.0.

    # --- API Server Settings ---
    SERVER_HOST: str = "0.0.0.0" 
    SERVER_PORT: int = 8000 
    DEBUG_RELOAD: bool = False 

    # --- NPC Memory Cache Settings ---
    NPC_MEMORY_SAVE_ON_SHUTDOWN: bool = True 
    NPC_MEMORY_AUTO_SAVE_INTERVAL_SECONDS: Optional[int] = 300 # Save every 5 minutes

    model_config = SettingsConfigDict(
        env_file=".env_apartment", # Optional: use a different .env file for apartment-specific overrides
        env_file_encoding='utf-8',
        extra='ignore', 
        case_sensitive=False 
    )

settings_instance = Settings()

def ensure_directories_exist():
    settings_instance.NPC_MEMORY_DIR.mkdir(parents=True, exist_ok=True)
    
    if settings_instance.LOG_TO_FILE:
        log_file_parent_dir = settings_instance.LOG_FILE_PATH.parent
        log_file_parent_dir.mkdir(parents=True, exist_ok=True)

ensure_directories_exist()