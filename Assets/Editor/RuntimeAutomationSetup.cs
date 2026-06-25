using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup: adds RuntimeAutomation component to the PersistentScene at play mode entry.
/// </summary>
[InitializeOnLoad]
public static class RuntimeAutomationSetup
{
    static RuntimeAutomationSetup()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            // Check if RuntimeAutomation exists in the scene
            var automation = GameObject.FindObjectOfType<YARG.RuntimeAutomation>();
            if (automation == null)
            {
                // Add it to the first scene
                var scene = SceneManager.GetSceneAt(0);
                var root = scene.GetRootGameObjects().FirstOrDefault();
                if (root != null)
                {
                    var go = new GameObject("RuntimeAutomation");
                    go.AddComponent<YARG.RuntimeAutomation>();
                    go.transform.SetParent(root.transform);
                    Debug.Log("[RuntimeAutomationSetup] Added RuntimeAutomation to scene");
                }
            }
        }
    }
}
