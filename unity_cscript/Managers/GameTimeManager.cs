// GameTimeManager.cs
// 放置路徑建議: Assets/Scripts/Managers/GameTimeManager.cs

using UnityEngine;
using System; // For DateTime, TimeSpan, DateTimeStyles, FormatException
using NpcApiModels; // For our GameTime data model

/// <summary>
/// Manages the in-game time, allowing for scalable time progression
/// and providing the current game time in a format suitable for API calls.
/// </summary>
public class GameTimeManager : MonoBehaviour
{
    [Header("Time Flow Settings")]
    [Tooltip("How fast game time progresses compared to real time. " +
             "1.0 = real-time, 60.0 = 1 real second is 1 game minute, 3600.0 = 1 real second is 1 game hour.")]
    public float timeScaleFactor = 60.0f; // Default: 1 real second = 1 game minute

    [Tooltip("The starting date and time for the game in ISO 8601 format (YYYY-MM-DDTHH:MM:SSZ). 'Z' denotes UTC.")]
    public string gameStartDateTimeString = "2025-01-01T08:00:00Z";

    private DateTime _currentInternalGameTime;
    private bool _isInitialized = false;
    private bool _isRunning = true; // To pause/resume game time

    // Singleton pattern for easy global access
    private static GameTimeManager _instance;
    public static GameTimeManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<GameTimeManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("GameTimeManager_AutoCreated");
                    _instance = go.AddComponent<GameTimeManager>();
                    Debug.LogWarning("GameTimeManager instance was auto-created. " +
                                     "It's recommended to add it to your scene manually via the Unity Editor.");
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Multiple GameTimeManager instances detected in the scene. Destroying this duplicate.", gameObject);
            Destroy(gameObject);
            return;
        }
        _instance = this;
        // DontDestroyOnLoad(gameObject); // Optional: if this manager should persist across scene loads

        InitializeTime();
    }

    void InitializeTime()
    {
        try
        {
            // Parse the start time string. DateTimeStyles.RoundtripKind helps with ISO 8601 and UTC 'Z'.
            _currentInternalGameTime = DateTime.Parse(gameStartDateTimeString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            // Ensure it's UTC if 'Z' was present or if we want to enforce it
            if (_currentInternalGameTime.Kind == DateTimeKind.Unspecified && gameStartDateTimeString.EndsWith("Z"))
            {
                _currentInternalGameTime = DateTime.SpecifyKind(_currentInternalGameTime, DateTimeKind.Utc);
            }
            else if (_currentInternalGameTime.Kind == DateTimeKind.Local) // Convert to UTC if parsed as local
            {
                _currentInternalGameTime = _currentInternalGameTime.ToUniversalTime();
            }


            _isInitialized = true;
            _isRunning = true;
            Debug.Log($"[GameTimeManager] Initialized. Game start time (UTC): {_currentInternalGameTime:o}"); // "o" is round-trip format
        }
        catch (FormatException ex)
        {
            _currentInternalGameTime = DateTime.UtcNow; // Fallback to current real UTC time
            _isInitialized = true;
            _isRunning = true;
            Debug.LogError($"[GameTimeManager] Invalid gameStartDateTimeString format: '{gameStartDateTimeString}'. Error: {ex.Message}. Defaulting to current real UTC time.", this);
        }
    }

    void Update()
    {
        if (_isInitialized && _isRunning && timeScaleFactor > 0)
        {
            // Add scaled real-world delta time to the game time
            _currentInternalGameTime = _currentInternalGameTime.AddSeconds(Time.deltaTime * timeScaleFactor);
            // Debug.Log($"Current Game Time: {_currentInternalGameTime:o}"); // Optional: for debugging time flow
        }
    }

    /// <summary>
    /// Gets the current game time packaged as an NpcApiModels.GameTime object.
    /// </summary>
    /// <returns>The current game time data structure for API calls.</returns>
    public NpcApiModels.GameTime GetCurrentGameTime()
    {
        if (!_isInitialized)
        {
            Debug.LogWarning("[GameTimeManager] GetCurrentGameTime called before initialization or after failure. " +
                             "Returning current real UTC time as a fallback.");
            DateTime nowUtc = DateTime.UtcNow;
             return new NpcApiModels.GameTime
            {
                current_timestamp = nowUtc.ToString("o"), // ISO 8601 round-trip format with Z
                time_of_day = GetTimeOfDayCategory(nowUtc),
                day_of_week = nowUtc.DayOfWeek.ToString()
            };
        }

        return new NpcApiModels.GameTime
        {
            current_timestamp = _currentInternalGameTime.ToString("o"), // "o" format is ISO 8601 and includes Z if UTC
            time_of_day = GetTimeOfDayCategory(_currentInternalGameTime),
            day_of_week = _currentInternalGameTime.DayOfWeek.ToString()
        };
    }

    /// <summary>
    /// Determines a categorical time of day based on the hour of the provided DateTime.
    /// </summary>
    private string GetTimeOfDayCategory(DateTime time) // Assumes 'time' is UTC or consistent kind
    {
        int hour = time.Hour; // Hour in 24-hour format

        if (hour >= 5 && hour < 9) return "morning";       // 5:00 - 8:59
        if (hour >= 9 && hour < 12) return "late_morning"; // 9:00 - 11:59
        if (hour >= 12 && hour < 14) return "midday";      // 12:00 - 13:59
        if (hour >= 14 && hour < 18) return "afternoon";   // 14:00 - 17:59
        if (hour >= 18 && hour < 21) return "evening";     // 18:00 - 20:59
        if (hour >= 21 && hour < 24) return "night";       // 21:00 - 23:59
        return "late_night"; // 00:00 - 04:59
    }

    // --- Public methods to control time flow ---
    public void PauseGameTime()
    {
        _isRunning = false;
        Debug.Log("[GameTimeManager] Game time paused.");
    }

    public void ResumeGameTime()
    {
        _isRunning = true;
        Debug.Log("[GameTimeManager] Game time resumed.");
    }

    public void SetTimeScaleFactor(float newScale)
    {
        if (newScale < 0)
        {
            Debug.LogWarning("[GameTimeManager] Time scale factor cannot be negative. Setting to 0.");
            timeScaleFactor = 0;
        }
        else
        {
            timeScaleFactor = newScale;
        }
        Debug.Log($"[GameTimeManager] Time scale factor set to: {timeScaleFactor}");
    }

    public DateTime GetCurrentInternalDateTime()
    {
        return _currentInternalGameTime;
    }
}