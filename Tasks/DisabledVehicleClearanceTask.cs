using System.Collections.Generic;
using System.Windows.Forms;
using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Watches whichever accident vehicles were left disabled/undriveable after their
    /// driver was dismissed. This never tows anything itself - the player's own external
    /// towing tools/mods handle that. Automatic detection (the vehicle disappearing or
    /// being moved well clear of the scene) is preferred; a manual T-press fallback near
    /// the scene exists in case a particular towing plugin doesn't relocate/delete the
    /// vehicle far enough for automatic detection to notice.
    /// </summary>
    public class DisabledVehicleClearanceTask
    {
        private const float ClearedDistanceThreshold = 40f;
        private const float FallbackPromptRadius = 15f;

        private readonly List<Vehicle> _vehicles;
        private readonly List<Vector3> _originalPositions;
        private readonly Vector3 _scenePosition;
        private readonly Keys _interactionKey;

        private readonly bool[] _cleared;
        private bool _keyWasDown;
        private bool _promptShown;

        public DisabledVehicleClearanceTask(IEnumerable<Vehicle> disabledVehicles, Vector3 scenePosition, Keys interactionKey = Keys.T)
        {
            _vehicles = new List<Vehicle>(disabledVehicles);
            _scenePosition = scenePosition;
            _interactionKey = interactionKey;

            _originalPositions = new List<Vector3>();
            foreach (Vehicle vehicle in _vehicles)
            {
                _originalPositions.Add(vehicle != null && vehicle.Exists() ? vehicle.Position : scenePosition);
            }

            _cleared = new bool[_vehicles.Count];

            // Seed from the live key state rather than assuming it's up - T may still be
            // held from the dismissal press that ended the previous stage.
            _keyWasDown = Game.IsKeyDown(_interactionKey);
        }

        public bool IsComplete
        {
            get
            {
                for (int i = 0; i < _cleared.Length; i++)
                {
                    if (!_cleared[i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Process()
        {
            if (IsComplete)
            {
                return;
            }

            if (!_promptShown)
            {
                Game.DisplayNotification("~b~Clear the remaining disabled vehicle(s) from the scene.");
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Waiting for disabled vehicle clearance");
                _promptShown = true;
            }

            for (int i = 0; i < _vehicles.Count; i++)
            {
                if (_cleared[i])
                {
                    continue;
                }

                Vehicle vehicle = _vehicles[i];

                bool goneOrTowed = vehicle == null || !vehicle.Exists()
                    || vehicle.Position.DistanceTo(_originalPositions[i]) >= ClearedDistanceThreshold;

                if (goneOrTowed)
                {
                    _cleared[i] = true;
                    Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Disabled vehicle cleared");
                }
            }

            if (IsComplete)
            {
                return;
            }

            ProcessFallbackPrompt();
        }

        /// <summary>Conservative manual override in case a towing plugin's behavior defeats automatic detection.</summary>
        private void ProcessFallbackPrompt()
        {
            Ped player = Game.LocalPlayer.Character;
            float distance = player.Position.DistanceTo(_scenePosition);

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (distance > FallbackPromptRadius)
            {
                return;
            }

            Game.DisplayHelp("Press " + _interactionKey + " when the accident scene is clear.");

            if (keyJustPressed)
            {
                for (int i = 0; i < _cleared.Length; i++)
                {
                    _cleared[i] = true;
                }

                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Disabled vehicle clearance confirmed manually");
            }
        }
    }
}
