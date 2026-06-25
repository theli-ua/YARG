using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using YARG.Gameplay;
using YARG.Song;

namespace YARG
{
    /// <summary>
    /// Runtime automation for the built player.
    /// Triggered via command-line arguments:
    ///   -automationDuration N   - Seconds to run (default: 15)
    ///   -automationScreenshotDir "path" - Directory for screenshots
    ///   -automationSongIndex N  - Song index to play (default: 0 = first song)
    /// </summary>
    public class RuntimeAutomation : MonoBehaviour
    {
        private int _duration = 15;
        private string _screenshotDir = "AutomationScreenshots";
        private int _songIndex = 0;
        private float _elapsedTime;
        private float _lastScreenshotTime = -10f;
        private bool _completed;
        private bool _songStarted;

        private void Awake()
        {
            // Parse command-line arguments
            var args = Environment.GetCommandLineArgs();
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
            }

            Debug.Log("[RuntimeAutomation] Duration: " + _duration + "s, Screenshot dir: " + _screenshotDir + ", Song index: " + _songIndex);
            Directory.CreateDirectory(_screenshotDir);

            // Run automation synchronously
            RunAutomationSync();
        }

        private void RunAutomationSync()
        {
            // Set the song to load
            if (GlobalVariables.State.CurrentSong == null && SongContainer.Songs.Any())
            {
                var song = SongContainer.Songs.OrderBy(s => s.Name).Skip(_songIndex).FirstOrDefault();
                if (song != null)
                {
                    GlobalVariables.State.CurrentSong = song;
                    Debug.Log("[RuntimeAutomation] Setting song: " + song.Name + " by " + song.Artist);
                }
                else
                {
                    Debug.LogError("[RuntimeAutomation] No songs found in library");
                    Application.Quit(1);
                    return;
                }
            }
            else if (GlobalVariables.State.CurrentSong != null)
            {
                Debug.Log("[RuntimeAutomation] Using existing song: " + GlobalVariables.State.CurrentSong.Name);
            }

            // Load the Gameplay scene additively (PersistentScene is already loaded)
            Debug.Log("[RuntimeAutomation] Loading Gameplay scene...");
            SceneManager.LoadScene((int)SceneIndex.Gameplay, LoadSceneMode.Additive);

            // Wait for GameManager to exist (polling)
            var gameManager = UnityEngine.Object.FindAnyObjectByType<GameManager>();
            int waitCount = 0;
            while (gameManager == null && waitCount < 300)
            {
                waitCount++;
                System.Threading.Thread.Sleep(16);
                if (waitCount % 30 == 0)
                {
                    Debug.LogWarning("[RuntimeAutomation] Waiting for GameManager... (" + waitCount + "/300)");
                }
            }

            if (gameManager == null)
            {
                Debug.LogError("[RuntimeAutomation] GameManager not found after 5 seconds");
                Application.Quit(1);
                return;
            }

            Debug.Log("[RuntimeAutomation] GameManager found");

            // Wait for song to start
            waitCount = 0;
            while (!gameManager.IsSongStarted && waitCount < 600)
            {
                waitCount++;
                System.Threading.Thread.Sleep(16);
                if (waitCount % 60 == 0)
                {
                    Debug.LogWarning("[RuntimeAutomation] Waiting for song to start... (" + waitCount + "/600)");
                }
            }

            if (!gameManager.IsSongStarted)
            {
                Debug.LogError("[RuntimeAutomation] Song did not start after 10 seconds");
                Application.Quit(1);
                return;
            }

            _songStarted = true;
            _elapsedTime = 0f;
            Debug.Log("[RuntimeAutomation] Song started, running for " + _duration + "s");
            TakeScreenshot("start");

            // Run for the specified duration
            while (_elapsedTime < _duration)
            {
                System.Threading.Thread.Sleep(16);
                _elapsedTime += 0.016f; // Approximate

                // Take screenshots periodically
                if (_elapsedTime - _lastScreenshotTime >= 2f)
                {
                    _lastScreenshotTime = _elapsedTime;
                    TakeScreenshot("t" + _elapsedTime.ToString("F1"));
                }
            }

            TakeScreenshot("end");
            _completed = true;
            Debug.Log("[RuntimeAutomation] Automation complete. Elapsed: " + _elapsedTime.ToString("F1") + "s");
            
            // Exit
            Debug.Log("[RuntimeAutomation] Exiting");
            Application.Quit(0);
        }

        private void TakeScreenshot(string suffix)
        {
            try
            {
                string filename = Path.Combine(_screenshotDir, "automation_" + suffix + "_" + _elapsedTime.ToString("F1") + ".png");
                ScreenCapture.CaptureScreenshot(filename, 2);
                Debug.Log("[RuntimeAutomation] Screenshot: " + filename);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuntimeAutomation] Screenshot failed: " + e.Message);
            }
        }
    }
}
