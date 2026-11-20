using System;
using System.Collections.Generic;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    public abstract class DomainEvent
    {
        public string OperationId { get; }
        public long Generation { get; }

        protected DomainEvent(string operationId, long generation)
        {
            OperationId = operationId ?? string.Empty;
            Generation = generation;
        }
    }

    public sealed class CapabilitiesProbedEvent : DomainEvent
    {
        public AdapterCapabilities Capabilities { get; }
        public CapabilitiesProbedEvent(string opId, long gen, AdapterCapabilities capabilities) : base(opId, gen)
        {
            Capabilities = capabilities ?? AdapterCapabilities.None();
        }
    }

    public sealed class WindowsSynchronizedEvent : DomainEvent
    {
        public IReadOnlyList<ObservedWindowSnapshot> ObservedWindows { get; }
        public bool IsReloadGraceActive { get; }

        public WindowsSynchronizedEvent(string opId, long gen, IReadOnlyList<ObservedWindowSnapshot> observedWindows, bool isReloadGraceActive)
            : base(opId, gen)
        {
            ObservedWindows = observedWindows ?? Array.Empty<ObservedWindowSnapshot>();
            IsReloadGraceActive = isReloadGraceActive;
        }
    }

    public sealed class ObservedWindowSnapshot
    {
        public WindowKey Key { get; }
        public bool ObservedLocked { get; }
        public string RepresentativeName { get; }
        public string IdentityEvidence { get; }
        public SelectionSnapshot ObservedLockedSelection { get; }

        public ObservedWindowSnapshot(
            WindowKey key,
            bool observedLocked,
            string representativeName,
            string identityEvidence,
            SelectionSnapshot observedLockedSelection = null)
        {
            Key = key;
            ObservedLocked = observedLocked;
            RepresentativeName = representativeName ?? string.Empty;
            IdentityEvidence = identityEvidence ?? string.Empty;
            ObservedLockedSelection = observedLockedSelection;
        }
    }

    public sealed class StartSessionRequestedEvent : DomainEvent
    {
        public StartSessionRequestedEvent(string opId, long gen) : base(opId, gen) { }
    }

    public sealed class StartSessionCompletedEvent : DomainEvent
    {
        public bool Succeeded { get; }
        public IReadOnlyDictionary<WindowKey, LockLease> Leases { get; }
        public RotationHistoryState SeededRotationHistory { get; }
        public string ErrorMessage { get; }
        public bool RestorationIncomplete { get; }

        public StartSessionCompletedEvent(
            string opId,
            long gen,
            bool succeeded,
            IReadOnlyDictionary<WindowKey, LockLease> leases,
            RotationHistoryState seededRotationHistory,
            string errorMessage,
            bool restorationIncomplete)
            : base(opId, gen)
        {
            Succeeded = succeeded;
            Leases = leases;
            SeededRotationHistory = seededRotationHistory;
            ErrorMessage = errorMessage ?? string.Empty;
            RestorationIncomplete = restorationIncomplete;
        }
    }

    public sealed class PauseRequestedEvent : DomainEvent
    {
        public PauseRequestedEvent(string opId, long gen) : base(opId, gen) { }
    }

    public sealed class ResumeRequestedEvent : DomainEvent
    {
        public ResumeRequestedEvent(string opId, long gen) : base(opId, gen) { }
    }

    public sealed class StopSessionRequestedEvent : DomainEvent
    {
        public StopReason Reason { get; }
        public bool SuspendMode { get; }

        public StopSessionRequestedEvent(string opId, long gen, StopReason reason, bool suspendMode = false)
            : base(opId, gen)
        {
            Reason = reason;
            SuspendMode = suspendMode;
        }
    }

    public sealed class StopSessionCompletedEvent : DomainEvent
    {
        public IReadOnlyDictionary<WindowKey, LockLease> UpdatedLeases { get; }
        public bool AllRestored { get; }
        public string ErrorMessage { get; }
        public bool RemainSuspended { get; }

        public StopSessionCompletedEvent(
            string opId,
            long gen,
            IReadOnlyDictionary<WindowKey, LockLease> updatedLeases,
            bool allRestored,
            string errorMessage,
            bool remainSuspended = false)
            : base(opId, gen)
        {
            UpdatedLeases = updatedLeases;
            AllRestored = allRestored;
            ErrorMessage = errorMessage ?? string.Empty;
            RemainSuspended = remainSuspended;
        }
    }

    public sealed class RetryRestoreCompletedEvent : DomainEvent
    {
        public IReadOnlyDictionary<WindowKey, LockLease> UpdatedLeases { get; }
        public bool AllRestored { get; }
        public string ErrorMessage { get; }

        public RetryRestoreCompletedEvent(string opId, long gen, IReadOnlyDictionary<WindowKey, LockLease> updatedLeases, bool allRestored, string errorMessage)
            : base(opId, gen)
        {
            UpdatedLeases = updatedLeases;
            AllRestored = allRestored;
            ErrorMessage = errorMessage ?? string.Empty;
        }
    }

    public sealed class SelectionReceivedEvent : DomainEvent
    {
        public SelectionSnapshot Snapshot { get; }
        public long NowMs { get; }

        public SelectionReceivedEvent(string opId, long gen, SelectionSnapshot snapshot, long nowMs) : base(opId, gen)
        {
            Snapshot = snapshot ?? SelectionSnapshot.Empty;
            NowMs = nowMs;
        }
    }

    public sealed class NavigationMoveRequestedEvent : DomainEvent
    {
        public int TargetIndex { get; }
        public long ExpiresAtMs { get; }

        public NavigationMoveRequestedEvent(string opId, long gen, int targetIndex, long expiresAtMs) : base(opId, gen)
        {
            TargetIndex = targetIndex;
            ExpiresAtMs = expiresAtMs;
        }
    }

    public sealed class NavigationNotificationExpiredEvent : DomainEvent
    {
        public SelectionSnapshot ObservedSelection { get; }

        public NavigationNotificationExpiredEvent(string opId, long gen, SelectionSnapshot observedSelection) : base(opId, gen)
        {
            ObservedSelection = observedSelection ?? SelectionSnapshot.Empty;
        }
    }

    public sealed class PlanCommittedEvent : DomainEvent
    {
        public AssignmentPlan Plan { get; }
        public long CommittedSequence { get; }
        public IReadOnlyDictionary<WindowKey, bool> VerifiedLocks { get; }

        public PlanCommittedEvent(
            string opId,
            long gen,
            AssignmentPlan plan,
            long committedSequence,
            IReadOnlyDictionary<WindowKey, bool> verifiedLocks)
            : base(opId, gen)
        {
            Plan = plan;
            CommittedSequence = committedSequence;
            VerifiedLocks = verifiedLocks ?? new Dictionary<WindowKey, bool>();
        }
    }

    public sealed class PlanFailedEvent : DomainEvent
    {
        public string ErrorMessage { get; }
        public IReadOnlyDictionary<WindowKey, LockLease> UpdatedLeases { get; }
        public bool AllRestored { get; }
        public WindowKey? FaultedWindowKey { get; }

        public PlanFailedEvent(
            string opId,
            long gen,
            string errorMessage,
            IReadOnlyDictionary<WindowKey, LockLease> updatedLeases,
            bool allRestored,
            WindowKey? faultedWindowKey = null)
            : base(opId, gen)
        {
            ErrorMessage = errorMessage ?? string.Empty;
            UpdatedLeases = updatedLeases;
            AllRestored = allRestored;
            FaultedWindowKey = faultedWindowKey;
        }
    }

    public sealed class ManualLockChangedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }
        public bool NewLocked { get; }
        public LockLease UpdatedLease { get; }

        public ManualLockChangedEvent(string opId, long gen, WindowKey windowKey, bool newLocked, LockLease updatedLease = null)
            : base(opId, gen)
        {
            WindowKey = windowKey;
            NewLocked = newLocked;
            UpdatedLease = updatedLease;
        }
    }

    public sealed class BulkManualLockChangedEvent : DomainEvent
    {
        public bool NewLocked { get; }
        public IReadOnlyList<WindowKey> AffectedWindows { get; }

        public BulkManualLockChangedEvent(string opId, long gen, bool newLocked, IReadOnlyList<WindowKey> affectedWindows)
            : base(opId, gen)
        {
            NewLocked = newLocked;
            AffectedWindows = affectedWindows ?? Array.Empty<WindowKey>();
        }
    }

    public sealed class ExplicitExclusionChangedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }
        public bool Excluded { get; }
        public bool ObservedLocked { get; }
        public LockLease AcquiredLease { get; }

        public ExplicitExclusionChangedEvent(string opId, long gen, WindowKey windowKey, bool excluded, bool observedLocked, LockLease acquiredLease = null)
            : base(opId, gen)
        {
            WindowKey = windowKey;
            Excluded = excluded;
            ObservedLocked = observedLocked;
            AcquiredLease = acquiredLease;
        }
    }

    public sealed class WindowAdoptedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }
        public bool ObservedLocked { get; }
        public string IdentityEvidence { get; }
        public LockLease AcquiredLease { get; }

        public WindowAdoptedEvent(string opId, long gen, WindowKey windowKey, bool observedLocked, string identityEvidence, LockLease acquiredLease = null)
            : base(opId, gen)
        {
            WindowKey = windowKey;
            ObservedLocked = observedLocked;
            IdentityEvidence = identityEvidence ?? string.Empty;
            AcquiredLease = acquiredLease;
        }
    }

    public sealed class WindowReorderedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }
        public int NewRoleIndex { get; }

        public WindowReorderedEvent(string opId, long gen, WindowKey windowKey, int newRoleIndex) : base(opId, gen)
        {
            WindowKey = windowKey;
            NewRoleIndex = newRoleIndex;
        }
    }

    public sealed class WindowClosedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }
        public LockLease FinalLease { get; }

        public WindowClosedEvent(string opId, long gen, WindowKey windowKey, LockLease finalLease = null) : base(opId, gen)
        {
            WindowKey = windowKey;
            FinalLease = finalLease;
        }
    }

    public sealed class SettingsUpdatedEvent : DomainEvent
    {
        public ManagerSettings NewSettings { get; }
        public bool IsExplicitLayoutChange { get; }

        public SettingsUpdatedEvent(string opId, long gen, ManagerSettings newSettings, bool isExplicitLayoutChange = false)
            : base(opId, gen)
        {
            NewSettings = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
            IsExplicitLayoutChange = isExplicitLayoutChange;
        }
    }

    public sealed class FavoritesUpdatedEvent : DomainEvent
    {
        public IReadOnlyList<FavoriteEntry> Favorites { get; }

        public FavoritesUpdatedEvent(string opId, long gen, IReadOnlyList<FavoriteEntry> favorites) : base(opId, gen)
        {
            Favorites = favorites ?? Array.Empty<FavoriteEntry>();
        }
    }

    public sealed class ClearNavigationHistoryEvent : DomainEvent
    {
        public ClearNavigationHistoryEvent(string opId, long gen) : base(opId, gen) { }
    }

    public sealed class ResetLayoutEvent : DomainEvent
    {
        public ResetLayoutEvent(string opId, long gen) : base(opId, gen) { }
    }

    public sealed class StatusNoticeEvent : DomainEvent
    {
        public string Notice { get; }
        public string Error { get; }
        public bool? PersistenceBlocked { get; }

        public StatusNoticeEvent(string opId, long gen, string notice, string error = null, bool? persistenceBlocked = null)
            : base(opId, gen)
        {
            Notice = notice;
            Error = error;
            PersistenceBlocked = persistenceBlocked;
        }
    }

    public sealed class UnknownLockDropDetectedEvent : DomainEvent
    {
        public WindowKey WindowKey { get; }

        public UnknownLockDropDetectedEvent(string opId, long gen, WindowKey windowKey) : base(opId, gen)
        {
            WindowKey = windowKey;
        }
    }

    public abstract class ReducerEffect
    {
    }

    public sealed class RequestEvaluateCurrentSelectionEffect : ReducerEffect
    {
        public SelectionSnapshot Snapshot { get; }
        public bool IsNavigationUpdate { get; }

        public RequestEvaluateCurrentSelectionEffect(SelectionSnapshot snapshot, bool isNavigationUpdate)
        {
            Snapshot = snapshot;
            IsNavigationUpdate = isNavigationUpdate;
        }
    }

    public sealed class RequestReassignHistoryEffect : ReducerEffect
    {
        public bool SeedFromObservedIfEmpty { get; }

        public RequestReassignHistoryEffect(bool seedFromObservedIfEmpty)
        {
            SeedFromObservedIfEmpty = seedFromObservedIfEmpty;
        }
    }

    public sealed class RequestSetGlobalSelectionEffect : ReducerEffect
    {
        public SelectionSnapshot TargetSnapshot { get; }
        public string OperationId { get; }

        public RequestSetGlobalSelectionEffect(SelectionSnapshot targetSnapshot, string operationId)
        {
            TargetSnapshot = targetSnapshot;
            OperationId = operationId;
        }
    }

    public sealed class RequestRecoverLockDropEffect : ReducerEffect
    {
        public WindowKey WindowKey { get; }

        public RequestRecoverLockDropEffect(WindowKey windowKey)
        {
            WindowKey = windowKey;
        }
    }

    public sealed class RequestPersistSettingsEffect : ReducerEffect
    {
    }

    public sealed class RequestPersistSessionLayoutEffect : ReducerEffect
    {
    }

    public readonly struct ReduceResult
    {
        public ManagerState State { get; }
        public IReadOnlyList<ReducerEffect> Effects { get; }

        public ReduceResult(ManagerState state, IReadOnlyList<ReducerEffect> effects = null)
        {
            State = state;
            Effects = effects ?? Array.Empty<ReducerEffect>();
        }
    }
}
