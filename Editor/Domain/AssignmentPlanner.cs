using System;
using System.Collections.Generic;
using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    public sealed class WindowAssignmentTarget
    {
        public WindowKey WindowKey { get; }
        public SelectionSnapshot TargetSelection { get; }
        public AssignmentLabel Label { get; }
        public bool KeepCurrentDisplay { get; }

        public WindowAssignmentTarget(
            WindowKey windowKey,
            SelectionSnapshot targetSelection,
            AssignmentLabel label,
            bool keepCurrentDisplay)
        {
            WindowKey = windowKey;
            TargetSelection = targetSelection;
            Label = label;
            KeepCurrentDisplay = keepCurrentDisplay;
        }
    }

    public sealed class AssignmentPlan
    {
        public string PlanId { get; }
        public long Generation { get; }
        public long Sequence { get; }
        public RotationHistoryState CandidateRotationHistory { get; }
        public IReadOnlyList<WindowAssignmentTarget> Targets { get; }

        public bool HasWriteOperations => Targets.Any(t => !t.KeepCurrentDisplay && t.TargetSelection != null && !t.TargetSelection.IsEmpty);

        public AssignmentPlan(
            string planId,
            long generation,
            long sequence,
            RotationHistoryState candidateRotationHistory,
            IReadOnlyList<WindowAssignmentTarget> targets)
        {
            PlanId = planId ?? string.Empty;
            Generation = generation;
            Sequence = sequence;
            CandidateRotationHistory = candidateRotationHistory ?? RotationHistoryState.Empty;
            Targets = targets ?? Array.Empty<WindowAssignmentTarget>();
        }
    }

    /// <summary>
    /// Pure deterministic History-mode assignment planner.
    /// Assigns participating Inspectors in RoleOrder to distinct MRU selections;
    /// insufficient slots preserve their current display and carry AssignmentLabel.HistoryUnassigned.
    /// </summary>
    public static class AssignmentPlanner
    {
        public static AssignmentPlan CreateHistoryPlan(
            string planId,
            long generation,
            long sequence,
            IReadOnlyList<WindowRecord> participatingOrdered,
            RotationHistoryState currentRotationHistory,
            SelectionSnapshot incomingSelection,
            Func<SelectionSnapshot, bool> isEligibleWholeSet,
            int configuredMaxRotationHistory)
        {
            int effectiveCapacity = Math.Max(configuredMaxRotationHistory, participatingOrdered.Count);
            if (effectiveCapacity < 1) effectiveCapacity = 1;

            RotationHistoryState candidateHistory = currentRotationHistory ?? RotationHistoryState.Empty;
            if (incomingSelection != null && !incomingSelection.IsEmpty)
            {
                candidateHistory = candidateHistory.PushFrontDeduplicated(incomingSelection, false, effectiveCapacity);
            }

            // Prune invalid selection sets as whole sets without shrinking multi-selections.
            candidateHistory = candidateHistory.PruneInvalid(isEligibleWholeSet, effectiveCapacity);

            var targets = new List<WindowAssignmentTarget>(participatingOrdered.Count);
            for (int i = 0; i < participatingOrdered.Count; i++)
            {
                var window = participatingOrdered[i];
                if (i < candidateHistory.Entries.Count)
                {
                    var entry = candidateHistory.Entries[i];
                    var label = entry.IsInitialDisplay ? AssignmentLabel.InitialDisplay : AssignmentLabel.Assigned;
                    targets.Add(new WindowAssignmentTarget(
                        window.Key,
                        entry.Snapshot,
                        label,
                        keepCurrentDisplay: false));
                }
                else
                {
                    targets.Add(new WindowAssignmentTarget(
                        window.Key,
                        window.AssignedSelection,
                        AssignmentLabel.HistoryUnassigned,
                        keepCurrentDisplay: true));
                }
            }

            return new AssignmentPlan(
                planId,
                generation,
                sequence,
                candidateHistory,
                targets);
        }
    }
}
