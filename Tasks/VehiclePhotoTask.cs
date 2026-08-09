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
    /// A dedicated GameFiber redraws the single active marker and checks for
    /// the photo trigger every frame while the task is active.
    /// </summary>
    public class VehiclePhotoTask
    {
        private const float TriggerRadius = 1.5f;

        // GTA native DRAW_MARKER type IDs: 1 = vertical cylinder (shows WHERE to
        // stand), 11-16 = numbered markers 1-6 (shows WHICH photo is next). Both
        // are drawn at the same location for the single active photo spot.
        private const int MarkerTypeVerticalCylinder = 1;

        private static readonly int[] MarkerTypeNumbers = { 11, 12, 13, 14, 15, 16 };

        private static readonly Vector3 CylinderMarkerScale = new Vector3(1.5f, 1.5f, 0.35f);
        private static readonly Vector3 NumberMarkerScale = new Vector3(1.0f, 1.0f, 1.0f);
        private const float NumberMarkerHeightOffset = 0.4f;

        private const int MarkerColorR = 0;
        private const int MarkerColorG = 120;
        private const int MarkerColorB = 255;
        private const int MarkerAlpha = 180;

        // Throttle for the "Rendering photo marker X" log line - DRAW_MARKER itself
        // still runs every frame, only the log call is rate-limited.
        private const int MarkerLogIntervalMs = 3000;

        // Offsets are relative to the vehicle's own position/heading via
        // GetOffsetPosition, then snapped to ground level (see CalculatePhotoSpots).
        private static readonly Vector3[] LocalPhotoOffsets =
        {
            new Vector3(0f, 8.0f, 0f),     // 1. Front
            new Vector3(6.0f, 6.0f, 0f),   // 2. Front-right
            new Vector3(6.0f, -6.0f, 0f),  // 3. Rear-right
            new Vector3(0f, -8.0f, 0f),    // 4. Rear
            new Vector3(-6.0f, -6.0f, 0f), // 5. Rear-left
            new Vector3(-6.0f, 6.0f, 0f),  // 6. Front-left
        };

        // Known-working paparazzi camera prop/animation combo.
        private const string CameraPropModelName = "prop_pap_camera_01";
        private const string CameraAnimationDictionary = "amb@world_human_paparazzi@male@base";
        private const string CameraAnimationName = "base";
        private const int CameraHandBoneId = 28422; // resolved per-ped via GET_PED_BONE_INDEX
        private const int CameraAnimationHoldMs = 1200;

        private readonly Vehicle _vehicle;
        private readonly string _label;
        private readonly Keys _interactionKey;

        private Vector3[] _photoSpots;
        private int _photoIndex;
        private int _lastMarkerLogTickCount;
        private bool _isActive;
        private bool _keyWasDown;
        private bool _isPlayerInMarkerRange;
        private bool _isTakingPhoto;

        private GameFiber _fiber;
        private Rage.Object _cameraProp;

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
            _lastMarkerLogTickCount = int.MinValue;
            IsComplete = false;
            _isActive = true;
            _keyWasDown = false;
            _isPlayerInMarkerRange = false;
            _isTakingPhoto = false;

            CalculatePhotoSpots();

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}] started");

            _fiber = GameFiber.StartNew(RunLoop, $"VehiclePhotoTask:{_label}");
        }

        /// <summary>
        /// Stops the task's GameFiber and guarantees the camera prop is removed,
        /// even if this is called mid-animation (e.g. the callout ending early).
        /// </summary>
        public void Stop()
        {
            _isActive = false;
            _isPlayerInMarkerRange = false;

            if (_fiber != null && _fiber.IsAlive)
            {
                _fiber.Abort();
            }

            _fiber = null;

            CleanupCameraProp();
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
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo {i + 1} = " +
                    $"{position.X:F2} {position.Y:F2} {position.Z:F2}");
            }
        }

        /// <summary>
        /// Runs on its own GameFiber for the lifetime of the task: redraws the single
        /// active marker and checks the photo trigger every frame via GameFiber.Yield().
        /// </summary>
        private void RunLoop()
        {
            while (_isActive && !IsComplete)
            {
                if (_vehicle == null || !_vehicle.Exists())
                {
                    _isActive = false;
                    _isPlayerInMarkerRange = false;
                    break;
                }

                RenderCurrentPhotoPoint();
                CheckForPhotoInteraction();

                GameFiber.Yield();
            }
        }

        /// <summary>
        /// Draws the two visuals for the current photo spot: a vertical cylinder marking
        /// where to stand, and a numbered marker just above it showing which photo is next.
        /// Never more than one photo spot's markers are drawn at a time.
        /// </summary>
        private void RenderCurrentPhotoPoint()
        {
            Vector3 position = _photoSpots[_photoIndex];

            int now = Environment.TickCount;
            if (now - _lastMarkerLogTickCount >= MarkerLogIntervalMs)
            {
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Rendering photo marker {_photoIndex + 1}");
                _lastMarkerLogTickCount = now;
            }

            DrawMarker(MarkerTypeVerticalCylinder, position, CylinderMarkerScale);

            Vector3 numberPosition = position;
            numberPosition.Z += NumberMarkerHeightOffset;
            DrawMarker(MarkerTypeNumbers[_photoIndex], numberPosition, NumberMarkerScale);
        }

        private void DrawMarker(int nativeMarkerType, Vector3 position, Vector3 scale)
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
            }
            catch (Exception ex)
            {
                Game.LogTrivial(
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: DRAW_MARKER threw for type {nativeMarkerType}: {ex.Message}");
            }
        }

        /// <summary>Checks the player's distance from the stored current photo spot and handles the T press.</summary>
        private void CheckForPhotoInteraction()
        {
            if (_isTakingPhoto)
            {
                return;
            }

            Vector3 currentSpot = _photoSpots[_photoIndex];
            Ped player = Game.LocalPlayer.Character;
            float distance = player.Position.DistanceTo(currentSpot);
            bool inRange = distance <= TriggerRadius;

            if (inRange && !_isPlayerInMarkerRange)
            {
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Entered photo point {_photoIndex + 1}");
            }

            _isPlayerInMarkerRange = inRange;

            // T must do nothing outside the active trigger radius.
            if (!inRange)
            {
                return;
            }

            Game.DisplayHelp("Press " + _interactionKey + " to take photo");

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (keyJustPressed)
            {
                TakePhoto();
            }
        }

        /// <summary>
        /// Faces the player toward the vehicle, plays the camera prop + animation, holds it
        /// long enough to be visible, then advances to the next photo spot. Runs entirely on
        /// this task's GameFiber, so GameFiber.Sleep here blocks only this task's loop.
        /// </summary>
        private void TakePhoto()
        {
            int photoNumber = _photoIndex + 1;
            _isTakingPhoto = true;
            _isPlayerInMarkerRange = false;

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Taking photo {photoNumber}");

            Ped player = Game.LocalPlayer.Character;

            FacePlayerTowardVehicle(player);

            try
            {
                CreateAndAttachCameraProp(player);
                PlayCameraAnimation(player);

                GameFiber.Sleep(CameraAnimationHoldMs);

                PlayPhotoFeedback();
            }
            finally
            {
                StopCameraAnimation(player);
                CleanupCameraProp();
            }

            _photoIndex++;
            _lastMarkerLogTickCount = int.MinValue; // next spot's marker logs its own "Rendering" line immediately
            _isTakingPhoto = false;

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo {photoNumber} taken");

            if (_photoIndex >= _photoSpots.Length)
            {
                IsComplete = true;
                _isActive = false;

                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Vehicle photo task complete");
            }
        }

        private void FacePlayerTowardVehicle(Ped player)
        {
            Vector3 direction = _vehicle.Position - player.Position;
            if (direction.X == 0f && direction.Y == 0f)
            {
                return;
            }

            // Matches the forward-vector convention used for road heading elsewhere in this
            // mod: forward.X = -sin(heading), forward.Y = cos(heading).
            float headingRadians = (float)Math.Atan2(-direction.X, direction.Y);
            player.Heading = headingRadians * (180f / (float)Math.PI);
        }

        private void CreateAndAttachCameraProp(Ped player)
        {
            _cameraProp = new Rage.Object(CameraPropModelName, player.Position);
            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Camera prop created");

            int boneIndex = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(player, CameraHandBoneId);
            _cameraProp.AttachTo(player, boneIndex, Vector3.Zero, Vector3.Zero);

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Camera prop attached");
        }

        private static void PlayCameraAnimation(Ped player)
        {
            var animationDictionary = new AnimationDictionary(CameraAnimationDictionary);
            if (!animationDictionary.IsLoaded)
            {
                animationDictionary.LoadAndWait();
            }

            player.Tasks.PlayAnimation(CameraAnimationDictionary, CameraAnimationName, 8.0f, AnimationFlags.None);
        }

        private static void StopCameraAnimation(Ped player)
        {
            try
            {
                player.Tasks.ClearImmediately();
            }
            catch (Exception)
            {
                // Best-effort - the camera prop cleanup right after this matters more
                // than a clean animation stop.
            }
        }

        /// <summary>Detaches and deletes the camera prop. Safe to call repeatedly/redundantly.</summary>
        private void CleanupCameraProp()
        {
            if (_cameraProp == null)
            {
                return;
            }

            try
            {
                if (_cameraProp.Exists())
                {
                    _cameraProp.Detach();
                    _cameraProp.Delete();
                }
            }
            finally
            {
                _cameraProp = null;
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Camera prop deleted");
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
