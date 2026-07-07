using System.Runtime.CompilerServices;
using UnityEngine;
using YARG.Gameplay.Player;

namespace YARG.Gameplay.Visuals
{
    public abstract class TrackElement<TPlayer> : BaseElement
        where TPlayer : TrackPlayer
    {
        protected const float REMOVE_POINT = -4f;

        protected TPlayer Player { get; private set; }

        protected override void GameplayAwake()
        {
            Player = GetComponentInParent<TPlayer>();

            base.GameplayAwake();
        }

        protected float GetZPositionAtTime(double time)
        {
            // Calibration is not taken into consideration here, as that is instead handled in more
            // critical areas such as the game manager and players

            return TrackPlayer.STRIKE_LINE_POS                          // Shift origin to the strike line
                + (float) (time - GameManager.VisualTime) // Get time of note relative to now
                * Player.NoteSpeed;                                  // Adjust speed (units/s)
        }

        protected override bool UpdateElementPosition()
        {
            // Calibration is not taken into consideration here, as that is instead handled in more
            // critical areas such as the game manager and players
            float z =
                TrackPlayer.STRIKE_LINE_POS                      // Shift origin to the strike line
                + (float) (ElementTime - GameManager.VisualTime) // Get time of note relative to now
                * Player.NoteSpeed;                              // Adjust speed (units/s)

            var cacheTransform = transform;
            cacheTransform.localPosition = cacheTransform.localPosition.WithZ(z);

            if (z < REMOVE_POINT - RemovePointOffset)
            {
                ParentPool.Return(this);
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static float GetElementX(float index, int subdivisions)
        {
            return TrackPlayer.TRACK_WIDTH / subdivisions * (index + 1) - TrackPlayer.TRACK_WIDTH / 2f - 1f / subdivisions;
        }
    }
}
