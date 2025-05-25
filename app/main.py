# npc_api_suite/app/main.py

from fastapi import FastAPI, Request, status, HTTPException as FastAPIHTTPException
from fastapi.responses import JSONResponse
from fastapi.middleware.cors import CORSMiddleware
from contextlib import asynccontextmanager
import time
import uuid # For request ID in middleware example
import ollama # For ollama.ResponseError type hinting

# --- Core Application Imports ---
from app.core.config import settings_instance as settings
from app.core.logging_config import main_app_logger # Use the pre-configured main app logger
from app.llm.ollama_client import OllamaService
from app.core.schemas import ( # Import necessary Pydantic models for responses
    HealthStatusResponse, APIErrorResponse, APIErrorDetail
)
# Import service needed for shutdown hook
from app.services.npc_memory_service import save_all_dirty_npc_memories

# --- API Routers ---
from app.routers import chat as router_chat
from app.routers import movement as router_movement
from app.routers import admin as router_admin

# --- Lifespan Management (Application Startup and Shutdown Events) ---
@asynccontextmanager
async def lifespan(app_instance: FastAPI):
    """
    Manages application startup and shutdown events.
    - Initializes Ollama client on startup.
    - Saves dirty NPC memories and closes Ollama client on shutdown.
    """
    main_app_logger.info(f"----- Starting up {settings.API_TITLE} v{settings.API_VERSION} -----")
    main_app_logger.info(f"Server Host: {settings.SERVER_HOST}, Port: {settings.SERVER_PORT}")
    main_app_logger.info(f"Ollama Host configured at: {settings.OLLAMA_HOST}")
    main_app_logger.info(f"Log Level set to: {settings.LOG_LEVEL}")
    if settings.LOG_TO_FILE:
        main_app_logger.info(f"Logging to file: {settings.LOG_FILE_PATH.resolve()}")
    
    # Initialize Ollama Service Client
    await OllamaService.initialize_client()
    if not OllamaService.is_ready():
        main_app_logger.critical(
            "FATAL: Ollama Client failed to initialize or connect! "
            "LLM-dependent features will be unavailable. Please check Ollama server and configuration."
        )
        # Depending on criticality, you might choose to exit here or let the app run in a degraded state.
        # For now, it will run but Ollama calls will fail.
    else:
        main_app_logger.info("Ollama Client initialized and connection verified successfully.")
    
    # --- Application is now running ---
    yield
    # --- Application is shutting down ---
    main_app_logger.info(f"----- Shutting down {settings.API_TITLE} -----")
    
    if settings.NPC_MEMORY_SAVE_ON_SHUTDOWN:
        main_app_logger.info("Attempting to save all outstanding NPC memories...")
        try:
            await save_all_dirty_npc_memories() # Defined in npc_memory_service.py
            main_app_logger.info("Finished saving outstanding NPC memories.")
        except Exception as e_mem_save:
            main_app_logger.error(f"Error during shutdown save of NPC memories: {e_mem_save}", exc_info=True)
            
    await OllamaService.close_client()
    main_app_logger.info("Ollama Client has been closed.")
    main_app_logger.info(f"{settings.API_TITLE} has been shut down gracefully.")

# --- Create FastAPI Application Instance ---
app = FastAPI(
    title=settings.API_TITLE,
    description=settings.API_DESCRIPTION,
    version=settings.API_VERSION,
    lifespan=lifespan, # Register the lifespan context manager
    #openapi_url="/api/v1/openapi.json", # Optional: customize OpenAPI spec URL
    #docs_url="/documentation", # Optional: customize Swagger UI URL
    #redoc_url="/redoc", # Optional: customize ReDoc URL
)

# --- Middleware Configuration ---

# CORS (Cross-Origin Resource Sharing)
if settings.ENABLE_CORS:
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.ALLOWED_ORIGINS if isinstance(settings.ALLOWED_ORIGINS, list) else [str(settings.ALLOWED_ORIGINS)], # Ensure it's a list of strings
        allow_credentials=True,
        allow_methods=["*"], # Allows all standard methods
        allow_headers=["*"], # Allows all headers
    )
    main_app_logger.info(f"CORS enabled. Allowed origins: {settings.ALLOWED_ORIGINS}")

# Middleware to add X-Request-ID and X-Process-Time-Ms headers
@app.middleware("http")
async def http_request_middleware(request: Request, call_next):
    request_id = str(uuid.uuid4())
    request.state.request_id = request_id # Make request_id available to route handlers if needed

    start_time = time.perf_counter()
    main_app_logger.debug(f"Request ID: {request_id} - START {request.method} {request.url.path}")

    response = await call_next(request) # Process the request

    process_time_ms = (time.perf_counter() - start_time) * 1000
    response.headers["X-Process-Time-Ms"] = f"{process_time_ms:.3f}"
    response.headers["X-Request-ID"] = request_id
    
    main_app_logger.info(
        f"Request ID: {request_id} - END {request.method} {request.url.path} - Status: {response.status_code} - Processed in: {process_time_ms:.3f}ms"
    )
    return response

# --- Global Exception Handlers ---

@app.exception_handler(FastAPIHTTPException) # Handles exceptions raised by FastAPI itself or our code (raise HTTPException)
async def custom_fastapi_http_exception_handler(request: Request, exc: FastAPIHTTPException):
    request_id = getattr(request.state, 'request_id', 'N/A')
    main_app_logger.warning(
        f"Request ID: {request_id} - FastAPIHTTPException: Status={exc.status_code}, Detail='{exc.detail}' for {request.method} {request.url.path}",
        exc_info=False # Typically, HTTPException details are sufficient, no full stack trace needed unless debugging
    )
    return JSONResponse(
        status_code=exc.status_code,
        content=APIErrorResponse(error=APIErrorDetail(message=exc.detail, error_code=f"HTTP_{exc.status_code}")).model_dump(),
        headers=getattr(exc, "headers", None), # Preserve headers if any from original HTTPException
    )

@app.exception_handler(ollama.ResponseError) # Handles specific errors from the Ollama client library
async def ollama_client_response_error_handler(request: Request, exc: ollama.ResponseError):
    request_id = getattr(request.state, 'request_id', 'N/A')
    status_code_to_return = exc.status_code if exc.status_code and 400 <= exc.status_code < 600 else status.HTTP_503_SERVICE_UNAVAILABLE
    
    log_message = (
        f"Request ID: {request_id} - Ollama Service Response Error: HTTP Status={exc.status_code}, "
        f"Ollama Error='{exc.error if exc.error else 'Unknown Ollama Error'}' for {request.method} {request.url.path}"
    )
    if status_code_to_return >= 500:
        main_app_logger.error(log_message, exc_info=True)
    else:
        main_app_logger.warning(log_message)

    detail_message = f"Ollama service interaction failed: {exc.error or 'Please check Ollama server status.'}"
    if exc.status_code == 404:
        detail_message = f"Ollama model not found or Ollama endpoint unavailable: {exc.error or 'Ensure model is pulled and Ollama server is correct.'}"
    
    return JSONResponse(
        status_code=status_code_to_return,
        content=APIErrorResponse(error=APIErrorDetail(message=detail_message, error_code="OLLAMA_API_ERROR")).model_dump()
    )

@app.exception_handler(ConnectionError) # Handles generic ConnectionErrors (e.g., OllamaService.get_client fails)
async def python_connection_error_handler(request: Request, exc: ConnectionError):
    request_id = getattr(request.state, 'request_id', 'N/A')
    main_app_logger.critical(
        f"Request ID: {request_id} - Python ConnectionError: Message='{str(exc)}' for {request.method} {request.url.path}",
        exc_info=True
    )
    return JSONResponse(
        status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
        content=APIErrorResponse(error=APIErrorDetail(message=f"A required external service is unreachable: {str(exc)}", error_code="SERVICE_CONNECTION_ERROR")).model_dump()
    )

@app.exception_handler(Exception) # Catch-all for any other unhandled Python exceptions
async def unhandled_python_exception_handler(request: Request, exc: Exception):
    request_id = getattr(request.state, 'request_id', 'N/A')
    main_app_logger.error(
        f"Request ID: {request_id} - Unhandled Python Exception: Type={type(exc).__name__}, Message='{str(exc)}' for {request.method} {request.url.path}",
        exc_info=True # Log the full stack trace for unhandled exceptions
    )
    return JSONResponse(
        status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
        content=APIErrorResponse(error=APIErrorDetail(message=f"An unexpected internal server error occurred. Ref ID: {request_id}", error_code="UNHANDLED_SERVER_ERROR")).model_dump()
    )


# --- Include API Routers ---
app.include_router(router_chat.router)
app.include_router(router_movement.router)
app.include_router(router_admin.router)
main_app_logger.info("API routers included: Dialogue, Movement, Admin.")

# --- Root Path Health Check Endpoint ---
@app.get("/", response_model=HealthStatusResponse, tags=["Health Check"])
async def root_health_check():
    """
    Provides a basic health check of the API and its connection to Ollama.
    """
    ollama_conn_status = "Unknown"
    if OllamaService.is_ready(): # Check if client was successfully initialized
        try:
            # A light check, like listing models (or just rely on is_ready flag)
            await OllamaService.list_available_models(log_success=False) # Don't log full list here
            ollama_conn_status = "Connected and Responsive"
        except Exception:
            ollama_conn_status = "Initialization Succeeded but Currently Unresponsive"
    else:
        ollama_conn_status = "Disconnected or Initialization Failed"

    return HealthStatusResponse(
        status="ok",
        api_version=settings.API_VERSION,
        service_name=settings.API_TITLE,
        ollama_connection_status=ollama_conn_status
    )

# --- Main Entry Point for Uvicorn (if running directly with `python app/main.py`) ---
if __name__ == "__main__":
    import uvicorn
    
    # Ensure logger is fully set up before Uvicorn takes over logging for its own messages
    main_app_logger.info(
        f"Attempting to start Uvicorn server for '{settings.API_TITLE}' on http://{settings.SERVER_HOST}:{settings.SERVER_PORT}"
    )
    if settings.DEBUG_RELOAD:
        main_app_logger.warning("Uvicorn --reload is ENABLED. Recommended for development only.")

    uvicorn.run(
        "app.main:app", # Points to the FastAPI instance `app` in this file (`app.main`)
        host=settings.SERVER_HOST,
        port=settings.SERVER_PORT,
        reload=settings.DEBUG_RELOAD,
        log_level=settings.LOG_LEVEL.lower(), # Pass our log level to Uvicorn
        # access_log=True, # Uvicorn's access logging, can be verbose
        # use_colors=True, # For Uvicorn's own logs
    )