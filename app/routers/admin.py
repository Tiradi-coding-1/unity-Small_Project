# npc_api_suite/app/routers/admin.py

from fastapi import APIRouter, Depends, HTTPException, Path as FastAPIPath, Query, status
from typing import Optional, Dict, Any

from app.core.schemas import (
    ClearMemoryAdministrativelyResponse, # 從 schemas 導入
    NPCMemoryFile # For potentially viewing memory (example, not fully implemented here)
)
from app.services.npc_memory_service import NPCMemoryService # NPCMemoryService
from app.core.logging_config import setup_logging # 日誌
from app.core.config import settings_instance as settings # 獲取設定, e.g. NPC_MEMORY_DIR
# For future admin auth:
# from app.utils.security import require_admin_auth # Hypothetical auth dependency

logger = setup_logging(__name__) # 此路由器的 Logger

router = APIRouter(
    prefix="/admin",
    tags=["Administrative Tools"],
    # dependencies=[Depends(require_admin_auth)] # Example: Protect all admin routes
)

# --- NPC Memory Management Endpoints ---

@router.delete(
    "/npc/{npc_id}/memory",
    response_model=ClearMemoryAdministrativelyResponse,
    summary="Clear NPC Memory File",
    description="Deletes the persisted JSON memory file for a specific NPC. This action is irreversible."
)
async def clear_npc_memory_file_endpoint(
    npc_id: str = FastAPIPath(
        ..., # Ellipsis means it's a required path parameter
        min_length=1,
        description="The unique identifier of the NPC whose memory is to be cleared."
    )
):
    """
    Clears (deletes) the memory file for the specified NPC.
    This will effectively reset the NPC's persisted state (personality, history, emotion, etc.)
    to its default when next loaded, unless a new memory file is created or restored.
    """
    logger.warning(f"ADMIN ACTION: Request received to clear memory for NPC ID: '{npc_id}'")
    
    # Instantiate NPCMemoryService for the specific NPC
    # This service handles the actual file deletion logic
    memory_service = NPCMemoryService(npc_id=npc_id)
    
    try:
        cleared_successfully = await memory_service.clear_all_memory_data_file()
        
        if cleared_successfully:
            logger.info(f"ADMIN ACTION: Memory for NPC '{npc_id}' was successfully cleared (or was already non-existent).")
            return ClearMemoryAdministrativelyResponse(
                status="success",
                message=f"Memory file for NPC '{npc_id}' has been cleared."
            )
        else:
            # This case might occur if unlink fails for OS reasons, though clear_all_memory_data_file tries to catch exceptions
            logger.error(f"ADMIN ACTION: Failed to clear memory for NPC '{npc_id}' due to an unexpected issue in the service.")
            raise HTTPException(
                status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
                detail=f"Could not clear memory for NPC '{npc_id}'. Check server logs for details."
            )
    except Exception as e:
        logger.error(f"ADMIN ACTION: Exception during memory clearing for NPC '{npc_id}': {e}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"An error occurred while attempting to clear memory for NPC '{npc_id}': {type(e).__name__}"
        )

@router.get(
    "/npc/{npc_id}/memory",
    response_model=Optional[NPCMemoryFile], # Might be None if memory file doesn't exist
    summary="View NPC Memory File Content",
    description="Retrieves and displays the current persisted JSON memory file content for a specific NPC. For debugging and inspection only."
)
async def view_npc_memory_file_endpoint(
    npc_id: str = FastAPIPath(
        ...,
        min_length=1,
        description="The unique identifier of the NPC whose memory is to be viewed."
    )
):
    """
    Allows an administrator to view the raw memory data of an NPC.
    This is primarily for debugging and should be used with caution,
    as it exposes internal NPC state.
    """
    logger.info(f"ADMIN ACTION: Request received to view memory for NPC ID: '{npc_id}'")
    memory_service = NPCMemoryService(npc_id=npc_id)

    try:
        # Use the get_memory_data method which loads from file if not cached,
        # or returns the cached version.
        # The _load_memory_from_file is the direct file access part.
        # We want to see what's currently persisted or would be loaded.
        npc_memory_content = await memory_service.get_memory_data() # This loads into cache if not present

        if npc_memory_content.name == npc_id and npc_memory_content.personality_description == "A typical villager, generally neutral and follows routines.": # Heuristic for a newly initialized memory due to no file
            # If the memory file didn't exist, get_memory_data would return a default initialized NPCMemoryFile.
            # We can check if the file actually exists to differentiate.
            if not memory_service.memory_file_path.exists():
                logger.info(f"ADMIN ACTION: Memory file for NPC '{npc_id}' does not exist. Returning null.")
                # Returning None or an empty dict is fine, but Pydantic response_model expects NPCMemoryFile or None
                # So, we should return None here if the file truly doesn't exist
                # However, get_memory_data always returns an NPCMemoryFile object (default if file not found).
                # A better check is if it's a "default" object AND the file doesn't exist.
                # For simplicity, if the file doesn't exist, it means there's "no memory to view".
                # This check might be slightly off if a default memory was explicitly saved.
                # A more direct check:
                # raw_data = await memory_service._load_memory_from_file()
                # if raw_data uses default values and file didn't exist initially...
                # For now, simply returning what get_memory_data provides.
                # To be more accurate for "view persisted file":
                # We should try to load it directly without cache, or check file existence
                # This is a simplification:
                if not memory_service.memory_file_path.is_file(): # More accurate check
                     logger.info(f"ADMIN ACTION: No persisted memory file found for NPC '{npc_id}'.")
                     return None # Indicate no persisted data
            # Else, it's a default memory that might have been created in memory but not yet saved, or a truly default saved one.

        logger.info(f"ADMIN ACTION: Successfully retrieved memory content for NPC '{npc_id}'.")
        return npc_memory_content
    except FileNotFoundError: # Should be caught by NPCMemoryService, but as a safeguard
        logger.warning(f"ADMIN ACTION: Memory file for NPC '{npc_id}' not found when trying to view.")
        return None # Return None or 404
    except Exception as e:
        logger.error(f"ADMIN ACTION: Exception during memory viewing for NPC '{npc_id}': {e}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"An error occurred while attempting to view memory for NPC '{npc_id}': {type(e).__name__}"
        )

# Potentially add other admin endpoints:
# - Reload configuration (might be complex and require server restart for some settings)
# - Trigger save for all dirty NPC memories manually
# - View server status, Ollama connection status (already on root path "/")
# - List all known NPC IDs (would require iterating over files in NPC_MEMORY_DIR)

@router.post("/trigger-save-all-memories", summary="Manually Trigger Save All Dirty NPC Memories")
async def trigger_save_all_memories_endpoint():
    """
    Manually triggers the process to save all 'dirty' NPC memory instances to disk.
    This is normally handled on shutdown.
    """
    logger.info("ADMIN ACTION: Manual trigger for saving all dirty NPC memories.")
    from app.services.npc_memory_service import save_all_dirty_npc_memories # Import here to avoid circular dependency at module level
    
    try:
        await save_all_dirty_npc_memories()
        return {"status": "success", "message": "Attempted to save all dirty NPC memories. Check logs for details."}
    except Exception as e:
        logger.error(f"ADMIN ACTION: Error during manual trigger of save_all_dirty_npc_memories: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail="Error triggering save all memories.")