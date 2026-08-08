using LSPD_First_Response.Mod.Callouts;
using Rage;

namespace RealPatrolCallouts.Callouts
{
    [CalloutInfo("TestCallout", CalloutProbability.Never)]
    public class TestCallout : Callout
    {
        private Vector3 _calloutPosition;

        public override bool OnBeforeCalloutDisplayed()
        {
            _calloutPosition = World.GetNextPositionOnStreet(Game.LocalPlayer.Character.Position.Around(50f));

            CalloutPosition = _calloutPosition;
            CalloutMessage = "Test Callout";

            AddMinimumDistanceCheck(10f, _calloutPosition);
            ShowCalloutAreaBlipBeforeAccepting(50f);

            Game.LogTrivial("RealPatrolCallouts: TestCallout offered");

            return base.OnBeforeCalloutDisplayed();
        }

        public override bool OnCalloutAccepted()
        {
            base.OnCalloutAccepted();

            Game.DisplayNotification("Real Patrol Callouts test callout started.");
            Game.LogTrivial("RealPatrolCallouts: TestCallout accepted");

            return true;
        }

        public override void OnCalloutNotAccepted()
        {
            base.OnCalloutNotAccepted();
        }

        public override void End()
        {
            Game.LogTrivial("RealPatrolCallouts: TestCallout ended");

            base.End();
        }
    }
}
