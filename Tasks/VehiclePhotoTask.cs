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
    /// There are no visible markers - all six positions are invisible triggers
    /// that are simultaneously active, and can be photographed in any order.
    /// A dedicated GameFiber checks the player's distance to every uncompleted
    /// position and handles the photo trigger every frame while the task is active.
    /// </summary>
    public class VehiclePhotoTask
    {
        private const float TriggerRadius = 1.5f;

        // Offsets are relative to the vehicle's own position/heading via
        // GetOffsetPosition, then snapped to ground level (see CalculatePhotoSpots).
        private static readonly Vector3[] LocalPhotoOffsets =
        {
            new Vector3(0f, 5.5f, 0f),      // 1. Front
            new Vector3(4.25f, 4.25f, 0f),  // 2. Front-right
            new Vector3(4.25f, -4.25f, 0f), // 3. Rear-right
            new Vector3(0f, -5.5f, 0f),     // 4. Rear
            new Vector3(-4.25f, -4.25f, 0f),// 5. Rear-left
            new Vector3(-4.25f, 4.25f, 0f), // 6. Front-left
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
        private bool[] _completed;
        private int _completedCount;
        private bool _isActive;
        private bool _keyWasDown;
        private bool _isPlayerInMarkerRange;
        private bool _isTakingPhoto;

        private GameFiber _fiber;
        private Rage.Object _cameraProp;

        public bool IsComplete { get; private set; }

        public int TotalPhotos => _photoSpots?.Length ?? 0;

        public int CompletedPhotos => _completedCount;

        /// <summary>The six calculated world-space photo positions (invisible triggers).</summary>
        public IReadOnlyList<Vector3> PhotoSpots => _photoSpots;

        /// <summary>True only while the player is standing within range of the nearest uncompleted position.</summary>
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
            _completedCount = 0;
            IsComplete = false;
            _isActive = true;
            _keyWasDown = false;
            _isPlayerInMarkerRange = false;
            _isTakingPhoto = false;

            CalculatePhotoSpots();
            _completed = new bool[_photoSpots.Length];

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}] started");
            Game.DisplayNotification($"~b~{_label} photographs: {_completedCount}/{TotalPhotos}");

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
                // the trigger position float instead of sitting at normal ground level.
                float? groundZ = World.GetGroundZ(position, false, true);
                if (groundZ.HasValue)
                {
                    position.Z = groundZ.Value;
                }

                _photoSpots[i] = position;

                Game.LogTrivial(
                    $"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo position {i + 1} = " +
                    $"{position.X:F2} {position.Y:F2} {position.Z:F2}");
            }
        }

        /// <summary>
        /// Runs on its own GameFiber for the lifetime of the task: checks the photo
        /// trigger every frame via GameFiber.Yield(). No markers are ever drawn.
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

                CheckForPhotoInteraction();

                GameFiber.Yield();
            }
        }

        /// <summary>
        /// Finds the nearest uncompleted photo position to the player. All six positions
        /// are active at once, so the player can approach them in any order; if the
        /// player is within range of more than one, the nearest uncompleted one wins.
        /// </summary>
        private bool TryGetNearestUncompletedPosition(Vector3 playerPosition, out int nearestIndex, out float nearestDistance)
        {
            nearestIndex = -1;
            nearestDistance = float.MaxValue;

            for (int i = 0; i < _photoSpots.Length; i++)
            {
                if (_completed[i])
                {
                    continue;
                }

                float distance = playerPosition.DistanceTo(_photoSpots[i]);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestIndex = i;
                }
            }

            return nearestIndex >= 0;
        }

        /// <summary>Checks the player's distance from the nearest uncompleted photo position and handles the T press.</summary>
        private void CheckForPhotoInteraction()
        {
            if (_isTakingPhoto)
            {
                return;
            }

            Ped player = Game.LocalPlayer.Character;

            bool hasUncompleted = TryGetNearestUncompletedPosition(player.Position, out int nearestIndex, out float nearestDistance);
            bool inRange = hasUncompleted && nearestDistance <= TriggerRadius;

            if (inRange && !_isPlayerInMarkerRange)
            {
                Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Entered photo point {nearestIndex + 1}");
            }

            _isPlayerInMarkerRange = inRange;

            // T must do nothing outside an active trigger radius.
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
                TakePhoto(nearestIndex);
            }
        }

        /// <summary>
        /// Faces the player toward the vehicle, plays the camera prop + animation, holds it
        /// long enough to be visible, then marks the given position completed. Runs entirely
        /// on this task's GameFiber, so GameFiber.Sleep here blocks only this task's loop.
        /// </summary>
        private void TakePhoto(int photoSpotIndex)
        {
            int photoNumber = photoSpotIndex + 1;
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

            // Positions cannot be counted again once completed.
            _completed[photoSpotIndex] = true;
            _completedCount++;
            _isTakingPhoto = false;

            Game.LogTrivial($"RealPatrolCallouts: VehiclePhotoTask [{_label}]: Photo {photoNumber} taken");
            Game.DisplayNotification($"~b~{_label} photographs: {_completedCount}/{TotalPhotos}");

            if (_completedCount >= _photoSpots.Length)
            {
                IsComplete = true;
                _isActive = false;

                Game.DisplayNotification($"~b~{_label} photographs complete.");
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
            _cameraProp.AttachTo(player, boneIndex, Vector3.Zero, new Rotator(0f, 0f, 0f));

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
