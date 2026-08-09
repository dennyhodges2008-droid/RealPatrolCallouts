using System;
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

        private enum ScenePhase
        {
            AwaitingArrival,
            PhotographingVehicle1,
            PhotographingVehicle2,
            PhotographsComplete
        }

        private Vector3 _calloutPosition;

        private Blip _sceneBlip;
        private Vehicle _vehicle1;
        private Vehicle _vehicle2;
        private Ped _driver1;
        private Ped _driver2;

        private VehiclePhotoTask _photoTask1;
        private VehiclePhotoTask _photoTask2;

        private DriverDialogueTask _dialogueTask1;
        private DriverDialogueTask _dialogueTask2;

        private ScenePhase _phase;
        private bool _hasArrived;

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

            _sceneBlip = new Blip(_calloutPosition)
            {
                Color = Color.Red
            };
            _sceneBlip.EnableRoute(Color.Yellow);

            SpawnScene();
            _phase = ScenePhase.AwaitingArrival;

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

            if (!_hasArrived)
            {
                CheckForArrival();
                return;
            }

            bool activePhotoMarkerInRange = false;

            // VehiclePhotoTask renders its marker and handles its own T-press interaction
            // on a dedicated GameFiber, so it no longer needs a per-tick Process() call here -
            // only its resulting state is read.
            switch (_phase)
            {
                case ScenePhase.PhotographingVehicle1:
                    activePhotoMarkerInRange = _photoTask1.IsPlayerInActiveMarkerRange;
                    if (_photoTask1.IsComplete)
                    {
                        StartVehicle2Photos();
                    }
                    break;

                case ScenePhase.PhotographingVehicle2:
                    activePhotoMarkerInRange = _photoTask2.IsPlayerInActiveMarkerRange;
                    if (_photoTask2.IsComplete)
                    {
                        CompletePhotographs();
                    }
                    break;
            }

            // Driver dialogue shares the T key with the photo task, so it must stay
            // silent while the player is standing inside an active photo marker.
            _dialogueTask1.Process(activePhotoMarkerInRange);
            _dialogueTask2.Process(activePhotoMarkerInRange);
        }

        public override void End()
        {
            _photoTask1?.Stop();
            _photoTask2?.Stop();

            if (_sceneBlip != null && _sceneBlip.Exists())
            {
                _sceneBlip.Delete();
            }

            DismissPed(_driver1);
            DismissPed(_driver2);
            DismissVehicle(_vehicle1);
            DismissVehicle(_vehicle2);

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

            _driver1 = SpawnStandingDriver("a_m_y_business_01", _vehicle1, new Vector3(-2.5f, 0f, 0f));
            _driver2 = SpawnStandingDriver("a_f_y_business_02", _vehicle2, new Vector3(2.5f, 0f, 0f));

            _photoTask1 = new VehiclePhotoTask(_vehicle1, "Vehicle 1");
            _photoTask2 = new VehiclePhotoTask(_vehicle2, "Vehicle 2");

            _dialogueTask1 = new DriverDialogueTask(
                _driver1,
                "Can you tell me what happened?",
                "I was driving through here when the other vehicle hit me. Nobody is hurt.");

            _dialogueTask2 = new DriverDialogueTask(
                _driver2,
                "Can you tell me what happened?",
                "We collided. I am okay, and I do not think anyone is injured.");
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

            _hasArrived = true;

            if (_sceneBlip != null && _sceneBlip.Exists())
            {
                _sceneBlip.IsRouteEnabled = false;
            }

            Game.DisplayHelp("The vehicles at the scene need to be photographed.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision player arrived at scene");

            StartVehicle1Photos();
        }

        private void StartVehicle1Photos()
        {
            _phase = ScenePhase.PhotographingVehicle1;
            _photoTask1.Start();
        }

        private void StartVehicle2Photos()
        {
            _phase = ScenePhase.PhotographingVehicle2;
            _photoTask2.Start();
        }

        private void CompletePhotographs()
        {
            _phase = ScenePhase.PhotographsComplete;

            Game.DisplayNotification("Accident scene photographs complete.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision scene photographs complete");
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
