using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Reusable eight-position "walk around the whole accident scene and photograph it"
    /// task. There are always exactly eight photo zones for the scene as a whole -
    /// regardless of how many vehicles are involved - covering the front/right/rear/left
    /// edge midpoints and the four corners of a rectangle that bounds every involved
    /// vehicle. The rectangle is oriented to the scene/roadway heading (never to an
    /// average of the crashed vehicles' individual headings, which a T-bone or angled
    /// vehicle would distort).
    ///
    /// Each of the eight zones is a generous invisible area (not a tiny point), all eight
    /// are active simultaneously, and can be photographed in any order. A dedicated
    /// GameFiber checks the player's distance to every uncompleted zone and handles the
    /// photo trigger every frame while the task is active.
    /// </summary>
    public class ScenePhotoTask
    {
        // ----- Tunable scene-bounds dimensions (tune these after in-game testing) -----

        /// <summary>How far outside the outer edge of the vehicles the photography perimeter sits.</summary>
        private const float PhotoClearance = 3.5f;

        /// <summary>Approximate half nose-to-tail length used to expand vehicle-center bounds when exact model dimensions aren't reliably available.</summary>
        private const float ApproxVehicleHalfLength = 2.5f;

        /// <summary>Approximate half body width used to expand vehicle-center bounds when exact model dimensions aren't reliably available.</summary>
        private const float ApproxVehicleHalfWidth = 1.2f;

        /// <summary>Inward/outward tolerance (toward/away from the scene) for an edge-midpoint photo zone.</summary>
        private const float EdgeInwardOutwardTolerance = 2.0f;

        /// <summary>Tolerance along the edge itself for an edge-midpoint photo zone.</summary>
        private const float EdgeAlongTolerance = 2.75f;

        /// <summary>Radius of the generous area around each corner photo zone.</summary>
        private const float CornerZoneRadius = 2.25f;

        private const int ZoneCount = 8;

        // ----- Known-working paparazzi camera prop/animation combo - do not change. -----
        private const string CameraPropModelName = "prop_pap_camera_01";
        private const string CameraAnimationDictionary = "amb@world_human_paparazzi@male@base";
        private const string CameraAnimationName = "base";
        private const int CameraHandBoneId = 28422; // resolved per-ped via GET_PED_BONE_INDEX
        private const int CameraAnimationHoldMs = 1200;

        private enum ZoneKind
        {
            /// <summary>Front/rear edge midpoint - inward/outward tolerance runs along the forward axis.</summary>
            EdgeForwardAligned,

            /// <summary>Left/right edge midpoint - inward/outward tolerance runs along the right axis.</summary>
            EdgeRightAligned,

            /// <summary>Corner zone - a simple generous radius around the ideal corner point.</summary>
            Corner
        }

        private struct PhotoZone
        {
            public string Name;
            public Vector3 WorldCenter;
            public float LocalForward;
            public float LocalRight;
            public ZoneKind Kind;
        }

        private readonly List<Vehicle> _vehicles;
        private readonly float _sceneHeading;
        private readonly string _label;
        private readonly Keys _interactionKey;

        private Vector3 _sceneCenter;
        private Vector3 _forwardAxis;
        private Vector3 _rightAxis;

        private PhotoZone[] _zones;
        private bool[] _completed;
        private int _completedCount;
        private bool _isActive;
        private bool _keyWasDown;
        private bool _isPlayerInZoneRange;
        private bool _isTakingPhoto;

        private GameFiber _fiber;
        private Rage.Object _cameraProp;

        public bool IsComplete { get; private set; }

        public int TotalPhotos => ZoneCount;

        public int CompletedPhotos => _completedCount;

        /// <summary>The eight calculated world-space photo zone centers (invisible areas, not points).</summary>
        public IReadOnlyList<Vector3> PhotoZoneCenters
        {
            get
            {
                var centers = new Vector3[_zones?.Length ?? 0];
                for (int i = 0; i < centers.Length; i++)
                {
                    centers[i] = _zones[i].WorldCenter;
                }
                return centers;
            }
        }

        /// <summary>True only while the player is standing within range of the nearest uncompleted zone.</summary>
        public bool IsPlayerInActiveZoneRange => _isPlayerInZoneRange;

        // interactionKey is a constructor parameter (not hard-coded internally) so a
        // future INI/controller-binding layer can simply pass a different Keys value.
        public ScenePhotoTask(IEnumerable<Vehicle> involvedVehicles, float sceneHeading, string label = "Accident scene", Keys interactionKey = Keys.T)
        {
            _vehicles = new List<Vehicle>(involvedVehicles);
            _sceneHeading = sceneHeading;
            _label = label;
            _interactionKey = interactionKey;
        }

        public void Start()
        {
            _completedCount = 0;
            IsComplete = false;
            _isActive = true;
            _keyWasDown = false;
            _isPlayerInZoneRange = false;
            _isTakingPhoto = false;

            CalculateZones();
            _completed = new bool[ZoneCount];

            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}] started");
            Game.DisplayNotification($"~b~{_label} photographs: {_completedCount}/{TotalPhotos}");

            _fiber = GameFiber.StartNew(RunLoop, $"ScenePhotoTask:{_label}");
        }

        /// <summary>
        /// Stops the task's GameFiber and guarantees the camera prop is removed,
        /// even if this is called mid-animation (e.g. the callout ending early).
        /// </summary>
        public void Stop()
        {
            _isActive = false;
            _isPlayerInZoneRange = false;

            if (_fiber != null && _fiber.IsAlive)
            {
                _fiber.Abort();
            }

            _fiber = null;

            CleanupCameraProp();
        }

        /// <summary>
        /// Calculates the scene-wide oriented photography perimeter and the eight zone
        /// centers on it. Called once, from Start() - never recalculated per frame.
        /// </summary>
        private void CalculateZones()
        {
            float headingRadians = _sceneHeading * (float)(Math.PI / 180.0);

            // Matches the forward-vector convention used for road heading elsewhere in this
            // mod: forward.X = -sin(heading), forward.Y = cos(heading). Right is forward
            // rotated 90 degrees so it points toward the driver's right at heading 0.
            _forwardAxis = new Vector3(-(float)Math.Sin(headingRadians), (float)Math.Cos(headingRadians), 0f);
            _rightAxis = new Vector3((float)Math.Cos(headingRadians), (float)Math.Sin(headingRadians), 0f);

            _sceneCenter = CalculateSceneCenter();

            float frontExtent = float.MinValue;
            float rearExtent = float.MaxValue;
            float rightExtent = float.MinValue;
            float leftExtent = float.MaxValue;

            foreach (Vehicle vehicle in _vehicles)
            {
                ToLocal(vehicle.Position, out float localForward, out float localRight);

                frontExtent = Math.Max(frontExtent, localForward + ApproxVehicleHalfLength);
                rearExtent = Math.Min(rearExtent, localForward - ApproxVehicleHalfLength);
                rightExtent = Math.Max(rightExtent, localRight + ApproxVehicleHalfWidth);
                leftExtent = Math.Min(leftExtent, localRight - ApproxVehicleHalfWidth);
            }

            float photoFront = frontExtent + PhotoClearance;
            float photoRear = rearExtent - PhotoClearance;
            float photoRight = rightExtent + PhotoClearance;
            float photoLeft = leftExtent - PhotoClearance;

            float forwardMid = (photoFront + photoRear) / 2f;
            float rightMid = (photoRight + photoLeft) / 2f;

            _zones = new PhotoZone[ZoneCount];
            _zones[0] = BuildZone("FRONT", photoFront, rightMid, ZoneKind.EdgeForwardAligned);
            _zones[1] = BuildZone("FRONT-RIGHT", photoFront, photoRight, ZoneKind.Corner);
            _zones[2] = BuildZone("RIGHT", forwardMid, photoRight, ZoneKind.EdgeRightAligned);
            _zones[3] = BuildZone("REAR-RIGHT", photoRear, photoRight, ZoneKind.Corner);
            _zones[4] = BuildZone("REAR", photoRear, rightMid, ZoneKind.EdgeForwardAligned);
            _zones[5] = BuildZone("REAR-LEFT", photoRear, photoLeft, ZoneKind.Corner);
            _zones[6] = BuildZone("LEFT", forwardMid, photoLeft, ZoneKind.EdgeRightAligned);
            _zones[7] = BuildZone("FRONT-LEFT", photoFront, photoLeft, ZoneKind.Corner);

            LogZoneSetup(photoFront, photoRear, photoLeft, photoRight);
        }

        private Vector3 CalculateSceneCenter()
        {
            float sumX = 0f;
            float sumY = 0f;
            float sumZ = 0f;

            foreach (Vehicle vehicle in _vehicles)
            {
                sumX += vehicle.Position.X;
                sumY += vehicle.Position.Y;
                sumZ += vehicle.Position.Z;
            }

            int count = _vehicles.Count;
            return new Vector3(sumX / count, sumY / count, sumZ / count);
        }

        /// <summary>Transforms a world position into scene-local (forward, right) coordinates relative to the scene center.</summary>
        private void ToLocal(Vector3 worldPosition, out float localForward, out float localRight)
        {
            float relativeX = worldPosition.X - _sceneCenter.X;
            float relativeY = worldPosition.Y - _sceneCenter.Y;

            localForward = (relativeX * _forwardAxis.X) + (relativeY * _forwardAxis.Y);
            localRight = (relativeX * _rightAxis.X) + (relativeY * _rightAxis.Y);
        }

        private PhotoZone BuildZone(string name, float localForward, float localRight, ZoneKind kind)
        {
            float worldX = _sceneCenter.X + (_forwardAxis.X * localForward) + (_rightAxis.X * localRight);
            float worldY = _sceneCenter.Y + (_forwardAxis.Y * localForward) + (_rightAxis.Y * localRight);
            Vector3 position = new Vector3(worldX, worldY, _sceneCenter.Z);

            // The scene center's own Z can sit above true ground level, which would make
            // the zone center float instead of sitting at normal ground level.
            float? groundZ = World.GetGroundZ(position, false, true);
            if (groundZ.HasValue)
            {
                position.Z = groundZ.Value;
            }

            return new PhotoZone
            {
                Name = name,
                WorldCenter = position,
                LocalForward = localForward,
                LocalRight = localRight,
                Kind = kind
            };
        }

        private void LogZoneSetup(float photoFront, float photoRear, float photoLeft, float photoRight)
        {
            Game.LogTrivial(
                $"RealPatrolCallouts: ScenePhotoTask [{_label}]: Scene center: " +
                $"{_sceneCenter.X:F2}/{_sceneCenter.Y:F2}/{_sceneCenter.Z:F2}");
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Scene heading: {_sceneHeading:F2}");
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Local front extent: {photoFront:F2}");
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Local rear extent: {photoRear:F2}");
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Local left extent: {photoLeft:F2}");
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Local right extent: {photoRight:F2}");

            for (int i = 0; i < _zones.Length; i++)
            {
                PhotoZone zone = _zones[i];
                Game.LogTrivial(
                    $"RealPatrolCallouts: ScenePhotoTask [{_label}]: Photo zone {i + 1} {zone.Name} = " +
                    $"{zone.WorldCenter.X:F2}/{zone.WorldCenter.Y:F2}/{zone.WorldCenter.Z:F2}");
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
                if (!AllVehiclesExist())
                {
                    _isActive = false;
                    _isPlayerInZoneRange = false;
                    break;
                }

                CheckForPhotoInteraction();

                GameFiber.Yield();
            }
        }

        private bool AllVehiclesExist()
        {
            foreach (Vehicle vehicle in _vehicles)
            {
                if (vehicle == null || !vehicle.Exists())
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Finds the nearest uncompleted photo zone the player currently qualifies for. All
        /// eight zones are active at once, so the player can approach them in any order; if
        /// the player is inside more than one zone's area, the nearest uncompleted one wins.
        /// </summary>
        private bool TryGetNearestUncompletedZone(Vector3 playerPosition, out int nearestIndex, out float nearestDistance)
        {
            nearestIndex = -1;
            nearestDistance = float.MaxValue;

            ToLocal(playerPosition, out float playerLocalForward, out float playerLocalRight);

            for (int i = 0; i < _zones.Length; i++)
            {
                if (_completed[i])
                {
                    continue;
                }

                if (!IsInsideZone(_zones[i], playerPosition, playerLocalForward, playerLocalRight))
                {
                    continue;
                }

                float distance = playerPosition.DistanceTo(_zones[i].WorldCenter);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestIndex = i;
                }
            }

            return nearestIndex >= 0;
        }

        /// <summary>
        /// Each zone is a generous area, not a tiny point: edge-midpoint zones use an
        /// oriented rectangle (tight inward/outward, looser along the edge), corner zones
        /// use a simple generous radius around the ideal corner. All checks are horizontal
        /// only (X/Y) so a sloped road can't shrink a zone against a ground-snapped Z.
        /// </summary>
        private static bool IsInsideZone(PhotoZone zone, Vector3 playerPosition, float playerLocalForward, float playerLocalRight)
        {
            switch (zone.Kind)
            {
                case ZoneKind.Corner:
                    float dx = playerPosition.X - zone.WorldCenter.X;
                    float dy = playerPosition.Y - zone.WorldCenter.Y;
                    float horizontalDistance = (float)Math.Sqrt((dx * dx) + (dy * dy));
                    return horizontalDistance <= CornerZoneRadius;

                case ZoneKind.EdgeForwardAligned:
                    return Math.Abs(playerLocalForward - zone.LocalForward) <= EdgeInwardOutwardTolerance
                        && Math.Abs(playerLocalRight - zone.LocalRight) <= EdgeAlongTolerance;

                case ZoneKind.EdgeRightAligned:
                    return Math.Abs(playerLocalRight - zone.LocalRight) <= EdgeInwardOutwardTolerance
                        && Math.Abs(playerLocalForward - zone.LocalForward) <= EdgeAlongTolerance;

                default:
                    return false;
            }
        }

        /// <summary>Checks the player's distance from the nearest uncompleted photo zone and handles the T press.</summary>
        private void CheckForPhotoInteraction()
        {
            if (_isTakingPhoto)
            {
                return;
            }

            Ped player = Game.LocalPlayer.Character;

            bool inRange = TryGetNearestUncompletedZone(player.Position, out int nearestIndex, out _);

            if (inRange && !_isPlayerInZoneRange)
            {
                Game.LogTrivial(
                    $"RealPatrolCallouts: ScenePhotoTask [{_label}]: Entered photo zone {nearestIndex + 1} " +
                    $"{_zones[nearestIndex].Name}");
            }

            _isPlayerInZoneRange = inRange;

            // T must do nothing outside an active zone.
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
        /// Faces the player toward the scene center, plays the camera prop + animation, holds
        /// it long enough to be visible, then marks the given zone completed. Runs entirely on
        /// this task's GameFiber, so GameFiber.Sleep here blocks only this task's loop.
        /// </summary>
        private void TakePhoto(int zoneIndex)
        {
            int photoNumber = zoneIndex + 1;
            _isTakingPhoto = true;
            _isPlayerInZoneRange = false;

            string zoneName = _zones[zoneIndex].Name;
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Taking photo {photoNumber} ({zoneName})");

            Ped player = Game.LocalPlayer.Character;

            FacePlayerTowardScene(player);

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

            // Zones cannot be counted again once completed.
            _completed[zoneIndex] = true;
            _completedCount++;
            _isTakingPhoto = false;

            Game.LogTrivial(
                $"RealPatrolCallouts: ScenePhotoTask [{_label}]: Scene photo {photoNumber} completed — total {_completedCount}/{TotalPhotos}");
            Game.DisplayNotification($"~b~{_label} photographs: {_completedCount}/{TotalPhotos}");

            if (_completedCount >= ZoneCount)
            {
                IsComplete = true;
                _isActive = false;

                Game.DisplayNotification($"~b~{_label} photographs complete.");
                Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Scene photo task complete");
            }
        }

        private void FacePlayerTowardScene(Ped player)
        {
            Vector3 direction = new Vector3(
                _sceneCenter.X - player.Position.X,
                _sceneCenter.Y - player.Position.Y,
                0f);

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
            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Camera prop created");

            int boneIndex = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(player, CameraHandBoneId);
            _cameraProp.AttachTo(player, boneIndex, Vector3.Zero, new Rotator(0f, 0f, 0f));

            Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Camera prop attached");
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
                Game.LogTrivial($"RealPatrolCallouts: ScenePhotoTask [{_label}]: Camera prop deleted");
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
