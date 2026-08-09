using LSPD_First_Response.Mod.API;
using Rage;
using RealPatrolCallouts.Callouts;

[assembly: Rage.Attributes.Plugin("RealPatrolCallouts", Author = "Denny Hodges", Description = "Real Patrol Callouts for LSPDFR")]

namespace RealPatrolCallouts
{
    public class Main : Plugin
    {
        public override void Initialize()
        {
            Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;

            Game.LogTrivial("RealPatrolCallouts initialized");
        }

        public override void Finally()
        {
        }

        private void OnOnDutyStateChanged(bool onDuty)
        {
            if (!onDuty)
            {
                return;
            }

            Game.LogTrivial("RealPatrolCallouts: player went on duty");

            Functions.RegisterCallout(typeof(TestCallout));
            Game.LogTrivial("RealPatrolCallouts: TestCallout registered");

            Functions.RegisterCallout(typeof(MinorTrafficCollision));
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision registered");
        }
    }
}
