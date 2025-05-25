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
    [Tooltip("A Collider2D (e.g., a large BoxCollider2D or PolygonCollider2D) that defines the " +
             "overall traversable boundaries of the current playable scene area.")]
    public Collider2D mainSceneBoundsCollider;

    [Tooltip("A general textual description of the current scene or area, " +
             "e.g., 'A bustling medieval marketplace', 'A quiet, ancient forest clearing'. " +
             "This can be used as global context for some API calls.")]
    public string generalSceneDescription = "A typical area within the game world.";

    // Cached lists of characters and landmarks for performance.
    // These could be updated periodically or when significant scene changes occur.
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
        // DontDestroyOnLoad(gameObject); // Optional

        if (mainSceneBoundsCollider == null)
        {
            Debug.LogWarning("[SceneContextManager] Main Scene Bounds Collider is not assigned. " +
                             "Boundary information will use large default values.", this);
        }
    }

    void Start()
    {
        // Initialize the cache of characters and landmarks
        RefreshSceneCache();
    }

    /// <summary>
    /// Re-scans the scene for all CharacterData and LandmarkDataComponent instances.
    /// Call this if new characters/landmarks are dynamically added or removed during gameplay.
    /// </summary>
    public void RefreshSceneCache()
    {
        _allCharactersInScene = FindObjectsOfType<CharacterData>().ToList();
        _allLandmarksInScene = FindObjectsOfType<LandmarkDataComponent>().ToList();
        _isCacheInitialized = true;
        Debug.Log($"[SceneContextManager] Cache refreshed. Found {_allCharactersInScene.Count} characters and {_allLandmarksInScene.Count} landmarks.");
    }


    /// <summary>
    /// Gets a list of EntityContextInfo for characters (NPCs/Player) near a given center point.
    /// </summary>
    public List<EntityContextInfo> GetNearbyEntities(string requestingNpcId, Vector3 centerPosition, float searchRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache(); // Ensure cache is ready

        List<EntityContextInfo> nearbyEntities = new List<EntityContextInfo>();
        float searchRadiusSqr = searchRadius * searchRadius; // Use squared distance for minor optimization

        foreach (CharacterData charData in _allCharactersInScene)
        {
            if (charData == null || !charData.enabled || !charData.gameObject.activeInHierarchy) continue; // Skip inactive or destroyed
            if (charData.npcId == requestingNpcId) continue; // Skip self

            // Using squared magnitude for distance check is slightly more performant than Vector3.Distance
            if ((charData.transform.position - centerPosition).sqrMagnitude <= searchRadiusSqr)
            {
                // Determine if this entity is significant to the requestingNpc (e.g., is a friend)
                // This might require access to the requesting NPC's CharacterData or a global relationship manager.
                // For this example, we'll assume the requesting NPC's CharacterData component can provide its friend list.
                bool isSignificant = false;
                CharacterData requestingNpcData = _allCharactersInScene.FirstOrDefault(cd => cd.npcId == requestingNpcId);
                if (requestingNpcData != null && requestingNpcData.friendNpcIds.Contains(charData.npcId))
                {
                    isSignificant = true;
                }
                nearbyEntities.Add(charData.ToEntityContextInfo(isSignificant));
            }
        }
        // Optionally sort by distance if the API consumer or LLM prompt benefits from it
        // nearbyEntities.Sort((a, b) => Vector2.Distance(new Vector2(centerPosition.x, centerPosition.y), new Vector2(a.x, a.y))
        //                               .CompareTo(Vector2.Distance(new Vector2(centerPosition.x, centerPosition.y), new Vector2(b.x, b.y))));
        return nearbyEntities;
    }

    /// <summary>
    /// Gets a list of LandmarkContextInfo for landmarks visible or relevant from a given center point.
    /// </summary>
    public List<LandmarkContextInfo> GetVisibleLandmarks(Vector3 centerPosition, float visibilityRadius)
    {
        if (!_isCacheInitialized) RefreshSceneCache();

        List<LandmarkContextInfo> visibleLandmarks = new List<LandmarkContextInfo>();
        float visibilityRadiusSqr = visibilityRadius * visibilityRadius;

        foreach (LandmarkDataComponent landmarkData in _allLandmarksInScene)
        {
            if (landmarkData == null || !landmarkData.enabled || !landmarkData.gameObject.activeInHierarchy) continue;

            if ((landmarkData.transform.position - centerPosition).sqrMagnitude <= visibilityRadiusSqr)
            {
                visibleLandmarks.Add(landmarkData.ToLandmarkContextInfo());
            }
        }
        return visibleLandmarks;
    }

    /// <summary>
    /// Gets the SceneBoundaryInfo based on the mainSceneBoundsCollider.
    /// </summary>
    public SceneBoundaryInfo GetCurrentSceneBoundaries()
    {
        if (mainSceneBoundsCollider != null && mainSceneBoundsCollider.enabled)
        {
            Bounds bounds = mainSceneBoundsCollider.bounds; // This is in world space
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
                             "Returning very large default boundaries. This might lead to NPCs trying to move outside the intended area.");
            // Return some large default bounds if not configured, NPCs should still respect these
            return new SceneBoundaryInfo { min_x = -1000f, max_x = 1000f, min_y = -1000f, max_y = 1000f };
        }
    }

    /// <summary>
    /// Gets a general textual description of the current scene.
    /// </summary>
    public string GetGeneralSceneDescription()
    {
        return string.IsNullOrEmpty(generalSceneDescription) ? "An area in the game world." : generalSceneDescription;
    }
}