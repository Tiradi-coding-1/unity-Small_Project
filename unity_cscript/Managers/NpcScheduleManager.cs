// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/Managers/NpcScheduleManager.cs
// NpcScheduleManager.cs
// 放置路徑建議: Assets/Scripts/Managers/NpcScheduleManager.cs

using UnityEngine;
using System.Collections.Generic;
using System.Linq; // For LINQ operations
using NpcApiModels; // For NPCScheduleRule and GameTime
using System; // For DateTime parsing (if needed for time_period_tag interpretation)

/// <summary>
/// 代表特定 NPC 的一組日程規則。
/// 此類別標記為 Serializable，因此可以在 Inspector 中設定
/// （如果 NpcScheduleManager 使用此類別的公開列表）。
/// </summary>
[System.Serializable]
public class NpcScheduleSet
{
    [Tooltip("此日程設定適用的 NPC ID。")]
    public string npcId;
    [Tooltip("此 NPC 的日程規則列表。")]
    public List<NpcApiModels.NPCScheduleRule> scheduleRules = new List<NpcApiModels.NPCScheduleRule>();
}

/// <summary>
/// 管理 NPC 日程並在給定時間為特定 NPC 提供當前活動的日程規則。
/// 這是一個基礎實作。更進階的系統可能涉及更複雜的時間條件解析、
/// 規則優先級處理以及從外部資料來源（例如 ScriptableObjects、JSON 檔案）載入日程。
/// </summary>
public class NpcScheduleManager : MonoBehaviour
{
    [Header("NPC Schedules Configuration")]
    [Tooltip("不同 NPC 的日程設定列表。在 Inspector 中設定此項，或從其他來源（例如 ScriptableObjects 或 JSON 檔案）動態載入。" +
             "對於公寓場景，日程應包含如 '在廚房做飯', '在客廳看電視', '在臥室休息' 等活動。")]
    public List<NpcScheduleSet> npcScheduleConfigurations = new List<NpcScheduleSet>();

    // 內部字典，用於按 npcId 快速查找日程
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
    /// 從公開列表（Inspector）載入日程到內部字典。
    /// 在更進階的系統中，這可以從 ScriptableObjects 或檔案載入。
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
            // 確保 rule_id 已填寫（如果 Inspector 中未設定，NPCScheduleRule 在 NpcApiDataModels.cs 中有預設工廠）
            foreach(var rule in scheduleSet.scheduleRules)
            {
                if(string.IsNullOrEmpty(rule.rule_id))
                {
                    // NPCScheduleRule in NpcApiDataModels.cs should have a default factory for rule_id.
                    // If it doesn't, or if you want to ensure uniqueness here:
                    // rule.rule_id = $"rule_{System.Guid.NewGuid().ToString().Substring(0, 8)}"; // Using System.Guid for uniqueness
                }
                 // 驗證 target_location_name_or_area 和 target_position 的一致性
                if (string.IsNullOrEmpty(rule.target_location_name_or_area) && rule.target_position == null)
                {
                    Debug.LogWarning($"[NpcScheduleManager] NPC '{scheduleSet.npcId}', Rule '{rule.rule_id}' ('{rule.activity_description}') has no target location or position defined. This might lead to undefined behavior.", this);
                }
            }
            _npcSchedules[scheduleSet.npcId] = new List<NpcApiModels.NPCScheduleRule>(scheduleSet.scheduleRules);
        }
        Debug.Log($"[NpcScheduleManager] Loaded schedules for {_npcSchedules.Count} NPCs from Inspector configurations.");
    }

    /// <summary>
    /// 獲取特定 NPC 在給定遊戲時間的當前活動日程規則。
    /// 此基礎版本根據 NpcApiModels.GameTime.time_of_day 和 NpcApiModels.GameTime.day_of_week（如果存在於 NPCScheduleRule.time_period_tag 中）進行匹配。
    /// 更進階的系統會更穩健地解析 time_period_tag（例如，“08:00-12:00”，“weekdays”）。
    /// </summary>
    /// <param name="npcId">NPC 的唯一 ID。</param>
    /// <param name="currentTime">當前的 NpcApiModels.GameTime。</param>
    /// <returns>NPC 的活動 NPCScheduleRule 物件列表。如果沒有活動規則或 NPC 沒有日程，則返回空列表。</returns>
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
            // if (activeRules.Any())
            // {
            //     Debug.Log($"[NpcScheduleManager] NPC '{npcId}' has {activeRules.Count} active schedule rule(s) at {currentTime.time_of_day}. First rule activity: '{activeRules[0].activity_description}' at '{activeRules[0].target_location_name_or_area ?? "unspecified location"}'.");
            // }
            return activeRules;
        }
        else
        {
            // Debug.Log($"[NpcScheduleManager] No schedule found for NPC ID: {npcId}");
            return new List<NpcApiModels.NPCScheduleRule>(); // No schedule defined for this NPC
        }
    }

    /// <summary>
    /// 根據當前遊戲時間判斷給定的日程規則是否活動。
    /// 這是一個基於 time_period_tag 中字串匹配的非常基礎的實作。
    /// 一個穩健的系統會從 time_period_tag 中解析時間範圍、日期條件等。
    /// </summary>
    private bool IsRuleActive(NpcApiModels.NPCScheduleRule rule, NpcApiModels.GameTime currentTime)
    {
        if (rule == null || string.IsNullOrEmpty(rule.time_period_tag))
        {
            // Debug.LogWarning($"[NpcScheduleManager] Rule or its time_period_tag is null/empty for rule_id: {rule?.rule_id}. Considered inactive.");
            return false;
        }

        // 將標籤和時間都轉換為小寫以便不區分大小寫比較
        string tag = rule.time_period_tag.ToLowerInvariant().Trim();
        string timeOfDay = currentTime.time_of_day.ToLowerInvariant().Trim();
        string dayOfWeek = currentTime.day_of_week?.ToLowerInvariant().Trim(); // 可為 null

        // --- 基礎匹配邏輯 ---
        // 1. "anytime" 或 "allday" 標籤始終匹配
        if (tag == "anytime" || tag == "all_day" || tag == "allday") return true;

        // 2. 檢查時間段 (time_of_day)
        //    例如，如果 tag 是 "morning_kitchen_routine"，它應該在 time_of_day 是 "morning" 時匹配。
        //    如果 tag 是 "0800-1200_work", 則需要更複雜的解析 (目前未實現)。
        bool timeMatch = tag.Contains(timeOfDay);

        // 3. 檢查星期幾 (day_of_week)
        bool dayMatch = true; // 預設為匹配，除非有明確的星期限制且不符
        if (dayOfWeek != null) // 只有當前遊戲時間提供了星期幾資訊時才進行檢查
        {
            if (tag.Contains("weekday") && (dayOfWeek == "saturday" || dayOfWeek == "sunday"))
            {
                dayMatch = false;
            }
            else if (tag.Contains("weekend") && !(dayOfWeek == "saturday" || dayOfWeek == "sunday"))
            {
                dayMatch = false;
            }
            else if (IsSpecificDayTag(tag) && !tag.Contains(dayOfWeek)) // 例如 tag 是 "monday_meeting"
            {
                dayMatch = false;
            }
        }
        else if (IsSpecificDayTag(tag) || tag.Contains("weekday") || tag.Contains("weekend"))
        {
            // 如果規則指定了日期限制，但當前時間沒有日期資訊，則為安全起見，假設不匹配
            dayMatch = false;
        }
        
        // 最終，規則活動需要時間和日期都匹配
        // Debug.Log($"[NpcScheduleManager] Rule '{rule.rule_id}' check: Tag='{tag}', TimeOfDay='{timeOfDay}', DayOfWeek='{dayOfWeek}'. TimeMatch={timeMatch}, DayMatch={dayMatch}. Active={timeMatch && dayMatch}");
        return timeMatch && dayMatch;
    }

    /// <summary>
    /// 輔助函式，檢查標籤是否包含特定的星期幾名稱。
    /// </summary>
    private bool IsSpecificDayTag(string tag)
    {
        string[] days = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
        return days.Any(day => tag.Contains(day));
    }


    // --- 用於在運行時可能修改日程的公開方法 (如果需要) ---

    /// <summary>
    /// 為 NPC 添加或更新日程規則。
    /// </summary>
    public void AddOrUpdateNpcSchedule(string npcId, List<NpcApiModels.NPCScheduleRule> rules)
    {
        if (string.IsNullOrEmpty(npcId) || rules == null)
        {
            Debug.LogError("[NpcScheduleManager] AddOrUpdateNpcSchedule called with invalid arguments.");
            return;
        }
        _npcSchedules[npcId] = new List<NpcApiModels.NPCScheduleRule>(rules);
        Debug.Log($"[NpcScheduleManager] Schedule dynamically updated for NPC '{npcId}'.");
    }

    /// <summary>
    /// 移除 NPC 的日程。
    /// </summary>
    public void RemoveNpcSchedule(string npcId)
    {
        if (string.IsNullOrEmpty(npcId)) return;
        if (_npcSchedules.ContainsKey(npcId))
        {
            _npcSchedules.Remove(npcId);
            Debug.Log($"[NpcScheduleManager] Schedule removed for NPC '{npcId}'.");
        }
    }

    /// <summary>
    /// 重新載入 Inspector 中設定的所有日程。
    /// </summary>
    [ContextMenu("Reload Schedules from Inspector")]
    public void ReloadSchedulesFromInspectorConfig()
    {
        Debug.Log("[NpcScheduleManager] Attempting to reload schedules from Inspector configuration...");
        LoadAndProcessSchedules();
    }
}