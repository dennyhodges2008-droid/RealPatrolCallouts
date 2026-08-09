using System;
using System.Windows.Forms;
using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Functional driver interview: a short sequence of officer/driver subtitle
    /// "conversation" exchanges covering an injury check, the driver's account of what
    /// happened, and a driver's license/ID request - triggered by proximity + a key press.
    /// The account exchange is still the same scripted flavor text passed in by the
    /// caller (its content will be revisited separately); the injury-check and ID-request
    /// exchanges are the functional steps every interview needs regardless of content.
    /// </summary>
    public class DriverDialogueTask
    {
        private const float ApproachRadius = 2.0f;
        private const int LineDurationMs = 4500;

        private readonly Ped _driver;
        private readonly string[] _officerLines;
        private readonly string[] _driverLines;
        private readonly Keys _interactionKey;

        private bool _keyWasDown;
        private bool _isTalking;
        private bool _isOnDriverLine;
        private int _exchangeIndex;
        private DateTime _currentLineStartedAt;

        public DriverDialogueTask(Ped driver, string accountQuestion, string accountAnswer, Keys interactionKey = Keys.T)
        {
            _driver = driver;
            _interactionKey = interactionKey;
            // Seed from the live key state rather than assuming it's up, so T can't cascade
            // in from whatever was held right before this driver's interview started owning it.
            _keyWasDown = Game.IsKeyDown(interactionKey);

            _officerLines = new[]
            {
                "Is anyone injured, or do you need medical attention?",
                accountQuestion,
                "Can I see your driver's license, please?"
            };

            _driverLines = new[]
            {
                "No, I'm okay. I don't think anyone's hurt.",
                accountAnswer,
                "Sure, here you go."
            };
        }

        /// <summary>True once all interview exchanges (injury check, account, ID request) have played out.</summary>
        public bool IsComplete { get; private set; }

        /// <summary>
        /// Call once per tick. <paramref name="suppressInteraction"/> should be true whenever
        /// another interaction (another driver's interview, the photo task, etc.) currently
        /// owns the shared T key, so two interactions can never react to the same press.
        /// </summary>
        public void Process(bool suppressInteraction)
        {
            if (IsComplete)
            {
                return;
            }

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
                StartExchange();
            }
        }

        private void StartExchange()
        {
            _isTalking = true;
            _isOnDriverLine = false;
            _currentLineStartedAt = DateTime.Now;

            Game.DisplaySubtitle("Officer: \"" + _officerLines[_exchangeIndex] + "\"", LineDurationMs);

            if (_exchangeIndex == 0)
            {
                Game.LogTrivial("RealPatrolCallouts: DriverDialogueTask started");
            }
        }

        private void AdvanceDialogue()
        {
            double elapsedMs = (DateTime.Now - _currentLineStartedAt).TotalMilliseconds;

            if (elapsedMs < LineDurationMs)
            {
                return;
            }

            if (!_isOnDriverLine)
            {
                _isOnDriverLine = true;
                _currentLineStartedAt = DateTime.Now;

                Game.DisplaySubtitle("Driver: \"" + _driverLines[_exchangeIndex] + "\"", LineDurationMs);
                return;
            }

            _exchangeIndex++;

            if (_exchangeIndex < _officerLines.Length)
            {
                _isOnDriverLine = false;
                _currentLineStartedAt = DateTime.Now;

                Game.DisplaySubtitle("Officer: \"" + _officerLines[_exchangeIndex] + "\"", LineDurationMs);
                return;
            }

            _isTalking = false;
            IsComplete = true;

            Game.LogTrivial("RealPatrolCallouts: DriverDialogueTask completed");
        }
    }
}
