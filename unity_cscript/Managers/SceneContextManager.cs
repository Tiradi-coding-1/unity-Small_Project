// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/Managers/SceneContextManager.cs
// SceneContextManager.cs
// 放置路徑建議: Assets/Scripts/Managers/SceneContextManager.cs

using UnityEngine;
using System.Collections.Generic;
using System.Linq; // For LINQ operations
using NpcApiModels;

/// <summary>
/// Manages and provides contextual information about the current game scene,
/// such as lists of nearby characters, visible landmarks, and scene boundaries.
/// It can dynamically find relevant GameObjects with CharacterData or LandmarkDataComponent.
/// </summary>
public class SceneContextManager : MonoBehaviour
{
    [Header("Scene Configuration")]
    [Tooltip("一個 Collider2D (例如，一個大的 BoxCollider2D 或 PolygonCollider2D)，用於定義當前可遊玩場景區域的整體可通行邊界。對於公寓場景，這應該精確描繪公寓的牆壁範圍。")]
    public Collider2D mainSceneBoundsCollider;

    [Tooltip("對當前場景或區域的通用文字描述，例如：'一個有四個臥室的合租公寓內部' 或 '一個繁忙的客廳區域'。這可以作為某些 API 呼叫的全域上下文。")]
    public string generalSceneDescription = "一個公寓內部。"; // Modified default for apartment

    // 為了效能而快取的角色和地標列表。
    // 這些列表可以在場景發生重大變化時定期更新或手動刷新。
    private List<CharacterData> _allCharactersInScene = new List<CharacterData>();
    private List<LandmarkDataComponent> _allLandmarksInScene = new List<LandmarkDataComponent>();
    private bool _isCacheInitialized = false;


    // Singleton pattern for easy global access
    private static SceneContextManager _instance;
    public static SceneContextManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<SceneContextManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("SceneContextManager_AutoCreated");
                    _instance = go.AddComponent<SceneContextManager>();
                    Debug.LogWarning("SceneContextManager instance was auto-created. " +
                                     "It's recommended to add it to your scene manually and configure it.", _instance);
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Multiple SceneContextManager instances detected. Destroying this duplicate.", gameObject);
            Destroy(gameObject);
            return;
        }
        _instance = this;
        // DontDestroyOnLoad(gameObject); // Optional: if this manager should persist across scene loads

        if (mainSceneBoundsCollider == null)
        {
            Debug.LogWarning("[SceneContextManager] Main Scene Bounds Collider is not assigned. " +
                             "Boundary information will use large default values. This is critical for an apartment scene!", this);
        }
    }

    void Start()
    {
        // Initialize the cache of characters and landmarks
        RefreshSceneCache();
    }

    /// <summary>
    /// Re-scans the scene for all CharacterData and LandmarkDataComponent instances.
    /// Call this if new characters/landmarks are dynamically added or removed during gameplay,
    /// or if their static data that this manager might cache changes (though landmark status should be dynamic).
    /// </summary>
    public void RefreshSceneCache()
    {
        _allCharactersInScene = FindObjectsOfType<CharacterData>().Where(cd => cd.enabled && cd.gameObject.activeInHierarchy).ToList();
        _allLandmarksInScene = FindObjectsOfType<LandmarkDataComponent>().Where(ldc => ldc.enabled && ldc.gameObject.activeInHierarchy).ToList();
        _isCacheInitialized = true;
        Debug.Log($"[SceneContextManager] Cache refreshed. Found {_allCharactersInScene.Count} active characters and {_allLandmarksInScene.Count} active landmarks.");
    }


    /// <summary>
    /// Gets a list of EntityContextInfo for characters (NPCs/Player) near a given center point.
    /// </summary>
    /// <param name="requestingNpcId">發出請求的 NPC 的 ID，用於排除自身並判斷關係。</param>
    /// <param name="centerPosition">搜索的中心點。</param>
    /// <param name="searchRadius">搜索半徑。</param>
    /// <returns>附近實體的列表。</returns>
    public List<EntityContextInfo> GetNearbyEntities(string requestingNpcId, Vector3 centerPosition, float searchRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache(); 

        List<EntityContextInfo> nearbyEntities = new List<EntityContextInfo>();
        float searchRadiusSqr = searchRadius * searchRadius; 

        foreach (CharacterData charData in _allCharactersInScene)
        {
            if (charData == null) continue; // Should be filtered by RefreshSceneCache if not enabled/active
            if (charData.npcId == requestingNpcId) continue; // Skip self

            if ((charData.transform.position - centerPosition).sqrMagnitude <= searchRadiusSqr)
            {
                bool isSignificant = false;
                CharacterData requestingNpcData = _allCharactersInScene.FirstOrDefault(cd => cd.npcId == requestingNpcId);
                if (requestingNpcData != null && requestingNpcData.friendNpcIds.Contains(charData.npcId))
                {
                    isSignificant = true;
                }
                nearbyEntities.Add(charData.ToEntityContextInfo(isSignificant));
            }
        }
        return nearbyEntities;
    }

    /// <summary>
    /// Gets a list of LandmarkContextInfo for landmarks visible or relevant from a given center point.
    /// This method now relies on LandmarkDataComponent.ToLandmarkContextInfo() to provide up-to-date status.
    /// </summary>
    /// <param name="centerPosition">搜索的中心點。</param>
    /// <param name="visibilityRadius">可見半徑。</param>
    /// <returns>可見地標的列表，包含其最新狀態。</returns>
    public List<LandmarkContextInfo> GetVisibleLandmarks(Vector3 centerPosition, float visibilityRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache();

        List<LandmarkContextInfo> visibleLandmarks = new List<LandmarkContextInfo>();
        float visibilityRadiusSqr = visibilityRadius * visibilityRadius;

        foreach (LandmarkDataComponent landmarkData in _allLandmarksInScene)
        {
            if (landmarkData == null) continue; // Should be filtered by RefreshSceneCache

            if ((landmarkData.transform.position - centerPosition).sqrMagnitude <= visibilityRadiusSqr)
            {
                // ToLandmarkContextInfo() in the modified LandmarkDataComponent now includes dynamic status
                visibleLandmarks.Add(landmarkData.ToLandmarkContextInfo());
            }
        }
        return visibleLandmarks;
    }

    /// <summary>
    /// Gets the SceneBoundaryInfo based on the mainSceneBoundsCollider.
    /// </summary>
    /// <returns>場景邊界資訊。</returns>
    public SceneBoundaryInfo GetCurrentSceneBoundaries()
    {
        if (mainSceneBoundsCollider != null && mainSceneBoundsCollider.enabled)
        {
            Bounds bounds = mainSceneBoundsCollider.bounds; 
            return new SceneBoundaryInfo
            {
                min_x = bounds.min.x,
                max_x = bounds.max.x,
                min_y = bounds.min.y,
                max_y = bounds.max.y
            };
        }
        else
        {
            Debug.LogWarning("[SceneContextManager] Main Scene Bounds Collider not set or disabled. " +
                             "Returning very large default boundaries. This might lead to NPCs trying to move outside the intended apartment area.");
            return new SceneBoundaryInfo { min_x = -1000f, max_x = 1000f, min_y = -1000f, max_y = 1000f };
        }
    }

    /// <summary>
    /// Gets a general textual description of the current scene.
    /// This should be set in the Inspector to reflect the apartment setting.
    /// </summary>
    /// <returns>場景的通用描述。</returns>
    public string GetGeneralSceneDescription()
    {
        return string.IsNullOrEmpty(generalSceneDescription) ? "An apartment interior." : generalSceneDescription;
    }

    /// <summary>
    /// 新增：提供對快取的 LandmarkDataComponent 列表的訪問。
    /// 這允許其他系統（如 NpcController）有效地查詢地標，而無需重複 FindObjectsOfType。
    /// </summary>
    /// <returns>場景中所有活動 LandmarkDataComponent 的列表副本。</returns>
    public List<LandmarkDataComponent> GetAllLandmarkDataComponents()
    {
        if (!_isCacheInitialized)
        {
            RefreshSceneCache(); // 確保快取已初始化
        }
        return new List<LandmarkDataComponent>(_allLandmarksInScene); // 返回副本以避免外部修改
    }
}