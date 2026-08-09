using System;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Reusable six-position "walk around the vehicle and photograph it" task.
    /// Not tied to any specific callout - positions are computed relative to the
    /// target vehicle's own position/heading so it works regardless of orientation.
    /// Only one ground marker exists at a time; it stays up until its photo is taken.
    /// </summary>
    public class VehiclePhotoTask
    {
        private const float TriggerRadius = 1.25f;
        private const float SideOffset = 3.25f;
        private const float FrontBackOffset = 4.25f;
        private const float DiagonalForward = FrontBackOffset * 0.55f;

        private readonly Vehicle _vehicle;
        private readonly string _label;
        private readonly Keys _interactionKey;
        private readonly Vector3[] _localPhotoOffsets;

        private int _photoIndex;
        private bool _isActive;
        private bool _keyWasDown;
        private bool _isPlayerInMarkerRange;

        public bool IsComplete { get; private set; }

        public int TotalPhotos => _localPhotoOffsets.Length;

        public int CompletedPhotos => _photoIndex;

        /// <summary>True only while the player is standing inside the currently active marker.</summary>
        public bool IsPlayerInActiveMarkerRange => _isPlayerInMarkerRange;

        // interactionKey is a constructor parameter (not hard-coded internally) so a
        // future INI/controller-binding layer can simply pass a different Keys value.
        public VehiclePhotoTask(Vehicle vehicle, string label = "Vehicle", Keys interactionKey = Keys.T)
        {
            _vehicle = vehicle;
            _label = label;
            _interactionKey = interactionKey;

            _localPhotoOffsets = new[]
            {
                new Vector3(0f, FrontBackOffset, 0f),           // 1. Front
                new Vector3(SideOffset, DiagonalForward, 0f),   // 2. Front-right
                new Vector3(SideOffset, -DiagonalForward, 0f),  // 3. Rear-right
                new Vector3(0f, -FrontBackOffset, 0f),          // 4. Rear
                new Vector3(-SideOffset, -DiagonalForward, 0f), // 5. Rear-left
                new Vector3(-SideOffset, DiagonalForward, 0f),  // 6. Front-left
            };
        }

        public void Start()
        {
            _photoIndex = 0;
            IsComplete = false;
            _isActive = true;
            _keyWasDown = false;
            _isPlayerInMarkerRange = false;

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

            Vector3 markerPosition = GetWorldPositionFor(_photoIndex);
            DrawGroundMarker(markerPosition);

            Ped player = Game.LocalPlayer.Character;
            float distance = player.Position.DistanceTo(markerPosition);
            bool inRange = distance <= TriggerRadius;
            _isPlayerInMarkerRange = inRange;

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (!inRange)
            {
                return;
            }

            Game.DisplayHelp("Press " + _interactionKey + " to take photo");

            // keyJustPressed is only ever honored here, inside the inRange guard, so a
            // T press can never register as a photo unless the player is standing in
            // the active marker.
            if (keyJustPressed)
            {
                CompleteCurrentPhoto();
            }
        }

        private void CompleteCurrentPhoto()
        {
            _photoIndex++;
            _isPlayerInMarkerRange = false;

            PlayPhotoFeedback();

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}] photo {_photoIndex}/{TotalPhotos} captured");

            if (_photoIndex >= _localPhotoOffsets.Length)
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
                NativeFunction.Natives.PLAY_SOUND_FRONTEND(-1, "SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET", false);
            }
            catch (Exception)
            {
                // Audio feedback is a nice-to-have; never let it interrupt the photo task.
            }
        }

        private Vector3 GetWorldPositionFor(int index)
        {
            Vector3 position = _vehicle.GetOffsetPosition(_localPhotoOffsets[index]);

            // The vehicle's own Z can sit above true ground level, which would make the
            // marker float instead of reading as a normal ground checkpoint.
            float? groundZ = World.GetGroundZ(position, false, true);
            if (groundZ.HasValue)
            {
                position.Z = groundZ.Value;
            }

            return position;
        }

        private static void DrawGroundMarker(Vector3 position)
        {
            try
            {
                NativeFunction.Natives.DRAW_MARKER(
                    1, // vertical cylinder - standard GTA checkpoint marker shape
                    position.X, position.Y, position.Z,
                    0f, 0f, 0f,
                    0f, 0f, 0f,
                    1.5f, 1.5f, 1.0f,
                    30, 144, 255, 200,
                    true,  // bobUpAndDown
                    false, // faceCamera
                    2,     // p19
                    true,  // rotate
                    "", "", false);
            }
            catch (Exception)
            {
                // Marker rendering is cosmetic; never let it interrupt the photo task.
            }
        }
    }
}
