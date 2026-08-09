using System;
using System.Windows.Forms;
using LSPD_First_Response.Mod.API;
using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// The manual bridge between the player's own MDT/PDComp work and this callout. Waits
    /// for the player to be seated in the right patrol vehicle after scene photography is
    /// complete, shows a single reminder, then waits for one T press to confirm the crash
    /// report was submitted. This class never reads or writes anything in PDComp/the MDT -
    /// that work is entirely the player's, done through the game's real MDT UI.
    /// </summary>
    public class CrashReportConfirmationTask
    {
        private readonly Vehicle _preferredVehicle;
        private readonly Keys _interactionKey;

        private Vehicle _reportVehicle;
        private bool _instructionShown;
        private bool _keyWasDown;

        public CrashReportConfirmationTask(Vehicle preferredVehicle, Keys interactionKey = Keys.T)
        {
            _preferredVehicle = preferredVehicle;
            _interactionKey = interactionKey;
        }

        public bool IsComplete { get; private set; }

        public void Process()
        {
            if (IsComplete)
            {
                return;
            }

            Ped player = Game.LocalPlayer.Character;
            Vehicle current = player.CurrentVehicle;

            if (_reportVehicle == null)
            {
                if (!TryIdentifyReportVehicle(current))
                {
                    _keyWasDown = false;
                    return;
                }
            }

            if (!_reportVehicle.Exists() || current != _reportVehicle)
            {
                _keyWasDown = false;
                return;
            }

            if (!_instructionShown)
            {
                Game.DisplayNotification("~b~Complete the driver checks and crash report in your MDT.");
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Waiting for crash report");
                _instructionShown = true;
            }

            Game.DisplayHelp("Press " + _interactionKey + " when the crash report is complete.");

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (keyJustPressed)
            {
                IsComplete = true;

                Game.DisplayNotification("~b~Crash report completed. Provide a copy to each involved driver.");
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Crash report confirmed complete");
            }
        }

        /// <summary>
        /// Prefers the vehicle the player actually responded in. If that reference is
        /// unavailable (not captured, or it no longer exists), falls back to accepting the
        /// first valid police vehicle the player gets into during this stage.
        /// </summary>
        private bool TryIdentifyReportVehicle(Vehicle current)
        {
            if (current == null)
            {
                return false;
            }

            bool preferredValid = _preferredVehicle != null && _preferredVehicle.Exists();
            bool isCandidate = preferredValid ? current == _preferredVehicle : IsPoliceVehicle(current);

            if (!isCandidate)
            {
                return false;
            }

            _reportVehicle = current;
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Report vehicle identified");
            return true;
        }

        private static bool IsPoliceVehicle(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
            {
                return false;
            }

            try
            {
                return Functions.IsVehiclePoliceVehicle(vehicle);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
