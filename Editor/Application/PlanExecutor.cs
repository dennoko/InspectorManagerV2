using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Application
{
    public enum PlanExecutionOutcomeKind
    {
        Committed = 0,
        PendingVerification = 1,
        AbortedNoWrite = 2,
        FailedCompensated = 3
    }

    public sealed class PlanExecutionOutcome
    {
        public PlanExecutionOutcomeKind Kind { get; }
        public AssignmentPlan Plan { get; }
        public IReadOnlyDictionary<WindowKey, bool> VerifiedLocks { get; }
        public IReadOnlyDictionary<WindowKey, LockLease> UpdatedLeases { get; }
        public bool AllLeasesRestored { get; }
        public WindowKey? FaultedWindowKey { get; }
        public string Error { get; }
        public int WriteSkipCount { get; }

        public PlanExecutionOutcome(
            PlanExecutionOutcomeKind kind,
            AssignmentPlan plan,
            IReadOnlyDictionary<WindowKey, bool> verifiedLocks = null,
            IReadOnlyDictionary<WindowKey, LockLease> updatedLeases = null,
            bool allLeasesRestored = true,
            WindowKey? faultedWindowKey = null,
            string error = null,
            int writeSkipCount = 0)
        {
            Kind = kind;
            Plan = plan;
            VerifiedLocks = verifiedLocks ?? new Dictionary<WindowKey, bool>();
            UpdatedLeases = updatedLeases;
            AllLeasesRestored = allLeasesRestored;
            FaultedWindowKey = faultedWindowKey;
            Error = error ?? string.Empty;
            WriteSkipCount = writeSkipCount;
        }
    }

    /// <summary>
    /// Executes AssignmentPlans with pre-write snapshots, write-skip verification cache (Section 14.2),
    /// PresentationPending deferred verification with timeout (Section 14.1),
    /// and reverse-order compensation + lease restoration on failure (Section 8.2).
    /// </summary>
    public sealed class PlanExecutor
    {
        private sealed class VerifiedWindowCacheEntry
        {
            public SelectionComparisonKey TargetsKey;
            public bool Locked;
            public long EntityGeneration;
            public bool SupportsActiveTarget;
        }

        private sealed class WrittenStepRecord
        {
            public WindowKey WindowKey;
            public string IdentityEvidence;
            public InspectorReadResult PreWriteState;
            public WindowAssignmentTarget Target;
            public bool NeedsDeferredVerify;
        }

        private sealed class PendingVerificationState
        {
            public AssignmentPlan Plan;
            public long Generation;
            public long ExpiresAtMs;
            public List<WrittenStepRecord> WrittenSteps;
            public Dictionary<WindowKey, bool> VerifiedLocks;
            public int WriteSkipCount;
        }

        private readonly IInspectorAdapter _inspectorAdapter;
        private readonly ISelectionAdapter _selectionAdapter;
        private readonly IPersistenceStore _persistenceStore;
        private readonly IMonotonicClock _clock;
        private readonly IDiagnosticLogger _logger;
        private readonly Dictionary<WindowKey, VerifiedWindowCacheEntry> _verifiedCache = new Dictionary<WindowKey, VerifiedWindowCacheEntry>();
        private readonly long _pendingVerifyTimeoutMs;

        private PendingVerificationState _pendingVerification;

        public bool HasPendingVerification => _pendingVerification != null;

        public PlanExecutor(
            IInspectorAdapter inspectorAdapter,
            ISelectionAdapter selectionAdapter,
            IPersistenceStore persistenceStore,
            IMonotonicClock clock,
            IDiagnosticLogger logger,
            long pendingVerifyTimeoutMs = 500)
        {
            _inspectorAdapter = inspectorAdapter ?? throw new ArgumentNullException(nameof(inspectorAdapter));
            _selectionAdapter = selectionAdapter ?? throw new ArgumentNullException(nameof(selectionAdapter));
            _persistenceStore = persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pendingVerifyTimeoutMs = pendingVerifyTimeoutMs > 0 ? pendingVerifyTimeoutMs : 500;
        }

        public void InvalidateAllCaches()
        {
            _verifiedCache.Clear();
            _pendingVerification = null;
        }

        public void InvalidateWindowCache(WindowKey key)
        {
            _verifiedCache.Remove(key);
        }

        public void CancelPendingVerification()
        {
            _pendingVerification = null;
        }

        /// <summary>
        /// Seeds the verification cache strictly from an Inspector's ACTUAL observed locked targets
        /// (Section 14.2: NEVER seed from current global Selection!).
        /// </summary>
        public void SeedCacheFromObservedRead(WindowKey key, long entityGeneration, InspectorReadResult readResult, bool supportsActiveTarget)
        {
            if (readResult == null || !readResult.IsReadable || !readResult.ObservedLocked)
            {
                _verifiedCache.Remove(key);
                return;
            }

            _verifiedCache[key] = new VerifiedWindowCacheEntry
            {
                TargetsKey = readResult.ObservedSelection.ComparisonKey,
                Locked = readResult.ObservedLocked,
                EntityGeneration = entityGeneration,
                SupportsActiveTarget = supportsActiveTarget
            };
        }

        public PlanExecutionOutcome ExecutePlan(ManagerState state, AssignmentPlan plan, string editorSessionId, string managementSessionId)
        {
            if (state == null || plan == null)
            {
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Invalid plan or state.");
            }

            // Invariant 5: Reject stale generation plan.
            if (plan.Generation != state.Generation || state.LifecycleState != RuntimeLifecycleState.Running)
            {
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Stale generation or not running.");
            }

            // Invariant 1: Never start a new write plan while one is pending verification.
            if (_pendingVerification != null)
            {
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Another plan is pending verification.");
            }

            var writeTargets = plan.Targets.Where(t => !t.KeepCurrentDisplay && t.TargetSelection != null && !t.TargetSelection.IsEmpty).ToList();
            if (writeTargets.Count == 0)
            {
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.Committed, plan);
            }

            // Step 1: Verify all target objects are still alive right before writing.
            for (int i = 0; i < writeTargets.Count; i++)
            {
                var keys = writeTargets[i].TargetSelection.GetOrderedKeys();
                if (!_selectionAdapter.AreAllTargetsAlive(keys))
                {
                    _logger.Info(plan.PlanId, state.Generation, writeTargets[i].WindowKey, "PlanAbort", "Target object disappeared before execution.");
                    return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Target object no longer alive.");
                }
            }

            // Step 2: Check write-skip cache & read pre-write state for EVERY window to be written (Section 8.2 #1 & 14.2).
            var stepsToWrite = new List<WrittenStepRecord>();
            var verifiedLocks = new Dictionary<WindowKey, bool>();
            int skipCount = 0;

            for (int i = 0; i < writeTargets.Count; i++)
            {
                var target = writeTargets[i];
                var win = state.FindWindow(target.WindowKey);
                if (win == null || !win.CanParticipate || !_inspectorAdapter.IsWindowAlive(win.Key, win.IdentityEvidence))
                {
                    _logger.Info(plan.PlanId, state.Generation, target.WindowKey, "PlanAbort", "Window no longer participating or alive.");
                    return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Window no longer alive or participating.");
                }

                if (CanSkipWrite(win, target.TargetSelection, state.Capabilities.SupportsActiveTargetSelection))
                {
                    verifiedLocks[win.Key] = true;
                    skipCount++;
                    continue;
                }

                var preRead = _inspectorAdapter.ReadInspectorState(win.Key);
                if (!preRead.IsReadable)
                {
                    // Section 8.2 #1: If we cannot read pre-write state of ANY target window, abort BEFORE writing anything.
                    _logger.Error(plan.PlanId, state.Generation, win.Key, "PreWriteReadFailed", preRead.Error);
                    return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Pre-write read failed: " + preRead.Error);
                }

                // Section 14.1: Do not write to an empty-display window if adapter cannot compensate back to empty display.
                if (preRead.Status == InspectorReadStatus.EmptyDisplay && !state.Capabilities.SupportsEmptyTargetCompensation)
                {
                    _logger.Error(plan.PlanId, state.Generation, win.Key, "EmptyCompensationUnsupported", "Empty display compensation unsupported.");
                    return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, plan, error: "Empty display compensation unsupported.");
                }

                stepsToWrite.Add(new WrittenStepRecord
                {
                    WindowKey = win.Key,
                    IdentityEvidence = win.IdentityEvidence,
                    PreWriteState = preRead,
                    Target = target
                });
            }

            if (stepsToWrite.Count == 0)
            {
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.Committed, plan, verifiedLocks: verifiedLocks, writeSkipCount: skipCount);
            }

            // Step 3: Execute writes sequentially; immediately stop & compensate on any error or mismatch (Section 8.2 #2-6).
            var writtenSoFar = new List<WrittenStepRecord>(stepsToWrite.Count);
            bool anyPending = false;

            for (int i = 0; i < stepsToWrite.Count; i++)
            {
                var step = stepsToWrite[i];
                AdapterWriteResult writeRes;
                try
                {
                    writeRes = _inspectorAdapter.WriteTargetsAndLock(
                        step.WindowKey,
                        step.Target.TargetSelection.GetOrderedKeys(),
                        step.Target.TargetSelection.ActiveTarget,
                        locked: true);
                }
                catch (Exception ex)
                {
                    writeRes = new AdapterWriteResult(WindowWriteStatus.Failed, false, ex.Message);
                }

                if (writeRes.Status == WindowWriteStatus.Failed)
                {
                    // Re-observe failed window: only add to compensation list if its state actually diverged from pre-write state.
                    var postFailRead = _inspectorAdapter.ReadInspectorState(step.WindowKey);
                    bool unchanged = postFailRead.IsReadable
                        && postFailRead.ObservedLocked == step.PreWriteState.ObservedLocked
                        && postFailRead.ObservedSelection.ComparisonKey.TargetSetEquals(step.PreWriteState.ObservedSelection.ComparisonKey);
                    if (!unchanged)
                    {
                        writtenSoFar.Add(step);
                    }

                    return CompensateAndRestoreLeases(
                        state,
                        plan,
                        writtenSoFar,
                        step.WindowKey,
                        "Write failed on window " + step.WindowKey + ": " + writeRes.Error,
                        editorSessionId,
                        managementSessionId);
                }

                writtenSoFar.Add(step);

                if (writeRes.Status == WindowWriteStatus.Verified || writeRes.Status == WindowWriteStatus.Skipped)
                {
                    verifiedLocks[step.WindowKey] = writeRes.ObservedLocked;
                    var win = state.FindWindow(step.WindowKey);
                    if (win != null)
                    {
                        _verifiedCache[step.WindowKey] = new VerifiedWindowCacheEntry
                        {
                            TargetsKey = step.Target.TargetSelection.ComparisonKey,
                            Locked = writeRes.ObservedLocked,
                            EntityGeneration = win.EntityGeneration,
                            SupportsActiveTarget = state.Capabilities.SupportsActiveTargetSelection
                        };
                    }
                }
                else if (writeRes.IsPending)
                {
                    step.NeedsDeferredVerify = true;
                    anyPending = true;
                }
            }

            if (anyPending)
            {
                _pendingVerification = new PendingVerificationState
                {
                    Plan = plan,
                    Generation = state.Generation,
                    ExpiresAtMs = _clock.NowMs + _pendingVerifyTimeoutMs,
                    WrittenSteps = writtenSoFar,
                    VerifiedLocks = verifiedLocks,
                    WriteSkipCount = skipCount
                };
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.PendingVerification, plan, verifiedLocks: verifiedLocks, writeSkipCount: skipCount);
            }

            return new PlanExecutionOutcome(PlanExecutionOutcomeKind.Committed, plan, verifiedLocks: verifiedLocks, writeSkipCount: skipCount);
        }

        public PlanExecutionOutcome PollPendingVerification(ManagerState state, string editorSessionId, string managementSessionId)
        {
            if (_pendingVerification == null)
            {
                return null;
            }

            var pending = _pendingVerification;

            // Section 14.5 #5: Discard timed-out or stale-generation delayed verification without re-locking or advancing order!
            if (pending.Generation != state.Generation || state.LifecycleState != RuntimeLifecycleState.Running)
            {
                _pendingVerification = null;
                return new PlanExecutionOutcome(PlanExecutionOutcomeKind.AbortedNoWrite, pending.Plan, error: "Pending verification invalidated by generation change.");
            }

            bool allVerified = true;
            for (int i = 0; i < pending.WrittenSteps.Count; i++)
            {
                var step = pending.WrittenSteps[i];
                if (!step.NeedsDeferredVerify)
                {
                    continue;
                }

                AdapterVerifyResult verifyRes;
                try
                {
                    verifyRes = _inspectorAdapter.VerifyTargetsAndLock(
                        step.WindowKey,
                        step.Target.TargetSelection.GetOrderedKeys(),
                        expectedLocked: true);
                }
                catch (Exception ex)
                {
                    verifyRes = new AdapterVerifyResult(WindowWriteStatus.Failed, false, ex.Message);
                }

                if (verifyRes.Status == WindowWriteStatus.Verified)
                {
                    step.NeedsDeferredVerify = false;
                    pending.VerifiedLocks[step.WindowKey] = verifyRes.ObservedLocked;
                    var win = state.FindWindow(step.WindowKey);
                    if (win != null)
                    {
                        _verifiedCache[step.WindowKey] = new VerifiedWindowCacheEntry
                        {
                            TargetsKey = step.Target.TargetSelection.ComparisonKey,
                            Locked = verifyRes.ObservedLocked,
                            EntityGeneration = win.EntityGeneration,
                            SupportsActiveTarget = state.Capabilities.SupportsActiveTargetSelection
                        };
                    }
                }
                else if (verifyRes.Status == WindowWriteStatus.Failed)
                {
                    _pendingVerification = null;
                    return CompensateAndRestoreLeases(
                        state,
                        pending.Plan,
                        pending.WrittenSteps,
                        step.WindowKey,
                        "Deferred verification failed: " + verifyRes.Error,
                        editorSessionId,
                        managementSessionId);
                }
                else
                {
                    allVerified = false;
                }
            }

            if (allVerified)
            {
                _pendingVerification = null;
                return new PlanExecutionOutcome(
                    PlanExecutionOutcomeKind.Committed,
                    pending.Plan,
                    verifiedLocks: pending.VerifiedLocks,
                    writeSkipCount: pending.WriteSkipCount);
            }

            if (_clock.NowMs >= pending.ExpiresAtMs)
            {
                _pendingVerification = null;
                return CompensateAndRestoreLeases(
                    state,
                    pending.Plan,
                    pending.WrittenSteps,
                    pending.WrittenSteps.FirstOrDefault(s => s.NeedsDeferredVerify)?.WindowKey,
                    "Deferred verification timed out.",
                    editorSessionId,
                    managementSessionId);
            }

            return null;
        }

        /// <summary>
        /// Restores all provided leases to their OriginalLocked state (idempotent per Invariant #6).
        /// </summary>
        public (Dictionary<WindowKey, LockLease> updatedLeases, bool allRestored, string error) RestoreLeases(
            IReadOnlyDictionary<WindowKey, LockLease> currentLeases,
            long generation,
            string operationId,
            string editorSessionId,
            string managementSessionId)
        {
            var result = new Dictionary<WindowKey, LockLease>();
            var errors = new List<string>();
            bool allRestored = true;

            foreach (var kvp in currentLeases)
            {
                var lease = kvp.Value;
                if (lease.RestorationStatus == LeaseRestorationStatus.Restored
                    || lease.RestorationStatus == LeaseRestorationStatus.WindowDisposed)
                {
                    result[kvp.Key] = lease;
                    continue;
                }

                // Section 6.2: Never substitute another window if the original window was closed.
                if (!_inspectorAdapter.IsWindowAlive(lease.WindowKey, lease.IdentityEvidence))
                {
                    result[kvp.Key] = lease.WithRestoration(LeaseRestorationStatus.WindowDisposed);
                    continue;
                }

                try
                {
                    var writeRes = _inspectorAdapter.WriteLockOnly(lease.WindowKey, lease.OriginalLocked);
                    if (writeRes.IsSuccess && writeRes.ObservedLocked == lease.OriginalLocked)
                    {
                        result[kvp.Key] = lease.WithRestoration(LeaseRestorationStatus.Restored);
                        _verifiedCache.Remove(lease.WindowKey);
                    }
                    else
                    {
                        allRestored = false;
                        string err = "Failed to restore lock on " + lease.WindowKey + ": " + writeRes.Error;
                        errors.Add(err);
                        result[kvp.Key] = lease.WithRestoration(LeaseRestorationStatus.RestoreFailed, err);
                        _logger.Error(operationId, generation, lease.WindowKey, "LeaseRestoreFailed", err);
                    }
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    string err = "Exception restoring lock on " + lease.WindowKey + ": " + ex.Message;
                    errors.Add(err);
                    result[kvp.Key] = lease.WithRestoration(LeaseRestorationStatus.RestoreFailed, err);
                    _logger.Error(operationId, generation, lease.WindowKey, "LeaseRestoreException", err);
                }
            }

            // Persist recovery journal progress (clear if all restored, else update remaining unrestored leases).
            if (allRestored)
            {
                _persistenceStore.ClearRecoveryJournal();
            }
            else
            {
                var journal = new RecoveryJournalDto
                {
                    EditorSessionId = editorSessionId ?? string.Empty,
                    ManagementSessionId = managementSessionId ?? string.Empty,
                    Generation = generation,
                    Leases = result.Values.Select(LeaseJournalEntryDto.FromDomain).ToList()
                };
                _persistenceStore.SaveRecoveryJournalImmediate(journal);
            }

            return (result, allRestored, string.Join(" | ", errors));
        }

        private PlanExecutionOutcome CompensateAndRestoreLeases(
            ManagerState state,
            AssignmentPlan plan,
            List<WrittenStepRecord> writtenSteps,
            WindowKey? faultedWindow,
            string failureReason,
            string editorSessionId,
            string managementSessionId)
        {
            _verifiedCache.Clear();
            _logger.Error(plan.PlanId, state.Generation, faultedWindow, "PlanFailedCompensating", failureReason);

            var compErrors = new List<string> { failureReason };
            bool allCompensated = true;

            // Section 8.2 #3: Compensate already-written alive Inspectors in REVERSE order back to their pre-write display & lock.
            for (int i = writtenSteps.Count - 1; i >= 0; i--)
            {
                var step = writtenSteps[i];
                if (!_inspectorAdapter.IsWindowAlive(step.WindowKey, step.IdentityEvidence))
                {
                    continue;
                }

                try
                {
                    var pre = step.PreWriteState;
                    if (pre.ObservedLocked && pre.Status == InspectorReadStatus.Populated && !pre.ObservedSelection.IsEmpty)
                    {
                        var compRes = _inspectorAdapter.WriteTargetsAndLock(
                            step.WindowKey,
                            pre.ObservedSelection.GetOrderedKeys(),
                            pre.ObservedSelection.ActiveTarget,
                            locked: true);
                        if (compRes.Status == WindowWriteStatus.Failed)
                        {
                            allCompensated = false;
                            compErrors.Add("Compensation failed for " + step.WindowKey + ": " + compRes.Error);
                        }
                    }
                    else
                    {
                        var compRes = _inspectorAdapter.WriteLockOnly(step.WindowKey, pre.ObservedLocked);
                        if (compRes.Status == WindowWriteStatus.Failed)
                        {
                            allCompensated = false;
                            compErrors.Add("Lock compensation failed for " + step.WindowKey + ": " + compRes.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    allCompensated = false;
                    compErrors.Add("Compensation exception for " + step.WindowKey + ": " + ex.Message);
                }
            }

            // Section 8.2 #5: Attempt to restore ALL leases to their pre-session OriginalLocked state and stop rotation.
            var (updatedLeases, allLeasesRestored, leaseError) = RestoreLeases(
                state.Leases,
                state.Generation,
                plan.PlanId,
                editorSessionId,
                managementSessionId);

            if (!string.IsNullOrEmpty(leaseError))
            {
                compErrors.Add(leaseError);
            }

            return new PlanExecutionOutcome(
                PlanExecutionOutcomeKind.FailedCompensated,
                plan,
                updatedLeases: updatedLeases,
                allLeasesRestored: allLeasesRestored && allCompensated,
                faultedWindowKey: faultedWindow,
                error: string.Join(" | ", compErrors));
        }

        private bool CanSkipWrite(WindowRecord win, SelectionSnapshot desired, bool supportsActiveTarget)
        {
            if (!_verifiedCache.TryGetValue(win.Key, out var cached))
            {
                return false;
            }

            if (cached.EntityGeneration != win.EntityGeneration || !cached.Locked)
            {
                return false;
            }

            if (supportsActiveTarget && cached.SupportsActiveTarget)
            {
                return cached.TargetsKey == desired.ComparisonKey;
            }

            return cached.TargetsKey.TargetSetEquals(desired.ComparisonKey);
        }
    }
}
