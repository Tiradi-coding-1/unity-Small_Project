// NpcScheduleManager.cs
// 放置路徑建議: Assets/Scripts/Managers/NpcScheduleManager.cs

using UnityEngine;
using System.Collections.Generic;
using System.Linq; // For LINQ operations
using NpcApiModels; // For NPCScheduleRule and GameTime
using System; // For DateTime parsing (if needed for time_period_tag interpretation)

/// <summary>
/// Represents a set of schedule rules for a specific NPC.
/// This class is marked Serializable so it can be configured in the Inspector
/// if NpcScheduleManager uses a public list of these.
/// </summary>
[System.Serializable]
public class NpcScheduleSet
{
    public string npcId; // The NPC this schedule set applies to
    public List<NpcApiModels.NPCScheduleRule> scheduleRules = new List<NpcApiModels.NPCScheduleRule>();
}

/// <summary>
/// Manages NPC schedules and provides currently active schedule rules for a given NPC at a given time.
/// This is a basic implementation. A more advanced system might involve more complex time condition parsing,
/// rule prioritization, and loading schedules from external data sources (e.g., ScriptableObjects, JSON files).
/// </summary>
public class NpcScheduleManager : MonoBehaviour
{
    [Header("NPC Schedules Configuration")]
    [Tooltip("List of schedule sets for different NPCs. Configure this in the Inspector " +
             "or load dynamically from another source (e.g., ScriptableObjects or JSON files).")]
    public List<NpcScheduleSet> npcScheduleConfigurations = new List<NpcScheduleSet>();

    // Internal dictionary for quick lookup of schedules by npcId
    private Dictionary<string, List<NpcApiModels.NPCScheduleRule>> _npcSchedules = new Dictionary<string, List<NpcApiModels.NPCScheduleRule>>();

    // Singleton pattern for easy global access
    private static NpcScheduleManager _instance;
    public static NpcScheduleManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<NpcScheduleManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("NpcScheduleManager_AutoCreated");
                    _instance = go.AddComponent<NpcScheduleManager>();
                    Debug.LogWarning("NpcScheduleManager instance was auto-created. " +
                                     "It's recommended to add it to your scene manually and configure schedules.", _instance);
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Multiple NpcScheduleManager instances detected. Destroying this duplicate.", gameObject);
            Destroy(gameObject);
            return;
        }
        _instance = this;
        // DontDestroyOnLoad(gameObject); // Optional

        LoadAndProcessSchedules();
    }

    /// <summary>
    /// Loads schedules from the public list (Inspector) into the internal dictionary.
    /// In a more advanced system, this could load from ScriptableObjects or files.
    /// </summary>
    private void LoadAndProcessSchedules()
    {
        _npcSchedules.Clear();
        foreach (var scheduleSet in npcScheduleConfigurations)
        {
            if (string.IsNullOrEmpty(scheduleSet.npcId))
            {
                Debug.LogWarning("[NpcScheduleManager] Found a schedule set with an empty npcId. Skipping it.", this);
                continue;
            }
            if (_npcSchedules.ContainsKey(scheduleSet.npcId))
            {
                Debug.LogWarning($"[NpcScheduleManager] Duplicate schedule configuration found for npcId '{scheduleSet.npcId}'. Overwriting with the last one found.", this);
            }
            // Ensure rule_id is populated if not set in Inspector (NPCScheduleRule has a default factory for it)
            foreach(var rule in scheduleSet.scheduleRules)
            {
                if(string.IsNullOrEmpty(rule.rule_id))
                {
                    // NPCScheduleRule in NpcApiDataModels.cs should have a default factory for rule_id.
                    // If it doesn't, or if you want to ensure uniqueness here:
                    // rule.rule_id = $"rule_{Guid.NewGuid().ToString().Substring(0, 6)}";
                }
            }
            _npcSchedules[scheduleSet.npcId] = new List<NpcApiModels.NPCScheduleRule>(scheduleSet.scheduleRules);
        }
        Debug.Log($"[NpcScheduleManager] Loaded schedules for {_npcSchedules.Count} NPCs.");
    }

    /// <summary>
    /// Gets the currently active schedule rules for a specific NPC at the given game time.
    /// This basic version matches based on GameTime.time_of_day and GameTime.day_of_week if present in NPCScheduleRule.time_period_tag.
    /// A more advanced system would parse time_period_tag more robustly (e.g., "08:00-12:00", "weekdays").
    /// </summary>
    /// <param name="npcId">The unique ID of the NPC.</param>
    /// <param name="currentTime">The current NpcApiModels.GameTime.</param>
    /// <returns>A list of active NPCScheduleRule objects for the NPC. Returns an empty list if no rules are active or NPC has no schedule.</returns>
    public List<NpcApiModels.NPCScheduleRule> GetActiveScheduleRulesForNpc(string npcId, NpcApiModels.GameTime currentTime)
    {
        if (string.IsNullOrEmpty(npcId) || currentTime == null)
        {
            Debug.LogWarning("[NpcScheduleManager] GetActiveScheduleRulesForNpc called with null npcId or currentTime.", this);
            return new List<NpcApiModels.NPCScheduleRule>();
        }

        if (_npcSchedules.TryGetValue(npcId, out List<NpcApiModels.NPCScheduleRule> allRulesForNpc))
        {
            List<NpcApiModels.NPCScheduleRule> activeRules = new List<NpcApiModels.NPCScheduleRule>();
            foreach (var rule in allRulesForNpc)
            {
                if (IsRuleActive(rule, currentTime))
                {
                    activeRules.Add(rule);
                }
            }
            if (activeRules.Any())
            {
                // Debug.Log($"[NpcScheduleManager] NPC '{npcId}' has {activeRules.Count} active schedule rule(s) at {currentTime.time_of_day}. First rule: {activeRules[0].activity_description}");
            }
            return activeRules;
        }
        else
        {
            // Debug.Log($"[NpcScheduleManager] No schedule found for NPC ID: {npcId}");
            return new List<NpcApiModels.NPCScheduleRule>(); // No schedule defined for this NPC
        }
    }

    /// <summary>
    /// Determines if a given schedule rule is active based on the current game time.
    /// This is a VERY basic implementation based on string matching in time_period_tag.
    /// A robust system would involve parsing time ranges, day conditions, etc. from time_period_tag.
    /// </summary>
    private bool IsRuleActive(NpcApiModels.NPCScheduleRule rule, NpcApiModels.GameTime currentTime)
    {
        if (rule == null || string.IsNullOrEmpty(rule.time_period_tag)) return false;

        string tag = rule.time_period_tag.ToLowerInvariant();
        string timeOfDay = currentTime.time_of_day.ToLowerInvariant();
        string dayOfWeek = currentTime.day_of_week?.ToLowerInvariant(); // Nullable

        // Basic matching examples:
        // Rule tag "morning" matches current time_of_day "morning".
        // Rule tag "weekday_morning" would require checking both day and time.
        // Rule tag "anytime" or "allday" should always match.

        if (tag == "anytime" || tag == "all_day" || tag == "allday") return true;

        bool timeMatch = tag.Contains(timeOfDay);
        bool dayMatch = true; // Assume day matches unless specified and doesn't match

        if (dayOfWeek != null) // Only check day if current game time has a day of week
        {
            if (tag.Contains("weekday") && (dayOfWeek == "saturday" || dayOfWeek == "sunday"))
            {
                dayMatch = false;
            }
            else if (tag.Contains("weekend") && !(dayOfWeek == "saturday" || dayOfWeek == "sunday"))
            {
                dayMatch = false;
            }
            else if (IsSpecificDay(tag) && !tag.Contains(dayOfWeek)) // e.g. tag "monday_morning"
            {
                dayMatch = false;
            }
        } else if (IsSpecificDay(tag) || tag.Contains("weekday") || tag.Contains("weekend")) {
            // If rule specifies a day constraint but current time has no day info, assume no match for safety
            dayMatch = false;
        }


        // This is a placeholder for more sophisticated time range parsing.
        // Example: if time_period_tag is "09:00-17:00"
        // You would parse these times and compare with currentTime.current_timestamp.Hour/Minute.
        // For now, simple tag matching:
        return timeMatch && dayMatch;
    }

    private bool IsSpecificDay(string tag)
    {
        string[] days = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
        return days.Any(day => tag.Contains(day));
    }


    // --- Public methods for potentially modifying schedules at runtime (if needed) ---
    public void AddOrUpdateNpcSchedule(string npcId, List<NpcApiModels.NPCScheduleRule> rules)
    {
        if (string.IsNullOrEmpty(npcId) || rules == null) return;
        _npcSchedules[npcId] = new List<NpcApiModels.NPCScheduleRule>(rules);
        Debug.Log($"[NpcScheduleManager] Schedule updated for NPC '{npcId}'.");
    }

    public void RemoveNpcSchedule(string npcId)
    {
        if (_npcSchedules.ContainsKey(npcId))
        {
            _npcSchedules.Remove(npcId);
            Debug.Log($"[NpcScheduleManager] Schedule removed for NPC '{npcId}'.");
        }
    }
}