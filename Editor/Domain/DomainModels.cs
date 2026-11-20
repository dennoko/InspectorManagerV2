using System;
using System.Collections.Generic;
using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    public sealed class ObjectItemSnapshot
    {
        public ObjectRefKey Key { get; }
        public string DisplayName { get; }
        public string TypeName { get; }
        public ObjectCategory Category { get; }
        public bool IsAsset { get; }
        public bool ClassificationFailed => Category == ObjectCategory.ClassificationFailed;

        public ObjectItemSnapshot(
            ObjectRefKey key,
            string displayName,
            string typeName,
            ObjectCategory category,
            bool isAsset)
        {
            Key = key;
            DisplayName = string.IsNullOrEmpty(displayName) ? "(Unnamed)" : displayName;
            TypeName = typeName ?? string.Empty;
            Category = category;
            IsAsset = isAsset;
        }
    }

    public sealed class SelectionSnapshot
    {
        public static readonly SelectionSnapshot Empty = new SelectionSnapshot(
            Array.Empty<ObjectItemSnapshot>(),
            null,
            0,
            0,
            SelectionSource.UserSelection);

        public IReadOnlyList<ObjectItemSnapshot> OrderedTargets { get; }
        public ObjectRefKey? ActiveTarget { get; }
        public SelectionComparisonKey ComparisonKey { get; }
        public long Sequence { get; }
        public long Generation { get; }
        public SelectionSource Source { get; }
        public bool IsEmpty => OrderedTargets.Count == 0;

        public SelectionSnapshot(
            IReadOnlyList<ObjectItemSnapshot> orderedTargets,
            ObjectRefKey? activeTarget,
            long sequence,
            long generation,
            SelectionSource source)
        {
            var list = orderedTargets != null
                ? orderedTargets.Where(t => t != null && t.Key.IsValid).ToArray()
                : Array.Empty<ObjectItemSnapshot>();

            OrderedTargets = list;
            ComparisonKey = SelectionComparisonKey.Create(list.Select(t => t.Key), activeTarget);
            ActiveTarget = ComparisonKey.ActiveTarget;
            Sequence = sequence;
            Generation = generation;
            Source = source;
        }

        public string BuildDisplaySummary()
        {
            if (OrderedTargets.Count == 0)
            {
                return string.Empty;
            }

            if (OrderedTargets.Count == 1)
            {
                return OrderedTargets[0].DisplayName;
            }

            ObjectItemSnapshot primary = OrderedTargets[0];
            if (ActiveTarget.HasValue)
            {
                for (int i = 0; i < OrderedTargets.Count; i++)
                {
                    if (OrderedTargets[i].Key == ActiveTarget.Value)
                    {
                        primary = OrderedTargets[i];
                        break;
                    }
                }
            }

            return primary.DisplayName + " (+" + (OrderedTargets.Count - 1) + ")";
        }

        public IReadOnlyList<ObjectRefKey> GetOrderedKeys()
        {
            if (OrderedTargets.Count == 0)
            {
                return Array.Empty<ObjectRefKey>();
            }

            var keys = new ObjectRefKey[OrderedTargets.Count];
            for (int i = 0; i < OrderedTargets.Count; i++)
            {
                keys[i] = OrderedTargets[i].Key;
            }
            return keys;
        }

        public SelectionSnapshot WithSource(SelectionSource source)
        {
            return new SelectionSnapshot(OrderedTargets, ActiveTarget, Sequence, Generation, source);
        }
    }

    public sealed class WindowRecord
    {
        public WindowKey Key { get; }
        public int FixedDisplayNumber { get; }
        public int RoleOrder { get; }
        public bool IsManaged { get; }
        public bool IsAlive { get; }
        public bool IsExplicitlyExcluded { get; }
        public bool IsManuallyUnlocked { get; }
        public bool IsFaultIsolated { get; }
        public string FaultReason { get; }
        public bool ObservedLocked { get; }
        public SelectionSnapshot AssignedSelection { get; }
        public AssignmentLabel AssignmentLabel { get; }
        public long EntityGeneration { get; }
        public string ObservedRepresentativeName { get; }
        public string IdentityEvidence { get; }

        public bool CanParticipate =>
            IsManaged
            && IsAlive
            && !IsExplicitlyExcluded
            && !IsManuallyUnlocked
            && !IsFaultIsolated;

        public WindowRecord(
            WindowKey key,
            int fixedDisplayNumber,
            int roleOrder,
            bool isManaged,
            bool isAlive,
            bool isExplicitlyExcluded,
            bool isManuallyUnlocked,
            bool isFaultIsolated,
            string faultReason,
            bool observedLocked,
            SelectionSnapshot assignedSelection,
            AssignmentLabel assignmentLabel,
            long entityGeneration,
            string observedRepresentativeName,
            string identityEvidence)
        {
            Key = key;
            FixedDisplayNumber = fixedDisplayNumber;
            RoleOrder = roleOrder;
            IsManaged = isManaged;
            IsAlive = isAlive;
            IsExplicitlyExcluded = isExplicitlyExcluded;
            IsManuallyUnlocked = isManuallyUnlocked;
            IsFaultIsolated = isFaultIsolated;
            FaultReason = faultReason ?? string.Empty;
            ObservedLocked = observedLocked;
            AssignedSelection = assignedSelection;
            AssignmentLabel = assignmentLabel;
            EntityGeneration = entityGeneration;
            ObservedRepresentativeName = observedRepresentativeName ?? string.Empty;
            IdentityEvidence = identityEvidence ?? string.Empty;
        }

        public WindowRecord With(
            int? roleOrder = null,
            bool? isManaged = null,
            bool? isAlive = null,
            bool? isExplicitlyExcluded = null,
            bool? isManuallyUnlocked = null,
            bool? isFaultIsolated = null,
            string faultReason = null,
            bool? observedLocked = null,
            SelectionSnapshot assignedSelection = null,
            bool clearAssignedSelection = false,
            AssignmentLabel? assignmentLabel = null,
            long? entityGeneration = null,
            string observedRepresentativeName = null,
            string identityEvidence = null)
        {
            return new WindowRecord(
                Key,
                FixedDisplayNumber,
                roleOrder ?? RoleOrder,
                isManaged ?? IsManaged,
                isAlive ?? IsAlive,
                isExplicitlyExcluded ?? IsExplicitlyExcluded,
                isManuallyUnlocked ?? IsManuallyUnlocked,
                isFaultIsolated ?? IsFaultIsolated,
                faultReason ?? FaultReason,
                observedLocked ?? ObservedLocked,
                clearAssignedSelection ? null : (assignedSelection ?? AssignedSelection),
                assignmentLabel ?? AssignmentLabel,
                entityGeneration ?? EntityGeneration,
                observedRepresentativeName ?? ObservedRepresentativeName,
                identityEvidence ?? IdentityEvidence);
        }
    }

    public sealed class LockLease
    {
        public string LeaseId { get; }
        public WindowKey WindowKey { get; }
        public string IdentityEvidence { get; }
        public bool OriginalLocked { get; }
        public bool LastWrittenLocked { get; }
        public bool HasWritten { get; }
        public LeaseRestorationStatus RestorationStatus { get; }
        public string LastError { get; }

        public LockLease(
            string leaseId,
            WindowKey windowKey,
            string identityEvidence,
            bool originalLocked,
            bool lastWrittenLocked,
            bool hasWritten,
            LeaseRestorationStatus restorationStatus,
            string lastError)
        {
            LeaseId = leaseId ?? string.Empty;
            WindowKey = windowKey;
            IdentityEvidence = identityEvidence ?? string.Empty;
            OriginalLocked = originalLocked;
            LastWrittenLocked = lastWrittenLocked;
            HasWritten = hasWritten;
            RestorationStatus = restorationStatus;
            LastError = lastError ?? string.Empty;
        }

        public LockLease WithWrite(bool writtenLocked)
        {
            return new LockLease(
                LeaseId,
                WindowKey,
                IdentityEvidence,
                OriginalLocked,
                writtenLocked,
                true,
                LeaseRestorationStatus.Acquired,
                string.Empty);
        }

        public LockLease WithRestoration(LeaseRestorationStatus status, string error = null)
        {
            return new LockLease(
                LeaseId,
                WindowKey,
                IdentityEvidence,
                OriginalLocked,
                LastWrittenLocked,
                HasWritten,
                status,
                error ?? string.Empty);
        }
    }

    public sealed class ManagerSettings
    {
        public long SettingsRevision { get; }
        public string Language { get; }
        public int MaxRotationHistory { get; }
        public bool RecordSceneObjects { get; }
        public bool RecordAssets { get; }
        public bool ShowInspectorOverlays { get; }
        public IReadOnlyCollection<ObjectCategory> BlockedCategories { get; }

        public ManagerSettings(
            long settingsRevision,
            string language,
            int maxRotationHistory,
            bool recordSceneObjects,
            bool recordAssets,
            bool showInspectorOverlays,
            IEnumerable<ObjectCategory> blockedCategories)
        {
            SettingsRevision = settingsRevision;
            Language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ja";
            MaxRotationHistory = Clamp(maxRotationHistory, 4, 200);
            RecordSceneObjects = recordSceneObjects;
            RecordAssets = recordAssets;
            ShowInspectorOverlays = showInspectorOverlays;
            BlockedCategories = blockedCategories != null
                ? new HashSet<ObjectCategory>(blockedCategories)
                : new HashSet<ObjectCategory>();
        }

        public static ManagerSettings CreateDefault(long revision = 1)
        {
            return new ManagerSettings(
                settingsRevision: revision,
                language: "ja",
                maxRotationHistory: 20,
                recordSceneObjects: true,
                recordAssets: true,
                showInspectorOverlays: true,
                blockedCategories: Array.Empty<ObjectCategory>());
        }

        public ManagerSettings WithNextRevision(
            string language = null,
            int? maxRotationHistory = null,
            bool? recordSceneObjects = null,
            bool? recordAssets = null,
            bool? showInspectorOverlays = null,
            IEnumerable<ObjectCategory> blockedCategories = null)
        {
            return new ManagerSettings(
                SettingsRevision + 1,
                language ?? Language,
                maxRotationHistory ?? MaxRotationHistory,
                recordSceneObjects ?? RecordSceneObjects,
                recordAssets ?? RecordAssets,
                showInspectorOverlays ?? ShowInspectorOverlays,
                blockedCategories ?? BlockedCategories);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    public sealed class AdapterCapabilities
    {
        public bool CanEnumerateAndIdentify { get; }
        public bool CanReadLock { get; }
        public bool CanWriteLock { get; }
        public bool CanReadTargets { get; }
        public bool CanWriteTargets { get; }
        public bool CanCreateWindow { get; }
        public bool CanCloseWindow { get; }
        public bool UsesWindowCombinedTargetLockApi { get; }
        public bool SupportsActiveTargetSelection { get; }
        public bool SupportsEmptyTargetCompensation { get; }
        public string DiagnosticSummary { get; }

        public bool SupportsRotation =>
            CanEnumerateAndIdentify
            && CanReadLock
            && CanWriteLock
            && CanReadTargets
            && CanWriteTargets;

        public AdapterCapabilities(
            bool canEnumerateAndIdentify,
            bool canReadLock,
            bool canWriteLock,
            bool canReadTargets,
            bool canWriteTargets,
            bool canCreateWindow,
            bool canCloseWindow,
            bool usesWindowCombinedTargetLockApi,
            bool supportsActiveTargetSelection,
            bool supportsEmptyTargetCompensation,
            string diagnosticSummary)
        {
            CanEnumerateAndIdentify = canEnumerateAndIdentify;
            CanReadLock = canReadLock;
            CanWriteLock = canWriteLock;
            CanReadTargets = canReadTargets;
            CanWriteTargets = canWriteTargets;
            CanCreateWindow = canCreateWindow;
            CanCloseWindow = canCloseWindow;
            UsesWindowCombinedTargetLockApi = usesWindowCombinedTargetLockApi;
            SupportsActiveTargetSelection = supportsActiveTargetSelection;
            SupportsEmptyTargetCompensation = supportsEmptyTargetCompensation;
            DiagnosticSummary = diagnosticSummary ?? string.Empty;
        }

        public static AdapterCapabilities None(string reason = "Not probed")
        {
            return new AdapterCapabilities(false, false, false, false, false, false, false, false, false, false, reason);
        }

        public static AdapterCapabilities Full(bool supportsActiveTarget = false, bool supportsEmptyCompensation = true)
        {
            return new AdapterCapabilities(
                canEnumerateAndIdentify: true,
                canReadLock: true,
                canWriteLock: true,
                canReadTargets: true,
                canWriteTargets: true,
                canCreateWindow: true,
                canCloseWindow: true,
                usesWindowCombinedTargetLockApi: true,
                supportsActiveTargetSelection: supportsActiveTarget,
                supportsEmptyTargetCompensation: supportsEmptyCompensation,
                diagnosticSummary: "Full capabilities");
        }
    }

    public sealed class ManagerState
    {
        public RuntimeLifecycleState LifecycleState { get; }
        public long Generation { get; }
        public long LastProcessedSequence { get; }
        public SelectionComparisonKey LastAcceptedSelectionKey { get; }
        public SelectionSnapshot CurrentLiveSelection { get; }
        public ManagerSettings Settings { get; }
        public AdapterCapabilities Capabilities { get; }
        public IReadOnlyList<WindowRecord> Windows { get; }
        public int NextFixedDisplayNumber { get; }
        public IReadOnlyDictionary<WindowKey, LockLease> Leases { get; }
        public RotationHistoryState RotationHistory { get; }
        public string LastError { get; }
        public string StatusNotice { get; }
        public bool PersistenceWriteBlocked { get; }
        public long LayoutOverrideRevision { get; }
        public StopReason? LastStopReason { get; }

        public bool HasIncompleteRestorations =>
            Leases.Values.Any(l => l.RestorationStatus == LeaseRestorationStatus.RestoreFailed
                || (LifecycleState == RuntimeLifecycleState.Faulted && l.RestorationStatus == LeaseRestorationStatus.Acquired));

        public ManagerState(
            RuntimeLifecycleState lifecycleState,
            long generation,
            long lastProcessedSequence,
            SelectionComparisonKey lastAcceptedSelectionKey,
            SelectionSnapshot currentLiveSelection,
            ManagerSettings settings,
            AdapterCapabilities capabilities,
            IReadOnlyList<WindowRecord> windows,
            int nextFixedDisplayNumber,
            IReadOnlyDictionary<WindowKey, LockLease> leases,
            RotationHistoryState rotationHistory,
            string lastError,
            string statusNotice,
            bool persistenceWriteBlocked,
            long layoutOverrideRevision,
            StopReason? lastStopReason)
        {
            LifecycleState = lifecycleState;
            Generation = generation;
            LastProcessedSequence = lastProcessedSequence;
            LastAcceptedSelectionKey = lastAcceptedSelectionKey ?? SelectionComparisonKey.Empty;
            CurrentLiveSelection = currentLiveSelection ?? SelectionSnapshot.Empty;
            Settings = settings ?? ManagerSettings.CreateDefault();
            Capabilities = capabilities ?? AdapterCapabilities.None();
            Windows = windows ?? Array.Empty<WindowRecord>();
            NextFixedDisplayNumber = nextFixedDisplayNumber < 1 ? 1 : nextFixedDisplayNumber;
            Leases = leases ?? new Dictionary<WindowKey, LockLease>();
            RotationHistory = rotationHistory ?? RotationHistoryState.Empty;
            LastError = lastError ?? string.Empty;
            StatusNotice = statusNotice ?? string.Empty;
            PersistenceWriteBlocked = persistenceWriteBlocked;
            LayoutOverrideRevision = layoutOverrideRevision;
            LastStopReason = lastStopReason;
        }

        public static ManagerState CreateInitial(ManagerSettings settings = null, AdapterCapabilities capabilities = null)
        {
            return new ManagerState(
                RuntimeLifecycleState.Off,
                generation: 1,
                lastProcessedSequence: 0,
                lastAcceptedSelectionKey: SelectionComparisonKey.Empty,
                currentLiveSelection: SelectionSnapshot.Empty,
                settings: settings ?? ManagerSettings.CreateDefault(),
                capabilities: capabilities ?? AdapterCapabilities.None(),
                windows: Array.Empty<WindowRecord>(),
                nextFixedDisplayNumber: 1,
                leases: new Dictionary<WindowKey, LockLease>(),
                rotationHistory: RotationHistoryState.Empty,
                lastError: string.Empty,
                statusNotice: string.Empty,
                persistenceWriteBlocked: false,
                layoutOverrideRevision: 0,
                lastStopReason: null);
        }

        public ManagerState With(
            RuntimeLifecycleState? lifecycleState = null,
            long? generation = null,
            long? lastProcessedSequence = null,
            SelectionComparisonKey lastAcceptedSelectionKey = null,
            SelectionSnapshot currentLiveSelection = null,
            ManagerSettings settings = null,
            AdapterCapabilities capabilities = null,
            IReadOnlyList<WindowRecord> windows = null,
            int? nextFixedDisplayNumber = null,
            IReadOnlyDictionary<WindowKey, LockLease> leases = null,
            RotationHistoryState rotationHistory = null,
            string lastError = null,
            string statusNotice = null,
            bool? persistenceWriteBlocked = null,
            long? layoutOverrideRevision = null,
            StopReason? lastStopReason = null,
            bool clearLastStopReason = false)
        {
            return new ManagerState(
                lifecycleState ?? LifecycleState,
                generation ?? Generation,
                lastProcessedSequence ?? LastProcessedSequence,
                lastAcceptedSelectionKey ?? LastAcceptedSelectionKey,
                currentLiveSelection ?? CurrentLiveSelection,
                settings ?? Settings,
                capabilities ?? Capabilities,
                windows ?? Windows,
                nextFixedDisplayNumber ?? NextFixedDisplayNumber,
                leases ?? Leases,
                rotationHistory ?? RotationHistory,
                lastError ?? LastError,
                statusNotice ?? StatusNotice,
                persistenceWriteBlocked ?? PersistenceWriteBlocked,
                layoutOverrideRevision ?? LayoutOverrideRevision,
                clearLastStopReason ? (StopReason?)null : (lastStopReason ?? LastStopReason));
        }

        public IReadOnlyList<WindowRecord> GetManagedAliveWindowsOrdered()
        {
            return Windows
                .Where(w => w.IsManaged && w.IsAlive)
                .OrderBy(w => w.RoleOrder)
                .ThenBy(w => w.FixedDisplayNumber)
                .ToArray();
        }

        public IReadOnlyList<WindowRecord> GetParticipatingWindowsOrdered()
        {
            return Windows
                .Where(w => w.CanParticipate)
                .OrderBy(w => w.RoleOrder)
                .ThenBy(w => w.FixedDisplayNumber)
                .ToArray();
        }

        public WindowRecord FindWindow(WindowKey key)
        {
            for (int i = 0; i < Windows.Count; i++)
            {
                if (Windows[i].Key == key)
                {
                    return Windows[i];
                }
            }
            return null;
        }
    }
}
