// LandmarkDataComponent.cs
// 放置路徑建議: Assets/Scripts/NpcLogic/Components/LandmarkDataComponent.cs

using UnityEngine;
using System.Collections.Generic; // For List
using NpcApiModels; // 引用我們在 NpcApiDataModels.cs 中定義的命名空間

/// <summary>
/// Stores descriptive data for a landmark or significant location within the game scene.
/// This component should be attached to GameObjects representing such landmarks.
/// The information is used by NPCs when making decisions (e.g., where to go, what to comment on).
/// </summary>
public class LandmarkDataComponent : MonoBehaviour
{
    [Header("Landmark Identification & Type")]
    [Tooltip("場景名稱")]
    public string landmarkName = "";

    [Tooltip("場景標籤")]
    public string landmarkTypeTag = "generic_point_of_interest";

    [Tooltip("擁有者ID")]
    public string ownerNpcId = ""; // Should match an npcId from a CharacterData component

    [Header("Landmark Context & Status")]
    [TextArea(2, 5)]
    [Tooltip("場景介紹說明")]
    public List<string> initialStatusNotes = new List<string>();

    /// <summary>
    /// Called in the editor when the script is loaded or a value is changed in the Inspector.
    /// Provides immediate feedback and default value population in the editor.
    /// </summary>
    void OnValidate()
    {
        if (string.IsNullOrEmpty(landmarkName))
        {
            landmarkName = gameObject.name;
        }

        if (string.IsNullOrEmpty(landmarkTypeTag))
        {
            // Ensure a default type tag if none is provided, to prevent null/empty strings
            // where the API might expect a value.
            landmarkTypeTag = "generic_point_of_interest";
            // It's good practice to inform the developer if a default is being applied.
            // Debug.LogWarning($"LandmarkDataComponent on '{gameObject.name}' had an empty 'Landmark Type Tag'. Defaulted to 'generic_point_of_interest'. " +
            //                  "It's highly recommended to set a more descriptive tag.", this);
        }
    }

    /// <summary>
    /// Helper method to convert this landmark's data into a LandmarkContextInfo object
    /// for use in API requests (e.g., when this landmark is visible to an NPC).
    /// </summary>
    /// <returns>A LandmarkContextInfo object populated with data from this component.</returns>
    public LandmarkContextInfo ToLandmarkContextInfo()
    {
        // Ensure that the position is correctly captured from the GameObject's transform
        NpcApiModels.Position currentPosition = new NpcApiModels.Position
        {
            x = transform.position.x,
            y = transform.position.y
            // Assuming 2D, so z is not included in the Position model for the API.
            // If your game is 3D and the API expects z, you'd add it to the Position model
            // in NpcApiDataModels.cs and populate it here.
        };

        return new LandmarkContextInfo
        {
            landmark_name = this.landmarkName,
            position = currentPosition,
            landmark_type_tag = this.landmarkTypeTag,
            owner_id = string.IsNullOrEmpty(this.ownerNpcId) ? null : this.ownerNpcId, // API expects null for empty optional strings
            current_status_notes = new List<string>(this.initialStatusNotes ?? new List<string>()) // Create a new list to avoid external modification issues, and handle if initialStatusNotes is null
        };
    }
}