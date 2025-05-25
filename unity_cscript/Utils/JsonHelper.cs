// JsonHelper.cs
// 放置路徑建議: Assets/Scripts/Utils/JsonHelper.cs

// 編譯指令，用於條件編譯 Newtonsoft.Json 的相關程式碼
// 您可以在 Player Settings -> Scripting Define Symbols 中添加 "NEWTONSOFT_JSON_AVAILABLE"
// 或者如果您確認已經安裝了 Newtonsoft.Json，可以直接在此處取消下面這行的註解。
// #define NEWTONSOFT_JSON_AVAILABLE

#if NEWTONSOFT_JSON_AVAILABLE
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization; // For CamelCasePropertyNamesContractResolver etc.
#endif

using UnityEngine; // For Debug.Log, Debug.LogError
using System;       // For System.Exception

/// <summary>
/// Provides utility methods for JSON serialization and deserialization.
/// Prioritizes Newtonsoft.Json if available (recommended), otherwise might offer
/// limited fallbacks or wrappers for Unity's JsonUtility.
/// </summary>
public static class JsonHelper
{
#if NEWTONSOFT_JSON_AVAILABLE
    private static readonly JsonSerializerSettings DefaultNewtonsoftSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore, // Don't include null properties in JSON output
        DefaultValueHandling = DefaultValueHandling.Include, // Include properties with default values
        MissingMemberHandling = MissingMemberHandling.Ignore, // Ignore JSON properties not in C# class
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore, // Ignore circular references
        // ContractResolver = new CamelCasePropertyNamesContractResolver(), // If your API uses camelCase by default
        // For snake_case (common with Python), ensure your C# models use [JsonProperty("snake_case_name")]
        // or configure a SnakeCaseNamingStrategy if you don't want to use attributes everywhere.
        // Example for SnakeCase:
        // ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        DateFormatHandling = DateFormatHandling.IsoDateFormat, // Expect ISO 8601 dates
        DateTimeZoneHandling = DateTimeZoneHandling.Utc // Assume UTC for DateTime if not specified
    };
#endif

    /// <summary>
    /// Serializes an object to a JSON string.
    /// Uses Newtonsoft.Json if available, otherwise falls back to JsonUtility (with limitations).
    /// </summary>
    /// <param name="obj">The object to serialize.</param>
    /// <param name="prettyPrint">If true, formats the JSON for readability.</param>
    /// <returns>A JSON string representation of the object, or null if serialization fails.</returns>
    public static string SerializeObject(object obj, bool prettyPrint = false)
    {
        if (obj == null) return null;

#if NEWTONSOFT_JSON_AVAILABLE
        try
        {
            Formatting formatting = prettyPrint ? Formatting.Indented : Formatting.None;
            return JsonConvert.SerializeObject(obj, formatting, DefaultNewtonsoftSettings);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonHelper] Newtonsoft.Json Serialization Error for type {obj.GetType().FullName}: {ex.Message}\nStack: {ex.StackTrace}");
            return null;
        }
#else
        // Fallback to Unity's JsonUtility (limited functionality)
        // JsonUtility only works with [System.Serializable] classes/structs and public fields
        // or fields marked with [SerializeField]. It does not support Dictionaries directly.
        try
        {
            return JsonUtility.ToJson(obj, prettyPrint);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonHelper] JsonUtility Serialization Error for type {obj.GetType().FullName}: {ex.Message}. " +
                           "Ensure the object is [System.Serializable] and all fields to be serialized are public or [SerializeField]. " +
                           "JsonUtility does not support Dictionaries or complex types well. Consider using Newtonsoft.Json.",
                           obj as UnityEngine.Object); // Pass context if obj is a Unity Object
            return null;
        }
#endif
    }

    /// <summary>
    /// Deserializes a JSON string to an object of the specified type.
    /// Uses Newtonsoft.Json if available, otherwise falls back to JsonUtility (with limitations).
    /// </summary>
    /// <typeparam name="T">The type of the object to deserialize to.</typeparam>
    /// <param name="jsonString">The JSON string to deserialize.</param>
    /// <returns>An object of type T, or null (or default for value types) if deserialization fails or JSON is empty.</returns>
    public static T DeserializeObject<T>(string jsonString)
    {
        if (string.IsNullOrEmpty(jsonString))
        {
            // Debug.LogWarning($"[JsonHelper] Attempted to deserialize an empty or null JSON string for type {typeof(T).FullName}. Returning default.");
            return default(T); // Returns null for reference types, 0 for int, false for bool, etc.
        }

#if NEWTONSOFT_JSON_AVAILABLE
        try
        {
            return JsonConvert.DeserializeObject<T>(jsonString, DefaultNewtonsoftSettings);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonHelper] Newtonsoft.Json Deserialization Error for type {typeof(T).FullName}: {ex.Message}\nJSON String: {jsonString.Substring(0, Mathf.Min(jsonString.Length, 500))}..."); // Log a snippet
            return default(T);
        }
#else
        // Fallback to Unity's JsonUtility
        try
        {
            return JsonUtility.FromJson<T>(jsonString);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonHelper] JsonUtility Deserialization Error for type {typeof(T).FullName}: {ex.Message}. " +
                           "Ensure the C# class fields exactly match JSON keys (case-sensitive) and the type is [System.Serializable]. " +
                           "JsonUtility has limitations. Consider using Newtonsoft.Json.\nJSON String: " +
                           jsonString.Substring(0, Mathf.Min(jsonString.Length, 500)) + "...");
            return default(T);
        }
#endif
    }

    // --- Example of how ApiService.cs might use this JsonHelper ---
    /*
    // In ApiService.cs (Conceptual change, ApiService currently uses JsonConvert directly)

    // private static T DeserializeResponse<T>(UnityWebRequest request, string urlForLog) where T : class
    // {
    //     // ... (error checking for request) ...
    //     if (request.result == UnityWebRequest.Result.Success)
    //     {
    //         return JsonHelper.DeserializeObject<T>(request.downloadHandler.text);
    //     }
    //     // ... (error handling) ...
    // }

    // public static async Task<TResponse> PostAsync<TRequest, TResponse>(string endpoint, TRequest payload)
    // {
    //     // ...
    //     string jsonPayload = JsonHelper.SerializeObject(payload);
    //     // ...
    // }
    */
}