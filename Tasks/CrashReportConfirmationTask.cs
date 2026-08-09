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

        private bool _wasInReportVehicle;
        private bool _vehicleEntryLogged;
        private bool _promptLogged;
        private bool _keyWasDown;

        public CrashReportConfirmationTask(Vehicle preferredVehicle, Keys interactionKey = Keys.T)
        {
            _preferredVehicle = preferredVehicle;
            _interactionKey = interactionKey;
            _keyWasDown = Game.IsKeyDown(interactionKey);
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

            if (!IsReportVehicle(current))
            {
                _wasInReportVehicle = false;
                _keyWasDown = false;
                return;
            }

            if (!_wasInReportVehicle)
            {
                // The player just sat down in a valid vehicle - this interaction is only
                // now starting to own T, so seed the debounce from the live key state
                // instead of assuming it's up (it may still be held from a press in the
                // previous stage).
                _wasInReportVehicle = true;
                _keyWasDown = Game.IsKeyDown(_interactionKey);

                if (!_vehicleEntryLogged)
                {
                    _vehicleEntryLogged = true;
                    Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Player entered valid report vehicle");
                }
            }

            if (!_promptLogged)
            {
                _promptLogged = true;
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Showing report completion prompt");
            }

            Game.DisplayHelp("Press " + _interactionKey + " to complete crash report");

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (keyJustPressed)
            {
                IsComplete = true;

                Game.DisplayNotification("~b~Crash report complete. Give a copy to each driver.");
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Crash report completed");
            }
        }

        /// <summary>
        /// The simplest reliable rule: any vehicle the player is currently seated in that is
        /// recognized as a police/emergency vehicle counts. The originally-captured response
        /// vehicle is preferred when it's still valid (an exact match always counts, even in
        /// the rare case the police-vehicle native doesn't flag it), but that preference can
        /// never be the sole gate - it must never block the prompt from appearing for a
        /// legitimate police vehicle just because it isn't the exact original reference.
        /// </summary>
        private bool IsReportVehicle(Vehicle current)
        {
            if (current == null || !current.Exists())
            {
                return false;
            }

            bool preferredValid = _preferredVehicle != null && _preferredVehicle.Exists();
            if (preferredValid && current == _preferredVehicle)
            {
                return true;
            }

            return IsPoliceVehicle(current);
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
