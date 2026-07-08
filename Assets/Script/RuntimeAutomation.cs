using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using YARG.Gameplay;
using YARG.Menu.Persistent;
using YARG.Core.Song;
using YARG.Song;

namespace YARG
{
#if YARG_TEST_BUILD
    /// <summary>
    /// Runtime automation for the built player.
    /// Triggered via command-line argument:
    ///   -automation               - Activate automation
    ///   -automationDuration N     - Seconds to run (default: 15)
    ///   -automationScreenshotDir "path" - Directory for screenshots
    ///   -automationSongIndex N    - Song index to play (default: 0 = first song, alphabetically ordered)
    ///   -automationSongName "name" - Song name to play (case-insensitive match; overrides -automationSongIndex)
    /// </summary>
    public class RuntimeAutomation : MonoBehaviour
    {
        private int _duration = 15;
        private string _screenshotDir = "AutomationScreenshots";
        private int _songIndex = 0;
        private string _songName = null;

        private void Awake()
        {
            // Parse command-line arguments
            var args = Environment.GetCommandLineArgs();
            bool hasAutomationFlag = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-automation")
                {
                    hasAutomationFlag = true;
                }
            }

            // Only run if explicitly triggered via command-line flag
            if (!hasAutomationFlag)
            {
                enabled = false;
                return;
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-automationDuration" && int.TryParse(args[i + 1], out var d))
                {
                    _duration = d;
                    i++;
                }
                else if (args[i] == "-automationScreenshotDir")
                {
                    _screenshotDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "-automationSongIndex" && int.TryParse(args[i + 1], out var si))
                {
                    _songIndex = si;
                    i++;
                }
                else if (args[i] == "-automationSongName")
                {
                    _songName = args[i + 1];
                    i++;
                }
            }

            Debug.Log($"[RuntimeAutomation] Duration: {_duration}s, Song Index: {_songIndex}, Song Name: {_songName ?? "(none)"}");

            // Start coroutine — do NOT block the main thread
            StartCoroutine(RunAutomationWithSongWait());
        }

        private IEnumerator RunAutomationWithSongWait()
        {
            // Wait for song library to be populated (up to ~8 seconds)
            for (int i = 0; i < 500; i++)
            {
                if (SongContainer.Songs.Length > 0) break;
                yield return new WaitForSecondsRealtime(0.016f);
            }

            if (SongContainer.Songs.Length == 0)
            {
                Debug.LogError("[RuntimeAutomation] Song library empty after 8s. Check song folders.");
                Application.Quit(1);
                yield break;
            }

            Debug.Log($"[RuntimeAutomation] Song library ready: {SongContainer.Songs.Length} songs");

            // Enable NoFail mode so the song doesn't fail without input
            YARG.Settings.SettingsManager.Settings.NoFail.Value = YARG.Gameplay.HUD.NoFailMode.NoMeter;
            Debug.Log("[RuntimeAutomation] NoFail mode enabled");

            yield return StartCoroutine(RunAutomation());
        }

        private IEnumerator RunAutomation()
        {
            // Set the song to load
            if (GlobalVariables.State.CurrentSong == null && SongContainer.Songs.Any())
            {
                var ordered = SongContainer.Songs.OrderBy(s => s.Name).ToArray();
                SongEntry song = null;

                // If song name specified, find by name (case-insensitive)
                if (!string.IsNullOrEmpty(_songName))
                {
                    song = ordered.FirstOrDefault(s => ((string)s.Name).IndexOf(_songName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (song != null)
                    {
                        Debug.Log($"[RuntimeAutomation] Matched song by name \"{_songName}\": {song.Name} by {song.Artist}");
                    }
                    else
                    {
                        Debug.LogError($"[RuntimeAutomation] No song matching \"{_songName}\" found. Available songs:");
                        foreach (var s in ordered)
                        {
                            Debug.Log($"  - {s.Name} by {s.Artist}");
                        }
                        Application.Quit(1);
                        yield break;
                    }
                }
                else
                {
                    // Fall back to index-based selection
                    song = _songIndex < ordered.Length ? ordered[_songIndex] : ordered[0];
                    Debug.Log($"[RuntimeAutomation] Selected song by index {_songIndex}: {song.Name} by {song.Artist}");
                }

                GlobalVariables.State.CurrentSong = song;
            }
            else if (GlobalVariables.State.CurrentSong != null)
            {
                Debug.Log($"[RuntimeAutomation] Using existing song: {GlobalVariables.State.CurrentSong.Name}");
            }

            // Disable menu music player before loading gameplay
            var musicPlayer = UnityEngine.Object.FindFirstObjectByType<MusicPlayer>();
            if (musicPlayer != null)
            {
                musicPlayer.gameObject.SetActive(false);
                Debug.Log("[RuntimeAutomation] Disabled menu music player");
            }

            // Load Gameplay scene through proper scene transition (unloads current, loads Gameplay)
            Debug.Log("[RuntimeAutomation] Loading Gameplay scene...");
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);

            // Wait for Gameplay scene to fully load
            yield return WaitForSceneLoaded(SceneIndex.Gameplay);
            Debug.Log("[RuntimeAutomation] Gameplay scene loaded");

            // Wait for GameManager to exist (up to ~5 seconds)
            GameManager gameManager = null;
            for (int i = 0; i < 300; i++)
            {
                gameManager = UnityEngine.Object.FindAnyObjectByType<GameManager>();
                if (gameManager != null) break;
                yield return new WaitForSecondsRealtime(0.016f);
                if (i % 60 == 0)
                    Debug.LogWarning($"[RuntimeAutomation] Waiting for GameManager... ({i}/300)");
            }

            if (gameManager == null)
            {
                Debug.LogError("[RuntimeAutomation] GameManager not found after 5 seconds");
                Application.Quit(1);
                yield break;
            }

            // Wait for song to start (up to ~10 seconds)
            for (int i = 0; i < 600; i++)
            {
                if (gameManager.IsSongStarted) break;
                yield return new WaitForSecondsRealtime(0.016f);
                if (i % 120 == 0)
                    Debug.LogWarning($"[RuntimeAutomation] Waiting for song to start... ({i}/600)");
            }

            if (!gameManager.IsSongStarted)
            {
                Debug.LogError("[RuntimeAutomation] Song did not start after 10 seconds");
                Application.Quit(1);
                yield break;
            }

            Debug.Log($"[RuntimeAutomation] Song started, waiting for notes...");

            // Wait for notes to appear (skip song intro)
            for (int i = 0; i < 2000; i++)
            {
                var tp = UnityEngine.Object.FindObjectsByType<YARG.Gameplay.Player.TrackPlayer>(
                    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
                bool hasNotes = false;
                foreach (var t in tp)
                {
                    if (t.NoteTracker != null && t.NoteTracker.ActiveCount > 0)
                    {
                        hasNotes = true;
                        break;
                    }
                }
                if (hasNotes) break;
                yield return new WaitForSecondsRealtime(0.016f);
            }

            Debug.Log($"[RuntimeAutomation] Notes appeared, running for {_duration}s");

            // Take initial screenshot
            Directory.CreateDirectory(_screenshotDir);
            TakeScreenshot("start");

            // Run for the specified duration
            float elapsed = 0f;
            float lastScreenshotTime = -10f;

            while (elapsed < _duration)
            {
                yield return null; // Wait one frame
                elapsed += Time.deltaTime;

                if (elapsed - lastScreenshotTime >= 2f)
                {
                    lastScreenshotTime = elapsed;
                    TakeScreenshot($"t{elapsed:F1}");
                }
            }

            // Final screenshot and exit
            TakeScreenshot("end");
            Debug.Log($"[RuntimeAutomation] Complete. Elapsed: {elapsed:F1}s");

            // Wait one frame before quitting to let Unity finish rendering
            yield return null;
            Application.Quit(0);
        }

        private IEnumerator WaitForSceneLoaded(SceneIndex scene)
        {
            var loaded = false;
            UnityAction<Scene, LoadSceneMode> handler = (s, m) =>
            {
                if (s.buildIndex == (int)scene)
                    loaded = true;
            };
            SceneManager.sceneLoaded += handler;

            while (!loaded)
                yield return null;

            SceneManager.sceneLoaded -= handler;
        }

        private void TakeScreenshot(string suffix)
        {
            try
            {
                string filename = Path.Combine(_screenshotDir, $"automation_{suffix}_{Time.realtimeSinceStartup:F1}.png");
                ScreenCapture.CaptureScreenshot(filename, 2);
                Debug.Log($"[RuntimeAutomation] Screenshot: {filename}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[RuntimeAutomation] Screenshot failed: {e.Message}");
            }
        }
    }
#endif
}
