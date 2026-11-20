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
        public bool AdvanceCycleSequence { get; }
        public bool KeepCurrentDisplay { get; }

        public WindowAssignmentTarget(
            WindowKey windowKey,
            SelectionSnapshot targetSelection,
            AssignmentLabel label,
            bool advanceCycleSequence,
            bool keepCurrentDisplay)
        {
            WindowKey = windowKey;
            TargetSelection = targetSelection;
            Label = label;
            AdvanceCycleSequence = advanceCycleSequence;
            KeepCurrentDisplay = keepCurrentDisplay;
        }
    }

    public sealed class AssignmentPlan
    {
        public string PlanId { get; }
        public long Generation { get; }
        public long Sequence { get; }
        public RotationMode Mode { get; }
        public RotationHistoryState CandidateRotationHistory { get; }
        public IReadOnlyList<WindowAssignmentTarget> Targets { get; }
        public WindowKey? FocusCandidateWindowKey { get; }
        public bool IsNavigationUpdate { get; }

        public bool HasWriteOperations => Targets.Any(t => !t.KeepCurrentDisplay && t.TargetSelection != null && !t.TargetSelection.IsEmpty);

        public AssignmentPlan(
            string planId,
            long generation,
            long sequence,
            RotationMode mode,
            RotationHistoryState candidateRotationHistory,
            IReadOnlyList<WindowAssignmentTarget> targets,
            WindowKey? focusCandidateWindowKey,
            bool isNavigationUpdate)
        {
            PlanId = planId ?? string.Empty;
            Generation = generation;
            Sequence = sequence;
            Mode = mode;
            CandidateRotationHistory = candidateRotationHistory ?? RotationHistoryState.Empty;
            Targets = targets ?? Array.Empty<WindowAssignmentTarget>();
            FocusCandidateWindowKey = focusCandidateWindowKey;
            IsNavigationUpdate = isNavigationUpdate;
        }
    }

    /// <summary>
    /// Pure deterministic assignment planner for History mode (Section 5.3) and Cycle mode (Section 5.4).
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
            int configuredMaxRotationHistory,
            bool isNavigationUpdate)
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
            WindowKey? focusKey = null;

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
                        advanceCycleSequence: false,
                        keepCurrentDisplay: false));

                    if (!focusKey.HasValue)
                    {
                        focusKey = window.Key;
                    }
                }
                else
                {
                    // Section 5.3: Slots without enough history keep their current display and show HistoryUnassigned.
                    targets.Add(new WindowAssignmentTarget(
                        window.Key,
                        window.AssignedSelection,
                        AssignmentLabel.HistoryUnassigned,
                        advanceCycleSequence: false,
                        keepCurrentDisplay: true));
                }
            }

            return new AssignmentPlan(
                planId,
                generation,
                sequence,
                RotationMode.History,
                candidateHistory,
                targets,
                focusKey,
                isNavigationUpdate);
        }

        public static AssignmentPlan CreateCyclePlan(
            string planId,
            long generation,
            long sequence,
            IReadOnlyList<WindowRecord> participatingOrdered,
            RotationHistoryState currentRotationHistory,
            SelectionSnapshot incomingSelection,
            bool isNavigationUpdate)
        {
            if (participatingOrdered.Count == 0 || incomingSelection == null || incomingSelection.IsEmpty)
            {
                return new AssignmentPlan(
                    planId,
                    generation,
                    sequence,
                    RotationMode.Cycle,
                    currentRotationHistory ?? RotationHistoryState.Empty,
                    Array.Empty<WindowAssignmentTarget>(),
                    null,
                    isNavigationUpdate);
            }

            // Section 5.4: Pick participating Inspector with minimum LastSuccessSequence (0 = never updated first),
            // breaking ties deterministically by RoleOrder ascending (then FixedDisplayNumber).
            WindowRecord chosen = participatingOrdered
                .OrderBy(w => w.LastSuccessSequence)
                .ThenBy(w => w.RoleOrder)
                .ThenBy(w => w.FixedDisplayNumber)
                .First();

            var target = new WindowAssignmentTarget(
                chosen.Key,
                incomingSelection,
                AssignmentLabel.Assigned,
                // Section 5.4: Back/Forward updates current candidate display WITHOUT advancing cycle order.
                advanceCycleSequence: !isNavigationUpdate,
                keepCurrentDisplay: false);

            return new AssignmentPlan(
                planId,
                generation,
                sequence,
                RotationMode.Cycle,
                currentRotationHistory ?? RotationHistoryState.Empty,
                new[] { target },
                chosen.Key,
                isNavigationUpdate);
        }
    }
}
