#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(CalibrationProfileDropdownAttribute))]
public class CalibrationProfileDropdownDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        List<string> profiles = GetProfiles();
        string current = property.stringValue;
        if (!string.IsNullOrWhiteSpace(current) && !profiles.Contains(current))
            profiles.Insert(0, current);
        if (profiles.Count == 0)
            profiles.Add(current);

        EditorGUI.BeginProperty(position, label, property);
        int currentIndex = Mathf.Max(0, profiles.IndexOf(current));
        int selectedIndex = EditorGUI.Popup(position, label.text, currentIndex, profiles.ToArray());
        property.stringValue = profiles[Mathf.Clamp(selectedIndex, 0, profiles.Count - 1)];
        EditorGUI.EndProperty();
    }

    private static List<string> GetProfiles()
    {
        string folder = Path.Combine(Application.dataPath, "CalibrationProfiles");
        List<string> profiles = new List<string>();
        if (!Directory.Exists(folder))
            return profiles;

        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name))
                profiles.Add(name);
        }

        profiles.Sort();
        return profiles;
    }
}
#endif
