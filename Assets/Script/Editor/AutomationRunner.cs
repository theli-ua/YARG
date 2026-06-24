using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using YARG.Gameplay;

namespace YARG.Editor.Automation
{
    /// <summary>
    /// Command-line automation for loading songs, running gameplay, taking screenshots, and exiting.
    /// 
    /// Usage from command line:
    ///   Unity -batchmode -nographics -executeMethod YARG.Editor.Automation.AutomationRunner.Run
    ///   Unity -batchmode -executeMethod YARG.Editor.Automation.AutomationRunner.Run -songPath "path/to/chart" -duration 10 -screenshotDir "./screenshots"
    ///   
    /// Arguments (passed as Unity command-line args):
    ///   -songPath "path"          - Path to a .chart file (relative to project or absolute)
    ///   -songId "id"              - Song ID from the songs database (alternative to songPath)
    ///   -duration N               - Seconds to run (default: 15)
    ///   -screenshotDir "path"     - Directory for screenshots (default: "./AutomationScreenshots")
    ///   -screenshotInterval N     - Seconds between screenshots (default: 2)
    ///   -profile                  - Enable Unity Profiler and output stats
    ///   -instrument "guitar"      - Instrument: guitar, drums, keys, prokeys, vocals (default: guitar)
    ///   -difficulty N             - Difficulty: 0-4 (default: 2)
    ///   -exit                     - Exit after completion (default in batchmode)
    /// </summary>
    public static class AutomationRunner
    {
        private const string DefaultScreenshotDir = "AutomationScreenshots";
        private const int DefaultDuration = 15;
        private const int DefaultScreenshotInterval = 2;

        /// <summary>
        /// Main entry point called via -executeMethod from command line.
        /// </summary>
        [MenuItem("YARG/Automation/Run Song (Batch)")]
        public static void Run()
        {
            // Parse command-line arguments
            var args = ParseArguments();
            
            Debug.Log($"[AutomationRunner] Starting automation run");
            Debug.Log($"[AutomationRunner] Arguments: {string.Join(", ", args.Select(a => $"{a.Key}={a.Value}"))}");

            // Validate we're in a playable state
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[AutomationRunner] Already in play mode. Skipping.");
                EditorApplication.Exit(0);
                return;
            }

            // Start coroutine for async operations
            var runner = new GameObject("_AutomationRunner");
            runner.AddComponent<AutomationRunnerBehaviour>();
            DontDestroyOnLoad(runner);
            
            var behaviour = runner.GetComponent<AutomationRunnerBehaviour>();
            behaviour.Run(args);
        }

        private static Dictionary<string, string> ParseArguments()
        {
            var args = new Dictionary<string, string>();
            string[] commandLine = Environment.GetCommandLineArgs();
            
            for (int i = 0; i < commandLine.Length - 1; i++)
            {
                string arg = commandLine[i].TrimStart('-');
                if (!string.IsNullOrEmpty(arg) && i + 1 < commandLine.Length)
                {
                    string value = commandLine[i + 1];
                    args[arg] = value;
                    i++; // Skip next as it's the value
                }
            }
            
            return args;
        }
    }

    /// <summary>
    /// Behaviour that runs the actual automation in the Unity player loop.
    /// </summary>
    public class AutomationRunnerBehaviour : MonoBehaviour
    {
        private Dictionary<string, string> _args;
        private string _screenshotDir;
        private int _duration;
        private int _screenshotInterval;
        private bool _profile;
        private float _elapsedTime;
        private float _lastScreenshotTime;
        private bool _completed;
        private bool _sceneLoaded;

        public void Run(Dictionary<string, string> args)
        {
            _args = args;
            
            // Parse settings
            _screenshotDir = _args.GetValueOrDefault("screenshotDir", AutomationRunner.DefaultScreenshotDir);
            _duration = int.TryParse(_args.GetValueOrDefault("duration", AutomationRunner.DefaultDuration.ToString()), out var d) ? d : AutomationRunner.DefaultDuration;
            _screenshotInterval = int.TryParse(_args.GetValueOrDefault("screenshotInterval", AutomationRunner.DefaultScreenshotInterval.ToString()), out var si) ? si : AutomationRunner.DefaultScreenshotInterval;
            _profile = _args.ContainsKey("profile");

            // Create screenshot directory
            Directory.CreateDirectory(_screenshotDir);

            Debug.Log($"[AutomationRunner] Duration: {_duration}s, Screenshot interval: {_screenshotInterval}s, Output: {_screenshotDir}");

            // Enable profiler if requested
            if (_profile)
            {
                Profiler.enabled = true;
                Debug.Log("[AutomationRunner] Profiler enabled");
            }

            // Load gameplay scene
            LoadGameplayScene();
        }

        private void LoadGameplayScene()
        {
            // Find the gameplay scene
            string gameplayScene = null;
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            
            foreach (var scene in scenes)
            {
                if (scene.Contains("Gameplay") || scene.Contains("gameplay"))
                {
                    gameplayScene = scene;
                    break;
                }
            }

            if (gameplayScene == null)
            {
                // Try to find any scene with Gameplay in the path
                var allScenes = Directory.GetFiles("Assets/Scenes", "*.unity", SearchOption.AllDirectories);
                gameplayScene = allScenes.FirstOrDefault(s => s.Contains("Gameplay"));
            }

            if (gameplayScene == null)
            {
                Debug.LogError("[AutomationRunner] Could not find Gameplay scene!");
                Exit(1);
                return;
            }

            Debug.Log($"[AutomationRunner] Loading scene: {gameplayScene}");
            
            // Use SceneManager to load (works in both Editor and player)
            UnityEngine.SceneManagement.SceneManager.LoadScene(Path.GetFileNameWithoutExtension(gameplayScene));
        }

        private void Update()
        {
            if (_completed) return;

            // Wait for scene to be ready and GameManager to exist
            if (!_sceneLoaded)
            {
                var gameManager = GameObject.FindObjectOfType<GameManager>();
                if (gameManager != null && gameManager.IsSongStarted)
                {
                    _sceneLoaded = true;
                    _elapsedTime = 0f;
                    Debug.Log($"[AutomationRunner] Song loaded, running for {_duration}s");
                    
                    // Take initial screenshot
                    TakeScreenshot("start");
                }
                return;
            }

            _elapsedTime += Time.deltaTime;

            // Periodic screenshots
            if (_elapsedTime - _lastScreenshotTime >= _screenshotInterval)
            {
                _lastScreenshotTime = _elapsedTime;
                TakeScreenshot($"t{_elapsedTime:F1}");
            }

            // Check completion
            if (_elapsedTime >= _duration)
            {
                TakeScreenshot("end");
                PrintStats();
                _completed = true;
                
                Debug.Log($"[AutomationRunner] Automation complete. Elapsed: {_elapsedTime:F1}s");
                
                // Exit after a brief delay for final screenshot to flush
                Invoke(nameof(ExitNow), 1f);
            }
        }

        private void TakeScreenshot(string suffix)
        {
            try
            {
                string filename = Path.Combine(_screenshotDir, $"automation_{suffix}_{_elapsedTime:F1}.png");
                ScreenCapture.CaptureScreenshot(filename, 2); // 2x scaling
                Debug.Log($"[AutomationRunner] Screenshot: {filename}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AutomationRunner] Screenshot failed: {e.Message}");
            }
        }

        private void PrintStats()
        {
            if (!_profile) return;

            var stats = new StringBuilder();
            stats.AppendLine($"=== Automation Stats ===");
            stats.AppendLine($"Duration: {_elapsedTime:F1}s");
            stats.AppendLine($"FPS: {1f / Time.deltaTime:F0}");
            stats.AppendLine($"Allocated Memory: {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1024 / 1024} MB");
            stats.AppendLine($"Total Reserved Memory: {UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1024 / 1024} MB");
            stats.AppendLine($"Texture Memory: {UnityEngine.Profiling.Profiler.GetTextureMemorySizeLong() / 1024 / 1024} MB");
            stats.AppendLine($"Mesh Memory: {UnityEngine.Profiling.Profiler.GetMeshMemorySizeLong() / 1024 / 1024} MB");
            
            // Try to get frame info
            var frameInfo = new UnityEngine.Rendering.Statistics();
            stats.AppendLine($"Triangles: {frameInfo.triangles:N0}");
            stats.AppendLine($"Batches: {frameInfo.batches}");
            stats.AppendLine($"SetPass Calls: {frameInfo.setPassCalls}");
            
            string statsFile = Path.Combine(_screenshotDir, "stats.txt");
            File.WriteAllText(statsFile, stats.ToString());
            Debug.Log($"[AutomationRunner] Stats written to {statsFile}");
            Debug.Log(stats.ToString());
        }

        private void ExitNow()
        {
            Exit(0);
        }

        private void Exit(int code)
        {
            if (_profile)
                Profiler.enabled = false;
                
            Debug.Log($"[AutomationRunner] Exiting with code {code}");
            
            #if UNITY_EDITOR
            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(code);
            #else
            Application.Quit(code);
            #endif
        }

        private void OnDestroy()
        {
            if (_profile)
                Profiler.enabled = false;
        }
    }
}
