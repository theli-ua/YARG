using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.HUD
{
    public class TrackViewManager : GameplayBehaviour
    {
        [Header("Prefabs")]
        [SerializeField]
        private GameObject _trackViewPrefab;
        [SerializeField]
        private GameObject _vocalHudPrefab;
        [SerializeField]
        private HighwayCameraRendering _highwayCameraRendering;

        [Header("References")]
        [SerializeField]
        private RectTransform _vocalImage;
        [SerializeField]
        private Transform _vocalHudParent;

        private readonly List<TrackView> _trackViews = new();

        public TrackView CreateTrackView()
        {
            // Create a track view
            var trackView = Instantiate(_trackViewPrefab, transform).GetComponent<TrackView>();
            trackView.Initialize(_highwayCameraRendering);
            _trackViews.Add(trackView);
            return trackView;
        }

        protected override void GameplayAwake()
        {
        }

        public void CreateVocalTrackView(int highwayIndex)
        {
            _vocalImage.gameObject.SetActive(true);
            GameManager.VocalTrack.InitializeCamera(_vocalImage);
            _highwayCameraRendering.AddVocalTrack(GameManager.VocalTrack, highwayIndex);
        }

        public VocalsPlayerHUD CreateVocalsPlayerHUD()
        {
            var go = Instantiate(_vocalHudPrefab, _vocalHudParent);
            return go.GetComponent<VocalsPlayerHUD>();
        }

        public void AddTrackPlayer(TrackPlayer trackPlayer)
        {
            _highwayCameraRendering.AddTrackPlayer(trackPlayer);
        }

        /// <summary>
        /// Begin BRG upload frame (reset batch activeCount) before any NoteTracker.UploadToGPU.
        /// Always call even when no notes are active — prevents ghost draws from last frame.
        /// </summary>
        public void BeginHighwayInstanceUploads()
        {
            _highwayCameraRendering?.BeginInstancedUploads();
        }

        /// <summary>
        /// After Collect dirtiness: decide skip vs re-append staging.
        /// </summary>
        public void FinalizeHighwayInstanceUploadPlan()
        {
            _highwayCameraRendering?.FinalizeInstancedUploadPlan();
        }

        /// <summary>
        /// Flush BRG instance uploads after all TrackPlayer.GameplayUpdate calls.
        /// Called from GameManager so commit does not depend on HighwayCameraRendering.LateUpdate order.
        /// </summary>
        public void FlushHighwayInstanceUploads()
        {
            _highwayCameraRendering?.FlushInstancedUploads();
        }

        protected override void GameplayDestroy()
        {
        }
    }
}
