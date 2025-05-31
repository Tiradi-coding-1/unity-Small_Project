// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/NpcLogic/Components/LandmarkDataComponent.cs
// LandmarkDataComponent.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/LandmarkDataComponent.cs

using UnityEngine;
using System.Collections.Generic; // For List
using System.Linq; // For Distinct() if needed
using NpcApiModels; // 引用我們在 NpcApiDataModels.cs 中定義的命名空間

/// <summary>
/// Stores descriptive data for a landmark or significant location within the game scene.
/// This component should be attached to GameObjects representing such landmarks.
/// The information is used by NPCs when making decisions (e.g., where to go, what to comment on).
/// </summary>
public class LandmarkDataComponent : MonoBehaviour
{
    [Header("Landmark Identification & Type")]
    [Tooltip("場景地標的唯一且易於辨識的名稱。")]
    public string landmarkName = "";

    [Tooltip("地標的類型標籤，用於分類和邏輯判斷 (例如: 'bedroom', 'kitchen', 'bathroom', 'furniture_sofa', 'appliance_stove')。")]
    public string landmarkTypeTag = "generic_point_of_interest";

    [Tooltip("此地標的擁有者 NPC ID (例如，如果是私人房間，則為房間主人的 NPC ID)。如果無特定擁有者則留空。")]
    public string ownerNpcId = "";

    [Header("Landmark Context & Status")]
    [TextArea(2, 5)]
    [Tooltip("關於此地標的靜態初始說明或描述 (例如，'一個舒適的客廳沙發', '老舊的廚房爐灶')。這些通常不會在遊戲中改變。")]
    public List<string> initialStaticNotes = new List<string>();

    // 用於儲存動態變化的狀態，例如 "occupancy_occupied", "owner_presence_present"
    // 這些狀態應該由其他遊戲邏輯（例如 NPC 進入/離開房間的腳本）來更新。
    private List<string> _dynamicStatusNotes = new List<string>();

    /// <summary>
    /// Called in the editor when the script is loaded or a value is changed in the Inspector.
    /// Provides immediate feedback and default value population in the editor.
    /// </summary>
    void OnValidate()
    {
        if (string.IsNullOrEmpty(landmarkName))
        {
            landmarkName = gameObject.name; // 如果未設定，預設使用 GameObject 的名稱
        }

        if (string.IsNullOrEmpty(landmarkTypeTag))
        {
            landmarkTypeTag = "generic_point_of_interest";
        }
    }

    void Awake()
    {
        // _dynamicStatusNotes is initialized as an empty list.
        // Any initial dynamic states would be set by other scripts during their Awake/Start
        // or in response to game events.
    }

    /// <summary>
    /// 更新或添加一個特定前綴的動態狀態。例如，更新佔用狀態。
    /// 這通常用於互斥的狀態，例如 "occupancy_occupied" 和 "occupancy_vacant"。
    /// </summary>
    /// <param name="notePrefixToRemove">要移除的狀態的前綴 (例如 "occupancy_")</param>
    /// <param name="newNoteFull">新的完整狀態 (例如 "occupancy_occupied" 或 "occupancy_vacant")。如果為空或null，則只移除舊狀態。</param>
    public void UpdateDynamicStatusByPrefix(string notePrefixToRemove, string newNoteFull)
    {
        bool changed = false;
        // 先移除所有以此前綴開頭的舊狀態
        int removedCount = _dynamicStatusNotes.RemoveAll(note => note.StartsWith(notePrefixToRemove));
        if (removedCount > 0) changed = true;
        
        // 如果新狀態有效，則添加它
        if (!string.IsNullOrEmpty(newNoteFull))
        {
            if (!_dynamicStatusNotes.Contains(newNoteFull)) // 避免重複添加相同的完整狀態
            {
                _dynamicStatusNotes.Add(newNoteFull);
                changed = true;
            }
        }
        if (changed) // <--- 取消註解此區塊
        {
            Debug.Log($"[{landmarkName}-{gameObject.GetInstanceID()}] Status updated with prefix '{notePrefixToRemove}'. New full note: '{newNoteFull ?? "NONE"}'. Current dynamic notes: {string.Join(", ", _dynamicStatusNotes)}");
        }
    }

    /// <summary>
    /// 直接添加一個動態狀態標籤，如果它不存在。
    /// 這適用於可以共存的狀態標籤。
    /// </summary>
    /// <param name="note">要添加的狀態標籤。</param>
    public void AddDynamicStatus(string note)
    {
        if (!string.IsNullOrEmpty(note) && !_dynamicStatusNotes.Contains(note))
        {
            _dynamicStatusNotes.Add(note);
            // Debug.Log($"[{landmarkName}-{gameObject.GetInstanceID()}] Status added: '{note}'. Current dynamic notes: {string.Join(", ", _dynamicStatusNotes)}");
        }
    }

    /// <summary>
    /// 直接移除一個特定的動態狀態標籤。
    /// </summary>
    /// <param name="note">要移除的狀態標籤。</param>
    public void RemoveDynamicStatus(string note)
    {
        if (_dynamicStatusNotes.Contains(note))
        {
            _dynamicStatusNotes.Remove(note);
            // Debug.Log($"[{landmarkName}-{gameObject.GetInstanceID()}] Status removed: '{note}'. Current dynamic notes: {string.Join(", ", _dynamicStatusNotes)}");
        }
    }

    /// <summary>
    /// 檢查是否存在某個特定的動態狀態標籤。
    /// </summary>
    /// <param name="note">要檢查的狀態標籤。</param>
    /// <returns>如果存在則返回 true，否則返回 false。</returns>
    public bool HasDynamicStatus(string note)
    {
        if (string.IsNullOrEmpty(note)) return false;
        return _dynamicStatusNotes.Contains(note);
    }
    
    /// <summary>
    /// 檢查是否存在以特定前綴開頭的動態狀態標籤。
    /// </summary>
    /// <param name="prefix">要檢查的狀態前綴。</param>
    /// <returns>如果存在則返回 true，否則返回 false。</returns>
    public bool HasDynamicStatusWithPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return false;
        return _dynamicStatusNotes.Exists(note => note.StartsWith(prefix));
    }


    /// <summary>
    /// 獲取所有當前的動態狀態標籤列表的副本。
    /// </summary>
    public List<string> GetCurrentDynamicStatusNotes()
    {
        return new List<string>(_dynamicStatusNotes); // 返回副本以防外部修改
    }

    /// <summary>
    /// 將此地標的資料轉換為 LandmarkContextInfo 物件，用於 API 請求。
    /// 它會合併靜態初始筆記和當前的動態狀態筆記。
    /// </summary>
    /// <returns>一個 LandmarkContextInfo 物件，填充了此組件的資料。</returns>
    public LandmarkContextInfo ToLandmarkContextInfo()
    {
        NpcApiModels.Position currentPosition = new NpcApiModels.Position
        {
            x = transform.position.x,
            y = transform.position.y
        };

        List<string> combinedNotes = new List<string>();
        if (initialStaticNotes != null)
        {
            combinedNotes.AddRange(initialStaticNotes);
        }
        // _dynamicStatusNotes 在 Awake 中已初始化為 new List<string>()，所以不為 null
        combinedNotes.AddRange(_dynamicStatusNotes);
        
        // 移除重複項是可選的，但可以確保傳輸的資料簡潔
        // combinedNotes = combinedNotes.Distinct().ToList(); 

        return new LandmarkContextInfo
        {
            landmark_name = this.landmarkName,
            position = currentPosition,
            landmark_type_tag = this.landmarkTypeTag,
            owner_id = string.IsNullOrEmpty(this.ownerNpcId) ? null : this.ownerNpcId,
            // 確保即使 combinedNotes 為空，也傳遞一個空的 List<string> 而不是 null
            current_status_notes = combinedNotes ?? new List<string>() 
        };
    }
}