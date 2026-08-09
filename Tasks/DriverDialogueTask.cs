using System;
using System.Windows.Forms;
using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Minimal two-line subtitle "conversation" with a driver ped: an officer prompt
    /// followed by the driver's fixed response. No branching, no fault-finding - just
    /// scripted flavor text triggered by proximity + a key press.
    /// </summary>
    public class DriverDialogueTask
    {
        private const float ApproachRadius = 2.0f;
        private const int LineDurationMs = 4500;

        private readonly Ped _driver;
        private readonly string _officerLine;
        private readonly string _driverLine;
        private readonly Keys _interactionKey;

        private bool _keyWasDown;
        private bool _isTalking;
        private bool _isOnSecondLine;
        private DateTime _currentLineStartedAt;

        public DriverDialogueTask(Ped driver, string officerLine, string driverLine, Keys interactionKey = Keys.T)
        {
            _driver = driver;
            _officerLine = officerLine;
            _driverLine = driverLine;
            _interactionKey = interactionKey;
        }

        /// <summary>
        /// Call once per tick. <paramref name="suppressInteraction"/> should be true whenever
        /// the player is standing inside an active VehiclePhotoTask marker, so the shared T
        /// key can never trigger both a photo and a line of dialogue on the same press.
        /// </summary>
        public void Process(bool suppressInteraction)
        {
            if (_driver == null || !_driver.Exists())
            {
                return;
            }

            if (_isTalking)
            {
                AdvanceDialogue();
                return;
            }

            if (suppressInteraction)
            {
                // Drop any press that happened while suppressed so it can't carry over
                // into a dialogue trigger the moment the player leaves the photo marker.
                _keyWasDown = Game.IsKeyDown(_interactionKey);
                return;
            }

            Ped player = Game.LocalPlayer.Character;
            float distance = player.Position.DistanceTo(_driver.Position);
            bool inRange = distance <= ApproachRadius;

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (!inRange)
            {
                return;
            }

            Game.DisplayHelp("Press " + _interactionKey + " to speak with driver");

            if (keyJustPressed)
            {
                StartDialogue();
            }
        }

        private void StartDialogue()
        {
            _isTalking = true;
            _isOnSecondLine = false;
            _currentLineStartedAt = DateTime.Now;

            Game.DisplaySubtitle("Officer: \"" + _officerLine + "\"", LineDurationMs);

            Game.LogTrivial("RealPatrolCallouts: DriverDialogueTask started");
        }

        private void AdvanceDialogue()
        {
            double elapsedMs = (DateTime.Now - _currentLineStartedAt).TotalMilliseconds;

            if (elapsedMs < LineDurationMs)
            {
                return;
            }

            if (!_isOnSecondLine)
            {
                _isOnSecondLine = true;
                _currentLineStartedAt = DateTime.Now;

                Game.DisplaySubtitle("Driver: \"" + _driverLine + "\"", LineDurationMs);
                return;
            }

            _isTalking = false;

            Game.LogTrivial("RealPatrolCallouts: DriverDialogueTask completed");
        }
    }
}
