using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Owns the "give crash report" / "dismiss driver" T-press interactions for every
    /// involved driver once the crash report has been confirmed complete. The brief report
    /// hand-off reuses the same safe create/attach/detach/delete prop pattern as
    /// ScenePhotoTask's camera prop, and runs on its own GameFiber (also like
    /// ScenePhotoTask) so the short hold never blocks the main callout tick.
    ///
    /// Once dismissed, a driver whose vehicle is still driveable is sent off normally;
    /// a driver whose vehicle is disabled leaves on foot and the vehicle is recorded here
    /// so the callout's tow/clearance stage knows what still needs to be cleared.
    /// </summary>
    public class ReportDistributionTask
    {
        private const float ApproachRadius = 2.0f;
        private const int HandoffHoldMs = 900;
        private const string PaperPropModel = "prop_notepad_01";
        private const int HandBoneId = 28422; // same right-hand bone id ScenePhotoTask uses for the camera prop
        private const float DepartureSpeed = 15.0f;
        private const int NormalDrivingStyle = 786603; // "Normal" - obeys traffic laws, no reckless/rushed behavior
        private const int DriverDoorIndex = 0;
        private const int VehicleEntryTimeoutMs = 15000;
        private const int DoorCloseDelayMs = 650; // lets the normal get-in animation close the door before the fallback fires

        private readonly List<AccidentParticipant> _participants;
        private readonly List<Vehicle> _disabledVehicles = new List<Vehicle>();
        private readonly List<GameFiber> _departureFibers = new List<GameFiber>();
        private readonly Keys _interactionKey;

        private bool _isActive;
        private bool _keyWasDown;
        private bool _isInteracting;
        private GameFiber _fiber;
        private Rage.Object _paperProp;

        public ReportDistributionTask(List<AccidentParticipant> participants, Keys interactionKey = Keys.T)
        {
            _participants = participants;
            _interactionKey = interactionKey;
        }

        /// <summary>Vehicles left behind by dismissed drivers because they weren't driveable.</summary>
        public IReadOnlyList<Vehicle> DisabledVehicles => _disabledVehicles;

        public bool IsComplete
        {
            get
            {
                foreach (AccidentParticipant participant in _participants)
                {
                    if (!participant.ReportGiven || !participant.Dismissed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Start()
        {
            _isActive = true;
            // Seed from the live key state rather than assuming it's up - T may still be
            // held from the press that completed the crash report in the previous stage,
            // and that must not be able to cascade into an immediate "give report".
            _keyWasDown = Game.IsKeyDown(_interactionKey);
            _fiber = GameFiber.StartNew(RunLoop, "ReportDistributionTask");
        }

        /// <summary>Stops the task's GameFiber and guarantees the paper prop is removed.</summary>
        public void Stop()
        {
            _isActive = false;

            if (_fiber != null && _fiber.IsAlive)
            {
                _fiber.Abort();
            }

            _fiber = null;

            foreach (GameFiber departureFiber in _departureFibers)
            {
                if (departureFiber != null && departureFiber.IsAlive)
                {
                    departureFiber.Abort();
                }
            }

            _departureFibers.Clear();

            CleanupPaperProp();
        }

        private void RunLoop()
        {
            while (_isActive && !IsComplete)
            {
                CheckForInteraction();

                GameFiber.Yield();
            }
        }

        private void CheckForInteraction()
        {
            if (_isInteracting)
            {
                return;
            }

            Ped player = Game.LocalPlayer.Character;
            AccidentParticipant nearest = FindNearestPendingParticipant(player.Position);

            bool keyDown = Game.IsKeyDown(_interactionKey);
            bool keyJustPressed = keyDown && !_keyWasDown;
            _keyWasDown = keyDown;

            if (nearest == null)
            {
                return;
            }

            if (!nearest.ReportGiven)
            {
                Game.DisplayHelp("Press " + _interactionKey + " to give crash report");

                if (keyJustPressed)
                {
                    GiveReport(nearest);
                }

                return;
            }

            if (!nearest.Dismissed)
            {
                Game.DisplayHelp("Press " + _interactionKey + " to dismiss driver");

                if (keyJustPressed)
                {
                    DismissDriver(nearest);
                }
            }
        }

        /// <summary>Only the nearest participant in range owns T, so two drivers standing close together can never both react to one press.</summary>
        private AccidentParticipant FindNearestPendingParticipant(Vector3 playerPosition)
        {
            AccidentParticipant nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (AccidentParticipant participant in _participants)
            {
                if (participant.Dismissed)
                {
                    continue;
                }

                if (participant.Driver == null || !participant.Driver.Exists())
                {
                    continue;
                }

                float distance = playerPosition.DistanceTo(participant.Driver.Position);
                if (distance <= ApproachRadius && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = participant;
                }
            }

            return nearest;
        }

        private void GiveReport(AccidentParticipant participant)
        {
            _isInteracting = true;

            PlayHandoff();

            participant.ReportGiven = true;
            _isInteracting = false;

            Game.DisplayHelp("Crash report provided.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Report given to Driver " + participant.Number);
        }

        private void PlayHandoff()
        {
            Ped player = Game.LocalPlayer.Character;

            try
            {
                CreateAndAttachPaperProp(player);
                GameFiber.Sleep(HandoffHoldMs);
            }
            catch (Exception)
            {
                // The paper prop is cosmetic only - a model/bone lookup failure must never
                // block the actual report hand-off from completing.
            }
            finally
            {
                CleanupPaperProp();
            }
        }

        private void CreateAndAttachPaperProp(Ped player)
        {
            _paperProp = new Rage.Object(PaperPropModel, player.Position);

            int boneIndex = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(player, HandBoneId);
            _paperProp.AttachTo(player, boneIndex, Vector3.Zero, new Rotator(0f, 0f, 0f));
        }

        /// <summary>Detaches and deletes the paper prop. Safe to call repeatedly/redundantly.</summary>
        private void CleanupPaperProp()
        {
            if (_paperProp == null)
            {
                return;
            }

            try
            {
                if (_paperProp.Exists())
                {
                    _paperProp.Detach();
                    _paperProp.Delete();
                }
            }
            finally
            {
                _paperProp = null;
            }
        }

        private void DismissDriver(AccidentParticipant participant)
        {
            participant.Dismissed = true;

            Ped driver = participant.Driver;
            Vehicle vehicle = participant.Vehicle;

            if (driver != null && driver.Exists())
            {
                driver.Tasks.ClearImmediately();

                if (IsVehicleDriveable(vehicle))
                {
                    vehicle.IsPositionFrozen = false;

                    // Runs on its own GameFiber, independent of this task's RunLoop - that loop's
                    // IsComplete flips true the instant the last driver is marked Dismissed, which
                    // used to cut a still-entering driver's departure short. A dedicated fiber per
                    // participant can't be raced like that, and it's identical for every driver.
                    GameFiber departureFiber = GameFiber.StartNew(
                        () => DepartInVehicle(participant),
                        "AccidentDeparture_Driver" + participant.Number);
                    _departureFibers.Add(departureFiber);
                }
                else
                {
                    if (vehicle != null && vehicle.Exists())
                    {
                        _disabledVehicles.Add(vehicle);
                    }

                    driver.Dismiss();
                }
            }

            Game.DisplayHelp("Driver dismissed.");
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: Driver " + participant.Number + " dismissed");
        }

        /// <summary>
        /// Common departure routine used identically for every driveable participant: enter
        /// the vehicle, wait until actually seated, let/force the door shut, start the engine,
        /// then hand off to the normal civilian driving task. Never called for a participant
        /// whose vehicle was rolled disabled - those drivers leave on foot via DismissDriver
        /// and the vehicle is left for towing.
        /// </summary>
        private void DepartInVehicle(AccidentParticipant participant)
        {
            Ped driver = participant.Driver;
            Vehicle vehicle = participant.Vehicle;
            string label = "Driver " + participant.Number;

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": starting vehicle departure");

            if (driver == null || !driver.Exists() || vehicle == null || !vehicle.Exists())
            {
                return;
            }

            // seat -1 = driver seat; flag 1 = normal (non-warp) vehicle entry.
            NativeFunction.Natives.TASK_ENTER_VEHICLE(driver, vehicle, -1, -1, 1.0f, 1, 0);
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": entering vehicle");

            if (!WaitUntilSeated(driver, vehicle, VehicleEntryTimeoutMs))
            {
                Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": vehicle entry timed out");
                if (vehicle.Exists())
                {
                    _disabledVehicles.Add(vehicle);
                }

                return;
            }

            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": seated in vehicle");

            // Let GTA's own get-in animation close the door naturally before falling back to
            // forcing it shut - SET_VEHICLE_DOOR_SHUT is a no-op if it's already closed.
            GameFiber.Sleep(DoorCloseDelayMs);

            if (!driver.Exists() || !vehicle.Exists())
            {
                return;
            }

            NativeFunction.Natives.SET_VEHICLE_DOOR_SHUT(vehicle, DriverDoorIndex, false);
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": vehicle door closed");

            vehicle.IsEngineOn = true;

            if (!driver.Exists() || !vehicle.Exists())
            {
                return;
            }

            NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(driver, vehicle, DepartureSpeed, NormalDrivingStyle);
            Game.LogTrivial("RealPatrolCallouts: MinorTrafficCollision: " + label + ": driving task started");
        }

        /// <summary>
        /// Polls until the driver is actually seated, not just mid-entry, or the timeout
        /// elapses. IsInVehicle(vehicle, false) is required rather than the true overload,
        /// since true can report the ped as "in" the vehicle while still in the process of
        /// climbing in - issuing the driving task on that signal is what let the departure
        /// race ahead of the entry animation.
        /// </summary>
        private static bool WaitUntilSeated(Ped driver, Vehicle vehicle, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (!driver.Exists() || !vehicle.Exists())
                {
                    return false;
                }

                if (driver.IsInVehicle(vehicle, false))
                {
                    return true;
                }

                GameFiber.Yield();
            }

            return false;
        }

        private static bool IsVehicleDriveable(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
            {
                return false;
            }

            try
            {
                return NativeFunction.Natives.IS_VEHICLE_DRIVEABLE<bool>(vehicle, false);
            }
            catch (Exception)
            {
                return vehicle.EngineHealth > 0f && !vehicle.IsDead;
            }
        }
    }
}
