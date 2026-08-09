using System;
using System.Collections.Generic;
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
        private const int DepartureTimeoutTicks = 900; // ~15s of GameFiber.Yield() ticks

        private class PendingDeparture
        {
            public Ped Driver;
            public Vehicle Vehicle;
            public int WaitTicks;
        }

        private readonly List<AccidentParticipant> _participants;
        private readonly List<Vehicle> _disabledVehicles = new List<Vehicle>();
        private readonly List<PendingDeparture> _pendingDepartures = new List<PendingDeparture>();
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

            CleanupPaperProp();
        }

        private void RunLoop()
        {
            while (_isActive && !IsComplete)
            {
                CheckForInteraction();
                ProcessPendingDepartures();

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

                    // seat -1 = driver seat; flag 1 = normal (non-warp) vehicle entry.
                    NativeFunction.Natives.TASK_ENTER_VEHICLE(driver, vehicle, -1, -1, 1.0f, 1, 0);
                    _pendingDepartures.Add(new PendingDeparture { Driver = driver, Vehicle = vehicle });
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
        /// Waits for a dismissed driver to finish getting into their vehicle before sending
        /// them off, so they never visibly warp in. Runs on the task's own GameFiber loop
        /// rather than a per-driver fiber, since that loop already ticks every frame.
        /// </summary>
        private void ProcessPendingDepartures()
        {
            for (int i = _pendingDepartures.Count - 1; i >= 0; i--)
            {
                PendingDeparture pending = _pendingDepartures[i];

                if (pending.Driver == null || !pending.Driver.Exists()
                    || pending.Vehicle == null || !pending.Vehicle.Exists())
                {
                    _pendingDepartures.RemoveAt(i);
                    continue;
                }

                if (pending.Driver.CurrentVehicle == pending.Vehicle)
                {
                    // 786603 is the standard "Normal" driving style used throughout GTA V
                    // scripting - obeys traffic laws, no reckless/rushed behavior.
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(pending.Driver, pending.Vehicle, DepartureSpeed, 786603);
                    _pendingDepartures.RemoveAt(i);
                    continue;
                }

                pending.WaitTicks++;
                if (pending.WaitTicks > DepartureTimeoutTicks)
                {
                    // Driver never finished entering the vehicle - leave them be rather than
                    // retrying indefinitely; the vehicle stays behind and is treated as one
                    // that still needs to be cleared/towed.
                    _disabledVehicles.Add(pending.Vehicle);
                    _pendingDepartures.RemoveAt(i);
                }
            }
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
