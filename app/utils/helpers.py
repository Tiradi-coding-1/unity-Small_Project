# npc_api_suite/app/utils/helpers.py

import re
from typing import Tuple, Optional
from app.core.schemas import SceneBoundaryInfo, Position # 從 schemas 導入相關模型
from app.core.config import settings_instance as settings # 獲取 SCENE_BOUNDARY_BUFFER 等設定
from app.core.logging_config import setup_logging # 可選，如果輔助函數需要日誌

logger = setup_logging(__name__) # 為此 utils 模組設定 logger

def parse_coordinates_from_text(text_input: Optional[str]) -> Tuple[Optional[float], Optional[float]]:
    """
    Parses coordinates (x, y) from a given text string.
    Handles various common formats like:
    - "x=1.0, y=2.0" or "x: 1.0; y: 2.0" (order of x and y can vary)
    - "(1.0, 2.0)"
    - "1.0, 2.0"
    - "{'x': 1.0, 'y': 2.0}" (JSON-like string)

    Args:
        text_input: The string potentially containing coordinates.

    Returns:
        A tuple (x, y) of floats if parsing is successful, otherwise (None, None).
    """
    if not text_input or not text_input.strip():
        logger.debug("parse_coordinates_from_text received empty input.")
        return None, None

    text = text_input.strip()

    # Pattern 1: Key-value pairs (e.g., x=12.3, y=45.6 or y:45.6, x:12.3)
    # Allows for numbers with optional decimal part and optional sign.
    # Handles optional spaces around '=', ':', and ',' or ';'.
    pattern_kv = (
        r"(?:x\s*[:=]\s*(?P<x_kv>[+-]?\d*\.?\d+)\s*[,;]?\s*y\s*[:=]\s*(?P<y_kv>[+-]?\d*\.?\d+))"  # x then y
        r"|(?:y\s*[:=]\s*(?P<y_kv_rev>[+-]?\d*\.?\d+)\s*[,;]?\s*x\s*[:=]\s*(?P<x_kv_rev>[+-]?\d*\.?\d+))" # y then x
    )
    match_kv = re.search(pattern_kv, text, re.IGNORECASE)
    if match_kv:
        try:
            if match_kv.group("x_kv") is not None and match_kv.group("y_kv") is not None:
                x_val = float(match_kv.group("x_kv"))
                y_val = float(match_kv.group("y_kv"))
                logger.debug(f"Parsed by K-V (x,y): ({x_val}, {y_val}) from '{text[:50]}...'")
                return x_val, y_val
            elif match_kv.group("x_kv_rev") is not None and match_kv.group("y_kv_rev") is not None:
                x_val = float(match_kv.group("x_kv_rev"))
                y_val = float(match_kv.group("y_kv_rev"))
                logger.debug(f"Parsed by K-V (y,x): ({x_val}, {y_val}) from '{text[:50]}...'")
                return x_val, y_val
        except ValueError:
            logger.warning(f"ValueError during K-V parsing for '{text[:50]}...'", exc_info=True)
            # Continue to try other patterns

    # Pattern 2: Tuple-like or CSV (e.g., (12.3, 45.6) or 12.3,45.6 or [12.3, 45.6])
    # Allows for optional parentheses or square brackets.
    pattern_tuple_csv = r"[\(\[]?\s*([+-]?\d*\.?\d+)\s*[,;]\s*([+-]?\d*\.?\d+)\s*[\)\]]?"
    match_tuple_csv = re.search(pattern_tuple_csv, text)
    if match_tuple_csv:
        try:
            x_val = float(match_tuple_csv.group(1))
            y_val = float(match_tuple_csv.group(2))
            logger.debug(f"Parsed by Tuple/CSV: ({x_val}, {y_val}) from '{text[:50]}...'")
            return x_val, y_val
        except ValueError:
            logger.warning(f"ValueError during Tuple/CSV parsing for '{text[:50]}...'", exc_info=True)
            # Continue

    # Pattern 3: JSON-like string (e.g., "{'x': 12.3, 'y': 45.6}" or '{"x": 12.3, "y": 45.6}')
    # This is more complex to parse robustly with regex alone, but can try for simple cases.
    # A more robust way would be to try `json.loads()` if it looks like JSON.
    pattern_json_like = r"{\s*['\"]x['\"]\s*:\s*([+-]?\d*\.?\d+)\s*,\s*['\"]y['\"]\s*:\s*([+-]?\d*\.?\d+)\s*}"
    match_json_like = re.search(pattern_json_like, text, re.IGNORECASE)
    if match_json_like:
        try:
            x_val = float(match_json_like.group(1))
            y_val = float(match_json_like.group(2))
            logger.debug(f"Parsed by JSON-like: ({x_val}, {y_val}) from '{text[:50]}...'")
            return x_val, y_val
        except ValueError:
            logger.warning(f"ValueError during JSON-like parsing for '{text[:50]}...'", exc_info=True)

    logger.warning(f"Could not parse coordinates using any known pattern from text: '{text[:70]}...'")
    return None, None


def clamp_position_to_bounds(
    x: float,
    y: float,
    bounds: SceneBoundaryInfo,
    buffer: Optional[float] = None # If None, uses default from settings
) -> Tuple[float, float]:
    """
    Clamps the given (x, y) position to be within the scene boundaries,
    considering an optional buffer from the edges.

    Args:
        x: The x-coordinate to clamp.
        y: The y-coordinate to clamp.
        bounds: A SceneBoundaryInfo object defining min_x, max_x, min_y, max_y.
        buffer: Optional buffer from the edges. If None, uses SCENE_BOUNDARY_BUFFER from settings.

    Returns:
        A tuple (clamped_x, clamped_y).
    """
    effective_buffer = buffer if buffer is not None else settings.SCENE_BOUNDARY_BUFFER

    # Calculate effective boundaries considering the buffer
    # Ensure buffer doesn't invert min/max if bounds are smaller than 2*buffer
    effective_min_x = min(bounds.min_x + effective_buffer, bounds.max_x - effective_buffer if bounds.max_x > bounds.min_x + 2 * effective_buffer else bounds.min_x + effective_buffer)
    effective_max_x = max(bounds.max_x - effective_buffer, bounds.min_x + effective_buffer if bounds.min_x < bounds.max_x - 2 * effective_buffer else bounds.max_x - effective_buffer)
    effective_min_y = min(bounds.min_y + effective_buffer, bounds.max_y - effective_buffer if bounds.max_y > bounds.min_y + 2 * effective_buffer else bounds.min_y + effective_buffer)
    effective_max_y = max(bounds.max_y - effective_buffer, bounds.min_y + effective_buffer if bounds.min_y < bounds.max_y - 2 * effective_buffer else bounds.max_y - effective_buffer)

    # Final clamping range must ensure min <= max
    final_min_x = min(effective_min_x, effective_max_x)
    final_max_x = max(effective_min_x, effective_max_x)
    final_min_y = min(effective_min_y, effective_max_y)
    final_max_y = max(effective_min_y, effective_max_y)

    clamped_x = max(final_min_x, min(x, final_max_x))
    clamped_y = max(final_min_y, min(y, final_max_y))
    
    if clamped_x != x or clamped_y != y:
        logger.debug(f"Position ({x:.2f}, {y:.2f}) clamped to ({clamped_x:.2f}, {clamped_y:.2f}) with buffer {effective_buffer:.1f} within bounds [{bounds.min_x:.1f}-{bounds.max_x:.1f}, {bounds.min_y:.1f}-{bounds.max_y:.1f}]")
        
    return clamped_x, clamped_y

# --- Potentially other helper functions could go here ---
# For example:
# - Calculating distance between two Position objects (though Position schema now has this method)
# - Formatting time durations in human-readable strings
# - Generating simple unique IDs if uuid is overkill for some minor cases
# - Text cleaning utilities (e.g., stripping extra whitespace, normalizing quotes)

# Example of a text cleaning utility that might be useful before sending to LLM or after
def clean_text_for_llm(text: Optional[str]) -> Optional[str]:
    """Basic text cleaning: strips whitespace, normalizes newlines."""
    if text is None:
        return None
    text = text.strip()
    text = re.sub(r'\s*\n\s*', '\n', text) # Normalize newlines to single \n, remove surrounding whitespace
    text = re.sub(r'[ \t]{2,}', ' ', text) # Replace multiple spaces/tabs with single space
    return text