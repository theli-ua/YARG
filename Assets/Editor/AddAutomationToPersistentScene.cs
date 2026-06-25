using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YARG;

/// <summary>
/// Editor utility: adds RuntimeAutomation component to the PersistentScene.
/// Run this once to set up the automation scene, then it will persist in the scene file.
/// </summary>
public static class AddAutomationToPersistentScene
{
    [MenuItem("YARG/Setup/Automation to PersistentScene")]
    public static void AddAutomation()
    {
        // Find the PersistentScene
        var scenePath = "Assets/Scenes/PersistentScene.unity";
        var scene = EditorSceneManager.OpenScene(scenePath);
        
        // Remove any existing RuntimeAutomation first
        var existing = scene.GetRootGameObjects()
            .SelectMany(g => g.GetComponentsInChildren<RuntimeAutomation>())
            .ToList();
        foreach (var ea in existing)
        {
            GameObject.DestroyImmediate(ea.gameObject);
        }
        
        // Add RuntimeAutomation to the first root object
        var root = scene.GetRootGameObjects().FirstOrDefault();
        if (root == null)
        {
            Debug.LogError("[AddAutomationToPersistentScene] No root objects found in PersistentScene");
            return;
        }
        
        var go = new GameObject("RuntimeAutomation");
        go.AddComponent<RuntimeAutomation>();
        // Make it a root object so DontDestroyOnLoad works
        go.transform.SetParent(null);
        
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[AddAutomationToPersistentScene] Added RuntimeAutomation to PersistentScene");
    }
}
        

