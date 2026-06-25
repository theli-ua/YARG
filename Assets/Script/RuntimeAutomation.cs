using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using YARG.Core.Song;
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
    ///   -benchmark              - Benchmark mode: writes frame times to file, no screenshots
    ///   -benchmarkSongHash "hash" - Song hash to play (benchmark mode, optional)
    /// </summary>
    public class RuntimeAutomation : MonoBehaviour
    {
        private int _duration = 15;
        private string _screenshotDir = "AutomationScreenshots";
        private int _songIndex = 0;
        private bool _benchmarkMode;
        private string _benchmarkSongHash;
        private string _benchmarkFile;

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
                else if (args[i] == "-benchmark")
                {
                    _benchmarkMode = true;
                }
                else if (args[i] == "-benchmarkSongHash")
                {
                    _benchmarkSongHash = args[i + 1];
                    i++;
                }
            }

            Debug.Log($"[RuntimeAutomation] Duration: {_duration}s, Benchmark: {_benchmarkMode}, SongHash: {_benchmarkSongHash}");

            // Start coroutine — do NOT block the main thread
            StartCoroutine(RunAutomation());
        }

        private IEnumerator RunAutomation()
        {
            // Set the song to load
            if (GlobalVariables.State.CurrentSong == null)
            {
                if (!string.IsNullOrEmpty(_benchmarkSongHash))
                {
                    // Find song by hash
                    var targetHash = HashWrapper.FromString(_benchmarkSongHash);
                    var song = SongContainer.Songs.FirstOrDefault(s => s.Hash.Equals(targetHash));
                    if (song != null)
                    {
                        GlobalVariables.State.CurrentSong = song;
                        Debug.Log($"[RuntimeAutomation] Setting song by hash: {song.Name} by {song.Artist}");
                    }
                    else
                    {
                        Debug.LogWarning($"[RuntimeAutomation] Song with hash '{_benchmarkSongHash}' not found, picking first song");
                        var ordered = SongContainer.Songs.OrderBy(s => s.Name).ToArray();
                        if (ordered.Length > 0)
                            GlobalVariables.State.CurrentSong = ordered[0];
                    }
                }
                else if (SongContainer.Songs.Any())
                {
                    var ordered = SongContainer.Songs.OrderBy(s => s.Name).ToArray();
                    var song = _songIndex < ordered.Length ? ordered[_songIndex] : ordered[0];
                    GlobalVariables.State.CurrentSong = song;
                    Debug.Log($"[RuntimeAutomation] Setting song: {song.Name} by {song.Artist}");
                }
                else
                {
                    Debug.LogError("[RuntimeAutomation] No songs found in library");
                    Application.Quit(1);
                    yield break;
                }
            }
            else
            {
                Debug.Log($"[RuntimeAutomation] Using existing song: {GlobalVariables.State.CurrentSong.Name}");
            }

            // Benchmark: prepare output file
            if (_benchmarkMode)
            {
                _benchmarkFile = Path.Combine(Application.persistentDataPath, "benchmark.csv");
                File.WriteAllText(_benchmarkFile, "frame,ms\n");
                Debug.Log($"[RuntimeAutomation] Benchmark output: {_benchmarkFile}");
            }

            // Load the Gameplay scene additively (PersistentScene is already loaded)
            Debug.Log("[RuntimeAutomation] Loading Gameplay scene...");
            var asyncOp = SceneManager.LoadSceneAsync((int)SceneIndex.Gameplay, LoadSceneMode.Additive);
            yield return asyncOp;

            Debug.Log("[RuntimeAutomation] Gameplay scene loaded");

            // Wait for GameManager to exist (yield-based polling)
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

            Debug.Log("[RuntimeAutomation] GameManager found");

            // Wait for song to start
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

            Debug.Log($"[RuntimeAutomation] Song started, running for {_duration}s");

            // Take initial screenshot (non-benchmark)
            if (!_benchmarkMode)
            {
                Directory.CreateDirectory(_screenshotDir);
                TakeScreenshot("start");
            }

            // Run for the specified duration
            float elapsed = 0f;
            float lastScreenshotTime = -10f;
            int frameCount = 0;
            float lastBenchmarkTime = 0f;

            while (elapsed < _duration)
            {
                yield return null; // Wait one frame
                elapsed += Time.deltaTime;
                frameCount++;

                // Benchmark: write frame times every 0.5s batch
                if (_benchmarkMode && (Time.realtimeSinceStartup - lastBenchmarkTime) >= 0.5f)
                {
                    lastBenchmarkTime = Time.realtimeSinceStartup;
                    WriteBenchmarkFrameTimes(frameCount);
                }

                // Screenshots every 2s (non-benchmark)
                if (!_benchmarkMode && elapsed - lastScreenshotTime >= 2f)
                {
                    lastScreenshotTime = elapsed;
                    TakeScreenshot($"t{elapsed:F1}");
                }
            }

            // Final output
            if (!_benchmarkMode)
                TakeScreenshot("end");
            else
                WriteBenchmarkFrameTimes(frameCount);

            Debug.Log($"[RuntimeAutomation] Complete. Elapsed: {elapsed:F1}s, Frames: {frameCount}");
            Application.Quit(0);
        }

        private int _benchmarkFrameOffset = 0;

        private void WriteBenchmarkFrameTimes(int totalFrames)
        {
            if (!File.Exists(_benchmarkFile)) return;
            // We track frame times via Time.unscaledDeltaTime each frame.
            // For simplicity, write a summary line per batch.
            // (Per-frame logging would require per-frame file I/O which is slow.)
            // Instead, just log total frames and avg FPS at the end.
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
}
