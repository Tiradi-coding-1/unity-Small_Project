# npc_api_suite/app/routers/movement.py

from fastapi import APIRouter, Depends, HTTPException, Body, status, Path as FastAPIPath
import time
import ollama # For ollama.ResponseError

from app.core.schemas import (
    NPCMovementRequest, NPCMovementResponse, NPCIdentifier # 從 schemas 導入
)
from app.llm.ollama_client import OllamaService, get_ollama_service # OllamaService 依賴
from app.services.npc_memory_service import NPCMemoryService # NPCMemoryService
from app.services.movement_service import MovementService # MovementService
from app.core.logging_config import setup_logging # 日誌
from app.core.config import settings_instance as settings # 設定檔

logger = setup_logging(__name__) # 此路由器的 Logger

router = APIRouter(
    prefix="/npc", # 沿用您原來的 /npc 前綴
    tags=["NPC Movement Engine"] # API 文件中的標籤
)

# --- 依賴注入 (Dependency Injection for Services) ---

async def get_movement_service(
    ollama_s: OllamaService = Depends(get_ollama_service) # MovementService 依賴 OllamaService
) -> MovementService:
    """FastAPI Dependency to get an instance of MovementService."""
    return MovementService(ollama_s=ollama_s)

# NPCMemoryService 是針對特定 NPC 的，所以它通常在端點內部根據 npc_id 實例化。
# 我們不在這裡創建通用的 get_npc_memory_service 依賴，
# 因為它需要 npc_id，而 npc_id 來自請求。

# --- API 端點 (API Endpoint for NPC Movement Decision) ---

@router.post(
    "/think",
    response_model=NPCMovementResponse,
    summary="NPC Movement Decision",
    description="Allows an NPC to decide its next movement based on its current state, environment, memory, dialogue context, and LLM-driven reasoning."
)
async def npc_think_and_decide_movement(
    request: NPCMovementRequest, # Request body contains npc_id and all other necessary data
    movement_s: MovementService = Depends(get_movement_service) # Inject MovementService
):
    """
    Handles NPC movement decision requests.

    The request body (`NPCMovementRequest`) must contain:
    - `npc_id` and `name` (optional) for identification.
    - `current_npc_position`: The NPC's current coordinates.
    - `current_game_time`: The current in-game time.
    - `nearby_entities`: Information about other characters or players nearby.
    - `visible_landmarks`: Information about significant landmarks in the scene.
    - `scene_boundaries`: The traversable boundaries of the current scene.
    - Optional context like `recent_dialogue_summary_for_movement` and `explicit_player_movement_request`.
    - Optional `model_override` for the LLM.

    The service will load the NPC's persisted memory (personality, emotional state, schedule, history),
    combine it with the request data, generate a prompt for the LLM, parse the LLM's structured
    decision, apply game logic (like avoiding recently visited spots or respecting boundaries),
    use fallback strategies if needed, update the NPC's memory, and return the decision.
    """
    request_start_time = time.perf_counter()
    
    # Pydantic 已經驗證了 npc_id 的存在 (因為它在 NPCMovementRequest 中是必需的)
    # 但如果 name 為空，日誌中會顯示 'N/A'
    logger.info(
        f"NPC think request received for ID: '{request.npc_id}', Name: '{request.name or 'N/A'}'. "
        f"Current Pos: ({request.current_npc_position.x:.1f}, {request.current_npc_position.y:.1f}), "
        f"Game Time: {request.current_game_time.time_of_day}."
    )

    # 為此請求實例化特定 NPC 的 NPCMemoryService
    # NPCMemoryService 實例的生命週期限定在此請求內
    # 其內部的快取和檔案鎖機制 (_NPC_FILE_LOCKS) 處理對同一 NPC 記憶檔案的併發訪問
    npc_memory_service = NPCMemoryService(npc_id=request.npc_id)
    
    try:
        # 調用 MovementService 來獲取決策
        npc_movement_response: NPCMovementResponse = await movement_s.decide_npc_movement(
            request_data=request,
            memory_service=npc_memory_service # 傳入為此 NPC 實例化的記憶服務
        )
        
        # api_processing_time_ms 已經在 MovementService 中計算並賦值
        router_total_processing_time_ms = (time.perf_counter() - request_start_time) * 1000
        
        logger.info(
            f"NPC '{request.npc_id}' movement decision processed. "
            f"Target: ({npc_movement_response.target_destination.x:.1f}, {npc_movement_response.target_destination.y:.1f}). "
            f"Action: '{npc_movement_response.chosen_action_summary}'. "
            f"Service Time: {npc_movement_response.api_processing_time_ms:.2f}ms. "
            f"Router Total Time: {router_total_processing_time_ms:.2f}ms."
        )
        
        return npc_movement_response
        
    except ConnectionError as e: # Ollama 客戶端未初始化或連接失敗
        logger.error(f"Ollama connection error during NPC think for '{request.npc_id}': {e}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=f"Ollama service is currently unavailable: {str(e)}"
        )
    except ollama.ResponseError as e: # Ollama API 返回錯誤
        logger.error(f"Ollama API error for NPC '{request.npc_id}': Status {e.status_code} - {e.error}", exc_info=True)
        raise HTTPException(
            status_code=e.status_code or status.HTTP_500_INTERNAL_SERVER_ERROR, # Use Ollama's status if available
            detail=f"Ollama API error: {e.error}"
        )
    except ValueError as ve: # 例如 Pydantic 驗證錯誤，或我們自己邏輯中的 ValueError
        logger.warning(f"Value error or validation issue during NPC think for '{request.npc_id}': {ve}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, # Unprocessable Entity
            detail=str(ve)
        )
    except Exception as e: # 捕獲所有其他未預期的錯誤
        # 對於未預期錯誤，記錄為 CRITICAL，因為它們可能表示程式碼中的 bug
        request_summary_for_log = request.model_dump(exclude={'nearby_entities', 'visible_landmarks'}) # 排除可能過大的列表
        logger.critical(
            f"Unexpected critical error during NPC think for '{request.npc_id}'. "
            f"Request (summary): {request_summary_for_log}. Error: {e}",
            exc_info=True
        )
        # 為了安全，不要在回應中暴露過多內部錯誤細節
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=(
                f"An unexpected server error occurred while processing the NPC decision for '{request.npc_id}'. "
                f"Please contact support if the issue persists. Error type: {type(e).__name__}"
            )
        )