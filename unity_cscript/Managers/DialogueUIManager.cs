// 檔案名稱: tiradi-coding-1/unity-small_project/unity-Small_Project-ec8a534c2acd0effbb69c32bc060ff9194dcfba1/unity_cscript/Managers/DialogueUIManager.cs
// DialogueUIManager.cs
// 放置路徑建議: Assets/Scripts/Managers/DialogueUIManager.cs

using UnityEngine;
using UnityEngine.UI; // For basic UI Text (保留以防舊UI元素仍在使用)
using TMPro;          // For TextMeshPro - 如果此管理器也升級到TMP
using System.Collections; // For IEnumerator

/// <summary>
/// Manages the display of dialogue in a global game UI panel.
/// NOTE: With the introduction of NPC-specific dialogue bubbles, this manager's role
/// might be reduced to displaying system messages, narrator lines, or as a fallback
/// if an NPC cannot display its own bubble.
/// </summary>
public class DialogueUIManager : MonoBehaviour
{
    [Header("UI Element References (Global Panel)")]
    [Tooltip("全域對話面板的父 GameObject。將被切換以控制可見性。")]
    public GameObject dialoguePanel; // Assign the root Panel of your global dialogue UI

    [Tooltip("（可選）用於顯示說話者名稱的 UI Text 或 TextMeshProUGUI 元素。")]
    public Text speakerNameText; // For Unity UI Text
    public TMP_Text speakerNameTextMeshPro; // For TextMeshPro

    [Tooltip("（可選）用於顯示對話內容的 UI Text 或 TextMeshProUGUI 元素。")]
    public Text dialogueContentText; // For Unity UI Text
    public TMP_Text dialogueContentTextMeshPro; // For TextMeshPro

    [Header("Dialogue Display Settings")]
    [Tooltip("如果未給定特定持續時間，則顯示對話行的預設持續時間（秒）。0 或更小表示保持顯示，直到明確調用 HideDialogue()。")]
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

    private Coroutine _hidePanelCoroutine; // 用於自動隱藏全域面板的協程

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

        // 基礎 UI 引用驗證 (如果 dialoguePanel 被指定)
        if (dialoguePanel != null)
        {
            if (speakerNameText == null && speakerNameTextMeshPro == null)
                Debug.LogWarning("[DialogueUIManager] Speaker Name Text (or TMP Text) for global panel is not assigned. Name display might not work.", this);
            if (dialogueContentText == null && dialogueContentTextMeshPro == null)
                Debug.LogWarning("[DialogueUIManager] Dialogue Content Text (or TMP Text) for global panel is not assigned. Content display might not work.", this);
            
            HideDialogueInternal(); // 預設隱藏對話面板
        }
        else
        {
            Debug.Log("[DialogueUIManager] Global Dialogue Panel is not assigned. This manager will only function if panel is assigned at runtime or if methods are used by other UIs.", this);
        }
    }

    /// <summary>
    /// 在全域UI面板中顯示一行對話。
    /// </summary>
    /// <param name="speakerName">說話者的名稱。</param>
    /// <param name="message">要顯示的對話訊息。</param>
    /// <param name="duration">可選：顯示訊息的時長（秒）。
    /// 如果為0或負數，則使用 defaultDisplayDuration（如果為正），或保持顯示直到 HideDialogue() 被調用。</param>
    public void ShowDialogue(string speakerName, string message, float duration = -1f)
    {
        if (dialoguePanel == null)
        {
            Debug.LogWarning($"[DialogueUIManager] Cannot show dialogue in global panel because Dialogue Panel is not assigned. Speaker: {speakerName}, Msg: {message?.Substring(0, Mathf.Min(message?.Length ?? 0, 50))}...");
            return;
        }
         if ((speakerNameText == null && speakerNameTextMeshPro == null) || (dialogueContentText == null && dialogueContentTextMeshPro == null ))
        {
             Debug.LogWarning($"[DialogueUIManager] Cannot show dialogue in global panel due to missing text UI references. Speaker: {speakerName}, Msg: {message?.Substring(0, Mathf.Min(message?.Length ?? 0, 50))}...");
        }


        // Debug.Log($"<color=#E6E6FA>[UI DIALOGUE - Global Panel] Speaker: '{speakerName}' Says: \"{message}\"</color>"); // Lavender color

        // 設定說話者名稱
        if (speakerNameTextMeshPro != null) speakerNameTextMeshPro.text = speakerName;
        else if (speakerNameText != null) speakerNameText.text = speakerName;

        // 設定對話內容
        if (dialogueContentTextMeshPro != null) dialogueContentTextMeshPro.text = message;
        else if (dialogueContentText != null) dialogueContentText.text = message;
        
        dialoguePanel.SetActive(true);

        // 停止任何先前運行的自動隱藏協程
        if (_hidePanelCoroutine != null)
        {
            StopCoroutine(_hidePanelCoroutine);
            _hidePanelCoroutine = null;
        }

        float displayDuration = (duration < 0) ? defaultDisplayDuration : duration;

        if (displayDuration > 0)
        {
            _hidePanelCoroutine = StartCoroutine(HideDialogueAfterDelayCoroutine(displayDuration));
        }
    }

    /// <summary>
    /// 立即隱藏全域對話面板。
    /// </summary>
    public void HideDialogue()
    {
        HideDialogueInternal();
    }

    private void HideDialogueInternal()
    {
        if (dialoguePanel != null && dialoguePanel.activeSelf)
        {
            dialoguePanel.SetActive(false);
            // Debug.Log("[DialogueUIManager] Global dialogue panel hidden.");
        }
        if (_hidePanelCoroutine != null) // 確保協程也被停止
        {
            StopCoroutine(_hidePanelCoroutine);
            _hidePanelCoroutine = null;
        }
    }

    private IEnumerator HideDialogueAfterDelayCoroutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        HideDialogueInternal();
        _hidePanelCoroutine = null;
    }

    /// <summary>
    /// 測試方法，可通過 UI 按鈕或其他腳本調用以測試全域 UI。
    /// </summary>
    [ContextMenu("Test Show Global Dialogue")] 
    public void TestShowSampleDialogue()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("TestShowSampleDialogue can only be run in Play Mode.");
            return;
        }
        if (dialoguePanel == null)
        {
             Debug.LogError("[DialogueUIManager] Cannot run test: Dialogue Panel is not assigned in the Inspector!");
             return;
        }
        if (gameObject.activeInHierarchy) // 確保管理器本身是活動的
        {
           ShowDialogue("System Announcer", "This is a sample dialogue message displayed in the global UI panel. It will disappear after the specified or default duration.", 5f);
        }
        else
        {
            Debug.LogWarning("DialogueUIManager GameObject is not active, cannot run test dialogue.");
        }
    }
}