# npc_api_suite/app/services/npc_memory_service.py

import json
import asyncio # For asyncio.Lock
from pathlib import Path
from datetime import datetime, timezone, timedelta
import math # For distance calculations
from typing import Optional, List, Dict, Tuple, Any, Set
import aiofiles # For asynchronous file operations

from app.core.config import settings_instance as settings
from app.core.logging_config import setup_logging
from app.core.schemas import (
    NPCMemoryFile, VisitedLocationEntry, NPCEmotionalState, Position,
    LongTermMemoryEntry, GameTime, NPCScheduleRule, default_aware_utcnow
)

logger = setup_logging(__name__)

_NPC_FILE_LOCKS: Dict[str, asyncio.Lock] = {}

def _get_npc_file_lock(npc_id: str) -> asyncio.Lock:
    """Gets or creates an asyncio.Lock for a given NPC ID."""
    if npc_id not in _NPC_FILE_LOCKS:
        _NPC_FILE_LOCKS[npc_id] = asyncio.Lock()
    return _NPC_FILE_LOCKS[npc_id]

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
        self._is_dirty: bool = False
        self._load_lock = asyncio.Lock()
        
        _INSTANTIATED_MEMORY_SERVICES[self.npc_id] = self
        # logger.debug(f"NPCMemoryService instance created for {self.npc_id}. File: {self.memory_file_path}")

    async def _load_memory_from_file(self) -> NPCMemoryFile:
        """
        Loads NPC memory from its JSON file using aiofiles.
        This is the actual disk read operation.
        """
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock:
            if not self.memory_file_path.exists() or self.memory_file_path.stat().st_size == 0:
                logger.info(f"Memory file for NPC '{self.npc_id}' not found or empty. Initializing new memory.")
                return NPCMemoryFile(
                    npc_id=self.npc_id,
                    name=self.npc_id,
                    last_saved_at=default_aware_utcnow()
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
            async with self._load_lock:
                if self._cached_memory_data is None:
                    logger.debug(f"Cache miss for NPC '{self.npc_id}'. Loading from disk.")
                    self._cached_memory_data = await self._load_memory_from_file()
                    self._is_dirty = False
                    _INSTANTIATED_MEMORY_SERVICES[self.npc_id] = self

    async def get_memory_data(self) -> NPCMemoryFile:
        """
        Retrieves the NPC's memory data, loading it into cache if necessary.
        This is the primary method to access an NPC's memory.
        """
        await self._ensure_cache_loaded()
        if self._cached_memory_data is None:
            logger.error(f"CRITICAL: Memory cache for NPC '{self.npc_id}' is None after ensure_loaded. This indicates a problem.")
            # This should ideally not happen if _ensure_cache_loaded works correctly.
            # If it does, re-initializing or raising a more specific error might be needed.
            # For now, returning a default to prevent None propagation, but this signals a deeper issue.
            # return NPCMemoryFile(npc_id=self.npc_id, name=self.npc_id, last_saved_at=default_aware_utcnow())
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
            return

        self._cached_memory_data.last_saved_at = default_aware_utcnow() # Real-world save time
        
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock:
            try:
                json_data = self._cached_memory_data.model_dump_json(indent=2)
                async with aiofiles.open(self.memory_file_path, mode='w', encoding='utf-8') as f:
                    await f.write(json_data)
                self._is_dirty = False
                logger.info(f"Successfully saved memory for NPC '{self.npc_id}' to {self.memory_file_path}")
            except Exception as e:
                logger.error(f"Failed to save memory for NPC '{self.npc_id}' to {self.memory_file_path}: {e}", exc_info=True)

    def _mark_dirty(self) -> None:
        """Marks the cached memory data as dirty, requiring a save."""
        if not self._is_dirty:
            logger.debug(f"Memory for NPC '{self.npc_id}' marked as dirty.")
            self._is_dirty = True

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

    async def update_snapshots(self, game_time: GameTime, position: Position):
        memory = await self.get_memory_data()
        memory.last_known_game_time = game_time
        memory.last_known_position = position
        self._mark_dirty()

    # MODIFIED: Changed 'timestamp: datetime' to 'game_timestamp: datetime' for clarity
    async def add_visited_location(self, x: float, y: float, game_timestamp: datetime):
        memory = await self.get_memory_data()
        # MODIFIED: 'timestamp_visited' now uses the passed 'game_timestamp'
        new_location = VisitedLocationEntry(x=x, y=y, timestamp_visited=game_timestamp)
        
        memory.short_term_location_history.append(new_location)
        memory.short_term_location_history = memory.short_term_location_history[-settings.MAX_LOCATIONS_IN_MEMORY:]
        self._mark_dirty()
        # MODIFIED: Log with game_timestamp
        logger.debug(f"NPC '{self.npc_id}': Added visited location ({x:.1f}, {y:.1f}) at game time {game_timestamp.isoformat()}")

    # MODIFIED: Parameter 'current_game_time_dt: datetime' explicitly expects the datetime object from GameTime
    async def has_been_visited_recently(self, x: float, y: float, current_game_time_dt: datetime) -> bool:
        memory = await self.get_memory_data()
        for loc_entry in memory.short_term_location_history:
            # loc_entry.timestamp_visited is already a datetime object due to Pydantic model
            # current_game_time_dt is also a datetime object
            
            # Create Position objects for distance calculation
            loc_pos = Position(x=loc_entry.x, y=loc_entry.y)
            target_pos = Position(x=x, y=y)
            distance = loc_pos.distance_to(target_pos)
            
            # Ensure both datetime objects are comparable (e.g., both aware or both naive)
            # Pydantic models with default_aware_utcnow and GameTime.current_timestamp (if parsed from ISO string)
            # should generally produce aware datetime objects.
            if loc_entry.timestamp_visited.tzinfo is None and current_game_time_dt.tzinfo is not None:
                # This case should be rare if GameTime.current_timestamp is always from Unity (ISO string -> aware datetime)
                # and VisitedLocationEntry.timestamp_visited is also stored as aware.
                # However, as a safeguard:
                loc_timestamp_aware = loc_entry.timestamp_visited.replace(tzinfo=timezone.utc)
                logger.warning(f"NPC '{self.npc_id}': Naive timestamp encountered in location history. Assuming UTC for comparison.")
            elif loc_entry.timestamp_visited.tzinfo is not None and current_game_time_dt.tzinfo is None:
                current_game_time_dt_aware = current_game_time_dt.replace(tzinfo=timezone.utc)
                logger.warning(f"NPC '{self.npc_id}': Naive current_game_time_dt encountered. Assuming UTC for comparison.")
            else:
                loc_timestamp_aware = loc_entry.timestamp_visited
                current_game_time_dt_aware = current_game_time_dt

            time_since_visit = current_game_time_dt_aware - loc_timestamp_aware # MODIFIED
            
            if distance < settings.VISIT_THRESHOLD_DISTANCE and \
               time_since_visit.total_seconds() >= 0 and \
               time_since_visit.total_seconds() < settings.REVISIT_INTERVAL_SECONDS: # MODIFIED: Ensure time_since_visit is not negative
                logger.debug(f"Location ({x:.1f}, {y:.1f}) for NPC '{self.npc_id}' is RECENTLY VISITED (visited {time_since_visit.total_seconds():.0f} game seconds ago).")
                return True
        return False

    async def update_emotional_state(self, new_primary_emotion: str, intensity: float, reason: Optional[str] = None, mood_tags: Optional[List[str]]=None):
        memory = await self.get_memory_data()
        memory.current_emotional_state = NPCEmotionalState(
            primary_emotion=new_primary_emotion,
            intensity=intensity,
            mood_tags=mood_tags if mood_tags is not None else memory.current_emotional_state.mood_tags,
            last_significant_change_at=default_aware_utcnow(), # Real-world time for this change
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
            # timestamp_created uses default_aware_utcnow() from schema
        )
        memory.long_term_event_memories.append(new_ltm)
        memory.long_term_event_memories = memory.long_term_event_memories[-settings.MAX_LONG_TERM_MEMORY_ENTRIES:]
        self._mark_dirty()
        logger.info(f"NPC '{self.npc_id}' added LTM: '{content[:50]}...' (Type: {memory_type})")

    async def get_relevant_long_term_memories(self, limit: int = 5) -> List[LongTermMemoryEntry]:
        memory = await self.get_memory_data()
        if not memory.long_term_event_memories:
            return []
        return sorted(memory.long_term_event_memories, key=lambda m: m.timestamp_created, reverse=True)[:limit]

    async def clear_all_memory_data_file(self) -> bool:
        """Deletes the NPC's memory file from disk. Cache will be invalidated on next get."""
        file_lock = _get_npc_file_lock(self.npc_id)
        async with file_lock:
            if self.memory_file_path.exists():
                try:
                    self.memory_file_path.unlink()
                    logger.info(f"Memory file for NPC '{self.npc_id}' at {self.memory_file_path} DELETED successfully.")
                    self._cached_memory_data = None
                    self._is_dirty = False
                    if self.npc_id in _INSTANTIATED_MEMORY_SERVICES:
                        del _INSTANTIATED_MEMORY_SERVICES[self.npc_id]
                    return True
                except Exception as e:
                    logger.error(f"Failed to delete memory file for NPC '{self.npc_id}': {e}", exc_info=True)
                    return False
            else:
                logger.info(f"No memory file found at {self.memory_file_path} to clear for NPC '{self.npc_id}'. Considered cleared.")
                self._cached_memory_data = None
                self._is_dirty = False
                return True

async def save_all_dirty_npc_memories():
    logger.info(f"Attempting to save all dirty NPC memories. Known instances: {len(_INSTANTIATED_MEMORY_SERVICES)}")
    saved_count = 0
    save_tasks = []
    
    # Iterate over a copy of items in case the dictionary is modified during iteration (though less likely here)
    for npc_id, service_instance in list(_INSTANTIATED_MEMORY_SERVICES.items()):
        # On shutdown, force_save might be true if we want to ensure even non-dirty loaded data is flushed.
        # However, sticking to _is_dirty is usually sufficient if all modifications correctly mark as dirty.
        if service_instance._is_dirty: # Only save if actually marked dirty
            logger.debug(f"Shutdown save: Queueing save for dirty NPC '{npc_id}'.")
            save_tasks.append(service_instance.save_memory_to_file(force_save=True)) # force_save=True to ensure it writes

    if save_tasks:
        results = await asyncio.gather(*save_tasks, return_exceptions=True)
        for i, result in enumerate(results):
            # This part of associating result with npc_id is a bit indirect here.
            # For more robust error reporting, task could carry npc_id.
            if isinstance(result, Exception):
                # We need a way to know which NPC's save failed if we want to log specific npc_id
                logger.error(f"Shutdown save: Error saving memory for an NPC: {result}", exc_info=result)
            else:
                saved_count +=1
        logger.info(f"Shutdown save: Processed {len(save_tasks)} potential saves. Successfully saved: {saved_count} dirty memories.")
    else:
        logger.info("Shutdown save: No dirty NPC memories found to save.")