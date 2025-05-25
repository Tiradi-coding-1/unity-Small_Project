// GameManager.cs
// 放置路徑建議: Assets/Scripts/Managers/GameManager.cs

using UnityEngine;
using NpcApiModels; // 雖然這個基礎 GameManager 可能不直接用，但子管理器會用

/// <summary>
/// Manages the overall game state, initialization of core services,
/// and acts as a central point for accessing other managers if needed.
/// Implements a simple singleton pattern for easy access.
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("API Configuration")]
    [Tooltip("The base URL of your FastAPI backend server. E.g., http://localhost:8000")]
    public string apiBaseUrl = "http://localhost:8000"; // 預設指向本地開發伺服器

    [Header("Manager References (Optional - for direct access or ensuring they exist)")]
    [Tooltip("Optional reference to GameTimeManager. If not set, it will try to find it or auto-create.")]
    public GameTimeManager gameTimeManagerInstance;
    [Tooltip("Optional reference to SceneContextManager.")]
    public SceneContextManager sceneContextManagerInstance;
    [Tooltip("Optional reference to DialogueUIManager.")]
    public DialogueUIManager dialogueUIManagerInstance;

    // Singleton instance
    private static GameManager _instance;
    public static GameManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<GameManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("GameManager_AutoCreated");
                    _instance = go.AddComponent<GameManager>();
                    Debug.LogWarning("GameManager instance was auto-created. " +
                                     "It's recommended to add it to your main scene manually and configure it.", _instance);
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        // Singleton pattern implementation
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Another GameManager instance detected. Destroying this duplicate.", gameObject);
            Destroy(gameObject);
            return;
        }
        _instance = this;

        // Make this GameManager persist across scene loads if it's a central manager
        // DontDestroyOnLoad(gameObject); // Uncomment if needed

        // Initialize core services and settings
        InitializeServices();
    }

    void InitializeServices()
    {
        Debug.Log("[GameManager] Initializing core services...");

        // 1. Set API Base URL for ApiService
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            ApiService.SetApiBaseUrl(apiBaseUrl);
        }
        else
        {
            Debug.LogError("[GameManager] API Base URL is not set in GameManager Inspector! ApiService will use its default or fail.", this);
            // Optionally, try to load from a config file or provide a more robust default
            ApiService.SetApiBaseUrl("http://localhost:8000"); // Fallback default
        }

        // 2. Ensure other managers are accessible (they might have their own singletons)
        // This also serves as a check that they are present in the scene.
        if (gameTimeManagerInstance == null) gameTimeManagerInstance = GameTimeManager.Instance;
        if (sceneContextManagerInstance == null) sceneContextManagerInstance = SceneContextManager.Instance;
        if (dialogueUIManagerInstance == null) dialogueUIManagerInstance = DialogueUIManager.Instance;

        if (gameTimeManagerInstance == null || sceneContextManagerInstance == null || dialogueUIManagerInstance == null)
        {
            Debug.LogError("[GameManager] One or more essential managers (GameTime, SceneContext, DialogueUI) are missing from the scene or could not be found/created!", this);
        }
        else
        {
            Debug.Log("[GameManager] All referenced managers seem to be available.");
        }

        // Add any other game-wide initializations here
        // For example, loading player data, setting game state, etc.
        Debug.Log("[GameManager] Core services initialization complete.");
    }

    // --- Public Methods (Examples) ---

    public void PauseGame()
    {
        Time.timeScale = 0; // Pauses Unity's time
        if (GameTimeManager.Instance != null) GameTimeManager.Instance.PauseGameTime(); // Pauses our custom game time
        Debug.Log("[GameManager] Game Paused.");
        // TODO: Add logic for showing a pause menu, etc.
    }

    public void ResumeGame()
    {
        Time.timeScale = 1;
        if (GameTimeManager.Instance != null) GameTimeManager.Instance.ResumeGameTime();
        Debug.Log("[GameManager] Game Resumed.");
        // TODO: Add logic for hiding a pause menu, etc.
    }

    // Example of how other scripts might get a manager reference if not using Singleton directly
    public GameTimeManager GetGameTimeManager() => gameTimeManagerInstance;
    public SceneContextManager GetSceneContextManager() => sceneContextManagerInstance;
    public DialogueUIManager GetDialogueUIManager() => dialogueUIManagerInstance;

    // Add other game management logic as needed (e.g., loading levels, saving game state)
}