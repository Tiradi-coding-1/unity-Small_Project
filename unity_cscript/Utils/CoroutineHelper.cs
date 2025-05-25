// CoroutineHelper.cs
// 放置路徑建議: Assets/Scripts/Utils/CoroutineHelper.cs

using UnityEngine;
using System.Collections; // Required for IEnumerator

/// <summary>
/// A utility class that allows starting Unity coroutines from non-MonoBehaviour classes.
/// It achieves this by creating a hidden, persistent MonoBehaviour instance in the scene
/// which acts as the host for running these coroutines.
/// </summary>
public class CoroutineHelper : MonoBehaviour
{
    private static CoroutineHelper _instance;

    /// <summary>
    /// Gets the singleton instance of the CoroutineHelper.
    /// If an instance doesn't exist, it will be created automatically in the scene.
    /// </summary>
    public static CoroutineHelper Instance
    {
        get
        {
            if (_instance == null)
            {
                // Try to find an existing instance in the scene
                _instance = FindObjectOfType<CoroutineHelper>();

                if (_instance == null)
                {
                    // If no instance exists, create a new GameObject and add this component
                    GameObject singletonObject = new GameObject("CoroutineHelper_Singleton");
                    _instance = singletonObject.AddComponent<CoroutineHelper>();
                    Debug.Log("[CoroutineHelper] Instance auto-created.");
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Ensures the CoroutineHelper instance persists across scene loads if needed.
    /// Called when the script instance is being loaded.
    /// </summary>
    void Awake()
    {
        // Singleton pattern: ensure only one instance exists.
        // If an instance already exists and it's not this one, destroy this new one.
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[CoroutineHelper] Duplicate CoroutineHelper instance found. Destroying this one.", gameObject);
            Destroy(gameObject);
            return;
        }

        _instance = this; // Set the static instance to this component instance.

        // Optional: Make this GameObject persistent across scene loads.
        // Useful if coroutines need to continue running even if the active scene changes.
        // DontDestroyOnLoad(gameObject); // Uncomment if persistence is required.
        // Debug.Log("[CoroutineHelper] Awake - Instance set and potentially marked as DontDestroyOnLoad.");
    }

    /// <summary>
    /// Starts a Unity coroutine from any context (static or non-MonoBehaviour).
    /// </summary>
    /// <param name="coroutine">The IEnumerator representing the coroutine to be started.</param>
    /// <returns>A Coroutine object, which can be used to stop the coroutine if needed.</returns>
    public static Coroutine Run(IEnumerator coroutine)
    {
        if (coroutine == null)
        {
            Debug.LogError("[CoroutineHelper] Cannot run a null coroutine.");
            return null;
        }
        // Ensure the instance is created and then start the coroutine on it.
        return Instance.StartCoroutine(coroutine);
    }

    /// <summary>
    /// Stops a specific coroutine that was previously started via CoroutineHelper.Run().
    /// </summary>
    /// <param name="coroutineToStop">The Coroutine object returned by Run() or StartCoroutine().</param>
    public static void Halt(Coroutine coroutineToStop)
    {
        if (coroutineToStop == null)
        {
            // Debug.LogWarning("[CoroutineHelper] Attempted to stop a null coroutine.");
            return;
        }
        if (_instance != null) // Ensure instance exists before trying to stop
        {
            _instance.StopCoroutine(coroutineToStop);
            // Debug.Log("[CoroutineHelper] Coroutine stopped.");
        }
        else
        {
            Debug.LogWarning("[CoroutineHelper] Instance not found, cannot stop coroutine. It might have already completed or the helper was destroyed.");
        }
    }

    /// <summary>
    /// Stops all coroutines currently running on the CoroutineHelper instance.
    /// </summary>
    public static void HaltAll()
    {
        if (_instance != null)
        {
            _instance.StopAllCoroutines();
            Debug.Log("[CoroutineHelper] All coroutines on helper instance stopped.");
        }
         else
        {
            Debug.LogWarning("[CoroutineHelper] Instance not found, cannot stop all coroutines.");
        }
    }

    // Optional: Cleanup when the application quits or the helper object is destroyed.
    void OnDestroy()
    {
        if (_instance == this)
        {
            // Debug.Log("[CoroutineHelper] Instance destroyed.");
            _instance = null; // Clear the static instance reference
        }
    }

    void OnApplicationQuit()
    {
        if (_instance == this)
        {
            // Debug.Log("[CoroutineHelper] Application quitting. Stopping all coroutines on helper instance.");
            StopAllCoroutines(); // Ensure all coroutines are stopped when the game exits
            _instance = null;
        }
    }

    // --- Example Usage (can be in another script) ---
    /*
    public class MyNonMonoBehaviourClass
    {
        public void DoSomethingWithDelay()
        {
            Debug.Log("Starting action with delay...");
            CoroutineHelper.Run(DelayedAction(2.0f));
        }

        private IEnumerator DelayedAction(float delay)
        {
            yield return new WaitForSeconds(delay);
            Debug.Log("Delayed action executed after " + delay + " seconds!");
        }
    }

    // To test from a MonoBehaviour:
    public class TestCoroutineHelper : MonoBehaviour
    {
        void Start()
        {
            MyNonMonoBehaviourClass myClass = new MyNonMonoBehaviourClass();
            myClass.DoSomethingWithDelay();

            // Or start directly
            CoroutineHelper.Run(AnotherDelayedLog(3.5f));
        }

        IEnumerator AnotherDelayedLog(float delay)
        {
            Debug.Log("Another coroutine starting...");
            yield return new WaitForSeconds(delay);
            Debug.Log("Another delayed log executed!");
        }
    }
    */
}