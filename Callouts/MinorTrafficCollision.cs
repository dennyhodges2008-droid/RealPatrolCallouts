using System;
using System.Collections.Generic;
using System.Drawing;
using LSPD_First_Response.Mod.API;
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

        // Location selection/validation. GTA's vehicle-node network includes dirt paths,
        // service paths, and parking areas alongside normal roads, so a single street-node
        // lookup is not sufficient - candidates are validated before use (see
        // TryFindValidatedScenePosition).
        private const int MaxLocationAttempts = 20;

        // Vehicle node search radius/type used for GET_CLOSEST_VEHICLE_NODE_WITH_HEADING and
        // GET_NTH_CLOSEST_VEHICLE_NODE_ID. nodeType/nodeFlags 1 = "any dry path" (see
        // gtaforums.com/topic/843561-pathfind-node-types).
        private const int VehicleNodeType = 1;
        private const float VehicleNodeSearchRadius = 3.0f;

        // Bit 0 of the flags returned by GET_VEHICLE_NODE_PROPERTIES marks the node OffRoad
        // (matches Flags1 bit 0 in the underlying .ynd path node data).
        private const int OffRoadNodeFlagBit = 0x1;

        // Code 2 motor vehicle accident dispatch. IN_OR_ON_POSITION lets LSPDFR splice in its
        // own location/street audio using CalloutPosition. Code 3 phrasing exists in the same
        // scanner set for future serious-injury/multi-vehicle callouts - not used here.
        private const string ScannerAudioString = "WE_HAVE CRIME_MOTOR_VEHICLE_ACCIDENT_02 IN_OR_ON_POSITION RESPOND_CODE_2";

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
        private float _calloutHeading;
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
            if (!TryFindValidatedScenePosition(out _calloutPosition, out _calloutHeading))
            {
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision declined: no valid roadway location found");
                return false;
            }

            CalloutPosition = _calloutPosition;
            CalloutMessage = "Minor Traffic Collision";

            AddMinimumDistanceCheck(DispatchMinimumDistance, _calloutPosition);
            ShowCalloutAreaBlipBeforeAccepting(_calloutPosition, SceneBlipRadius);

            Functions.PlayScannerAudioUsingPosition(ScannerAudioString, CalloutPosition);

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

        // Generates candidate positions 300-700m from the player and validates each one
        // against GTA's PATHFIND road/node natives before accepting it, rejecting dirt
        // paths, service paths, off-road areas, and parking areas that a bare
        // World.GetNextPositionOnStreet/vehicle-node lookup would otherwise accept.
        private static bool TryFindValidatedScenePosition(out Vector3 position, out float heading)
        {
            Vector3 playerPosition = Game.LocalPlayer.Character.Position;
            var rng = new Random();

            for (int attempt = 1; attempt <= MaxLocationAttempts; attempt++)
            {
                float distance = MinCalloutDistance + (float)(rng.NextDouble() * (MaxCalloutDistance - MinCalloutDistance));
                float angleRadians = (float)(rng.NextDouble() * 2.0 * Math.PI);

                Vector3 roughOffset = new Vector3(
                    (float)Math.Sin(angleRadians) * distance,
                    (float)Math.Cos(angleRadians) * distance,
                    0f);

                if (TryValidateRoadwayCandidate(playerPosition + roughOffset, attempt, out Vector3 candidatePosition, out float candidateHeading))
                {
                    position = candidatePosition;
                    heading = candidateHeading;
                    return true;
                }
            }

            Game.LogTrivial($"RealPatrolCallouts: MinorTrafficCollision: No valid roadway location found after {MaxLocationAttempts} attempts");

            position = Vector3.Zero;
            heading = 0f;
            return false;
        }

        // Validates a single rough candidate position as a normal traffic-bearing roadway:
        // 1) snap to the closest vehicle-road node (with heading), 2) reject nodes GTA
        // itself reports as switched-off/off-road, 3) reject positions IS_POINT_ON_ROAD
        // disagrees with, 4) log node density/flags and reject only the case where the
        // node is both silent (density 0) and separately flagged off-road, so quiet
        // residential streets are not broken.
        private static bool TryValidateRoadwayCandidate(Vector3 roughPosition, int attempt, out Vector3 position, out float heading)
        {
            position = Vector3.Zero;
            heading = 0f;

            bool nodeFound;
            Vector3 nodePosition;
            float nodeHeading;
            try
            {
                nodeFound = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                    roughPosition.X, roughPosition.Y, roughPosition.Z,
                    out nodePosition, out nodeHeading,
                    VehicleNodeType, VehicleNodeSearchRadius, 0f);
            }
            catch (Exception)
            {
                nodeFound = false;
                nodePosition = Vector3.Zero;
                nodeHeading = 0f;
            }

            if (!nodeFound)
            {
                Game.LogTrivial($"RealPatrolCallouts: Accident location rejected: invalid vehicle node (attempt {attempt}/{MaxLocationAttempts})");
                return false;
            }

            int nodeId;
            try
            {
                nodeId = NativeFunction.Natives.GET_NTH_CLOSEST_VEHICLE_NODE_ID<int>(
                    nodePosition.X, nodePosition.Y, nodePosition.Z,
                    1, VehicleNodeType, VehicleNodeSearchRadius, 0f);
            }
            catch (Exception)
            {
                nodeId = 0;
            }

            if (nodeId == 0)
            {
                Game.LogTrivial($"RealPatrolCallouts: Accident location rejected: invalid vehicle node (attempt {attempt}/{MaxLocationAttempts})");
                return false;
            }

            bool switchedOff;
            try
            {
                switchedOff = NativeFunction.Natives.GET_VEHICLE_NODE_IS_SWITCHED_OFF<bool>(nodeId);
            }
            catch (Exception)
            {
                // If the native can't be resolved, fall through to the remaining checks
                // rather than silently accepting an unvalidated node.
                switchedOff = false;
            }

            if (switchedOff)
            {
                Game.LogTrivial($"RealPatrolCallouts: Accident location rejected: off-road vehicle node (attempt {attempt}/{MaxLocationAttempts})");
                return false;
            }

            bool onRoad;
            try
            {
                onRoad = NativeFunction.Natives.IS_POINT_ON_ROAD<bool>(
                    nodePosition.X, nodePosition.Y, nodePosition.Z, 0);
            }
            catch (Exception)
            {
                onRoad = false;
            }

            if (!onRoad)
            {
                Game.LogTrivial($"RealPatrolCallouts: Accident location rejected: not on road (attempt {attempt}/{MaxLocationAttempts})");
                return false;
            }

            int density = -1;
            int flags = 0;
            try
            {
                NativeFunction.Natives.GET_VEHICLE_NODE_PROPERTIES<bool>(
                    nodePosition.X, nodePosition.Y, nodePosition.Z,
                    out density, out flags);
            }
            catch (Exception)
            {
                density = -1;
                flags = 0;
            }

            bool flaggedOffRoad = (flags & OffRoadNodeFlagBit) != 0;
            if (density == 0 && flaggedOffRoad)
            {
                Game.LogTrivial($"RealPatrolCallouts: Accident location rejected: off-road vehicle node (attempt {attempt}/{MaxLocationAttempts})");
                return false;
            }

            Game.LogTrivial("RealPatrolCallouts: Accident road location accepted");
            Game.LogTrivial($"RealPatrolCallouts: Position: {nodePosition.X:F1}/{nodePosition.Y:F1}/{nodePosition.Z:F1}");
            Game.LogTrivial($"RealPatrolCallouts: Heading: {nodeHeading:F1}");
            Game.LogTrivial($"RealPatrolCallouts: Traffic density: {density}");
            Game.LogTrivial($"RealPatrolCallouts: Node flags: {flags}");

            position = nodePosition;
            heading = nodeHeading;
            return true;
        }

        private void SpawnScene()
        {
            // Heading comes from the validated road node found during location selection
            // (TryFindValidatedScenePosition), not a fresh lookup.
            float roadHeading = _calloutHeading;
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

        // ----- Stage 1: initial investigation (injury check + interview per driver) -----

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
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: All driver interviews completed");
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
            // InterviewCompleted is the only requirement for this driver's investigation
            // stage - it is never reset for the rest of the callout, so once this fires
            // the "speak with driver" interaction can never appear again for them.
            participant.InterviewCompleted = true;
            participant.DisplayName = PersonaHelper.GetDisplayName(participant.Driver);

            string dob = PersonaHelper.GetDateOfBirthText(participant.Driver);
            string idMessage = "Driver identified: " + participant.DisplayName;
            if (!string.IsNullOrEmpty(dob))
            {
                idMessage += " (DOB " + dob + ")";
            }

            Game.DisplayNotification("~b~" + idMessage);
            Game.LogTrivial($"RealPatrolCallouts: MinorTrafficCollision: Driver {participant.Number} conversation completed");
        }

        private bool AllInterviewsComplete()
        {
            foreach (AccidentParticipant participant in _participants)
            {
                if (!participant.InterviewCompleted)
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

            Game.DisplayNotification("~b~Return to your patrol vehicle to complete the crash report.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Photography completed");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Entered ReportPreparation state");

            _reportConfirmationTask = new CrashReportConfirmationTask(_responseVehicle);
        }

        // ----- Stage 3/4: report preparation (manual MDT/PDComp) and distribution -----

        private void BeginReportDistribution()
        {
            _stage = CalloutStage.ReportDistribution;
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Entered ReportDistribution state");

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
