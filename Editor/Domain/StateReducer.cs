using System;
using System.Collections.Generic;
using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    /// <summary>
    /// Pure deterministic state reducer for History-mode Inspector management.
    /// Zero Unity API, clock, I/O, or UI dependencies.
    /// </summary>
    public static class StateReducer
    {
        public static ReduceResult Reduce(ManagerState state, DomainEvent evt)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (evt == null) return new ReduceResult(state);

            bool isGenerationAgnostic = evt is CapabilitiesProbedEvent
                || evt is WindowsSynchronizedEvent
                || evt is SettingsUpdatedEvent
                || evt is StatusNoticeEvent
                || evt is ResetLayoutEvent;

            if (!isGenerationAgnostic && evt.Generation != 0 && evt.Generation != state.Generation)
            {
                return new ReduceResult(state);
            }

            switch (evt)
            {
                case CapabilitiesProbedEvent e:
                    return ReduceCapabilitiesProbed(state, e);

                case WindowsSynchronizedEvent e:
                    return ReduceWindowsSynchronized(state, e);

                case StartSessionRequestedEvent _:
                    return ReduceStartRequested(state);

                case StartSessionCompletedEvent e:
                    return ReduceStartCompleted(state, e);

                case StopSessionRequestedEvent e:
                    return ReduceStopRequested(state, e);

                case StopSessionCompletedEvent e:
                    return ReduceStopCompleted(state, e);

                case RetryRestoreCompletedEvent e:
                    return ReduceRetryRestoreCompleted(state, e);

                case SelectionReceivedEvent e:
                    return ReduceSelectionReceived(state, e);

                case PlanCommittedEvent e:
                    return ReducePlanCommitted(state, e);

                case PlanFailedEvent e:
                    return ReducePlanFailed(state, e);

                case ManualLockChangedEvent e:
                    return ReduceManualLockChanged(state, e);

                case ExplicitExclusionChangedEvent e:
                    return ReduceExplicitExclusionChanged(state, e);

                case WindowAdoptedEvent e:
                    return ReduceWindowAdopted(state, e);

                case WindowReorderedEvent e:
                    return ReduceWindowReordered(state, e);

                case WindowClosedEvent e:
                    return ReduceWindowClosed(state, e);

                case SettingsUpdatedEvent e:
                    return ReduceSettingsUpdated(state, e);

                case ResetLayoutEvent _:
                    return ReduceResetLayout(state);

                case StatusNoticeEvent e:
                    return new ReduceResult(state.With(
                        statusNotice: e.Notice ?? state.StatusNotice,
                        lastError: e.Error ?? state.LastError,
                        persistenceWriteBlocked: e.PersistenceBlocked ?? state.PersistenceWriteBlocked));

                case UnknownLockDropDetectedEvent e:
                    return ReduceUnknownLockDrop(state, e);

                default:
                    return new ReduceResult(state);
            }
        }

        private static ReduceResult ReduceCapabilitiesProbed(ManagerState state, CapabilitiesProbedEvent e)
        {
            string notice = e.Capabilities.SupportsRotation
                ? state.StatusNotice
                : "Rotation disabled: required Unity Inspector capabilities unavailable.";

            return new ReduceResult(state.With(
                capabilities: e.Capabilities,
                statusNotice: notice));
        }

        private static ReduceResult ReduceWindowsSynchronized(ManagerState state, WindowsSynchronizedEvent e)
        {
            var observedByKey = new Dictionary<WindowKey, ObservedWindowSnapshot>();
            for (int i = 0; i < e.ObservedWindows.Count; i++)
            {
                observedByKey[e.ObservedWindows[i].Key] = e.ObservedWindows[i];
            }

            var updatedWindows = new List<WindowRecord>();
            var updatedLeases = new Dictionary<WindowKey, LockLease>(state.Leases);
            var existingKeys = new HashSet<WindowKey>();
            int nextNumber = state.NextFixedDisplayNumber;
            int maxRoleOrder = -1;

            for (int i = 0; i < state.Windows.Count; i++)
            {
                var existing = state.Windows[i];
                existingKeys.Add(existing.Key);
                if (existing.RoleOrder > maxRoleOrder)
                {
                    maxRoleOrder = existing.RoleOrder;
                }

                if (observedByKey.TryGetValue(existing.Key, out var observed))
                {
                    updatedWindows.Add(existing.With(
                        isAlive: true,
                        observedLocked: observed.IsLockReadable ? observed.ObservedLocked : existing.ObservedLocked,
                        observedRepresentativeName: observed.RepresentativeName,
                        identityEvidence: string.IsNullOrEmpty(observed.IdentityEvidence) ? existing.IdentityEvidence : observed.IdentityEvidence));
                }
                else if (e.IsReloadGraceActive)
                {
                    updatedWindows.Add(existing);
                }
                else
                {
                    if (updatedLeases.TryGetValue(existing.Key, out var lease) && lease.RestorationStatus == LeaseRestorationStatus.Acquired)
                    {
                        updatedLeases[existing.Key] = lease.WithRestoration(LeaseRestorationStatus.WindowDisposed);
                    }
                }
            }

            bool hasManagedAlready = updatedWindows.Any(w => w.IsManaged && w.IsAlive);
            for (int i = 0; i < e.ObservedWindows.Count; i++)
            {
                var obs = e.ObservedWindows[i];
                if (existingKeys.Contains(obs.Key))
                {
                    continue;
                }

                bool autoManageOnBootstrap = state.Windows.Count == 0 && !hasManagedAlready && state.LifecycleState == RuntimeLifecycleState.Off;
                int assignedNumber = nextNumber++;
                int assignedRole = ++maxRoleOrder;

                updatedWindows.Add(new WindowRecord(
                    key: obs.Key,
                    fixedDisplayNumber: assignedNumber,
                    roleOrder: assignedRole,
                    isManaged: autoManageOnBootstrap,
                    isAlive: true,
                    isExplicitlyExcluded: false,
                    isManuallyUnlocked: false,
                    isFaultIsolated: false,
                    faultReason: string.Empty,
                    observedLocked: obs.IsLockReadable && obs.ObservedLocked,
                    assignedSelection: obs.ObservedLockedSelection,
                    assignmentLabel: obs.ObservedLockedSelection != null && !obs.ObservedLockedSelection.IsEmpty
                        ? AssignmentLabel.InitialDisplay
                        : AssignmentLabel.None,
                    entityGeneration: 1,
                    observedRepresentativeName: obs.RepresentativeName,
                    identityEvidence: obs.IdentityEvidence));
            }

            var normalized = NormalizeRoleOrders(updatedWindows);
            var nextState = state.With(
                windows: normalized,
                nextFixedDisplayNumber: nextNumber,
                leases: updatedLeases);

            return new ReduceResult(nextState);
        }

        private static ReduceResult ReduceStartRequested(ManagerState state)
        {
            if (state.LifecycleState != RuntimeLifecycleState.Off && state.LifecycleState != RuntimeLifecycleState.Suspended)
            {
                return new ReduceResult(state);
            }

            if (!state.Capabilities.SupportsRotation)
            {
                return new ReduceResult(state.With(
                    lastError: "Cannot start rotation: required Inspector adapter capabilities are missing.",
                    statusNotice: "Rotation unavailable."));
            }

            // Clear session-scoped manual unlock and fault isolation from previous session.
            var freshWindows = state.Windows.Select(w => w.IsManuallyUnlocked || w.IsFaultIsolated
                ? w.With(isManuallyUnlocked: false, isFaultIsolated: false, faultReason: string.Empty)
                : w).ToArray();

            return new ReduceResult(state.With(
                lifecycleState: RuntimeLifecycleState.Starting,
                windows: freshWindows,
                generation: state.Generation + 1,
                lastError: string.Empty,
                statusNotice: "Starting rotation...",
                clearLastStopReason: true));
        }

        private static ReduceResult ReduceStartCompleted(ManagerState state, StartSessionCompletedEvent e)
        {
            if (state.LifecycleState != RuntimeLifecycleState.Starting)
            {
                return new ReduceResult(state);
            }

            if (!e.Succeeded)
            {
                var failLifecycle = e.RestorationIncomplete ? RuntimeLifecycleState.Faulted : RuntimeLifecycleState.Off;
                return new ReduceResult(state.With(
                    lifecycleState: failLifecycle,
                    generation: state.Generation + 1,
                    leases: e.Leases ?? state.Leases,
                    lastError: e.ErrorMessage,
                    statusNotice: e.RestorationIncomplete ? "Start failed with incomplete lock restoration." : "Start failed; locks restored."));
            }

            var syncedWindows = state.Windows.Select(w => w.With(
                observedLocked: e.Leases != null && e.Leases.TryGetValue(w.Key, out var lease) && lease.HasWritten
                    ? lease.LastWrittenLocked
                    : w.ObservedLocked)).ToArray();

            var nextState = state.With(
                lifecycleState: RuntimeLifecycleState.Running,
                windows: syncedWindows,
                leases: e.Leases ?? state.Leases,
                rotationHistory: e.SeededRotationHistory ?? RotationHistoryState.Empty,
                lastError: string.Empty,
                statusNotice: syncedWindows.Any(w => w.CanParticipate) ? "Running" : "No participating Inspectors.");

            var effects = new List<ReducerEffect>
            {
                new RequestPersistSessionLayoutEffect(),
                new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false)
            };

            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceStopRequested(ManagerState state, StopSessionRequestedEvent e)
        {
            if (state.LifecycleState == RuntimeLifecycleState.Off && !state.HasIncompleteRestorations)
            {
                return new ReduceResult(state.With(
                    lifecycleState: e.SuspendMode ? RuntimeLifecycleState.Suspended : RuntimeLifecycleState.Off,
                    generation: state.Generation + 1,
                    lastStopReason: e.Reason));
            }

            var nextLifecycle = e.SuspendMode ? RuntimeLifecycleState.Suspended : RuntimeLifecycleState.Stopping;
            return new ReduceResult(state.With(
                lifecycleState: nextLifecycle,
                generation: state.Generation + 1,
                lastStopReason: e.Reason,
                statusNotice: e.SuspendMode ? "Suspended" : "Stopping..."));
        }

        private static ReduceResult ReduceStopCompleted(ManagerState state, StopSessionCompletedEvent e)
        {
            // Clear session-only RotationHistory, but PRESERVE explicit exclusions, role order, and fixed numbers.
            var cleanedWindows = state.Windows.Select(w => w.With(
                observedLocked: e.ForceUnlocked && w.IsManaged && w.IsAlive
                    ? false
                    : (e.UpdatedLeases != null && e.UpdatedLeases.TryGetValue(w.Key, out var lease)
                        && lease.RestorationStatus == LeaseRestorationStatus.Restored
                        ? lease.OriginalLocked
                        : w.ObservedLocked))).ToArray();

            RuntimeLifecycleState finalLifecycle;
            if (!e.AllRestored)
            {
                finalLifecycle = RuntimeLifecycleState.Faulted;
            }
            else
            {
                finalLifecycle = e.RemainSuspended ? RuntimeLifecycleState.Suspended : RuntimeLifecycleState.Off;
            }

            return new ReduceResult(state.With(
                lifecycleState: finalLifecycle,
                generation: state.Generation + 1,
                windows: cleanedWindows,
                leases: e.AllRestored ? new Dictionary<WindowKey, LockLease>() : (e.UpdatedLeases ?? state.Leases),
                rotationHistory: RotationHistoryState.Empty,
                lastError: e.AllRestored ? string.Empty : e.ErrorMessage,
                statusNotice: e.AllRestored ? (e.RemainSuspended ? "Suspended" : "Off") : "Lock restoration incomplete."));
        }

        private static ReduceResult ReduceRetryRestoreCompleted(ManagerState state, RetryRestoreCompletedEvent e)
        {
            var cleanedWindows = state.Windows.Select(w => w.With(
                observedLocked: e.UpdatedLeases != null && e.UpdatedLeases.TryGetValue(w.Key, out var lease)
                    && lease.RestorationStatus == LeaseRestorationStatus.Restored
                    ? lease.OriginalLocked
                    : w.ObservedLocked)).ToArray();

            return new ReduceResult(state.With(
                lifecycleState: e.AllRestored ? RuntimeLifecycleState.Off : RuntimeLifecycleState.Faulted,
                windows: cleanedWindows,
                leases: e.AllRestored ? new Dictionary<WindowKey, LockLease>() : (e.UpdatedLeases ?? state.Leases),
                lastError: e.AllRestored ? string.Empty : e.ErrorMessage,
                statusNotice: e.AllRestored ? "Locks restored (Off)." : "Lock restoration still incomplete."));
        }

        private static ReduceResult ReduceSelectionReceived(ManagerState state, SelectionReceivedEvent e)
        {
            var incoming = e.Snapshot ?? SelectionSnapshot.Empty;
            var incomingKey = incoming.ComparisonKey;

            if (incomingKey == state.LastAcceptedSelectionKey)
            {
                return new ReduceResult(state);
            }

            // Empty selection keeps current assignments, but updates LastAcceptedSelectionKey to Empty so A -> Empty -> A is accepted.
            if (incoming.IsEmpty)
            {
                return new ReduceResult(state.With(
                    lastAcceptedSelectionKey: SelectionComparisonKey.Empty,
                    currentLiveSelection: SelectionSnapshot.Empty));
            }

            var nextState = state.With(
                lastAcceptedSelectionKey: incomingKey,
                currentLiveSelection: incoming.WithSource(SelectionSource.UserSelection));

            if (nextState.LifecycleState != RuntimeLifecycleState.Running)
            {
                return new ReduceResult(nextState);
            }

            var filterResult = SelectionFilter.Evaluate(nextState.CurrentLiveSelection, nextState.Settings);
            if (!filterResult.IsAllowed)
            {
                return new ReduceResult(nextState.With(statusNotice: filterResult.Reason));
            }

            if (nextState.GetParticipatingWindowsOrdered().Count == 0)
            {
                return new ReduceResult(nextState.With(statusNotice: "No participating Inspectors."));
            }

            return new ReduceResult(
                nextState.With(statusNotice: "Running"),
                new ReducerEffect[] { new RequestEvaluateCurrentSelectionEffect(nextState.CurrentLiveSelection) });
        }

        private static ReduceResult ReducePlanCommitted(ManagerState state, PlanCommittedEvent e)
        {
            if (e.Plan == null)
            {
                return new ReduceResult(state);
            }

            var targetsByKey = new Dictionary<WindowKey, WindowAssignmentTarget>();
            for (int i = 0; i < e.Plan.Targets.Count; i++)
            {
                targetsByKey[e.Plan.Targets[i].WindowKey] = e.Plan.Targets[i];
            }

            var updatedWindows = new List<WindowRecord>(state.Windows.Count);
            for (int i = 0; i < state.Windows.Count; i++)
            {
                var w = state.Windows[i];
                if (targetsByKey.TryGetValue(w.Key, out var target))
                {
                    bool observedLock = e.VerifiedLocks.TryGetValue(w.Key, out var vl) ? vl : w.ObservedLocked;
                    updatedWindows.Add(w.With(
                        observedLocked: observedLock,
                        assignedSelection: target.KeepCurrentDisplay ? w.AssignedSelection : target.TargetSelection,
                        assignmentLabel: target.Label,
                        observedRepresentativeName: !target.KeepCurrentDisplay && target.TargetSelection != null
                            ? target.TargetSelection.BuildDisplaySummary()
                            : w.ObservedRepresentativeName));
                }
                else
                {
                    updatedWindows.Add(w);
                }
            }

            return new ReduceResult(state.With(
                lastProcessedSequence: Math.Max(state.LastProcessedSequence, e.CommittedSequence),
                windows: updatedWindows,
                rotationHistory: e.Plan.CandidateRotationHistory,
                lastError: string.Empty));
        }

        private static ReduceResult ReducePlanFailed(ManagerState state, PlanFailedEvent e)
        {
            var updatedWindows = state.Windows.Select(w =>
            {
                bool faulted = e.FaultedWindowKey.HasValue && w.Key == e.FaultedWindowKey.Value;
                bool restoredLock = e.UpdatedLeases != null
                    && e.UpdatedLeases.TryGetValue(w.Key, out var lease)
                    && lease.RestorationStatus == LeaseRestorationStatus.Restored
                    ? lease.OriginalLocked
                    : w.ObservedLocked;

                return w.With(
                    isFaultIsolated: faulted || w.IsFaultIsolated,
                    faultReason: faulted ? e.ErrorMessage : w.FaultReason,
                    observedLocked: restoredLock);
            }).ToArray();

            return new ReduceResult(state.With(
                lifecycleState: e.AllRestored ? RuntimeLifecycleState.Off : RuntimeLifecycleState.Faulted,
                generation: state.Generation + 1,
                windows: updatedWindows,
                leases: e.AllRestored ? new Dictionary<WindowKey, LockLease>() : (e.UpdatedLeases ?? state.Leases),
                rotationHistory: RotationHistoryState.Empty,
                lastError: e.ErrorMessage,
                statusNotice: e.AllRestored ? "Auto-rotation stopped after update failure (locks restored)." : "Update failed and lock restoration incomplete.",
                lastStopReason: StopReason.ExecutionFailure));
        }

        private static ReduceResult ReduceManualLockChanged(ManagerState state, ManualLockChangedEvent e)
        {
            bool isSessionActive = state.LifecycleState == RuntimeLifecycleState.Running;
            var updatedLeases = new Dictionary<WindowKey, LockLease>(state.Leases);
            if (e.UpdatedLease != null)
            {
                updatedLeases[e.WindowKey] = e.UpdatedLease;
            }

            bool participationChanged = false;
            var updatedWindows = new List<WindowRecord>(state.Windows.Count);
            for (int i = 0; i < state.Windows.Count; i++)
            {
                var w = state.Windows[i];
                if (w.Key == e.WindowKey)
                {
                    bool nextManuallyUnlocked = isSessionActive ? !e.NewLocked : w.IsManuallyUnlocked;
                    var nextWindow = w.With(
                        observedLocked: e.NewLocked,
                        isManuallyUnlocked: nextManuallyUnlocked,
                        entityGeneration: w.EntityGeneration + 1);
                    if (nextWindow.CanParticipate != w.CanParticipate)
                    {
                        participationChanged = true;
                    }
                    updatedWindows.Add(nextWindow);
                }
                else
                {
                    updatedWindows.Add(w);
                }
            }

            var nextState = state.With(windows: updatedWindows, leases: updatedLeases);
            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (participationChanged && nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }
            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceExplicitExclusionChanged(ManagerState state, ExplicitExclusionChangedEvent e)
        {
            var updatedLeases = new Dictionary<WindowKey, LockLease>(state.Leases);
            if (e.AcquiredLease != null && !updatedLeases.ContainsKey(e.WindowKey))
            {
                updatedLeases[e.WindowKey] = e.AcquiredLease;
            }

            var updatedWindows = state.Windows.Select(w =>
            {
                if (w.Key != e.WindowKey) return w;
                return w.With(
                    isExplicitlyExcluded: e.Excluded,
                    observedLocked: e.ObservedLocked,
                    entityGeneration: w.EntityGeneration + 1);
            }).ToArray();

            var nextState = state.With(windows: updatedWindows, leases: updatedLeases);
            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }
            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceWindowAdopted(ManagerState state, WindowAdoptedEvent e)
        {
            var updatedLeases = new Dictionary<WindowKey, LockLease>(state.Leases);
            if (e.AcquiredLease != null && !updatedLeases.ContainsKey(e.WindowKey))
            {
                updatedLeases[e.WindowKey] = e.AcquiredLease;
            }

            var updatedWindows = new List<WindowRecord>(state.Windows);
            int nextNum = state.NextFixedDisplayNumber;
            int idx = updatedWindows.FindIndex(w => w.Key == e.WindowKey);

            if (idx >= 0)
            {
                updatedWindows[idx] = updatedWindows[idx].With(
                    isManaged: true,
                    isAlive: true,
                    observedLocked: e.ObservedLocked,
                    identityEvidence: string.IsNullOrEmpty(e.IdentityEvidence) ? updatedWindows[idx].IdentityEvidence : e.IdentityEvidence);
            }
            else
            {
                updatedWindows.Add(new WindowRecord(
                    key: e.WindowKey,
                    fixedDisplayNumber: nextNum++,
                    roleOrder: updatedWindows.Count,
                    isManaged: true,
                    isAlive: true,
                    isExplicitlyExcluded: false,
                    isManuallyUnlocked: false,
                    isFaultIsolated: false,
                    faultReason: string.Empty,
                    observedLocked: e.ObservedLocked,
                    assignedSelection: null,
                    assignmentLabel: AssignmentLabel.None,
                    entityGeneration: 1,
                    observedRepresentativeName: string.Empty,
                    identityEvidence: e.IdentityEvidence));
            }

            var nextState = state.With(
                windows: NormalizeRoleOrders(updatedWindows),
                nextFixedDisplayNumber: nextNum,
                leases: updatedLeases);

            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }

            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceWindowReordered(ManagerState state, WindowReorderedEvent e)
        {
            var managed = state.GetManagedAliveWindowsOrdered().ToList();
            int currentIdx = managed.FindIndex(w => w.Key == e.WindowKey);
            if (currentIdx < 0)
            {
                return new ReduceResult(state);
            }

            int clampedNewIndex = Math.Max(0, Math.Min(managed.Count - 1, e.NewRoleIndex));
            if (clampedNewIndex == currentIdx)
            {
                return new ReduceResult(state);
            }

            var moved = managed[currentIdx];
            managed.RemoveAt(currentIdx);
            managed.Insert(clampedNewIndex, moved);

            var newRoleByKey = new Dictionary<WindowKey, int>();
            for (int i = 0; i < managed.Count; i++)
            {
                newRoleByKey[managed[i].Key] = i;
            }

            // Reordering changes RoleOrder ONLY; FixedDisplayNumber is preserved.
            var updatedWindows = state.Windows.Select(w =>
                newRoleByKey.TryGetValue(w.Key, out int newOrder) ? w.With(roleOrder: newOrder) : w).ToArray();

            var nextState = state.With(
                windows: updatedWindows,
                layoutOverrideRevision: state.LayoutOverrideRevision + 1);

            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }
            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceWindowClosed(ManagerState state, WindowClosedEvent e)
        {
            var remaining = state.Windows.Where(w => w.Key != e.WindowKey).ToList();
            var updatedLeases = new Dictionary<WindowKey, LockLease>(state.Leases);
            if (e.FinalLease != null)
            {
                if (e.FinalLease.RestorationStatus == LeaseRestorationStatus.Restored
                    || e.FinalLease.RestorationStatus == LeaseRestorationStatus.WindowDisposed)
                {
                    updatedLeases.Remove(e.WindowKey);
                }
                else
                {
                    updatedLeases[e.WindowKey] = e.FinalLease;
                }
            }
            else
            {
                updatedLeases.Remove(e.WindowKey);
            }

            var nextState = state.With(
                windows: NormalizeRoleOrders(remaining),
                leases: updatedLeases);

            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }
            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceSettingsUpdated(ManagerState state, SettingsUpdatedEvent e)
        {
            var nextState = state.With(
                settings: e.NewSettings,
                layoutOverrideRevision: e.IsExplicitLayoutChange
                    ? state.LayoutOverrideRevision + 1
                    : state.LayoutOverrideRevision);

            return new ReduceResult(nextState, new ReducerEffect[] { new RequestPersistSettingsEffect() });
        }

        private static ReduceResult ReduceResetLayout(ManagerState state)
        {
            var orderedByFixedNumber = state.Windows
                .OrderBy(w => w.FixedDisplayNumber)
                .Select((w, idx) => w.With(
                    roleOrder: idx,
                    isExplicitlyExcluded: false,
                    isManuallyUnlocked: false,
                    isFaultIsolated: false,
                    faultReason: string.Empty))
                .ToArray();

            var nextState = state.With(
                windows: orderedByFixedNumber,
                layoutOverrideRevision: state.LayoutOverrideRevision + 1);

            var effects = new List<ReducerEffect> { new RequestPersistSessionLayoutEffect() };
            if (nextState.LifecycleState == RuntimeLifecycleState.Running)
            {
                effects.Add(new RequestReassignHistoryEffect(seedFromObservedIfEmpty: false));
            }
            return new ReduceResult(nextState, effects);
        }

        private static ReduceResult ReduceUnknownLockDrop(ManagerState state, UnknownLockDropDetectedEvent e)
        {
            var updatedWindows = state.Windows.Select(w =>
                w.Key == e.WindowKey
                    ? w.With(observedLocked: false, entityGeneration: w.EntityGeneration + 1)
                    : w).ToArray();

            return new ReduceResult(
                state.With(windows: updatedWindows),
                new ReducerEffect[] { new RequestRecoverLockDropEffect(e.WindowKey) });
        }

        private static IReadOnlyList<WindowRecord> NormalizeRoleOrders(List<WindowRecord> windows)
        {
            var managed = windows
                .Where(w => w.IsManaged && w.IsAlive)
                .OrderBy(w => w.RoleOrder)
                .ThenBy(w => w.FixedDisplayNumber)
                .ToList();

            var roleMap = new Dictionary<WindowKey, int>();
            for (int i = 0; i < managed.Count; i++)
            {
                roleMap[managed[i].Key] = i;
            }

            int extRole = managed.Count;
            var result = new WindowRecord[windows.Count];
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                if (roleMap.TryGetValue(w.Key, out int order))
                {
                    result[i] = w.RoleOrder == order ? w : w.With(roleOrder: order);
                }
                else
                {
                    result[i] = w.With(roleOrder: extRole++);
                }
            }
            return result;
        }
    }
}
