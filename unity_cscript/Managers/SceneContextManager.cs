// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/Managers/SceneContextManager.cs
// SceneContextManager.cs
// 放置路徑建議: Assets/Scripts/Managers/SceneContextManager.cs

using UnityEngine;
using System.Collections.Generic;
using System.Linq; // For LINQ operations
using NpcApiModels;

/// <summary>
/// Manages and provides contextual information about the current game scene,
/// such as lists of nearby characters, visible landmarks (including rooms and their contents), 
/// and scene boundaries.
/// </summary>
public class SceneContextManager : MonoBehaviour
{
    [Header("Scene Configuration")]
    [Tooltip("一個 Collider2D，用於定義當前可遊玩場景區域的整體可通行邊界。")]
    public Collider2D mainSceneBoundsCollider;

    [Tooltip("對當前場景或區域的通用文字描述，例如：'一個合租公寓的內部'。")]
    public string generalSceneDescription = "一個合租公寓的內部。"; // Updated default

    // 快取的列表
    private List<CharacterData> _allCharactersInScene = new List<CharacterData>();
    private List<LandmarkDataComponent> _allIndividualLandmarksInScene = new List<LandmarkDataComponent>();
    private List<RoomDataComponent> _allRoomsInScene = new List<RoomDataComponent>(); // 新增：快取所有房間
    private bool _isCacheInitialized = false;

    // Singleton pattern
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

        if (mainSceneBoundsCollider == null)
        {
            Debug.LogWarning("[SceneContextManager] Main Scene Bounds Collider is not assigned. Boundary info will be default.", this);
        }
    }

    void Start()
    {
        RefreshSceneCache();
    }

    /// <summary>
    /// 重新掃描場景以獲取所有 CharacterData, LandmarkDataComponent, 和 RoomDataComponent 實例。
    /// </summary>
    public void RefreshSceneCache()
    {
        _allCharactersInScene = FindObjectsOfType<CharacterData>()
                                .Where(cd => cd.enabled && cd.gameObject.activeInHierarchy).ToList();
        _allIndividualLandmarksInScene = FindObjectsOfType<LandmarkDataComponent>()
                                .Where(ldc => ldc.enabled && ldc.gameObject.activeInHierarchy).ToList();
        _allRoomsInScene = FindObjectsOfType<RoomDataComponent>()
                                .Where(rdc => rdc.enabled && rdc.gameObject.activeInHierarchy).ToList(); // 查找並快取 RoomDataComponent
        
        _isCacheInitialized = true;
        Debug.Log($"[SceneContextManager] Cache refreshed. Found: " +
                  $"{_allCharactersInScene.Count} active characters, " +
                  $"{_allIndividualLandmarksInScene.Count} active individual landmarks, " +
                  $"{_allRoomsInScene.Count} active rooms.");
    }

    /// <summary>
    /// 獲取附近實體的列表。
    /// </summary>
    public List<EntityContextInfo> GetNearbyEntities(string requestingNpcId, Vector3 centerPosition, float searchRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache(); 

        List<EntityContextInfo> nearbyEntities = new List<EntityContextInfo>();
        float searchRadiusSqr = searchRadius * searchRadius; 

        foreach (CharacterData charData in _allCharactersInScene)
        {
            if (charData.npcId == requestingNpcId) continue; 

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
    /// 獲取可見地標的列表，現在會整合房間資訊和獨立地標資訊。
    /// </summary>
    public List<LandmarkContextInfo> GetVisibleLandmarks(Vector3 centerPosition, float visibilityRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache();

        List<LandmarkContextInfo> visibleLandmarks = new List<LandmarkContextInfo>();
        float visibilityRadiusSqr = visibilityRadius * visibilityRadius;
        HashSet<string> addedLandmarkNames = new HashSet<string>(); // 用於避免重複添加同名地標

        // 1. 處理房間 (RoomDataComponent)
        foreach (RoomDataComponent roomData in _allRoomsInScene)
        {
            if (roomData == null) continue;

            // 判斷房間是否可見 (例如，NPC 是否在房間內，或房間的中心點是否在可見半徑內)
            // 一個簡單的判斷是基於房間中心點的距離
            Vector3 roomCenter = roomData.roomBoundsCollider != null ? roomData.roomBoundsCollider.bounds.center : roomData.transform.position;
            if ((roomCenter - centerPosition).sqrMagnitude <= visibilityRadiusSqr)
            {
                LandmarkContextInfo roomAsLandmark = roomData.ToRoomAsLandmarkContextInfo();
                if (!addedLandmarkNames.Contains(roomAsLandmark.landmark_name))
                {
                    visibleLandmarks.Add(roomAsLandmark);
                    addedLandmarkNames.Add(roomAsLandmark.landmark_name);
                }
            }
        }

        // 2. 處理獨立的地標物件 (LandmarkDataComponent)
        //    這些可能是房間外的地標，或者是房間內需要單獨精確標識的物件（即使它們已在房間描述中提及）
        foreach (LandmarkDataComponent landmarkData in _allIndividualLandmarksInScene)
        {
            if (landmarkData == null) continue;

            // 如果一個 LandmarkDataComponent 所在的 GameObject 已經有一個 RoomDataComponent，
            // 那麼這個房間的資訊已經透過上面的循環處理了。
            // 我們主要關心那些不是房間本身，而是具體物品或其他獨立地標的 LandmarkDataComponent。
            // 這裡我們簡單地添加所有可見的獨立地標。如果 LLM 提示設計得好，它可以處理一定程度的資訊冗餘。
            // （例如，房間的 notes 提到了沙發，同時沙發本身也是一個獨立地標）
            if ((landmarkData.transform.position - centerPosition).sqrMagnitude <= visibilityRadiusSqr)
            {
                // 檢查是否已經作為 RoomDataComponent 添加過同名的主要地標
                // 這是一個簡化處理，如果 LandmarkDataComponent 掛載在 RoomDataComponent 同一個 GameObject 上，
                // 且 landmarkName 相同，則可能重複。更好的做法是確保 RoomDataComponent 和 LandmarkDataComponent
                // 在同一個 GameObject 上時，它們的 landmarkName/roomName 是獨特的，或者有不同的 typeTag。
                // RoomDataComponent.ToRoomAsLandmarkContextInfo() 使用的是 roomName。
                if (!addedLandmarkNames.Contains(landmarkData.landmarkName))
                {
                    visibleLandmarks.Add(landmarkData.ToLandmarkContextInfo());
                    addedLandmarkNames.Add(landmarkData.landmarkName);
                }
                // 如果允許物品同時作為房間內容描述和獨立地標出現，可以移除 !addedLandmarkNames.Contains 的檢查，
                // 但需確保 LLM 能正確處理。目前保留此檢查以避免日誌中出現看似重複的條目。
            }
        }
        // Debug.Log($"[SceneContextManager] GetVisibleLandmarks found {visibleLandmarks.Count} items for API.");
        return visibleLandmarks;
    }

    /// <summary>
    /// 獲取場景邊界資訊。
    /// </summary>
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
            Debug.LogWarning("[SceneContextManager] Main Scene Bounds Collider not set or disabled. Returning large default boundaries.");
            return new SceneBoundaryInfo { min_x = -1000f, max_x = 1000f, min_y = -1000f, max_y = 1000f };
        }
    }

    /// <summary>
    /// 獲取場景的通用描述。
    /// </summary>
    public string GetGeneralSceneDescription()
    {
        return string.IsNullOrEmpty(generalSceneDescription) ? "An apartment interior." : generalSceneDescription;
    }

    /// <summary>
    /// 提供對快取的獨立 LandmarkDataComponent 列表的訪問。
    /// </summary>
    public List<LandmarkDataComponent> GetAllIndividualLandmarkDataComponents()
    {
        if (!_isCacheInitialized) RefreshSceneCache();
        return new List<LandmarkDataComponent>(_allIndividualLandmarksInScene); 
    }
    
    /// <summary>
    /// 新增：提供對快取的 RoomDataComponent 列表的訪問。
    /// </summary>
    public List<RoomDataComponent> GetAllRoomDataComponents()
    {
        if (!_isCacheInitialized) RefreshSceneCache();
        return new List<RoomDataComponent>(_allRoomsInScene);
    }
}