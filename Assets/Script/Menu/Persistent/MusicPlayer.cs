using System.Diagnostics.CodeAnalysis;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Settings;
using YARG.Helpers.Extensions;
using YARG.Core.Audio;
using YARG.Core.Song;
using YARG.Song;
using System.Threading.Tasks;

namespace YARG.Menu.Persistent
{
    public class MusicPlayer : MonoBehaviour
    {
        private static SongEntry _nowPlaying = null;
        public static SongEntry NowPlaying => _nowPlaying;

        private object _lock = new();
        private StemMixer _mixer = null;

        [SerializeField]
        private Image _playPauseButton;
        [SerializeField]
        private TextMeshProUGUI _songText;
        [SerializeField]
        private TextMeshProUGUI _artistText;

        [Space]
        [SerializeField]
        private Sprite _playSprite;
        [SerializeField]
        private Sprite _pauseSprite;

        private async void OnEnable()
        {
            _songText.text = _artistText.text = string.Empty;

            // Wait until the loading is done
            await UniTask.WaitUntil(() => !LoadingScreen.IsActive);

            // Disable if there are no songs to play
            if (SongContainer.Count <= 0)
            {
                gameObject.SetActive(false);
                return;
            }
            StemSettings.ApplySettings = SettingsManager.Settings.ApplyVolumesInMusicPlayer.Value;
            NextSong();
        }

        private void OnDisable()
        {
            StopPlayback();
            StemSettings.ApplySettings = SettingsManager.Settings.ApplyVolumesInMusicLibrary.Value; // reset to default value
        }

        /// <summary>
        /// Stop menu preview audio and cancel any in-flight LoadAudio handoff.
        /// Safe to call when already stopped. Automation uses this before Gameplay load.
        /// </summary>
        public void StopPlayback()
        {
            lock (_lock)
            {
                // Invalidate in-flight NextSong tasks so they dispose and exit (do not retry).
                _current = null;

                if (_mixer != null)
                {
                    // Detach SongEnd first — Dispose can fire end and would re-enter NextSong.
                    _mixer.SongEnd -= OnMixerSongEnd;
                    _mixer.Dispose();
                    _mixer = null;
                }
            }
        }

        private void OnMixerSongEnd()
        {
            lock (_lock)
            {
                if (_mixer != null)
                {
                    _mixer.SongEnd -= OnMixerSongEnd;
                    _mixer.Dispose();
                    _mixer = null;
                }
            }

            // Do not chain while disabled (or while object is being torn down).
            if (!isActiveAndEnabled)
                return;

            NextSong();
        }

        private static Task<StemMixer> _current;

        public async void NextSong()
        {
            // Never start or retry loads while inactive — previous code `continue`d the
            // try-loop on disable and hammered CreateMixer/Dispose (heap corruption risk).
            if (!isActiveAndEnabled)
                return;

            const int MAX_TRIES = 20;
            for (int tries = 0; tries < MAX_TRIES; tries++)
            {
                if (!isActiveAndEnabled)
                    return;

                var entry = SongContainer.GetRandomSong();
                if (entry == _nowPlaying)
                {
                    continue;
                }
                _nowPlaying = entry;

                Task<StemMixer> task;
                lock (_lock)
                {
                    if (!isActiveAndEnabled)
                        return;

                    const float SPEED = 1f;
                    _current = task = Task.Run(() => entry.LoadAudio(SPEED, SettingsManager.Settings.MusicPlayerVolume.Value, SongStem.Crowd));
                }

                var mixer = await task;
                if (mixer == null)
                {
                    continue;
                }

                lock (_lock)
                {
                    // Superseded, cancelled (StopPlayback), or disabled → drop this mixer and stop.
                    if (_current != task || !isActiveAndEnabled)
                    {
                        mixer.Dispose();
                        return;
                    }

                    if (_mixer != null)
                    {
                        _mixer.SongEnd -= OnMixerSongEnd;
                        _mixer.Dispose();
                    }

                    _mixer = mixer;
                    _mixer.SongEnd += OnMixerSongEnd;
                    _mixer.Play();

                    _songText.text = _nowPlaying.Name;
                    _artistText.text = _nowPlaying.Artist;
                    _playPauseButton.sprite = _pauseSprite;
                }
                return;
            }
            _nowPlaying = null;
        }

        public void UpdateVolume(double volume)
        {
            lock (_lock)
            {
                _mixer?.SetVolume(volume);
            }
        }

        public void TogglePlay()
        {
            lock (_lock)
            {
                if (_mixer == null)
                {
                    return;
                }

                if (!_mixer.IsPaused)
                {
                    _mixer.Pause();
                    _playPauseButton.sprite = _playSprite;
                }
                else
                {
                    _mixer.Play();
                    _playPauseButton.sprite = _pauseSprite;
                }
            }
        }
    }
}