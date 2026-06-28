using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using YARG.Gameplay;

namespace YARG.Editor.Automation
{
    /// <summary>
    /// Command-line automation for loading songs, running gameplay, taking screenshots, and exiting.
    /// 
    /// Usage from command line:
    ///   Unity -batchmode -executeMethod YARG.Editor.Automation.AutomationRunner.Run
    ///   Unity -batchmode -executeMethod YARG.Editor.Automation.AutomationRunner.Run -duration 10 -screenshotDir "./screenshots"
    ///   
    /// Arguments (passed as Unity command-line args):
    ///   -duration N               - Seconds to run (default: 15)
    ///   -screenshotDir "path"     - Directory for screenshots (default: "./AutomationScreenshots")
    ///   -screenshotInterval N     - Seconds between screenshots (default: 2)
    ///   -profile                  - Enable Unity Profiler and output stats
    ///   -exit                     - Exit after completion (default in batchmode)
    /// </summary>
    public static class AutomationRunner
    {
        public const string DefaultScreenshotDir = "AutomationScreenshots";
        public const int DefaultDuration = 15;
        public const int DefaultScreenshotInterval = 2;

        private static readonly System.Threading.ManualResetEvent s_playModeEvent = new(false);

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

            // Enter play mode.
            // We use playModeStateChanged to wait for EnteredPlayMode before
            // creating the automation runner, because the runtime scene needs
            // to be fully initialized first.
            // IMPORTANT: Do NOT use -exit flag on command line. The AutomationRunnerBehaviour
            // calls EditorApplication.Exit(0) itself when done.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Enter play mode (works in batch mode)
            EditorApplication.isPlaying = true;

            // Block until play mode enters. ManualResetEvent.WaitOne() yields the thread
            // but Unity still processes editor callbacks (playModeStateChanged fires).
            s_playModeEvent.WaitOne(TimeSpan.FromSeconds(30));

            if (!s_playModeEntered)
            {
                Debug.LogError("[AutomationRunner] Timed out waiting for play mode.");
                EditorApplication.Exit(1);
            }
        }

        private static bool s_playModeEntered;

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                s_playModeEntered = true;
                s_playModeEvent.Set();
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                Debug.Log("[AutomationRunner] Entered play mode");

                // Create runner in the runtime scene
                var runner = new GameObject("_AutomationRunner");
                var behaviour = runner.AddComponent<AutomationRunnerBehaviour>();
                UnityEngine.Object.DontDestroyOnLoad(runner);

                behaviour.Run(ParseArguments());
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            }
        }

        private static System.Collections.Generic.Dictionary<string, string> ParseArguments()
        {
            var args = new System.Collections.Generic.Dictionary<string, string>();
            string[] commandLine = Environment.GetCommandLineArgs();
            
            for (int i = 1; i < commandLine.Length; i++)
            {
                string arg = commandLine[i];
                if (!arg.StartsWith("-")) continue;
                
                string key = arg.Substring(1).TrimStart('-');
                
                // Check if next argument is a value (doesn't start with '-')
                if (i + 1 < commandLine.Length && !commandLine[i + 1].StartsWith("-"))
                {
                    args[key] = commandLine[i + 1];
                    i++; // Skip next as it's the value
                }
                else
                {
                    args[key] = "";
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
        private System.Collections.Generic.Dictionary<string, string> _args;
        private string _screenshotDir;
        private int _duration;
        private int _screenshotInterval;
        private bool _profile;
        private float _elapsedTime;
        private float _lastScreenshotTime;
        private bool _completed;
        private bool _sceneReady;

        public void Run(System.Collections.Generic.Dictionary<string, string> args)
        {
            _args = args;
            
            // Parse settings
            _screenshotDir = _args.TryGetValue("screenshotDir", out var sd) ? sd : AutomationRunner.DefaultScreenshotDir;
            _duration = _args.TryGetValue("duration", out var dur) && int.TryParse(dur, out var d) ? d : AutomationRunner.DefaultDuration;
            _screenshotInterval = _args.TryGetValue("screenshotInterval", out var siStr) && int.TryParse(siStr, out var si) ? si : AutomationRunner.DefaultScreenshotInterval;
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

            _sceneReady = false;
        }

        private void Update()
        {
            if (_completed) return;

            // Wait for GameManager to exist and song to be started
            if (!_sceneReady)
            {
                var gameManager = UnityEngine.Object.FindAnyObjectByType<GameManager>();
                if (gameManager != null && gameManager.IsSongStarted)
                {
                    _sceneReady = true;
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
            stats.AppendLine($"Allocated Memory: {Profiler.GetTotalAllocatedMemoryLong() / 1024 / 1024} MB");
            stats.AppendLine($"Total Reserved Memory: {Profiler.GetTotalReservedMemoryLong() / 1024 / 1024} MB");
            stats.AppendLine($"GC Heap Size: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
            
            string statsFile = Path.Combine(_screenshotDir, "stats.txt");
            File.WriteAllText(statsFile, stats.ToString());
            Debug.Log($"[AutomationRunner] Stats written to {statsFile}");
            Debug.Log(stats.ToString());
        }

        private void ExitNow()
        {
            if (_profile)
                Profiler.enabled = false;
                
            Debug.Log("[AutomationRunner] Exiting");
            
            #if UNITY_EDITOR
            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(0);
            #else
            Application.Quit(0);
            #endif
        }

        private void OnDestroy()
        {
            if (_profile)
                Profiler.enabled = false;
        }
    }
}
