// 檔案名稱: RoomDataComponent.cs (這是一個新檔案)
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/RoomDataComponent.cs

using UnityEngine;
using System.Collections.Generic;
using NpcApiModels; // 如果需要轉換為 API 模型

// 用於在 Inspector 中定義房間內的物件及其描述
[System.Serializable]
public class ContainedObjectInfo
{
    [Tooltip("對房間內物件 GameObject 的引用。該物件本身可以掛載 LandmarkDataComponent。")]
    public GameObject objectReference; // GameObject 本身可能帶有 LandmarkDataComponent
    [Tooltip("對此物件在該房間內的額外文字說明或情境描述。")]
    [TextArea(2,4)]
    public string descriptionInRoom;
    [Tooltip("此物件在此房間內的相對重要性或用途標籤。")]
    public string objectRoleTag = "general_item"; // 例如 "main_furniture", "interactable_appliance", "decoration"
}

/// <summary>
/// 專門用於定義房間屬性的組件。
/// 它定義了房間的邊界、出入口以及內部包含的關鍵物件。
/// </summary>
[RequireComponent(typeof(Collider2D))] // 房間本身需要一個 Collider 來定義其範圍
public class RoomDataComponent : MonoBehaviour
{
    [Header("Room Identification")]
    [Tooltip("房間的名稱，例如：客廳、艾莉絲的臥室、廚房。")]
    public string roomName = "Default Room Name";
    [Tooltip("房間的類型標籤，例如：living_room, bedroom, kitchen, bathroom。")]
    public string roomTypeTag = "generic_room";
    [Tooltip("房間擁有者的 NPC ID (如果是私人房間)。")]
    public string ownerNpcId = ""; // 應與 CharacterData 中的 npcId 匹配

    [Header("Room Physical Definition")]
    [Tooltip("定義房間主要範圍的 Collider2D (通常掛載在同一個 GameObject 上)。")]
    public Collider2D roomBoundsCollider; // 通常是 this.GetComponent<Collider2D>()
    [Tooltip("房間的出入口 Collider2D 列表。這些 Collider2D 應設為 Trigger，並放置在代表門口或通道的子 GameObject 上。")]
    public List<Collider2D> entrances;

    [Header("Room Contents")]
    [Tooltip("房間內包含的關鍵物件及其描述列表。")]
    public List<ContainedObjectInfo> containedObjects = new List<ContainedObjectInfo>();

    // --- 動態狀態，例如房間是否被佔用，主人是否在場 ---
    // 這些可以類似於 LandmarkDataComponent 中的 _dynamicStatusNotes 來管理
    private List<string> _roomDynamicStatusNotes = new List<string>();
    private const string OccupancyStatusPrefix = "occupancy_";
    private const string OccupancyStatusOccupied = "occupancy_occupied";
    private const string OwnerPresenceStatusPrefix = "owner_presence_";
    private const string OwnerPresenceAbsent = "owner_presence_absent";
    private const string OwnerPresencePresent = "owner_presence_present";


    void OnValidate()
    {
        if (string.IsNullOrEmpty(roomName))
        {
            roomName = gameObject.name;
        }
        if (roomBoundsCollider == null)
        {
            roomBoundsCollider = GetComponent<Collider2D>();
            if (roomBoundsCollider == null)
            {
                Debug.LogWarning($"RoomDataComponent on '{gameObject.name}' needs a Collider2D to define its bounds. Please add one.", this);
            }
        }
    }

    void Awake()
    {
        if (roomBoundsCollider == null)
        {
            roomBoundsCollider = GetComponent<Collider2D>();
        }
        // 可以在這裡做一些初始化檢查，例如 entrances 是否都設定為 Trigger
        foreach (var entrance in entrances)
        {
            if (entrance != null && !entrance.isTrigger)
            {
                Debug.LogWarning($"Entrance Collider '{entrance.name}' for room '{roomName}' is not set to 'Is Trigger'. This might affect NPC enter/exit detection.", entrance);
            }
        }
    }

    // --- 動態狀態管理方法 (類似 LandmarkDataComponent) ---
    public void UpdateRoomDynamicStatusByPrefix(string notePrefixToRemove, string newNoteFull)
    {
        _roomDynamicStatusNotes.RemoveAll(note => note.StartsWith(notePrefixToRemove));
        if (!string.IsNullOrEmpty(newNoteFull))
        {
            if (!_roomDynamicStatusNotes.Contains(newNoteFull))
            {
                _roomDynamicStatusNotes.Add(newNoteFull);
            }
        }
    }
    public void AddRoomDynamicStatus(string note)
    {
        if (!string.IsNullOrEmpty(note) && !_roomDynamicStatusNotes.Contains(note))
            _roomDynamicStatusNotes.Add(note);
    }
    public void RemoveRoomDynamicStatus(string note)
    {
        _roomDynamicStatusNotes.Remove(note);
    }
    public bool HasRoomDynamicStatus(string note)
    {
        return _roomDynamicStatusNotes.Contains(note);
    }
     public bool HasRoomDynamicStatusWithPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return false;
        return _roomDynamicStatusNotes.Exists(note => note.StartsWith(prefix));
    }
    public List<string> GetCurrentRoomDynamicStatusNotes()
    {
        return new List<string>(_roomDynamicStatusNotes);
    }


    /// <summary>
    /// 將此房間的資料轉換為一個或多個 LandmarkContextInfo 物件，
    /// 或者一個更豐富的 RoomContextInfo (如果後端 API 支援)。
    /// 目前，我們將房間本身視為一個主要地標，其包含的物件是其屬性的一部分。
    /// </summary>
    public LandmarkContextInfo ToRoomAsLandmarkContextInfo()
    {
        NpcApiModels.Position roomCenterPosition = new NpcApiModels.Position
        {
            // 使用 bounds 的中心點作為房間的代表位置
            x = roomBoundsCollider != null ? roomBoundsCollider.bounds.center.x : transform.position.x,
            y = roomBoundsCollider != null ? roomBoundsCollider.bounds.center.y : transform.position.y
        };

        List<string> allNotes = new List<string>();
        // 可以加入房間的整體描述
        allNotes.Add($"This is the {roomName} (type: {roomTypeTag}).");
        if (!string.IsNullOrEmpty(ownerNpcId))
        {
            allNotes.Add($"It is the private room of {ownerNpcId}.");
        }

        // 添加房間內的物件描述
        foreach (var item in containedObjects)
        {
            if (item.objectReference != null)
            {
                LandmarkDataComponent itemLandmark = item.objectReference.GetComponent<LandmarkDataComponent>();
                string itemName = itemLandmark != null ? itemLandmark.landmarkName : item.objectReference.name;
                string itemType = itemLandmark != null ? itemLandmark.landmarkTypeTag : "unknown_item_type";
                
                string note = $"Contains '{itemName}' (type: {itemType}, role: {item.objectRoleTag}).";
                if (!string.IsNullOrEmpty(item.descriptionInRoom))
                {
                    note += $" Description: {item.descriptionInRoom}";
                }
                // 可以考慮也加入物品自身的 LandmarkDataComponent 的 static notes 和 dynamic notes
                if (itemLandmark != null)
                {
                    var itemStaticNotes = itemLandmark.initialStaticNotes;
                    if(itemStaticNotes != null && itemStaticNotes.Count > 0) note += $" Item static notes: {string.Join("; ", itemStaticNotes)}.";
                    var itemDynamicNotes = itemLandmark.GetCurrentDynamicStatusNotes();
                    if(itemDynamicNotes != null && itemDynamicNotes.Count > 0) note += $" Item dynamic status: {string.Join("; ", itemDynamicNotes)}.";
                }
                allNotes.Add(note);
            }
        }
        
        // 合併房間自身的動態狀態
        allNotes.AddRange(GetCurrentRoomDynamicStatusNotes());

        return new LandmarkContextInfo
        {
            landmark_name = this.roomName,
            position = roomCenterPosition,
            landmark_type_tag = this.roomTypeTag, // 例如 "bedroom", "kitchen"
            owner_id = string.IsNullOrEmpty(this.ownerNpcId) ? null : this.ownerNpcId,
            current_status_notes = allNotes
        };
    }

    /// <summary>
    /// 獲取房間內所有被定義的 ContainedObjectInfo 中的 LandmarkDataComponent 列表。
    /// 這可以用於讓 SceneContextManager 也能收集到這些被「包含」的地標。
    /// </summary>
    public List<LandmarkDataComponent> GetContainedLandmarks()
    {
        List<LandmarkDataComponent> landmarks = new List<LandmarkDataComponent>();
        foreach (var item in containedObjects)
        {
            if (item.objectReference != null)
            {
                LandmarkDataComponent ldc = item.objectReference.GetComponent<LandmarkDataComponent>();
                if (ldc != null)
                {
                    landmarks.Add(ldc);
                }
            }
        }
        return landmarks;
    }
}