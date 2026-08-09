using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Per-driver state for the investigation -&gt; report -&gt; distribution -&gt; clearance
    /// workflow. One of these exists per involved driver, so the workflow scales to
    /// future 2/3/4-car accident callouts without hard-coding around exactly two people.
    /// </summary>
    public class AccidentParticipant
    {
        /// <summary>1-based index used only for logging/UI (e.g. "Driver 1").</summary>
        public int Number { get; }

        public Ped Driver { get; }

        public Vehicle Vehicle { get; }

        public DriverDialogueTask InterviewTask { get; }

        /// <summary>
        /// The only requirement for this participant's investigation stage. Set once their
        /// scripted interview dialogue finishes; nothing else (no separate ID/ped check) gates
        /// progression, and this never resets for the rest of the callout.
        /// </summary>
        public bool InterviewCompleted { get; set; }

        public bool ReportGiven { get; set; }

        public bool Dismissed { get; set; }

        /// <summary>Name pulled from the ped's LSPDFR persona once the ID has been collected.</summary>
        public string DisplayName { get; set; } = "Unknown Driver";

        public AccidentParticipant(int number, Ped driver, Vehicle vehicle, DriverDialogueTask interviewTask)
        {
            Number = number;
            Driver = driver;
            Vehicle = vehicle;
            InterviewTask = interviewTask;
        }
    }
}
