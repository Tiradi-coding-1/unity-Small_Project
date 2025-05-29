# npc_api_suite/app/core/logging_config.py

import logging
import sys
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Optional, Set
from datetime import datetime # NEW: Import datetime

# 導入應用程式設定
from app.core.config import settings_instance as settings

# 創建一個集合來追蹤已經設定過的 logger 名稱
_configured_loggers: Set[str] = set()

# NEW: 在模組加載時生成一次性的時間戳，用於該次伺服器運行的所有日誌檔案
# 這樣可以確保即使 setup_logging 被意外調用多次（針對不同logger），
# 主檔案日誌路徑對於同一次運行也是一致的。
_RUN_TIMESTAMP = datetime.now().strftime("%Y%m%d_%H%M%S")
_MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN: Optional[Path] = None


def _parse_log_rotation_size_to_bytes(size_str: str) -> int:
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
        # Use a temporary logger for this specific warning, as the main one might not be set up yet.
        temp_logger = logging.getLogger(f"{__name__}_parser")
        temp_logger.warning(f"Could not parse numeric part from log rotation size '{size_str}'. Defaulting to 5MB.")
        return 5 * 1024 * 1024
    try:
        num = float(num_part)
    except ValueError:
        temp_logger = logging.getLogger(f"{__name__}_parser")
        temp_logger.warning(f"Invalid numeric value '{num_part}' in log rotation size '{size_str}'. Defaulting to 5MB.")
        return 5 * 1024 * 1024
    if "GB" in unit_part: return int(num * 1024 * 1024 * 1024)
    elif "MB" in unit_part: return int(num * 1024 * 1024)
    elif "KB" in unit_part: return int(num * 1024)
    elif "B" in unit_part or not unit_part: return int(num)
    else:
        temp_logger = logging.getLogger(f"{__name__}_parser")
        temp_logger.warning(f"Unknown unit '{unit_part}' in log rotation size '{size_str}'. Assuming bytes. Defaulting to 5MB if num is problematic.")
        return int(num) if num > 0 else 5*1024*1024


def setup_logging(name: Optional[str] = "app") -> logging.Logger:
    global _MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN # NEW: Use the global variable

    logger_name = name if name else "app_root"
    if logger_name in _configured_loggers:
        return logging.getLogger(logger_name)

    log_level_str = settings.LOG_LEVEL.upper()
    log_level = getattr(logging, log_level_str, logging.INFO)
    if not isinstance(log_level, int):
        logging.warning(f"Invalid LOG_LEVEL '{log_level_str}' in settings. Defaulting to INFO.")
        log_level = logging.INFO

    logger = logging.getLogger(logger_name)
    logger.setLevel(log_level)
    if logger.hasHandlers():
        logger.handlers.clear()

    formatter = logging.Formatter(
        '%(asctime)s [%(levelname)-8s] [%(name)s:%(lineno)d] %(message)s',
        datefmt='%Y-%m-%d %H:%M:%S'
    )

    stream_handler = logging.StreamHandler(sys.stdout)
    stream_handler.setFormatter(formatter)
    stream_handler.setLevel(log_level)
    logger.addHandler(stream_handler)

    if settings.LOG_TO_FILE:
        # NEW: Determine the run-specific log file path only once for the main logger setup
        # or if it hasn't been determined yet for this run.
        if _MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN is None:
            base_log_path = settings.LOG_FILE_PATH
            log_dir = base_log_path.parent
            log_stem = base_log_path.stem
            log_suffix = base_log_path.suffix
            _MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN = log_dir / f"{log_stem}_{_RUN_TIMESTAMP}{log_suffix}"
        
        # Ensure the directory for the current run's log file exists
        # This also handles the base log directory from settings.
        _MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN.parent.mkdir(parents=True, exist_ok=True)

        max_bytes = _parse_log_rotation_size_to_bytes(settings.LOG_ROTATION_SIZE)
        backup_count = settings.LOG_RETENTION_COUNT

        file_handler = RotatingFileHandler(
            filename=_MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN, # MODIFIED: Use run-specific path
            maxBytes=max_bytes,
            backupCount=backup_count,
            encoding='utf-8'
        )
        file_handler.setFormatter(formatter)
        file_handler.setLevel(log_level)
        logger.addHandler(file_handler)
        
        # Log the actual file path being used for this run, especially for the main logger
        if logger_name == "app_root" or (name is not None and name.endswith("main")):
            logger.info(f"File logging enabled for this run. Logs will be written to: {_MAIN_LOG_FILE_PATH_FOR_CURRENT_RUN.resolve()}")
            logger.info(f"Log rotation: maxBytes={max_bytes}, backupCount={backup_count}")

    logger.propagate = False
    _configured_loggers.add(logger_name)
    return logger

main_app_logger = setup_logging("npc_api_suite_main")