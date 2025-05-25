// DialogueUIManager.cs
// 放置路徑建議: Assets/Scripts/Managers/DialogueUIManager.cs

using UnityEngine;
// using UnityEngine.UI; // For basic UI Text - No longer primary if using TMP
using TMPro;          // For TextMeshPro
using System.Collections; // For IEnumerator

/// <summary>
/// Manages the display of dialogue in the game's UI.
/// This is a basic example; a real system would be more complex with features like
/// character portraits, dialogue choices, typing effects, etc.
/// </summary>
public class DialogueUIManager : MonoBehaviour
{
    [Header("UI Element References")]
    [Tooltip("Assign a UI Text or TextMeshProUGUI element for displaying the speaker's name.")]
    // public Text speakerNameText; // For Unity UI Text - Commented out
    public TMP_Text speakerNameTextMeshPro; // Using TextMeshPro

    [Tooltip("Assign a UI Text or TextMeshProUGUI element for displaying the dialogue content.")]
    // public Text dialogueContentText; // For Unity UI Text - Commented out
    public TMP_Text dialogueContentTextMeshPro; // Using TextMeshPro

    [Tooltip("The parent GameObject of the dialogue UI panel. This will be toggled for visibility.")]
    public GameObject dialoguePanel; // Assign the root Panel of your dialogue UI

    [Header("Dialogue Display Settings")]
    [Tooltip("Default duration (in seconds) to display a dialogue line if no specific duration is given. 0 or less means it stays until HideDialogue() is called explicitly.")]
    public float defaultDisplayDuration = 4.0f;

    // Singleton pattern for easy global access
    private static DialogueUIManager _instance;
    public static DialogueUIManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<DialogueUIManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("DialogueUIManager_AutoCreated");
                    _instance = go.AddComponent<DialogueUIManager>();
                    Debug.LogWarning("DialogueUIManager instance was auto-created. " +
                                     "It's recommended to add it to your scene manually and configure its UI references.", _instance);
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Multiple DialogueUIManager instances detected. Destroying this duplicate.", gameObject);
            Destroy(gameObject);
            return;
        }
        _instance = this;
        // DontDestroyOnLoad(gameObject); // Optional

        // Basic validation of UI references
        if (dialoguePanel == null)
            Debug.LogError("[DialogueUIManager] Dialogue Panel is not assigned in the Inspector!", this);
        if (speakerNameTextMeshPro == null)
            Debug.LogError("[DialogueUIManager] Speaker Name TextMeshPro is not assigned!", this);
        if (dialogueContentTextMeshPro == null)
            Debug.LogError("[DialogueUIManager] Dialogue Content TextMeshPro is not assigned!", this);
        
        HideDialogueInternal(); // Start with dialogue panel hidden
    }

    /// <summary>
    /// Displays a line of dialogue in the UI.
    /// </summary>
    /// <param name="speakerName">The name of the character speaking.</param>
    /// <param name="message">The dialogue message to display.</param>
    /// <param name="duration">Optional: How long (in seconds) to display the message before auto-hiding.
    /// If 0 or negative, uses defaultDisplayDuration (if positive) or stays until HideDialogue() is called explicitly.</param>
    public void ShowDialogue(string speakerName, string message, float duration = -1f)
    {
        if (dialoguePanel == null || speakerNameTextMeshPro == null || dialogueContentTextMeshPro == null)
        {
            Debug.LogWarning($"[DialogueUIManager] Cannot show dialogue due to missing UI references. Speaker: {speakerName}, Msg: {message?.Substring(0, Mathf.Min(message?.Length ?? 0, 50))}...");
            return;
        }

        Debug.Log($"<color=#DDA0DD>[UI DIALOGUE] Speaker: '{speakerName}' Says: \"{message}\"</color>"); // Plum color

        // Set text for speaker name
        if (speakerNameTextMeshPro != null) speakerNameTextMeshPro.text = speakerName;

        // Set text for dialogue content
        if (dialogueContentTextMeshPro != null) dialogueContentTextMeshPro.text = message;
        
        dialoguePanel.SetActive(true);

        // Stop any previously running auto-hide coroutine to prevent premature hiding
        StopAllCoroutines(); 

        float displayDuration = (duration < 0) ? defaultDisplayDuration : duration;

        if (displayDuration > 0)
        {
            StartCoroutine(HideDialogueAfterDelayCoroutine(displayDuration));
        }
        // If displayDuration is 0 or less, dialogue remains visible until HideDialogue() is called.
    }

    /// <summary>
    /// Hides the dialogue panel immediately.
    /// </summary>
    public void HideDialogue()
    {
        HideDialogueInternal();
    }

    private void HideDialogueInternal()
    {
        if (dialoguePanel != null)
        {
            if(dialoguePanel.activeSelf) // Only log if it was actually active and is now being hidden
            {
                // Debug.Log("[DialogueUIManager] Hiding dialogue panel.");
            }
            dialoguePanel.SetActive(false);
        }
    }

    private IEnumerator HideDialogueAfterDelayCoroutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        HideDialogueInternal();
    }


    // Example Test method that can be called via a UI button or another script for testing the UI
    [ContextMenu("Test Show Dialogue")] // Allows right-clicking component in Inspector to run this
    public void TestShowSampleDialogue()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("TestShowSampleDialogue can only be run in Play Mode.");
            return;
        }
        if (gameObject.activeInHierarchy)
        {
           ShowDialogue("Test Narrator", "This is a sample dialogue message. It will disappear after the default duration or if a specific duration is passed.", 5f);
        }
        else
        {
            Debug.LogWarning("DialogueUIManager GameObject is not active, cannot run test dialogue.");
        }
    }
}