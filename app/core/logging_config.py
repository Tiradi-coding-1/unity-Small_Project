# npc_api_suite/app/core/logging_config.py

import logging
import sys
from logging.handlers import RotatingFileHandler # 用於日誌輪替
from pathlib import Path
from typing import Optional, Set # 明確導入 Set

# 導入應用程式設定
from app.core.config import settings_instance as settings

# 創建一個集合來追蹤已經設定過的 logger 名稱，避免重複添加 handler
# 這在開發模式下（例如 Uvicorn 自動重載）尤其重要
_configured_loggers: Set[str] = set()

def _parse_log_rotation_size_to_bytes(size_str: str) -> int:
    """
    Parses log rotation size string (e.g., "10 MB", "500 KB", "1 GB") into bytes.
    Returns a default size (5MB) if parsing fails.
    """
    size_str_upper = size_str.strip().upper()
    num_part = ""
    unit_part = ""

    for char in size_str_upper:
        if char.isdigit() or char == '.':
            num_part += char
        else:
            unit_part += char
    
    unit_part = unit_part.strip()

    if not num_part:
        logger_instance = logging.getLogger(__name__) # 臨時 logger 用於此函數的警告
        logger_instance.warning(f"Could not parse numeric part from log rotation size '{size_str}'. Defaulting to 5MB.")
        return 5 * 1024 * 1024 # Default to 5MB

    try:
        num = float(num_part)
    except ValueError:
        logger_instance = logging.getLogger(__name__)
        logger_instance.warning(f"Invalid numeric value '{num_part}' in log rotation size '{size_str}'. Defaulting to 5MB.")
        return 5 * 1024 * 1024

    if "GB" in unit_part:
        return int(num * 1024 * 1024 * 1024)
    elif "MB" in unit_part:
        return int(num * 1024 * 1024)
    elif "KB" in unit_part:
        return int(num * 1024)
    elif "B" in unit_part or not unit_part: # Treat as bytes if "B" or no unit
        return int(num)
    else:
        logger_instance = logging.getLogger(__name__)
        logger_instance.warning(f"Unknown unit '{unit_part}' in log rotation size '{size_str}'. Assuming bytes. Defaulting to 5MB if num is also problematic.")
        return int(num) if num > 0 else 5*1024*1024


def setup_logging(name: Optional[str] = "app") -> logging.Logger:
    """
    Sets up and returns a logger instance with specified configuration.
    If a name is provided, a logger with that name is returned.
    If name is None or "app", it configures a main application logger.
    Prevents re-configuring a logger if it has already been set up by this function.
    """
    logger_name = name if name else "app_root" # Use "app_root" for the main app logger if name is None

    # 如果 logger 已經被此函數配置過，直接返回，避免重複 handlers
    if logger_name in _configured_loggers:
        return logging.getLogger(logger_name)

    log_level_str = settings.LOG_LEVEL.upper()
    log_level = getattr(logging, log_level_str, logging.INFO) # Default to INFO if invalid
    if not isinstance(log_level, int): # Double check if getattr returned something unexpected
        # This should not happen if LOG_LEVEL is one of the standard names
        logging.warning(f"Invalid LOG_LEVEL '{log_level_str}' in settings. Defaulting to INFO.")
        log_level = logging.INFO

    logger = logging.getLogger(logger_name)
    logger.setLevel(log_level)

    # 清除此 logger 實例上可能已存在的 handlers (主要為了應對 hot-reloading)
    # 確保我們是唯一設定 handler 的來源
    if logger.hasHandlers():
        # This print is for debugging the logging setup itself
        # print(f"Clearing existing handlers for logger '{logger_name}' before new setup.", file=sys.stderr)
        logger.handlers.clear()

    # 定義日誌格式
    formatter = logging.Formatter(
        '%(asctime)s [%(levelname)-8s] [%(name)s:%(lineno)d] %(message)s', # -8s for levelname padding
        datefmt='%Y-%m-%d %H:%M:%S'
    )

    # Stream Handler (輸出到控制台 - stdout)
    # 所有 logger 都應該有 stream handler，除非特定情況下不想讓它打印到控制台
    stream_handler = logging.StreamHandler(sys.stdout)
    stream_handler.setFormatter(formatter)
    stream_handler.setLevel(log_level) # Handler level should also be set
    logger.addHandler(stream_handler)

    # File Handler (輸出到檔案，可選，帶輪替功能)
    if settings.LOG_TO_FILE:
        log_file_path = settings.LOG_FILE_PATH
        
        # 目錄創建已在 config.py 中處理 (ensure_directories_exist)
        # 但再次確認父目錄存在是安全的
        log_file_path.parent.mkdir(parents=True, exist_ok=True)

        max_bytes = _parse_log_rotation_size_to_bytes(settings.LOG_ROTATION_SIZE)
        backup_count = settings.LOG_RETENTION_COUNT

        # RotatingFileHandler
        file_handler = RotatingFileHandler(
            filename=log_file_path,
            maxBytes=max_bytes,
            backupCount=backup_count,
            encoding='utf-8'
        )
        file_handler.setFormatter(formatter)
        file_handler.setLevel(log_level) # Handler level
        logger.addHandler(file_handler)
        
        # 初始啟動時，主 logger 打印一次日誌檔案位置
        if logger_name == "app_root" or (name is not None and name.endswith("main")): # Heuristic for main app logger
            logger.info(f"File logging enabled. Logs will be written to: {log_file_path.resolve()}")
            logger.info(f"Log rotation: maxBytes={max_bytes}, backupCount={backup_count}")


    # 防止日誌消息向父級 (root) logger 傳播，除非是 root logger 本身
    # 如果我們為 logger 設定了 handlers，通常不希望它再傳播，以避免重複打印
    logger.propagate = False

    _configured_loggers.add(logger_name) # 標記此 logger 已被配置
    
    # print(f"Logger '{logger_name}' setup complete. Level: {log_level_str}. Handlers: {logger.handlers}", file=sys.stderr)
    return logger

# 提供一個獲取主應用程式 logger 的便捷函數
# 可以在 app/main.py 中使用: from app.core.logging_config import main_app_logger
# main_app_logger.info(...)
main_app_logger = setup_logging("npc_api_suite_main") # 創建並配置主應用程式 logger