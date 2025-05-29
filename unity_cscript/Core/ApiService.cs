// ApiService.cs
// 放置路徑建議: Assets/Scripts/Core/ApiService.cs

using UnityEngine;
using UnityEngine.Networking; // Unity's web request module
using System.Text;          // For Encoding.UTF8
using System.Threading.Tasks; // For Task, async, await
using System.Collections.Generic; // For Dictionary (query parameters)

// **重要**: 強烈建議使用 Newtonsoft.Json (Json.NET)
// 如果您已安裝，請取消以下行的註解。
//否則，您需要修改下面的 JSON 序列化/反序列化邏輯以使用 JsonUtility (功能較有限)。
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization; // For CamelCasePropertyNamesContractResolver if needed

// 引用您在 NpcApiDataModels.cs 中定義的資料模型命名空間
using NpcApiModels;

public static class ApiService
{
    private static string _apiBaseUrl = "http://localhost:8000"; // 預設值，應通過 SetApiBaseUrl 覆蓋
    private static bool _isBaseUrlSet = false;

    // 用於 Newtonsoft.Json 的序列化設定，例如忽略 null 值，並可選地使用駝峰命名
    private static readonly JsonSerializerSettings _jsonSerializerSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore, // 發送 JSON 時不包含值為 null 的欄位
        // ContractResolver = new CamelCasePropertyNamesContractResolver(), // 如果 Python API 使用 camelCase
        // Python API 使用 snake_case，Newtonsoft.Json 預設對 snake_case 有一定的自動映射能力，
        // 但在 NpcApiDataModels.cs 中使用 [JsonProperty("snake_case_name")] 是最可靠的。
        // 如果不使用 [JsonProperty]，並且希望自動轉換，則需要自訂 ContractResolver。
        // 為簡單起見，我們假設 NpcApiDataModels.cs 中會使用 [JsonProperty] 或 C# 欄位名已是 snake_case。
        // DateParseHandling = DateParseHandling.DateTimeOffset, // 更精確地處理時區信息
        // DateTimeZoneHandling = DateTimeZoneHandling.Utc // 假設所有日期都是UTC
    };

    /// <summary>
    /// 設定 API 的基地址。應在遊戲初始化時調用一次。
    /// </summary>
    /// <param name="baseUrl">您的 FastAPI 服務的基地址 (例如 "http://localhost:8000")</param>
    public static void SetApiBaseUrl(string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            Debug.LogError("[ApiService] API Base URL cannot be null or empty.");
            _isBaseUrlSet = false;
            return;
        }
        _apiBaseUrl = baseUrl.TrimEnd('/'); // 移除末尾的斜杠，以防重複
        _isBaseUrlSet = true;
        Debug.Log($"[ApiService] API Base URL has been set to: {_apiBaseUrl}");
    }

    /// <summary>
    /// 內部輔助方法，用於處理 UnityWebRequest 的結果並反序列化 JSON。
    /// </summary>
    private static TResponse DeserializeResponse<TResponse>(UnityWebRequest request, string requestUrlForLog) where TResponse : class
    {
        string responseText = request.downloadHandler?.text; // 安全地獲取回應文本

        if (request.result == UnityWebRequest.Result.Success)
        {
            if (string.IsNullOrEmpty(responseText))
            {
                // 某些成功的請求 (例如 HTTP 204 No Content) 可能沒有回應體
                if (request.responseCode == 204 && typeof(TResponse) == typeof(object)) // 假設 TResponse 為 object 時表示不期望有內容
                {
                     Debug.Log($"[ApiService] Request to {requestUrlForLog} successful (HTTP {request.responseCode} No Content).");
                     return null; // 或者一個特殊的成功標記物件
                }
                Debug.LogWarning($"[ApiService] Successful request to {requestUrlForLog} (HTTP {request.responseCode}) but received an empty response body where one was expected for type {typeof(TResponse).Name}.");
                return null;
            }

            try
            {
                // 使用 Newtonsoft.Json 進行反序列化
                TResponse deserializedObject = JsonConvert.DeserializeObject<TResponse>(responseText, _jsonSerializerSettings);
                Debug.Log($"[ApiService] Successfully deserialized response from {requestUrlForLog} into {typeof(TResponse).Name}.");
                return deserializedObject;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ApiService] JSON Deserialization Error for type {typeof(TResponse).Name} from {requestUrlForLog}. HTTP Status: {request.responseCode}. Error: {ex.Message}\nRaw JSON Response: {responseText}");
                return null;
            }
        }
        else // 處理網路錯誤、協議錯誤或伺服器端錯誤 (非 2xx 狀態碼)
        {
            Debug.LogError($"[ApiService] API Request Failed! URL: {requestUrlForLog}, HTTP Status: {request.responseCode}, Error Type: {request.result}, Message: {request.error}");
            if (!string.IsNullOrEmpty(responseText))
            {
                Debug.LogError($"[ApiService] Error Response Body: {responseText}");
                // 嘗試將錯誤回應體解析為標準的 APIErrorResponse 結構
                try
                {
                    ApiErrorResponse errorResponse = JsonConvert.DeserializeObject<ApiErrorResponse>(responseText, _jsonSerializerSettings);
                    if (errorResponse != null && errorResponse.error != null)
                    {
                        Debug.LogError($"[ApiService] Parsed API Error Detail: '{errorResponse.error.message}' (Internal Code: {errorResponse.error.error_code ?? "N/A"})");
                        // 在這裡可以根據 errorResponse.error.error_code 觸發特定的遊戲內錯誤處理邏輯
                    }
                }
                catch (System.Exception ex)
                {
                    // 如果錯誤回應體不是預期的 JSON 格式
                    Debug.LogWarning($"[ApiService] Could not parse error response body as ApiErrorResponse: {ex.Message}");
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 發送一個異步 POST 請求到指定的 API 端點。
    /// </summary>
    /// <typeparam name="TRequest">請求 payload 的 C# 類型。</typeparam>
    /// <typeparam name="TResponse">期望 API 回應的 C# 類型。</typeparam>
    /// <param name="endpoint">API 端點路徑 (例如 "/dialogue/simple-chat")。</param>
    /// <param name="payload">要發送的請求 payload 物件。</param>
    /// <returns>一個 Task，完成後返回反序列化後的 TResponse 物件，如果出錯則返回 null。</returns>
    public static async Task<TResponse> PostAsync<TRequest, TResponse>(string endpoint, TRequest payload)
        where TRequest : class
        where TResponse : class
    {
        if (!_isBaseUrlSet)
        {
            Debug.LogError("[ApiService] API Base URL not set. Call ApiService.SetApiBaseUrl() first before making API calls.");
            return null;
        }
        string fullUrl = $"{_apiBaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}"; // <--- 修改處

        string jsonPayload = "{}"; // 預設為空 JSON 物件，以防 payload 為 null
        if (payload != null)
        {
            try
            {
                jsonPayload = JsonConvert.SerializeObject(payload, _jsonSerializerSettings);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ApiService] JSON Serialization Error for payload type {typeof(TRequest).Name} to {fullUrl}: {ex.Message}");
                return null;
            }
        }
        
        // 為了調試，可以打印 payload，但要注意如果 payload 包含敏感資訊
        // Debug.Log($"[ApiService] Sending POST to {fullUrl}\nPayload: {jsonPayload}");
        Debug.Log($"[ApiService] Sending POST to {fullUrl} with payload of type {typeof(TRequest).Name}");
        Debug.LogWarning($"[ApiService] For PostAsync - Endpoint: '{endpoint}', _apiBaseUrl: '{_apiBaseUrl}', Constructed fullUrl: '{fullUrl}'");


        using (UnityWebRequest request = new UnityWebRequest(fullUrl, UnityWebRequest.kHttpVerbPOST))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer(); // 必須有 downloadHandler 來接收回應體
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json"); // 表明我們期望接收 JSON

            // 發送請求並等待完成 (異步)
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                // 如果不在主線程（例如在純 C# 線程中調用此 Task），Task.Yield 可能不是最佳選擇
                // 但在 MonoBehaviour 的協程或 async void 方法中，它有助於將控制權交回 Unity
                await Task.Yield(); 
            }
            return DeserializeResponse<TResponse>(request, fullUrl);
        }
    }

    /// <summary>
    /// 發送一個異步 GET 請求到指定的 API 端點。
    /// </summary>
    /// <typeparam name="TResponse">期望 API 回應的 C# 類型。</typeparam>
    /// <param name="endpoint">API 端點路徑 (例如 "/dialogue/list-models")。</param>
    /// <param name="queryParams">可選的查詢參數字典。</param>
    /// <returns>一個 Task，完成後返回反序列化後的 TResponse 物件，如果出錯則返回 null。</returns>
    public static async Task<TResponse> GetAsync<TResponse>(string endpoint, Dictionary<string, string> queryParams = null)
        where TResponse : class
    {
        if (!_isBaseUrlSet)
        {
            Debug.LogError("[ApiService] API Base URL not set. Call ApiService.SetApiBaseUrl() first.");
            return null;
        }
        string fullUrlWithoutQuery = $"{_apiBaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}"; // <--- 修改處
        string fullUrl = fullUrlWithoutQuery;

        if (queryParams != null && queryParams.Count > 0)
        {
            fullUrl += "?";
            var paramList = new List<string>();
            foreach (var param in queryParams)
            {
                // 對鍵和值進行 URL 編碼以處理特殊字符
                paramList.Add($"{UnityWebRequest.EscapeURL(param.Key)}={UnityWebRequest.EscapeURL(param.Value)}");
            }
            fullUrl += string.Join("&", paramList);
        }

        Debug.Log($"[ApiService] Sending GET to {fullUrl}");

        using (UnityWebRequest request = UnityWebRequest.Get(fullUrl)) // UnityWebRequest.Get 內部處理了 GET 方法
        {
            request.SetRequestHeader("Accept", "application/json"); // 表明我們期望接收 JSON

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }
            return DeserializeResponse<TResponse>(request, fullUrl);
        }
    }

    /// <summary>
    /// 發送一個異步 DELETE 請求到指定的 API 端點。
    /// </summary>
    /// <param name="endpoint">API 端點路徑 (例如 "/admin/npc/{npc_id}/memory")。</param>
    /// <returns>一個 Task，完成後返回一個布林值，表示請求是否被認為成功 (基於 HTTP 狀態碼或回應內容)。</returns>
    public static async Task<bool> DeleteAsync(string endpoint)
    {
        if (!_isBaseUrlSet)
        {
            Debug.LogError("[ApiService] API Base URL not set. Call ApiService.SetApiBaseUrl() first.");
            return false;
        }
        string fullUrl = $"{_apiBaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}"; // <--- 修改處
        Debug.Log($"[ApiService] Sending DELETE to {fullUrl}");

        using (UnityWebRequest request = UnityWebRequest.Delete(fullUrl)) // UnityWebRequest.Delete 創建 DELETE 請求
        {
            // DELETE 請求可能也會返回一個 JSON body (例如我們的 ClearMemoryAdministrativelyResponse)
            // 或者一個錯誤 JSON (ApiErrorResponse)，所以需要 downloadHandler
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Accept", "application/json");

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[ApiService] DELETE request to {fullUrl} successful with HTTP Status: {request.responseCode}.");
                // HTTP 204 No Content 也是成功的 DELETE
                if (request.responseCode == 204) return true;

                // 對於 HTTP 200 OK，我們期望一個 ClearMemoryAdministrativelyResponse
                if (request.responseCode == 200)
                {
                    var responseObj = DeserializeResponse<ClearMemoryAdministrativelyResponse>(request, fullUrl);
                    return responseObj != null && responseObj.status == "success"; // 檢查業務邏輯上的成功
                }
                // 其他 2xx 狀態碼也可能表示成功
                return true; 
            }
            else
            {
                // DeserializeResponse 會記錄詳細的錯誤日誌
                DeserializeResponse<object>(request, fullUrl); // 傳入 object 只是為了觸發日誌記錄
                return false;
            }
        }
    }
}