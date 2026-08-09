using System;
using System.Collections.Generic;
using System.Drawing;
using LSPD_First_Response.Mod.Callouts;
using Rage;
using Rage.Native;
using RealPatrolCallouts.Tasks;

namespace RealPatrolCallouts.Callouts
{
    [CalloutInfo("Minor Traffic Collision", CalloutProbability.Medium)]
    public class MinorTrafficCollision : Callout
    {
        private const float MinCalloutDistance = 300f;
        private const float MaxCalloutDistance = 700f;
        private const float DispatchMinimumDistance = 100f;
        private const float SceneBlipRadius = 30f;
        private const float ArrivalDistance = 25f;

        // Approximate half-lengths (nose-to-tail / 2) for the two spawned models, used to
        // keep the vehicles bumper-to-bumper without intersecting geometry.
        private const float Vehicle1HalfLength = 2.0f; // Blista compact hatchback (~4.0m long)
        private const float Vehicle2HalfLength = 2.3f; // Asea mid-size sedan (~4.6m long)
        private const float MinImpactGap = 0.5f;
        private const float MaxImpactGap = 1.5f;
        private const float MinVehicle2Rotation = 10f;
        private const float MaxVehicle2Rotation = 25f;

        /// <summary>
        /// The patrol workflow this callout drives: arrive -&gt; investigate/interview
        /// every driver -&gt; photograph the scene -&gt; the player manually runs IDs/writes
        /// the crash report in PDComp -&gt; distribute the report to each driver and
        /// dismiss them -&gt; clear/tow whatever's left -&gt; done. Each stage owns the T key
        /// exclusively; no two stages ever process a T press at the same time.
        /// </summary>
        private enum CalloutStage
        {
            Responding,
            InitialInvestigation,
            Photography,
            ReportPreparation,
            ReportDistribution,
            VehicleClearance,
            Complete
        }

        private Vector3 _calloutPosition;
        private float _sceneHeading;

        private Blip _sceneBlip;
        private Vehicle _vehicle1;
        private Vehicle _vehicle2;

        /// <summary>The patrol vehicle the player responded to the call in, captured on acceptance if available.</summary>
        private Vehicle _responseVehicle;

        private List<AccidentParticipant> _participants;

        private ScenePhotoTask _photoTask;
        private CrashReportConfirmationTask _reportConfirmationTask;
        private ReportDistributionTask _reportDistributionTask;
        private DisabledVehicleClearanceTask _vehicleClearanceTask;

        private CalloutStage _stage;

        public override bool OnBeforeCalloutDisplayed()
        {
            _calloutPosition = FindScenePosition();

            CalloutPosition = _calloutPosition;
            CalloutMessage = "Minor Traffic Collision";

            AddMinimumDistanceCheck(DispatchMinimumDistance, _calloutPosition);
            ShowCalloutAreaBlipBeforeAccepting(_calloutPosition, SceneBlipRadius);

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision offered");

            return base.OnBeforeCalloutDisplayed();
        }

        public override bool OnCalloutAccepted()
        {
            base.OnCalloutAccepted();

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision accepted");

            // Best-effort capture of the vehicle the player is responding in. If they
            // aren't in a vehicle yet (or it can't be resolved), ReportPreparation falls
            // back to accepting the first police vehicle the player gets into later.
            _responseVehicle = Game.LocalPlayer.Character.CurrentVehicle;

            _sceneBlip = new Blip(_calloutPosition)
            {
                Color = Color.Red
            };
            _sceneBlip.EnableRoute(Color.Yellow);

            SpawnScene();
            _stage = CalloutStage.Responding;

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision scene spawned");

            return true;
        }

        public override void OnCalloutNotAccepted()
        {
            base.OnCalloutNotAccepted();
        }

        public override void Process()
        {
            base.Process();

            if (_vehicle1 == null)
            {
                return;
            }

            switch (_stage)
            {
                case CalloutStage.Responding:
                    CheckForArrival();
                    break;

                case CalloutStage.InitialInvestigation:
                    ProcessInitialInvestigation();
                    break;

                case CalloutStage.Photography:
                    if (_photoTask.IsComplete)
                    {
                        CompletePhotographs();
                    }
                    break;

                case CalloutStage.ReportPreparation:
                    _reportConfirmationTask.Process();
                    if (_reportConfirmationTask.IsComplete)
                    {
                        BeginReportDistribution();
                    }
                    break;

                case CalloutStage.ReportDistribution:
                    if (_reportDistributionTask.IsComplete)
                    {
                        BeginVehicleClearance();
                    }
                    break;

                case CalloutStage.VehicleClearance:
                    _vehicleClearanceTask.Process();
                    if (_vehicleClearanceTask.IsComplete)
                    {
                        CompleteCallout();
                    }
                    break;

                case CalloutStage.Complete:
                    break;
            }
        }

        public override void End()
        {
            _photoTask?.Stop();
            _reportDistributionTask?.Stop();

            if (_sceneBlip != null && _sceneBlip.Exists())
            {
                _sceneBlip.Delete();
            }

            if (_participants != null)
            {
                foreach (AccidentParticipant participant in _participants)
                {
                    DismissPed(participant.Driver);
                    DismissVehicle(participant.Vehicle);
                }
            }

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision ended");

            base.End();
        }

        private static Vector3 FindScenePosition()
        {
            Vector3 playerPosition = Game.LocalPlayer.Character.Position;

            var rng = new Random();
            float distance = MinCalloutDistance + (float)(rng.NextDouble() * (MaxCalloutDistance - MinCalloutDistance));
            float angleRadians = (float)(rng.NextDouble() * 2.0 * Math.PI);

            Vector3 roughOffset = new Vector3(
                (float)Math.Sin(angleRadians) * distance,
                (float)Math.Cos(angleRadians) * distance,
                0f);

            return World.GetNextPositionOnStreet(playerPosition + roughOffset);
        }

        private void SpawnScene()
        {
            float roadHeading = GetRoadHeading(_calloutPosition);
            _sceneHeading = roadHeading;
            float headingRadians = roadHeading * (float)(Math.PI / 180.0);

            Vector3 forward = new Vector3(-(float)Math.Sin(headingRadians), (float)Math.Cos(headingRadians), 0f);

            var rng = new Random();
            float impactGap = MinImpactGap + (float)(rng.NextDouble() * (MaxImpactGap - MinImpactGap));
            float rotationOffset = MinVehicle2Rotation + (float)(rng.NextDouble() * (MaxVehicle2Rotation - MinVehicle2Rotation));
            if (rng.Next(2) == 0)
            {
                rotationOffset = -rotationOffset;
            }

            // Vehicle 1 sits on the road heading; Vehicle 2 rear-ended it, so it comes to
            // rest just behind it, angled slightly off-heading rather than perfectly
            // nose-to-tail. Centers are spaced by both half-lengths plus the impact gap so
            // the bodies never intersect.
            float centerDistance = Vehicle1HalfLength + impactGap + Vehicle2HalfLength;

            Vector3 vehicle1Position = _calloutPosition;
            Vector3 vehicle2Position = _calloutPosition - forward * centerDistance;

            _vehicle1 = SpawnCollisionVehicle("blista", vehicle1Position, roadHeading, rearEndDamage: true);
            _vehicle2 = SpawnCollisionVehicle("asea", vehicle2Position, roadHeading + rotationOffset, rearEndDamage: false);

            Ped driver1 = SpawnStandingDriver("a_m_y_business_01", _vehicle1, new Vector3(-2.5f, 0f, 0f));
            Ped driver2 = SpawnStandingDriver("a_f_y_business_02", _vehicle2, new Vector3(2.5f, 0f, 0f));

            _photoTask = new ScenePhotoTask(new[] { _vehicle1, _vehicle2 }, _sceneHeading, "Accident scene");

            var dialogueTask1 = new DriverDialogueTask(
                driver1,
                "Can you tell me what happened?",
                "I was driving through here when the other vehicle hit me. Nobody is hurt.");

            var dialogueTask2 = new DriverDialogueTask(
                driver2,
                "Can you tell me what happened?",
                "We collided. I am okay, and I do not think anyone is injured.");

            _participants = new List<AccidentParticipant>
            {
                new AccidentParticipant(1, driver1, _vehicle1, dialogueTask1),
                new AccidentParticipant(2, driver2, _vehicle2, dialogueTask2)
            };
        }

        private static Vehicle SpawnCollisionVehicle(string modelName, Vector3 position, float heading, bool rearEndDamage)
        {
            var vehicle = new Vehicle(modelName, position, heading)
            {
                IsPersistent = true
            };

            vehicle.IsPositionFrozen = true;

            // Moderate engine health + a local dent keep damage visible without fire/explosions.
            vehicle.EngineHealth = 480f;
            vehicle.IsDeformationEnabled = true;

            Vector3 damageOffset = rearEndDamage ? new Vector3(0f, -1.8f, 0.2f) : new Vector3(0f, 1.8f, 0.2f);
            vehicle.Deform(damageOffset, 120f, 2.2f);

            return vehicle;
        }

        private static Ped SpawnStandingDriver(string modelName, Vehicle vehicle, Vector3 localOffset)
        {
            Vector3 pedPosition = vehicle.GetOffsetPosition(localOffset);

            float? groundZ = World.GetGroundZ(pedPosition, false, true);
            if (groundZ.HasValue)
            {
                pedPosition.Z = groundZ.Value;
            }

            var driver = new Ped(modelName, pedPosition, vehicle.Heading)
            {
                IsPersistent = true,
                BlockPermanentEvents = true
            };

            driver.Tasks.StandStill(-1);

            return driver;
        }

        private static float GetRoadHeading(Vector3 position)
        {
            try
            {
                bool found = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                    position.X, position.Y, position.Z,
                    out Vector3 nodePosition, out float nodeHeading,
                    1, 3.0f, 0f);

                if (found)
                {
                    return nodeHeading;
                }
            }
            catch (Exception)
            {
                // Fall back to a default heading if this native's signature differs on the
                // installed game/RPH build - the scene will still spawn, just unaligned.
            }

            return 0f;
        }

        private void CheckForArrival()
        {
            float distanceToScene = Game.LocalPlayer.Character.Position.DistanceTo(_calloutPosition);

            if (distanceToScene > ArrivalDistance)
            {
                return;
            }

            if (_sceneBlip != null && _sceneBlip.Exists())
            {
                _sceneBlip.IsRouteEnabled = false;
            }

            Game.DisplayHelp("Check on the involved drivers.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision player arrived at scene");

            _stage = CalloutStage.InitialInvestigation;
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Initial investigation started");
        }

        // ----- Stage 1: initial investigation (injury check, interview, ID collection) -----

        private void ProcessInitialInvestigation()
        {
            Ped player = Game.LocalPlayer.Character;
            AccidentParticipant nearestPending = FindNearestPendingInterview(player.Position);

            foreach (AccidentParticipant participant in _participants)
            {
                if (participant.InterviewCompleted)
                {
                    continue;
                }

                // Only the nearest not-yet-interviewed driver owns T this tick, so two
                // drivers standing close together can never both react to one press.
                bool suppress = participant != nearestPending;
                participant.InterviewTask.Process(suppress);

                if (participant.InterviewTask.IsComplete)
                {
                    CompleteInterview(participant);
                }
            }

            if (AllInterviewsComplete())
            {
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: All participants interviewed");
                BeginPhotography();
            }
        }

        private AccidentParticipant FindNearestPendingInterview(Vector3 playerPosition)
        {
            AccidentParticipant nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (AccidentParticipant participant in _participants)
            {
                if (participant.InterviewCompleted)
                {
                    continue;
                }

                if (participant.Driver == null || !participant.Driver.Exists())
                {
                    continue;
                }

                float distance = playerPosition.DistanceTo(participant.Driver.Position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = participant;
                }
            }

            return nearest;
        }

        private void CompleteInterview(AccidentParticipant participant)
        {
            participant.InterviewCompleted = true;
            participant.IdCollected = true;
            participant.DisplayName = PersonaHelper.GetDisplayName(participant.Driver);

            string dob = PersonaHelper.GetDateOfBirthText(participant.Driver);
            string idMessage = "ID Collected: " + participant.DisplayName;
            if (!string.IsNullOrEmpty(dob))
            {
                idMessage += " (DOB " + dob + ")";
            }

            Game.DisplayNotification("~b~" + idMessage);
            Game.LogTrivial($"RealPatrolCallouts: MinorTrafficCollision: Driver {participant.Number} interview complete / ID collected");
        }

        private bool AllInterviewsComplete()
        {
            foreach (AccidentParticipant participant in _participants)
            {
                if (!participant.InterviewCompleted || !participant.IdCollected)
                {
                    return false;
                }
            }

            return true;
        }

        // ----- Stage 2: scene photography (existing 8-photo ScenePhotoTask, unchanged) -----

        private void BeginPhotography()
        {
            _stage = CalloutStage.Photography;
            _photoTask.Start();

            Game.DisplayHelp("Document the accident scene.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Photography started");
        }

        private void CompletePhotographs()
        {
            _stage = CalloutStage.ReportPreparation;

            Game.DisplayNotification("~b~Return to your patrol vehicle to complete your checks and crash report.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Photography complete");

            _reportConfirmationTask = new CrashReportConfirmationTask(_responseVehicle);
        }

        // ----- Stage 3/4: report preparation (manual MDT/PDComp) and distribution -----

        private void BeginReportDistribution()
        {
            _stage = CalloutStage.ReportDistribution;

            _reportDistributionTask = new ReportDistributionTask(_participants);
            _reportDistributionTask.Start();
        }

        // ----- Stage 5: vehicle clearance/tow -----

        private void BeginVehicleClearance()
        {
            _stage = CalloutStage.VehicleClearance;

            _vehicleClearanceTask = new DisabledVehicleClearanceTask(_reportDistributionTask.DisabledVehicles, _calloutPosition);
        }

        private void CompleteCallout()
        {
            _stage = CalloutStage.Complete;

            Game.DisplayNotification("~b~Accident scene clear.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Scene clear");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Callout complete");

            End();
        }

        private static void DismissPed(Ped ped)
        {
            if (ped != null && ped.Exists())
            {
                ped.Dismiss();
            }
        }

        private static void DismissVehicle(Vehicle vehicle)
        {
            if (vehicle != null && vehicle.Exists())
            {
                vehicle.Dismiss();
            }
        }
    }
}
