using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Reusable six-position "walk around the vehicle and photograph it" task.
    /// Photo spots are calculated once (relative to the target vehicle's own
    /// position/heading) when the task starts, not recalculated every frame.
    /// Only one visible marker exists at a time - the one for the current spot.
    /// </summary>
    public class VehiclePhotoTask
    {
        private const float TriggerRadius = 1.5f;

        // GTA native DRAW_MARKER type IDs. The numbered markers (11-16) render the
        // digit 1-6 in 3D so the player can tell at a glance which photo is next;
        // vertical cylinder (1) is the fallback if a numbered marker fails to draw.
        private const int MarkerTypeNumber1 = 11;
        private const int MarkerTypeNumber2 = 12;
        private const int MarkerTypeNumber3 = 13;
        private const int MarkerTypeNumber4 = 14;
        private const int MarkerTypeNumber5 = 15;
        private const int MarkerTypeNumber6 = 16;
        private const int MarkerTypeVerticalCylinder = 1;

        private static readonly int[] MarkerTypeNumbers =
        {
            MarkerTypeNumber1, MarkerTypeNumber2, MarkerTypeNumber3,
            MarkerTypeNumber4, MarkerTypeNumber5, MarkerTypeNumber6,
        };

        // Bright blue, large enough to read from the opposite side of the vehicle.
        private static readonly Vector3 NumberedMarkerScale = new Vector3(2.0f, 2.0f, 2.0f);
        private static readonly Vector3 CylinderMarkerScale = new Vector3(2.25f, 2.25f, 1.5f);
        private const int MarkerColorR = 30;
        private const int MarkerColorG = 144;
        private const int MarkerColorB = 255;
        private const int MarkerAlpha = 230;

        private static readonly Vector3[] LocalPhotoOffsets =
        {
            new Vector3(0f, 7.5f, 0f),      // 1. Front
            new Vector3(5.5f, 5.5f, 0f),    // 2. Front-right
            new Vector3(5.5f, -5.5f, 0f),   // 3. Rear-right
            new Vector3(0f, -7.5f, 0f),     // 4. Rear
            new Vector3(-5.5f, -5.5f, 0f),  // 5. Rear-left
            new Vector3(-5.5f, 5.5f, 0f),   // 6. Front-left
        };

        private readonly Vehicle _vehicle;
        private readonly string _label;
        private readonly Keys _interactionKey;

        private Vector3[] _photoSpots;
        private int _photoIndex;
        private int _lastRenderedIndex;
        private bool _isActive;
        private bool _keyWasDown;
        private bool _isPlayerInMarkerRange;

        public bool IsComplete { get; private set; }

        public int TotalPhotos => _photoSpots?.Length ?? 0;

        public int CompletedPhotos => _photoIndex;

        /// <summary>The six calculated world-space photo positions, in order.</summary>
        public IReadOnlyList<Vector3> PhotoSpots => _photoSpots;

        /// <summary>True only while the player is standing inside the currently active marker.</summary>
        public bool IsPlayerInActiveMarkerRange => _isPlayerInMarkerRange;

        // interactionKey is a constructor parameter (not hard-coded internally) so a
        // future INI/controller-binding layer can simply pass a different Keys value.
        public VehiclePhotoTask(Vehicle vehicle, string label = "Vehicle", Keys interactionKey = Keys.T)
        {
            _vehicle = vehicle;
            _label = label;
            _interactionKey = interactionKey;
        }

        public void Start()
        {
            _photoIndex = 0;
            _lastRenderedIndex = -1;
            IsComplete = false;
            _isActive = true;
            _keyWasDown = false;
            _isPlayerInMarkerRange = false;

            CalculatePhotoSpots();

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}] started");
        }

        public void Stop()
        {
            _isActive = false;
            _isPlayerInMarkerRange = false;
        }

        /// <summary>Call once per tick while the task is active.</summary>
        public void Process()
        {
            if (!_isActive || IsComplete)
            {
                _isPlayerInMarkerRange = false;
                return;
            }

            if (_vehicle == null || !_vehicle.Exists())
            {
                Stop();
                return;
            }

            RenderCurrentPhotoMarker();
            CheckForPhotoTrigger();

            if (!_isPlayerInMarkerRange)
            {
                return;
            }

            Game.DisplayHelp("Press " + _interactionKey + " to take photo");

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            // keyJustPressed is only ever honored here, inside the in-range guard, so a
            // T press can never register as a photo unless the player is standing in
            // the active marker.
            if (keyJustPressed)
            {
                TakePhoto();
            }
        }

        /// <summary>
        /// Calculates and stores the six world-space photo positions relative to the
        /// vehicle's current position/heading. Called once, from Start() - never
        /// recalculated per frame.
        /// </summary>
        private void CalculatePhotoSpots()
        {
            _photoSpots = new Vector3[LocalPhotoOffsets.Length];

            for (int i = 0; i < LocalPhotoOffsets.Length; i++)
            {
                Vector3 position = _vehicle.GetOffsetPosition(LocalPhotoOffsets[i]);

                // The vehicle's own Z can sit above true ground level, which would make
                // the marker float instead of reading as a normal ground checkpoint.
                float? groundZ = World.GetGroundZ(position, false, true);
                if (groundZ.HasValue)
                {
                    position.Z = groundZ.Value;
                }

                _photoSpots[i] = position;

                Game.LogTrivial(
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo {i + 1} position = " +
                    $"{position.X:F2}/{position.Y:F2}/{position.Z:F2}");
            }

            Game.LogTrivial(
                $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Calculated {_photoSpots.Length} photo spots using direct offsets.");
        }

        /// <summary>Draws only the marker for the current photo spot. Never more than one at a time.</summary>
        private void RenderCurrentPhotoMarker()
        {
            Vector3 position = _photoSpots[_photoIndex];

            if (_lastRenderedIndex != _photoIndex)
            {
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Rendering marker {_photoIndex + 1}");
                _lastRenderedIndex = _photoIndex;
            }

            int numberedMarkerType = MarkerTypeNumbers[_photoIndex];

            if (!TryDrawMarker(numberedMarkerType, position, NumberedMarkerScale))
            {
                Game.LogTrivial(
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Numbered marker type {numberedMarkerType} " +
                    $"failed to render for marker {_photoIndex + 1}; falling back to vertical cylinder");

                TryDrawMarker(MarkerTypeVerticalCylinder, position, CylinderMarkerScale);
            }
        }

        private bool TryDrawMarker(int nativeMarkerType, Vector3 position, Vector3 scale)
        {
            try
            {
                NativeFunction.Natives.DRAW_MARKER(
                    nativeMarkerType,
                    position.X, position.Y, position.Z,
                    0f, 0f, 0f,
                    0f, 0f, 0f,
                    scale.X, scale.Y, scale.Z,
                    MarkerColorR, MarkerColorG, MarkerColorB, MarkerAlpha,
                    true,  // bobUpAndDown
                    false, // faceCamera
                    2,     // p19
                    true,  // rotate
                    "", "", false);
                return true;
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: DRAW_MARKER threw for type {nativeMarkerType}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Only checks the player's distance from the stored current photo spot.</summary>
        private void CheckForPhotoTrigger()
        {
            Vector3 currentSpot = _photoSpots[_photoIndex];
            Ped player = Game.LocalPlayer.Character;
            float distance = player.Position.DistanceTo(currentSpot);
            bool inRange = distance <= TriggerRadius;

            if (inRange && !_isPlayerInMarkerRange)
            {
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Player entered photo trigger {_photoIndex + 1}");
            }

            _isPlayerInMarkerRange = inRange;
        }

        private void TakePhoto()
        {
            int photoNumber = _photoIndex + 1;

            _photoIndex++;
            _isPlayerInMarkerRange = false;
            _lastRenderedIndex = -1; // so the next spot's marker logs its own "Rendering marker" line

            PlayPhotoFeedback();

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo {photoNumber} taken");

            if (_photoIndex >= _photoSpots.Length)
            {
                IsComplete = true;
                _isActive = false;

                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}] completed");
            }
        }

        private static void PlayPhotoFeedback()
        {
            Game.DisplayNotification("~b~Photo captured.");

            try
            {
                NativeFunction.Natives.PLAY_SOUND_FRONTEND(-1, "Camera_Shutter", "Phone_Soundset_Michael", false);
            }
            catch (Exception)
            {
                try
                {
                    NativeFunction.Natives.PLAY_SOUND_FRONTEND(-1, "SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET", false);
                }
                catch (Exception)
                {
                    // Audio feedback is a nice-to-have; never let it interrupt the photo task.
                }
            }
        }
    }
}
