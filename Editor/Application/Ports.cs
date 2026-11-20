using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Application
{
    public enum InspectorReadStatus
    {
        Populated = 0,
        EmptyDisplay = 1,
        PendingTrackerReady = 2,
        Failed = 3
    }

    public sealed class InspectorReadResult
    {
        public InspectorReadStatus Status { get; }
        public bool ObservedLocked { get; }
        public SelectionSnapshot ObservedSelection { get; }
        public string RepresentativeName { get; }
        public string IdentityEvidence { get; }
        public string Error { get; }

        public bool IsReadable => Status == InspectorReadStatus.Populated || Status == InspectorReadStatus.EmptyDisplay;

        public InspectorReadResult(
            InspectorReadStatus status,
            bool observedLocked,
            SelectionSnapshot observedSelection,
            string representativeName,
            string identityEvidence,
            string error = null)
        {
            Status = status;
            ObservedLocked = observedLocked;
            ObservedSelection = observedSelection ?? SelectionSnapshot.Empty;
            RepresentativeName = representativeName ?? string.Empty;
            IdentityEvidence = identityEvidence ?? string.Empty;
            Error = error ?? string.Empty;
        }
    }

    public sealed class AdapterWriteResult
    {
        public WindowWriteStatus Status { get; }
        public bool ObservedLocked { get; }
        public string Error { get; }

        public bool IsSuccess => Status == WindowWriteStatus.Verified || Status == WindowWriteStatus.Skipped;
        public bool IsPending => Status == WindowWriteStatus.PresentationPending || Status == WindowWriteStatus.Written;

        public AdapterWriteResult(WindowWriteStatus status, bool observedLocked, string error = null)
        {
            Status = status;
            ObservedLocked = observedLocked;
            Error = error ?? string.Empty;
        }
    }

    public sealed class AdapterVerifyResult
    {
        public WindowWriteStatus Status { get; }
        public bool ObservedLocked { get; }
        public string Error { get; }

        public AdapterVerifyResult(WindowWriteStatus status, bool observedLocked, string error = null)
        {
            Status = status;
            ObservedLocked = observedLocked;
            Error = error ?? string.Empty;
        }
    }

    public sealed class AdapterWindowCreateResult
    {
        public bool Succeeded { get; }
        public WindowKey WindowKey { get; }
        public bool ObservedLocked { get; }
        public string IdentityEvidence { get; }
        public bool ConfirmedEmptyNewWindow { get; }
        public string Error { get; }

        public AdapterWindowCreateResult(
            bool succeeded,
            WindowKey windowKey,
            bool observedLocked,
            string identityEvidence,
            bool confirmedEmptyNewWindow,
            string error = null)
        {
            Succeeded = succeeded;
            WindowKey = windowKey;
            ObservedLocked = observedLocked;
            IdentityEvidence = identityEvidence ?? string.Empty;
            ConfirmedEmptyNewWindow = confirmedEmptyNewWindow;
            Error = error ?? string.Empty;
        }
    }

    public sealed class AdapterResult
    {
        public bool Succeeded { get; }
        public string Error { get; }

        public AdapterResult(bool succeeded, string error = null)
        {
            Succeeded = succeeded;
            Error = error ?? string.Empty;
        }

        public static AdapterResult Ok() => new AdapterResult(true);
        public static AdapterResult Fail(string error) => new AdapterResult(false, error);
    }

    public sealed class FavoriteResolutionResult
    {
        public FavoriteResolutionState State { get; }
        public ObjectRefKey? ResolvedRuntimeKey { get; }
        public PersistentObjectId? UpgradedPersistentId { get; }
        public string ResolvedDisplayName { get; }
        public string ResolvedTypeName { get; }
        public string Detail { get; }

        public FavoriteResolutionResult(
            FavoriteResolutionState state,
            ObjectRefKey? resolvedRuntimeKey,
            PersistentObjectId? upgradedPersistentId,
            string resolvedDisplayName,
            string resolvedTypeName,
            string detail)
        {
            State = state;
            ResolvedRuntimeKey = resolvedRuntimeKey;
            UpgradedPersistentId = upgradedPersistentId;
            ResolvedDisplayName = resolvedDisplayName ?? string.Empty;
            ResolvedTypeName = resolvedTypeName ?? string.Empty;
            Detail = detail ?? string.Empty;
        }
    }

    public sealed class LoadResult<T>
    {
        public bool Succeeded { get; }
        public T Value { get; }
        public bool UsedBackup { get; }
        public bool FellBackToDefault { get; }
        public bool NewerSchemaDetected { get; }
        public string Notice { get; }

        public LoadResult(
            bool succeeded,
            T value,
            bool usedBackup = false,
            bool fellBackToDefault = false,
            bool newerSchemaDetected = false,
            string notice = null)
        {
            Succeeded = succeeded;
            Value = value;
            UsedBackup = usedBackup;
            FellBackToDefault = fellBackToDefault;
            NewerSchemaDetected = newerSchemaDetected;
            Notice = notice ?? string.Empty;
        }
    }

    public sealed class SaveResult
    {
        public bool Succeeded { get; }
        public bool BlockedByNewerSchema { get; }
        public string Error { get; }

        public SaveResult(bool succeeded, bool blockedByNewerSchema = false, string error = null)
        {
            Succeeded = succeeded;
            BlockedByNewerSchema = blockedByNewerSchema;
            Error = error ?? string.Empty;
        }

        public static SaveResult Ok() => new SaveResult(true);
        public static SaveResult Fail(string error) => new SaveResult(false, false, error);
    }

    public sealed class PerformanceMetrics
    {
        public double LastSelectionToCommitMs { get; }
        public double LastPlanExecutionMs { get; }
        public int CurrentQueueDepth { get; }
        public int PeakQueueDepth { get; }
        public long TotalCommittedPlans { get; }
        public long TotalWriteSkips { get; }
        public long TotalAllocBytesEstimate { get; }

        public PerformanceMetrics(
            double lastSelectionToCommitMs,
            double lastPlanExecutionMs,
            int currentQueueDepth,
            int peakQueueDepth,
            long totalCommittedPlans,
            long totalWriteSkips,
            long totalAllocBytesEstimate)
        {
            LastSelectionToCommitMs = lastSelectionToCommitMs;
            LastPlanExecutionMs = lastPlanExecutionMs;
            CurrentQueueDepth = currentQueueDepth;
            PeakQueueDepth = peakQueueDepth;
            TotalCommittedPlans = totalCommittedPlans;
            TotalWriteSkips = totalWriteSkips;
            TotalAllocBytesEstimate = totalAllocBytesEstimate;
        }
    }

    public interface IInspectorAdapter
    {
        AdapterCapabilities ProbeCapabilities();
        IReadOnlyList<ObservedWindowSnapshot> EnumerateInspectors();
        bool IsWindowAlive(WindowKey key, string expectedIdentityEvidence = null);
        InspectorReadResult ReadInspectorState(WindowKey key);
        AdapterWriteResult WriteLockOnly(WindowKey key, bool locked);
        AdapterWriteResult WriteTargetsAndLock(WindowKey key, IReadOnlyList<ObjectRefKey> targets, ObjectRefKey? activeTarget, bool locked);
        AdapterVerifyResult VerifyTargetsAndLock(WindowKey key, IReadOnlyList<ObjectRefKey> expectedTargets, bool expectedLocked);
        AdapterWindowCreateResult CreateInspectorWindow();
        AdapterResult CloseInspectorWindow(WindowKey key, string expectedIdentityEvidence);
        WindowKey? GetFocusedInspectorKey();

        /// <summary>
        /// True when the given Inspector is currently focused or under the mouse cursor.
        /// Used as evidence that an observed lock change came from the user via the standard Inspector UI (padlock etc.).
        /// </summary>
        bool IsUserInteractingWithInspector(WindowKey key);

        bool IsSelectionSourceContinuous();
        void FocusInspector(WindowKey key);
        void InvalidateCache();
    }

    public interface ISelectionAdapter
    {
        SelectionSnapshot CaptureCurrentSelection(long sequence, long generation, SelectionSource source);
        bool TrySetGlobalSelection(IReadOnlyList<ObjectRefKey> targets, ObjectRefKey? activeTarget, out string error);
        bool AreAllTargetsAlive(IReadOnlyList<ObjectRefKey> targets);
        bool IsTargetAlive(ObjectRefKey key);
        bool PingObject(ObjectRefKey key);
        bool TryStartDrag(IReadOnlyList<ObjectRefKey> targets, string title, out string error);
    }

    public interface IFavoriteResolver
    {
        FavoriteResolutionResult Resolve(FavoriteEntry entry);
        FavoriteEntry CreateFromLiveObject(ObjectRefKey key, int sortOrder);
        void InvalidateCache();
    }

    public interface IPersistenceStore : IDisposable
    {
        bool IsWriteBlockedByNewerSchema { get; }
        LoadResult<SettingsAndFavoritesDto> LoadSettingsAndFavorites();
        SaveResult QueueSaveSettingsAndFavorites(SettingsAndFavoritesDto dto, long revision);
        SaveResult FlushSettingsAndFavorites();
        LoadResult<RecoveryJournalDto> LoadRecoveryJournal();
        SaveResult SaveRecoveryJournalImmediate(RecoveryJournalDto dto);
        void ClearRecoveryJournal();
    }

    public interface ISessionLayoutStore
    {
        string GetEditorSessionId();
        SessionLayoutDto LoadSessionLayout();
        void SaveSessionLayout(SessionLayoutDto dto);
        void ClearSessionLayout();
    }

    public interface IMonotonicClock
    {
        long NowMs { get; }
    }

    public interface IDiagnosticLogger
    {
        void Info(string operationId, long generation, WindowKey? windowKey, string stage, string message);
        void Error(string operationId, long generation, WindowKey? windowKey, string stage, string message);
        IReadOnlyList<DiagnosticLogEntry> GetRecentEntries();
        void Clear();
    }

    public interface IOverlayPresenter : IDisposable
    {
        void SyncOverlays(ManagerState state, long nowMs, IReadOnlyCollection<WindowKey> flashedWindows);
        void ClearAllOverlays();
    }
}
