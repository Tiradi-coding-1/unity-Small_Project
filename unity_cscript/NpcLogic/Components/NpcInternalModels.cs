// NpcInternalModels.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/NpcInternalModels.cs (或類似的 Utils/Core 資料夾)

using UnityEngine;

namespace NpcInternalModels
{
    public enum ResponseItemType
    {
        None,
        DialogueMessage,     // 對話訊息
        MovementCommand,     // 移動指令
        WaitCommand,         // 等待指令 (可以是特定時間或直到條件滿足)
        AnimationTrigger,    // 動畫觸發 (未來擴充)
        SoundEffect,         // 音效播放 (未來擴充)
        CustomAction         // 自訂動作回呼 (未來擴充)
    }

    [System.Serializable]
    public class ResponseItem
    {
        public ResponseItemType itemType = ResponseItemType.None;

        // Payloads - 根據 itemType 使用合適的 payload
        public string stringPayload;     // 用於 DialogueMessage (訊息內容), AnimationTrigger (動畫名稱), SoundEffect (音效名稱)
        public Vector3 vector3Payload;   // 用於 MovementCommand (目標座標)
        public float floatPayload1;      // 用於 DialogueMessage (持續時間), WaitCommand (等待時間)
        public float floatPayload2;      // 保留作其他用途
        public bool boolPayload;         // 保留作其他用途
        public Object objectPayload;     // 用於更複雜的上下文傳遞，例如目標 LandmarkDataComponent
        public System.Action customActionPayload; // 用於 CustomAction

        // --- 靜態工廠方法，方便創建不同類型的 ResponseItem ---

        public static ResponseItem AsDialogue(string message, float duration)
        {
            return new ResponseItem
            {
                itemType = ResponseItemType.DialogueMessage,
                stringPayload = message,
                floatPayload1 = duration
            };
        }

        public static ResponseItem AsMovement(Vector3 targetPosition)
        {
            return new ResponseItem
            {
                itemType = ResponseItemType.MovementCommand,
                vector3Payload = targetPosition
            };
        }

        public static ResponseItem AsWait(float duration, LandmarkDataComponent targetLandmarkWhileWaiting = null)
        {
            return new ResponseItem
            {
                itemType = ResponseItemType.WaitCommand,
                floatPayload1 = duration, // duration <= 0 表示等待條件 (例如 targetLandmarkWhileWaiting 變可用)
                objectPayload = targetLandmarkWhileWaiting
            };
        }

        public static ResponseItem AsAnimation(string triggerName)
        {
            return new ResponseItem
            {
                itemType = ResponseItemType.AnimationTrigger,
                stringPayload = triggerName
            };
        }

        public static ResponseItem AsCustomAction(System.Action action)
        {
            return new ResponseItem
            {
                itemType = ResponseItemType.CustomAction,
                customActionPayload = action
            };
        }

        public override string ToString()
        {
            string details = "";
            switch (itemType)
            {
                case ResponseItemType.DialogueMessage:
                    details = $"Msg: '{stringPayload?.Substring(0, Mathf.Min(stringPayload?.Length ?? 0, 20))}...', Dur: {floatPayload1}";
                    break;
                case ResponseItemType.MovementCommand:
                    details = $"Target: {vector3Payload}";
                    break;
                case ResponseItemType.WaitCommand:
                    details = $"Duration: {floatPayload1}, For: {((LandmarkDataComponent)objectPayload)?.landmarkName ?? "Condition"}";
                    break;
                case ResponseItemType.AnimationTrigger:
                    details = $"Anim: {stringPayload}";
                    break;
                case ResponseItemType.CustomAction:
                    details = "Custom Action";
                    break;
            }
            return $"ResponseItem ({itemType} - {details})";
        }
    }
}