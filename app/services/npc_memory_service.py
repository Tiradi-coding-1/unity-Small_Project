# npc_api_suite/app/services/npc_memory_service.py

import json
import asyncio # For asyncio.Lock
from pathlib import Path
from datetime import datetime, timezone, timedelta
import math # For distance calculations
from typing import Optional, List, Dict, Tuple, Any, Set # Added Set
import aiofiles # For asynchronous file operations

from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.core.schemas import (
    NPCMemoryFile, VisitedLocationEntry, NPCEmotionalState, Position,
    LongTermMemoryEntry, GameTime, NPCScheduleRule, default_aware_utcnow # <--- 在此處添加 default_aware_utcnow
)

logger = setup_logging(__name__)

# --- 全域管理器，用於追蹤髒記憶實例和鎖 ---
# 這部分對於實現 "關閉時保存所有髒記憶" 非常重要
# _active_memory_instances: Dict[str, 'NPCMemoryService'] = {} # npc_id -> instance
# _dirty_npc_ids: Set[str] = set() # Set of npc_ids whose memory is dirty

# 為了簡化，我們讓每個 NPCMemoryService 實例自我管理其 dirty 狀態。
# 在 main.py 中，我們需要一種方式來收集所有可能存在的 "髒" 實例。
# 一個簡單的方法是維護一個全域的 NPCMemoryService 實例註冊表。
# 但這會引入潛在的記憶體洩漏，如果實例不被正確移除。

# 更簡單的（但可能不完美）方法是，假設 memory service 主要在請求期間被實例化和使用。
# 關閉時保存的邏輯可以通過一個更集中的數據管理器來實現，或者依賴於
# 請求結束時的保存（如果適用於您的使用模式）。

# 這裡，我們將專注於單個 NPCMemoryService 實例的緩存和寫入邏輯。
# 全域的 "關閉時保存" 邏輯需要在 main.py 中配合實現。

# 為每個 NPC 的記憶檔案操作加上非同步鎖，防止同一NPC檔案的併發讀寫
_NPC_FILE_LOCKS: Dict[str, asyncio.Lock] = {}

def _get_npc_file_lock(npc_id: str) -> asyncio.Lock:
    """Gets or creates an asyncio.Lock for a given NPC ID."""
    if npc_id not in _NPC_FILE_LOCKS:
        _NPC_FILE_LOCKS[npc_id] = asyncio.Lock()
    return _NPC_FILE_LOCKS[npc_id]


# 全域追蹤所有實例化的 NPCMemoryService，以便在關閉時保存
# 注意：這可能會導致記憶體中保留許多 NPCMemoryService 實例，除非有清除機制。
# 另一種方法是讓Unity端在NPC下線時通知API來清理/保存。
_INSTANTIATED_MEMORY_SERVICES: Dict[str, 'NPCMemoryService'] = {}


class NPCMemoryService:
    """
    Manages loading, updating, and saving an individual NPC's memory data.
    Implements a caching strategy to reduce frequent disk I/O.
    """
    def __init__(self, npc_id: str, memory_dir: Path = settings.NPC_MEMORY_DIR):
        if not npc_id:
            raise ValueError("NPC ID cannot be empty for NPCMemoryService.")
        self.npc_id = npc_id
        self.memory_file_path = memory_dir / f"{self.npc_id}.json"
        
        self._cached_memory_data: Optional[NPCMemoryFile] = None
        self._is_dirty: bool = False # True if _cached_memory_data has unsaved changes
        self._load_lock = asyncio.Lock() # Lock for initial loading of this instance's cache
        
        # 註冊此實例以便在關閉時保存 (如果需要)
        # 這部分需要謹慎處理，以避免記憶體洩漏
        # _INSTANTIATED_MEMORY_SERVICES[self.npc_id] = self #
        # logger.debug(f"NPCMemoryService instance created for {self.npc_id}. File: {self.memory_file_path}")


    async def _load_memory_from_file(self) -> NPCMemoryFile:
        """
        Loads NPC memory from its JSON file using aiofiles.
        This is the actual disk read operation.
        """
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock: # Ensure exclusive file access during read
            if not self.memory_file_path.exists() or self.memory_file_path.stat().st_size == 0:
                logger.info(f"Memory file for NPC '{self.npc_id}' not found or empty. Initializing new memory.")
                return NPCMemoryFile(
                    npc_id=self.npc_id,
                    name=self.npc_id, # Default name to npc_id, Unity can provide actual name
                    last_saved_at=default_aware_utcnow()
                    # Personality will use Pydantic default
                )
            try:
                async with aiofiles.open(self.memory_file_path, mode='r', encoding='utf-8') as f:
                    content = await f.read()
                data = json.loads(content)
                loaded_memory = NPCMemoryFile(**data)
                logger.debug(f"Successfully loaded memory for NPC '{self.npc_id}' from {self.memory_file_path}")
                return loaded_memory
            except json.JSONDecodeError:
                logger.warning(f"Invalid JSON in memory file for NPC '{self.npc_id}': {self.memory_file_path}. Re-initializing.")
                return NPCMemoryFile(npc_id=self.npc_id, name=self.npc_id, last_saved_at=default_aware_utcnow())
            except Exception as e:
                logger.error(f"Failed to load or parse memory for NPC '{self.npc_id}' from {self.memory_file_path}: {e}", exc_info=True)
                return NPCMemoryFile(npc_id=self.npc_id, name=self.npc_id, last_saved_at=default_aware_utcnow())

    async def _ensure_cache_loaded(self) -> None:
        """
        Ensures that the NPC's memory data is loaded into the instance cache.
        Uses a lock to prevent concurrent loading for the same instance.
        """
        if self._cached_memory_data is None:
            async with self._load_lock: # Lock specific to this instance's cache loading
                if self._cached_memory_data is None: # Double-check after acquiring lock
                    logger.debug(f"Cache miss for NPC '{self.npc_id}'. Loading from disk.")
                    self._cached_memory_data = await self._load_memory_from_file()
                    self._is_dirty = False # Freshly loaded data is not dirty
                    # Add this instance to the global tracker if it's newly loaded and wasn't there
                    # This part is tricky for robust shutdown saving without memory leaks.
                    # A better approach for shutdown save might be for Unity to list active NPCs.
                    _INSTANTIATED_MEMORY_SERVICES[self.npc_id] = self

    async def get_memory_data(self) -> NPCMemoryFile:
        """
        Retrieves the NPC's memory data, loading it into cache if necessary.
        This is the primary method to access an NPC's memory.
        """
        await self._ensure_cache_loaded()
        if self._cached_memory_data is None: # Should not happen if _ensure_cache_loaded works
            logger.error(f"CRITICAL: Memory cache for NPC '{self.npc_id}' is None after ensure_loaded. This indicates a problem.")
            raise RuntimeError(f"Failed to retrieve memory for NPC '{self.npc_id}'. Cache is unexpectedly None.")
        return self._cached_memory_data

    async def save_memory_to_file(self, force_save: bool = False) -> None:
        """
        Saves the current cached NPC memory data to its JSON file if it's dirty or if force_save is True.
        Uses aiofiles for asynchronous write and a file lock.
        """
        if not self._is_dirty and not force_save:
            logger.debug(f"Memory for NPC '{self.npc_id}' is not dirty and force_save is False. Skipping save.")
            return

        if self._cached_memory_data is None:
            logger.warning(f"Attempted to save memory for NPC '{self.npc_id}', but cache is empty. Load memory first.")
            # Optionally, try to load it first, then save if successful
            # await self._ensure_cache_loaded()
            # if self._cached_memory_data is None: return # Still nothing to save
            return

        self._cached_memory_data.last_saved_at = default_aware_utcnow()
        
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock: # Ensure exclusive file access during write
            try:
                # model_dump_json is Pydantic V2, ensure_ascii=False for proper UTF-8
                json_data = self._cached_memory_data.model_dump_json(indent=2)
                async with aiofiles.open(self.memory_file_path, mode='w', encoding='utf-8') as f:
                    await f.write(json_data)
                self._is_dirty = False # Mark as not dirty after successful save
                logger.info(f"Successfully saved memory for NPC '{self.npc_id}' to {self.memory_file_path}")
            except Exception as e:
                logger.error(f"Failed to save memory for NPC '{self.npc_id}' to {self.memory_file_path}: {e}", exc_info=True)
                # If save fails, data remains dirty, could retry later or log for manual intervention

    def _mark_dirty(self) -> None:
        """Marks the cached memory data as dirty, requiring a save."""
        if not self._is_dirty:
            logger.debug(f"Memory for NPC '{self.npc_id}' marked as dirty.")
            self._is_dirty = True
        # No immediate save here, save will be triggered by save_memory_to_file explicitly,
        # or by a background task, or on shutdown.

    # --- Public methods to modify memory (examples) ---

    async def update_npc_name_and_personality(self, name: Optional[str], personality: Optional[str]):
        memory = await self.get_memory_data()
        changed = False
        if name and memory.name != name:
            memory.name = name
            changed = True
        if personality and memory.personality_description != personality:
            memory.personality_description = personality
            changed = True
        if changed:
            self._mark_dirty()
            # Decide if this kind of update triggers an immediate save or relies on periodic/shutdown save
            # await self.save_memory_to_file() # Example of immediate save after this specific change

    async def update_snapshots(self, game_time: GameTime, position: Position):
        memory = await self.get_memory_data()
        memory.last_known_game_time = game_time
        memory.last_known_position = position
        self._mark_dirty()

    async def add_visited_location(self, x: float, y: float, timestamp: datetime):
        memory = await self.get_memory_data()
        new_location = VisitedLocationEntry(x=x, y=y, timestamp_visited=timestamp)
        
        memory.short_term_location_history.append(new_location)
        memory.short_term_location_history = memory.short_term_location_history[-settings.MAX_LOCATIONS_IN_MEMORY:]
        self._mark_dirty()
        logger.debug(f"NPC '{self.npc_id}': Added visited location ({x:.1f}, {y:.1f}) at {timestamp.isoformat()}")

    async def has_been_visited_recently(self, x: float, y: float, current_game_time: GameTime) -> bool:
        memory = await self.get_memory_data()
        for loc in memory.short_term_location_history:
            # Ensure comparison is between Position objects if loc is just a dict
            loc_pos = Position(x=loc.x, y=loc.y)
            target_pos = Position(x=x, y=y)
            distance = loc_pos.distance_to(target_pos)
            
            time_since_visit = current_game_time.current_timestamp - loc.timestamp_visited
            if distance < settings.VISIT_THRESHOLD_DISTANCE and \
               time_since_visit.total_seconds() < settings.REVISIT_INTERVAL_SECONDS:
                logger.debug(f"Location ({x:.1f}, {y:.1f}) for NPC '{self.npc_id}' is RECENTLY VISITED (visited {time_since_visit.total_seconds():.0f}s ago).")
                return True
        return False

    async def update_emotional_state(self, new_primary_emotion: str, intensity: float, reason: Optional[str] = None, mood_tags: Optional[List[str]]=None):
        memory = await self.get_memory_data()
        memory.current_emotional_state = NPCEmotionalState(
            primary_emotion=new_primary_emotion,
            intensity=intensity,
            mood_tags=mood_tags if mood_tags is not None else memory.current_emotional_state.mood_tags, # Preserve old if not provided
            last_significant_change_at=default_aware_utcnow(),
            reason_for_last_change=reason
        )
        self._mark_dirty()
        logger.info(f"NPC '{self.npc_id}' emotional state updated to: {new_primary_emotion} (Intensity: {intensity:.1f}). Reason: {reason or 'N/A'}")

    async def add_long_term_memory(self, content: str, memory_type: str, keywords: Optional[List[str]]=None, related_ids: Optional[List[str]]=None):
        memory = await self.get_memory_data()
        new_ltm = LongTermMemoryEntry(
            content_text=content,
            memory_type_tag=memory_type,
            keywords=keywords or [],
            related_npc_ids=related_ids or []
        )
        memory.long_term_event_memories.append(new_ltm)
        memory.long_term_event_memories = memory.long_term_event_memories[-settings.MAX_LONG_TERM_MEMORY_ENTRIES:]
        self._mark_dirty()
        logger.info(f"NPC '{self.npc_id}' added LTM: '{content[:50]}...' (Type: {memory_type})")

    async def get_relevant_long_term_memories(self, # query_context: Optional[str] = None, # For future LLM-based retrieval
                                             limit: int = 5) -> List[LongTermMemoryEntry]:
        memory = await self.get_memory_data()
        if not memory.long_term_event_memories:
            return []
        # Simple retrieval: newest N memories.
        # TODO: Implement more sophisticated retrieval (keyword-based, or LLM-based semantic search if embeddings are used).
        return sorted(memory.long_term_event_memories, key=lambda m: m.timestamp_created, reverse=True)[:limit]


    async def clear_all_memory_data_file(self) -> bool:
        """Deletes the NPC's memory file from disk. Cache will be invalidated on next get."""
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock:
            if self.memory_file_path.exists():
                try:
                    # Use aiofiles for async unlink if preferred, though os.unlink is often fast enough
                    # For consistency with aiofiles usage elsewhere:
                    # await aiofiles.os.remove(self.memory_file_path)
                    self.memory_file_path.unlink() # Pathlib's unlink is synchronous
                    logger.info(f"Memory file for NPC '{self.npc_id}' at {self.memory_file_path} DELETED successfully.")
                    self._cached_memory_data = None # Invalidate cache
                    self._is_dirty = False
                    if self.npc_id in _INSTANTIATED_MEMORY_SERVICES: # Remove from global tracker if present
                        del _INSTANTIATED_MEMORY_SERVICES[self.npc_id]
                    return True
                except Exception as e:
                    logger.error(f"Failed to delete memory file for NPC '{self.npc_id}': {e}", exc_info=True)
                    return False
            else:
                logger.info(f"No memory file found at {self.memory_file_path} to clear for NPC '{self.npc_id}'. Considered cleared.")
                self._cached_memory_data = None # Invalidate cache even if file didn't exist
                self._is_dirty = False
                return True

# --- Functions for managing all dirty NPC memories (to be called from main.py lifespan) ---
async def save_all_dirty_npc_memories():
    """
    Iterates through all instantiated NPCMemoryService instances known to be dirty
    and saves their memory to file. This is crucial for shutdown.
    """
    logger.info(f"Attempting to save all dirty NPC memories. Known instances: {len(_INSTANTIATED_MEMORY_SERVICES)}")
    saved_count = 0
    # Create a copy of items to iterate over, as save_memory_to_file might modify the dict (e.g., on error)
    # However, _INSTANTIATED_MEMORY_SERVICES should ideally only be modified by the service's own lifecycle
    # For now, a simple iteration:
    # This relies on _INSTANTIATED_MEMORY_SERVICES being populated correctly.
    # A robust system might involve a dedicated "dirty manager" or event bus.
    
    # Create a list of tasks to save dirty memories concurrently
    save_tasks = []
    for npc_id, service_instance in list(_INSTANTIATED_MEMORY_SERVICES.items()): # Iterate over a copy
        if service_instance._is_dirty or service_instance._cached_memory_data is not None: # Save if dirty or if loaded and might have been missed
            logger.debug(f"Shutdown save: Checking NPC '{npc_id}'. Dirty: {service_instance._is_dirty}")
            # We call force_save=True on shutdown to ensure even non-dirty but loaded data is flushed
            # if it represents the latest state.
            # However, if _is_dirty is the sole source of truth for "needs saving", then only save if dirty.
            if service_instance._is_dirty:
                 save_tasks.append(service_instance.save_memory_to_file(force_save=True)) # Force save on shutdown if dirty


    if save_tasks:
        results = await asyncio.gather(*save_tasks, return_exceptions=True)
        for i, result in enumerate(results):
            # Find corresponding npc_id for logging (this is a bit clunky)
            # A better way: tasks could return their npc_id or be part of a dict
            # For now, just log general success/failure count
            if isinstance(result, Exception):
                logger.error(f"Shutdown save: Error saving memory for an NPC: {result}", exc_info=result)
            else:
                saved_count +=1
        logger.info(f"Shutdown save: Processed {len(save_tasks)} potential saves. Successfully saved: {saved_count}.")
    else:
        logger.info("Shutdown save: No dirty NPC memories found to save.")

# --- Dependency Injection for Routers ---
# This is tricky because NPCMemoryService is per-NPC.
# Routers will typically get npc_id from path/body and then instantiate this service.
# Example:
# async def get_npc_memory_service_for_request(npc_id: str = Path(...)) -> NPCMemoryService:
#     service = NPCMemoryService(npc_id=npc_id)
#     # Add to _INSTANTIATED_MEMORY_SERVICES if not already there by get_memory_data
#     # _INSTANTIATED_MEMORY_SERVICES[npc_id] = service # This line needs careful thought about when/how instances are globally managed
#     return service