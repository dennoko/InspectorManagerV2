using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Application
{
    public sealed class RuntimeCommand
    {
        public string Kind { get; }
        public string OperationId { get; }
        public long EnqueuedAtMs { get; }
        public long Generation { get; }
        public object Payload { get; }

        public RuntimeCommand(string kind, string operationId, long enqueuedAtMs, long generation, object payload = null)
        {
            Kind = kind;
            OperationId = operationId;
            EnqueuedAtMs = enqueuedAtMs;
            Generation = generation;
            Payload = payload;
        }
    }

    public sealed class UiSnapshot
    {
        public ManagerState State { get; }
        public bool HasPendingVerification { get; }
        public long CapturedAtMs { get; }

        public UiSnapshot(
            ManagerState state,
            bool hasPendingVerification,
            long capturedAtMs)
        {
            State = state;
            HasPendingVerification = hasPendingVerification;
            CapturedAtMs = capturedAtMs;
        }
    }

    /// <summary>
    /// Single-queue main-thread runtime controller for History-mode Inspector management.
    /// All UI events and Unity callbacks enqueue commands here and execute deterministically
    /// with bounded queue depth, per-tick budgets, lease restoration, and standard padlock detection.
    /// </summary>
    public sealed class InspectorManagerCoordinator : IDisposable
    {
        public const int DefaultMaxQueueSize = 128;
        public const int DefaultCommandsPerTickBudget = 4;
        public const long DefaultReconcileIntervalMs = 350;
        public const long DefaultPersistDebounceMs = 800;
        public const long DefaultReloadGraceMs = 1500;

        private readonly IInspectorAdapter _inspectorAdapter;
        private readonly ISelectionAdapter _selectionAdapter;
        private readonly IPersistenceStore _persistenceStore;
        private readonly ISessionLayoutStore _sessionLayoutStore;
        private readonly IMonotonicClock _clock;
        private readonly IDiagnosticLogger _logger;
        private readonly IOverlayPresenter _overlayPresenter;
        private readonly PlanExecutor _planExecutor;
        private readonly string _projectId;

        private readonly Queue<RuntimeCommand> _queue = new Queue<RuntimeCommand>();
        private readonly int _maxQueueSize;
        private readonly int _commandsPerTickBudget;
        private readonly long _reconcileIntervalMs;
        private readonly long _persistDebounceMs;
        private readonly long _reloadGraceMs;

        private ManagerState _state;
        private string _editorSessionId;
        private string _managementSessionId = string.Empty;
        private long _nextOpId = 1;
        private long _nextSelectionSeq = 1;
        private long _nextCommitSeq = 1;
        private long _lastReconcileMs;
        private long _persistDirtyAtMs = -1;
        private long _reloadGraceUntilMs = -1;
        private bool _isInputBlocked;
        private bool _disposed;

        public event Action StateChanged;

        public ManagerState CurrentState => _state;

        public InspectorManagerCoordinator(
            string projectId,
            IInspectorAdapter inspectorAdapter,
            ISelectionAdapter selectionAdapter,
            IPersistenceStore persistenceStore,
            ISessionLayoutStore sessionLayoutStore,
            IMonotonicClock clock,
            IDiagnosticLogger logger,
            IOverlayPresenter overlayPresenter = null,
            int maxQueueSize = DefaultMaxQueueSize,
            int commandsPerTickBudget = DefaultCommandsPerTickBudget,
            long reconcileIntervalMs = DefaultReconcileIntervalMs,
            long persistDebounceMs = DefaultPersistDebounceMs,
            long reloadGraceMs = DefaultReloadGraceMs,
            long pendingVerifyTimeoutMs = 500)
        {
            _projectId = projectId ?? string.Empty;
            _inspectorAdapter = inspectorAdapter ?? throw new ArgumentNullException(nameof(inspectorAdapter));
            _selectionAdapter = selectionAdapter ?? throw new ArgumentNullException(nameof(selectionAdapter));
            _persistenceStore = persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore));
            _sessionLayoutStore = sessionLayoutStore ?? throw new ArgumentNullException(nameof(sessionLayoutStore));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _overlayPresenter = overlayPresenter;

            _maxQueueSize = maxQueueSize > 0 ? maxQueueSize : DefaultMaxQueueSize;
            _commandsPerTickBudget = commandsPerTickBudget > 0 ? commandsPerTickBudget : DefaultCommandsPerTickBudget;
            _reconcileIntervalMs = reconcileIntervalMs >= 0 ? reconcileIntervalMs : DefaultReconcileIntervalMs;
            _persistDebounceMs = persistDebounceMs >= 0 ? persistDebounceMs : DefaultPersistDebounceMs;
            _reloadGraceMs = reloadGraceMs >= 0 ? reloadGraceMs : DefaultReloadGraceMs;

            _planExecutor = new PlanExecutor(
                _inspectorAdapter,
                _selectionAdapter,
                _persistenceStore,
                _clock,
                _logger,
                pendingVerifyTimeoutMs);

            _state = ManagerState.CreateInitial();
            _editorSessionId = _sessionLayoutStore.GetEditorSessionId() ?? Guid.NewGuid().ToString("N");
        }

        public void Initialize()
        {
            string opId = NextOperationId();
            _isInputBlocked = false;
            _editorSessionId = _sessionLayoutStore.GetEditorSessionId() ?? _editorSessionId;

            var loadSettings = _persistenceStore.LoadSettings();
            var settings = loadSettings.Value != null ? loadSettings.Value.ToDomainSettings() : ManagerSettings.CreateDefault();

            _state = ManagerState.CreateInitial(settings, AdapterCapabilities.None())
                .With(
                    persistenceWriteBlocked: loadSettings.NewerSchemaDetected || _persistenceStore.IsWriteBlockedByNewerSchema,
                    statusNotice: loadSettings.Notice);

            var caps = _inspectorAdapter.ProbeCapabilities();
            ApplyReduce(new CapabilitiesProbedEvent(opId, _state.Generation, caps));

            var sessionLayout = _sessionLayoutStore.LoadSessionLayout();
            if (sessionLayout != null && string.Equals(sessionLayout.EditorSessionId, _editorSessionId, StringComparison.Ordinal))
            {
                _reloadGraceUntilMs = _clock.NowMs + _reloadGraceMs;
                RestoreSessionLayoutIntoState(sessionLayout);
            }

            SynchronizeWindows(opId);
            RecoverPendingJournalOnStartup(opId);

            var currentSel = _selectionAdapter.CaptureCurrentSelection(_nextSelectionSeq++, _state.Generation, SelectionSource.RecoveryObservation);
            if (!currentSel.IsEmpty)
            {
                _state = _state.With(
                    lastAcceptedSelectionKey: currentSel.ComparisonKey,
                    currentLiveSelection: currentSel);
            }

            _lastReconcileMs = _clock.NowMs;
            SyncOverlays();
            NotifyStateChanged();
        }

        public UiSnapshot GetUiSnapshot()
        {
            return new UiSnapshot(
                _state,
                _planExecutor.HasPendingVerification,
                _clock.NowMs);
        }

        // --- Public Command Enqueue Entry Points ---

        public void EnqueueStartRotation() => Enqueue("StartRotation", null);
        public void EnqueueStopRotation(StopReason reason = StopReason.UserRequested) => Enqueue("StopRotation", reason);
        public void EnqueueRetryRestore() => Enqueue("RetryRestore", null);
        public void EnqueueAddInspector() => Enqueue("AddInspector", null);
        public void EnqueueAdoptInspector(WindowKey key) => Enqueue("AdoptInspector", key);
        public void EnqueueCloseInspector(WindowKey key) => Enqueue("CloseInspector", key);
        public void EnqueueToggleLock(WindowKey key) => Enqueue("ToggleLock", key);
        public void EnqueueSetExcluded(WindowKey key, bool excluded) => Enqueue("SetExcluded", (key, excluded));
        public void EnqueueReorderWindow(WindowKey key, int newRoleIndex) => Enqueue("ReorderWindow", (key, newRoleIndex));
        public void EnqueueFocusInspector(WindowKey key) => Enqueue("FocusInspector", key);
        public void EnqueueUpdateSettings(ManagerSettings newSettings, bool isExplicitLayoutChange = false) => Enqueue("UpdateSettings", (newSettings, isExplicitLayoutChange));
        public void EnqueueResetSettings() => Enqueue("ResetSettings", null);
        public void EnqueueResetLayout() => Enqueue("ResetLayout", null);
        public void EnqueueInvalidateEnvironmentCaches() => Enqueue("InvalidateEnvironmentCaches", null);
        public void EnqueueSynchronizeWindows() => Enqueue("SyncWindows", null);

        public void OnSelectionChangedImmediate()
        {
            if (_isInputBlocked || _disposed) return;

            var snapshot = _selectionAdapter.CaptureCurrentSelection(
                _nextSelectionSeq++,
                _state.Generation,
                SelectionSource.UserSelection);

            Enqueue("SelectionSnapshot", snapshot);
        }

        public void EnqueueSelectionSnapshot(SelectionSnapshot snapshot)
        {
            if (_isInputBlocked || _disposed) return;
            Enqueue("SelectionSnapshot", snapshot);
        }

        public void HandleLifecycleBoundaryStop(StopReason reason, bool suspendMode)
        {
            string opId = NextOperationId();
            _isInputBlocked = suspendMode || reason == StopReason.EditorQuitting;
            _queue.Clear();
            _planExecutor.CancelPendingVerification();
            _planExecutor.InvalidateAllCaches();
            _inspectorAdapter.InvalidateCache();

            PersistSessionLayoutNow();
            _persistenceStore.FlushSettings();
            _persistDirtyAtMs = -1;

            if (_state.Leases.Count > 0 || _state.LifecycleState == RuntimeLifecycleState.Running)
            {
                ApplyReduce(new StopSessionRequestedEvent(opId, _state.Generation, reason, suspendMode));
                var (updatedLeases, allRestored, error) = _planExecutor.RestoreLeases(
                    _state.Leases,
                    _state.Generation,
                    opId,
                    _editorSessionId,
                    _managementSessionId);

                ApplyReduce(new StopSessionCompletedEvent(opId, _state.Generation, updatedLeases, allRestored, error, remainSuspended: suspendMode));
            }
            else if (suspendMode)
            {
                ApplyReduce(new StopSessionRequestedEvent(opId, _state.Generation, reason, suspendMode: true));
            }

            if (reason == StopReason.EditorQuitting || reason == StopReason.LastManagerWindowClosed)
            {
                _overlayPresenter?.ClearAllOverlays();
            }
            else
            {
                SyncOverlays();
            }

            NotifyStateChanged();
        }

        public void ResumeInputAfterPlayModeReturn()
        {
            _isInputBlocked = false;
            _inspectorAdapter.InvalidateCache();
            _planExecutor.InvalidateAllCaches();

            if (_state.LifecycleState == RuntimeLifecycleState.Suspended)
            {
                _state = _state.With(lifecycleState: RuntimeLifecycleState.Off, statusNotice: "Off (returned from Play Mode).");
            }

            EnqueueSynchronizeWindows();
            EnqueueInvalidateEnvironmentCaches();
            NotifyStateChanged();
        }

        public void Tick()
        {
            if (_disposed) return;

            long now = _clock.NowMs;
            bool stateDirty = false;
            var flashedWindows = new HashSet<WindowKey>();

            // 1. Poll deferred PresentationPending verification first.
            if (_planExecutor.HasPendingVerification)
            {
                var pendingOutcome = _planExecutor.PollPendingVerification(_state, _editorSessionId, _managementSessionId);
                if (pendingOutcome != null)
                {
                    HandlePlanOutcome(pendingOutcome, flashedWindows);
                    stateDirty = true;
                }
            }

            // 2. Process queued commands within per-tick budget.
            int processed = 0;
            while (processed < _commandsPerTickBudget && _queue.Count > 0)
            {
                if (_planExecutor.HasPendingVerification)
                {
                    var peek = _queue.Peek();
                    if (IsWritePlanCommand(peek.Kind))
                    {
                        break;
                    }
                }

                var cmd = _queue.Dequeue();
                processed++;
                if (ExecuteCommand(cmd, flashedWindows))
                {
                    stateDirty = true;
                }
            }

            // 3. Low-frequency reconciliation (window changes, padlock unlock/relock, unknown lock drop recovery).
            if (!_isInputBlocked && !_planExecutor.HasPendingVerification && (now - _lastReconcileMs >= _reconcileIntervalMs))
            {
                _lastReconcileMs = now;
                if (RunReconciliation(now, flashedWindows))
                {
                    stateDirty = true;
                }
            }

            // 4. Debounced settings persistence flush.
            if (_persistDirtyAtMs >= 0 && (now - _persistDirtyAtMs >= _persistDebounceMs))
            {
                _persistDirtyAtMs = -1;
                var saveRes = _persistenceStore.FlushSettings();
                if (!saveRes.Succeeded)
                {
                    ApplyReduce(new StatusNoticeEvent(
                        NextOperationId(),
                        _state.Generation,
                        notice: saveRes.BlockedByNewerSchema ? "Saving paused: newer schema detected." : "Settings save failed.",
                        error: saveRes.Error,
                        persistenceBlocked: saveRes.BlockedByNewerSchema));
                    stateDirty = true;
                }
            }

            if (stateDirty || flashedWindows.Count > 0)
            {
                SyncOverlays(flashedWindows);
                NotifyStateChanged();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _persistenceStore.FlushSettings();
            _overlayPresenter?.ClearAllOverlays();
            _overlayPresenter?.Dispose();
            _persistenceStore.Dispose();
        }

        private void Enqueue(string kind, object payload)
        {
            if (_disposed) return;
            if (_isInputBlocked && kind != "StopRotation") return;

            if (_queue.Count >= _maxQueueSize)
            {
                string opId = NextOperationId();
                _logger.Error(opId, _state.Generation, null, "QueueOverflow", "Command queue limit reached (" + _maxQueueSize + "); stopping auto-rotation.");
                _queue.Clear();
                if (_state.LifecycleState == RuntimeLifecycleState.Running)
                {
                    HandleLifecycleBoundaryStop(StopReason.QueueOverflow, suspendMode: false);
                    _isInputBlocked = false;
                }
                ApplyReduce(new StatusNoticeEvent(
                    opId,
                    _state.Generation,
                    notice: "Queue limit exceeded: unprocessed history was dropped and auto-rotation stopped.",
                    error: "Queue overflow"));
                NotifyStateChanged();
                return;
            }

            _queue.Enqueue(new RuntimeCommand(kind, NextOperationId(), _clock.NowMs, _state.Generation, payload));
        }

        private bool ExecuteCommand(RuntimeCommand cmd, HashSet<WindowKey> flashedWindows)
        {
            switch (cmd.Kind)
            {
                case "StartRotation":
                    ExecuteStartRotation(cmd.OperationId, flashedWindows);
                    return true;

                case "StopRotation":
                    ExecuteStopRotation(cmd.OperationId, cmd.Payload is StopReason r ? r : StopReason.UserRequested);
                    return true;

                case "RetryRestore":
                    ExecuteRetryRestore(cmd.OperationId);
                    return true;

                case "SelectionSnapshot":
                    if (cmd.Payload is SelectionSnapshot snap)
                    {
                        ApplyReduceAndRunEffects(new SelectionReceivedEvent(cmd.OperationId, _state.Generation, snap), flashedWindows);
                        return true;
                    }
                    return false;

                case "AddInspector":
                    ExecuteAddInspector(cmd.OperationId, flashedWindows);
                    return true;

                case "AdoptInspector":
                    if (cmd.Payload is WindowKey adoptKey)
                    {
                        ExecuteAdoptInspector(cmd.OperationId, adoptKey, flashedWindows);
                        return true;
                    }
                    return false;

                case "CloseInspector":
                    if (cmd.Payload is WindowKey closeKey)
                    {
                        ExecuteCloseInspector(cmd.OperationId, closeKey, flashedWindows);
                        return true;
                    }
                    return false;

                case "ToggleLock":
                    if (cmd.Payload is WindowKey toggleKey)
                    {
                        ExecuteToggleLock(cmd.OperationId, toggleKey, flashedWindows);
                        return true;
                    }
                    return false;

                case "SetExcluded":
                    if (cmd.Payload is ValueTuple<WindowKey, bool> exTuple)
                    {
                        ExecuteSetExcluded(cmd.OperationId, exTuple.Item1, exTuple.Item2, flashedWindows);
                        return true;
                    }
                    return false;

                case "ReorderWindow":
                    if (cmd.Payload is ValueTuple<WindowKey, int> roTuple)
                    {
                        ApplyReduceAndRunEffects(new WindowReorderedEvent(cmd.OperationId, _state.Generation, roTuple.Item1, roTuple.Item2), flashedWindows);
                        return true;
                    }
                    return false;

                case "FocusInspector":
                    if (cmd.Payload is WindowKey focusKey)
                    {
                        _inspectorAdapter.FocusInspector(focusKey);
                        return false;
                    }
                    return false;

                case "UpdateSettings":
                    if (cmd.Payload is ValueTuple<ManagerSettings, bool> sTuple)
                    {
                        ApplyReduceAndRunEffects(new SettingsUpdatedEvent(cmd.OperationId, _state.Generation, sTuple.Item1, sTuple.Item2), flashedWindows);
                        return true;
                    }
                    return false;

                case "ResetSettings":
                {
                    var resetSettings = ManagerSettings.CreateDefault(_state.Settings.SettingsRevision + 1);
                    ApplyReduceAndRunEffects(new SettingsUpdatedEvent(cmd.OperationId, _state.Generation, resetSettings, isExplicitLayoutChange: true), flashedWindows);
                    return true;
                }

                case "ResetLayout":
                    ApplyReduceAndRunEffects(new ResetLayoutEvent(cmd.OperationId, _state.Generation), flashedWindows);
                    return true;

                case "InvalidateEnvironmentCaches":
                    _inspectorAdapter.InvalidateCache();
                    _planExecutor.InvalidateAllCaches();
                    return true;

                case "SyncWindows":
                    SynchronizeWindows(cmd.OperationId);
                    return true;

                default:
                    return false;
            }
        }

        private void ExecuteStartRotation(string opId, HashSet<WindowKey> flashedWindows)
        {
            var caps = _inspectorAdapter.ProbeCapabilities();
            ApplyReduce(new CapabilitiesProbedEvent(opId, _state.Generation, caps));
            if (!caps.SupportsRotation)
            {
                return;
            }

            SynchronizeWindows(opId);
            ApplyReduce(new StartSessionRequestedEvent(opId, _state.Generation));
            if (_state.LifecycleState != RuntimeLifecycleState.Starting)
            {
                return;
            }

            _managementSessionId = Guid.NewGuid().ToString("N");
            var managedWindows = _state.GetManagedAliveWindowsOrdered();
            var leases = new Dictionary<WindowKey, LockLease>();
            var observedDisplays = new List<SelectionSnapshot>();

            var reads = new Dictionary<WindowKey, InspectorReadResult>();
            foreach (var w in managedWindows)
            {
                var read = _inspectorAdapter.ReadInspectorState(w.Key);
                if (!read.IsReadable)
                {
                    ApplyReduce(new StartSessionCompletedEvent(
                        opId,
                        _state.Generation,
                        succeeded: false,
                        leases: leases,
                        seededRotationHistory: RotationHistoryState.Empty,
                        errorMessage: "Failed to read Inspector #" + w.FixedDisplayNumber + ": " + read.Error,
                        restorationIncomplete: false));
                    return;
                }

                reads[w.Key] = read;
                if (read.Status == InspectorReadStatus.Populated && !read.ObservedSelection.IsEmpty)
                {
                    observedDisplays.Add(read.ObservedSelection);
                }

                leases[w.Key] = new LockLease(
                    Guid.NewGuid().ToString("N"),
                    w.Key,
                    string.IsNullOrEmpty(read.IdentityEvidence) ? w.IdentityEvidence : read.IdentityEvidence,
                    originalLocked: read.ObservedLocked,
                    lastWrittenLocked: read.ObservedLocked,
                    hasWritten: false,
                    restorationStatus: LeaseRestorationStatus.Acquired,
                    lastError: string.Empty);
            }

            if (leases.Count > 0)
            {
                var journalSave = SaveRecoveryJournal(leases);
                if (!journalSave.Succeeded)
                {
                    ApplyReduce(new StartSessionCompletedEvent(
                        opId,
                        _state.Generation,
                        succeeded: false,
                        leases: new Dictionary<WindowKey, LockLease>(),
                        seededRotationHistory: RotationHistoryState.Empty,
                        errorMessage: "Cannot acquire lock leases because recovery journal could not be saved: " + journalSave.Error,
                        restorationIncomplete: false));
                    return;
                }
            }

            var acquiredSoFar = new Dictionary<WindowKey, LockLease>();
            foreach (var w in managedWindows)
            {
                var lease = leases[w.Key];
                bool shouldLock = !w.IsManuallyUnlocked;
                if (shouldLock && !lease.OriginalLocked)
                {
                    var writeRes = _inspectorAdapter.WriteLockOnly(w.Key, true);
                    if (!writeRes.IsSuccess || !writeRes.ObservedLocked)
                    {
                        var (restored, allRestored, restoreErr) = _planExecutor.RestoreLeases(
                            acquiredSoFar,
                            _state.Generation,
                            opId,
                            _editorSessionId,
                            _managementSessionId);

                        ApplyReduce(new StartSessionCompletedEvent(
                            opId,
                            _state.Generation,
                            succeeded: false,
                            leases: restored,
                            seededRotationHistory: RotationHistoryState.Empty,
                            errorMessage: "Failed to lock Inspector #" + w.FixedDisplayNumber + ": " + writeRes.Error + (string.IsNullOrEmpty(restoreErr) ? "" : " | " + restoreErr),
                            restorationIncomplete: !allRestored));
                        return;
                    }

                    lease = lease.WithWrite(true);
                }

                acquiredSoFar[w.Key] = lease;
                leases[w.Key] = lease;
                _planExecutor.SeedCacheFromObservedRead(w.Key, w.EntityGeneration, reads[w.Key], _state.Capabilities.SupportsActiveTargetSelection);
            }

            if (leases.Count > 0)
            {
                SaveRecoveryJournal(leases);
            }

            int capacity = Math.Max(_state.Settings.MaxRotationHistory, managedWindows.Count);
            var seededHistory = RotationHistoryState.BuildSeeded(
                _state.CurrentLiveSelection,
                observedDisplays,
                IsSelectionEligibleForRotation,
                capacity);

            ApplyReduceAndRunEffects(new StartSessionCompletedEvent(
                opId,
                _state.Generation,
                succeeded: true,
                leases: leases,
                seededRotationHistory: seededHistory,
                errorMessage: string.Empty,
                restorationIncomplete: false), flashedWindows);
        }

        private void ExecuteStopRotation(string opId, StopReason reason)
        {
            _planExecutor.CancelPendingVerification();
            _planExecutor.InvalidateAllCaches();

            ApplyReduce(new StopSessionRequestedEvent(opId, _state.Generation, reason, suspendMode: false));

            var (updatedLeases, allRestored, error) = _planExecutor.RestoreLeases(
                _state.Leases,
                _state.Generation,
                opId,
                _editorSessionId,
                _managementSessionId);

            ApplyReduce(new StopSessionCompletedEvent(opId, _state.Generation, updatedLeases, allRestored, error));
        }

        private void ExecuteRetryRestore(string opId)
        {
            if (_state.Leases.Count == 0)
            {
                ApplyReduce(new RetryRestoreCompletedEvent(opId, _state.Generation, _state.Leases, true, string.Empty));
                return;
            }

            var (updatedLeases, allRestored, error) = _planExecutor.RestoreLeases(
                _state.Leases,
                _state.Generation,
                opId,
                _editorSessionId,
                _managementSessionId);

            ApplyReduce(new RetryRestoreCompletedEvent(opId, _state.Generation, updatedLeases, allRestored, error));
        }

        private void ExecuteAddInspector(string opId, HashSet<WindowKey> flashedWindows)
        {
            if (!_state.Capabilities.CanCreateWindow)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Creating Inspector windows is not supported by the current adapter."));
                return;
            }

            var created = _inspectorAdapter.CreateInspectorWindow();
            if (!created.Succeeded || !created.WindowKey.IsValid)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to create Inspector window.", created.Error));
                return;
            }

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running;
            LockLease lease = null;
            bool finalLocked = created.ObservedLocked;

            if (isSessionActive)
            {
                lease = new LockLease(
                    Guid.NewGuid().ToString("N"),
                    created.WindowKey,
                    created.IdentityEvidence,
                    originalLocked: created.ObservedLocked,
                    lastWrittenLocked: created.ObservedLocked,
                    hasWritten: false,
                    restorationStatus: LeaseRestorationStatus.Acquired,
                    lastError: string.Empty);

                var tempLeases = new Dictionary<WindowKey, LockLease>(_state.Leases) { [created.WindowKey] = lease };
                var journalSave = SaveRecoveryJournal(tempLeases);
                if (!journalSave.Succeeded)
                {
                    if (created.ConfirmedEmptyNewWindow)
                    {
                        _inspectorAdapter.CloseInspectorWindow(created.WindowKey, created.IdentityEvidence);
                    }
                    ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to register new Inspector (journal write failed).", journalSave.Error));
                    return;
                }

                var lockRes = _inspectorAdapter.WriteLockOnly(created.WindowKey, true);
                if (!lockRes.IsSuccess || !lockRes.ObservedLocked)
                {
                    if (created.ConfirmedEmptyNewWindow)
                    {
                        _inspectorAdapter.CloseInspectorWindow(created.WindowKey, created.IdentityEvidence);
                    }
                    ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to lock newly created Inspector.", lockRes.Error));
                    return;
                }

                lease = lease.WithWrite(true);
                finalLocked = true;
                tempLeases[created.WindowKey] = lease;
                SaveRecoveryJournal(tempLeases);
            }

            ApplyReduceAndRunEffects(new WindowAdoptedEvent(opId, _state.Generation, created.WindowKey, finalLocked, created.IdentityEvidence, lease), flashedWindows);
        }

        private void ExecuteAdoptInspector(string opId, WindowKey key, HashSet<WindowKey> flashedWindows)
        {
            var win = _state.FindWindow(key);
            if (win == null || !win.IsAlive) return;

            var read = _inspectorAdapter.ReadInspectorState(key);
            if (!read.IsReadable)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Cannot manage Inspector: unable to read state.", read.Error));
                return;
            }

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running;
            LockLease lease = null;
            bool finalLocked = read.ObservedLocked;

            if (isSessionActive)
            {
                if (_state.Leases.TryGetValue(key, out var existingLease))
                {
                    lease = existingLease;
                }
                else
                {
                    lease = new LockLease(
                        Guid.NewGuid().ToString("N"),
                        key,
                        string.IsNullOrEmpty(read.IdentityEvidence) ? win.IdentityEvidence : read.IdentityEvidence,
                        originalLocked: read.ObservedLocked,
                        lastWrittenLocked: read.ObservedLocked,
                        hasWritten: false,
                        restorationStatus: LeaseRestorationStatus.Acquired,
                        lastError: string.Empty);

                    var tempLeases = new Dictionary<WindowKey, LockLease>(_state.Leases) { [key] = lease };
                    var journalSave = SaveRecoveryJournal(tempLeases);
                    if (!journalSave.Succeeded)
                    {
                        ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to save recovery journal before adopting Inspector.", journalSave.Error));
                        return;
                    }
                }

                if (!read.ObservedLocked)
                {
                    var lockRes = _inspectorAdapter.WriteLockOnly(key, true);
                    if (!lockRes.IsSuccess || !lockRes.ObservedLocked)
                    {
                        ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to lock adopted Inspector.", lockRes.Error));
                        return;
                    }
                    lease = lease.WithWrite(true);
                    finalLocked = true;
                    var updated = new Dictionary<WindowKey, LockLease>(_state.Leases) { [key] = lease };
                    SaveRecoveryJournal(updated);
                }
            }

            ApplyReduceAndRunEffects(new WindowAdoptedEvent(opId, _state.Generation, key, finalLocked, read.IdentityEvidence, lease), flashedWindows);
        }

        private void ExecuteCloseInspector(string opId, WindowKey key, HashSet<WindowKey> flashedWindows)
        {
            var win = _state.FindWindow(key);
            if (win == null) return;

            if (!_inspectorAdapter.IsWindowAlive(key, win.IdentityEvidence))
            {
                LockLease disposedLease = _state.Leases.TryGetValue(key, out var l)
                    ? l.WithRestoration(LeaseRestorationStatus.WindowDisposed)
                    : null;
                ApplyReduceAndRunEffects(new WindowClosedEvent(opId, _state.Generation, key, disposedLease), flashedWindows);
                return;
            }

            LockLease finalLease = null;
            if (_state.Leases.TryGetValue(key, out var lease) && lease.RestorationStatus == LeaseRestorationStatus.Acquired)
            {
                var restoreRes = _inspectorAdapter.WriteLockOnly(key, lease.OriginalLocked);
                if (!restoreRes.IsSuccess || restoreRes.ObservedLocked != lease.OriginalLocked)
                {
                    ApplyReduce(new StatusNoticeEvent(
                        opId,
                        _state.Generation,
                        "Lock restoration failed before closing Inspector; window was kept open for retry.",
                        restoreRes.Error));
                    return;
                }
                finalLease = lease.WithRestoration(LeaseRestorationStatus.Restored);
            }

            var closeRes = _inspectorAdapter.CloseInspectorWindow(key, win.IdentityEvidence);
            if (!closeRes.Succeeded)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to close Inspector window.", closeRes.Error));
                return;
            }

            _planExecutor.InvalidateWindowCache(key);
            ApplyReduceAndRunEffects(new WindowClosedEvent(opId, _state.Generation, key, finalLease), flashedWindows);
            if (_state.Leases.Count > 0) SaveRecoveryJournal(_state.Leases);
            else _persistenceStore.ClearRecoveryJournal();
        }

        private void ExecuteToggleLock(string opId, WindowKey key, HashSet<WindowKey> flashedWindows)
        {
            var win = _state.FindWindow(key);
            if (win == null || !win.IsAlive) return;

            var read = _inspectorAdapter.ReadInspectorState(key);
            if (!read.IsReadable)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Unable to read Inspector lock state.", read.Error));
                return;
            }

            bool desiredLock = !read.ObservedLocked;
            var writeRes = _inspectorAdapter.WriteLockOnly(key, desiredLock);
            if (!writeRes.IsSuccess || writeRes.ObservedLocked != desiredLock)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to change Inspector lock state.", writeRes.Error));
                return;
            }

            _planExecutor.InvalidateWindowCache(key);

            LockLease updatedLease = null;
            if (_state.Leases.TryGetValue(key, out var existingLease))
            {
                updatedLease = existingLease.WithWrite(desiredLock);
            }

            ApplyReduceAndRunEffects(new ManualLockChangedEvent(opId, _state.Generation, key, desiredLock, updatedLease), flashedWindows);
        }

        private void ExecuteSetExcluded(string opId, WindowKey key, bool excluded, HashSet<WindowKey> flashedWindows)
        {
            var win = _state.FindWindow(key);
            if (win == null || !win.IsAlive) return;

            var read = _inspectorAdapter.ReadInspectorState(key);
            if (!read.IsReadable)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Cannot update exclusion: Inspector read failed.", read.Error));
                return;
            }

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running;
            LockLease lease = null;
            bool finalLocked = read.ObservedLocked;

            // While Off there is no lease to restore from, so only the flag is recorded; Start locks excluded windows under a lease.
            if (excluded && isSessionActive)
            {
                if (!_state.Leases.ContainsKey(key))
                {
                    lease = new LockLease(
                        Guid.NewGuid().ToString("N"),
                        key,
                        string.IsNullOrEmpty(read.IdentityEvidence) ? win.IdentityEvidence : read.IdentityEvidence,
                        originalLocked: read.ObservedLocked,
                        lastWrittenLocked: read.ObservedLocked,
                        hasWritten: false,
                        restorationStatus: LeaseRestorationStatus.Acquired,
                        lastError: string.Empty);

                    var temp = new Dictionary<WindowKey, LockLease>(_state.Leases) { [key] = lease };
                    var journalSave = SaveRecoveryJournal(temp);
                    if (!journalSave.Succeeded)
                    {
                        ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to save recovery journal for exclusion.", journalSave.Error));
                        return;
                    }
                }

                if (!read.ObservedLocked)
                {
                    var lockRes = _inspectorAdapter.WriteLockOnly(key, true);
                    if (!lockRes.IsSuccess || !lockRes.ObservedLocked)
                    {
                        ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to lock Inspector on exclusion.", lockRes.Error));
                        return;
                    }
                    finalLocked = true;
                    if (lease != null) lease = lease.WithWrite(true);
                }
            }

            _planExecutor.InvalidateWindowCache(key);
            ApplyReduceAndRunEffects(new ExplicitExclusionChangedEvent(opId, _state.Generation, key, excluded, finalLocked, lease), flashedWindows);
        }

        private void ApplyReduce(DomainEvent evt)
        {
            var result = StateReducer.Reduce(_state, evt);
            _state = result.State;
        }

        private void ApplyReduceAndRunEffects(DomainEvent evt, HashSet<WindowKey> flashedWindows)
        {
            var result = StateReducer.Reduce(_state, evt);
            _state = result.State;

            for (int i = 0; i < result.Effects.Count; i++)
            {
                switch (result.Effects[i])
                {
                    case RequestEvaluateCurrentSelectionEffect eval:
                        RunAssignmentPlan(eval.Snapshot, flashedWindows);
                        break;

                    case RequestReassignHistoryEffect reassign:
                        RunHistoryReassignment(reassign.SeedFromObservedIfEmpty, flashedWindows);
                        break;

                    case RequestRecoverLockDropEffect recover:
                        RecoverUnknownLockDrop(recover.WindowKey, flashedWindows);
                        break;

                    case RequestPersistSettingsEffect _:
                        QueuePersistSettings();
                        break;

                    case RequestPersistSessionLayoutEffect _:
                        PersistSessionLayoutNow();
                        break;
                }
            }
        }

        private void RunAssignmentPlan(SelectionSnapshot incoming, HashSet<WindowKey> flashedWindows)
        {
            if (_state.LifecycleState != RuntimeLifecycleState.Running)
            {
                return;
            }

            var participating = _state.GetParticipatingWindowsOrdered();
            if (participating.Count == 0)
            {
                return;
            }

            string planId = NextOperationId();
            long seq = _nextCommitSeq++;
            var plan = AssignmentPlanner.CreateHistoryPlan(
                planId,
                _state.Generation,
                seq,
                participating,
                _state.RotationHistory,
                incoming,
                IsSelectionEligibleForRotation,
                _state.Settings.MaxRotationHistory);

            var outcome = _planExecutor.ExecutePlan(_state, plan, _editorSessionId, _managementSessionId);
            HandlePlanOutcome(outcome, flashedWindows);
        }

        private void RunHistoryReassignment(bool seedFromObservedIfEmpty, HashSet<WindowKey> flashedWindows)
        {
            if (_state.LifecycleState != RuntimeLifecycleState.Running)
            {
                return;
            }

            var participating = _state.GetParticipatingWindowsOrdered();
            if (participating.Count == 0)
            {
                return;
            }

            RotationHistoryState historyToUse = _state.RotationHistory;
            if (seedFromObservedIfEmpty && historyToUse.Entries.Count == 0)
            {
                var observedDisplays = new List<SelectionSnapshot>();
                foreach (var w in participating)
                {
                    var read = _inspectorAdapter.ReadInspectorState(w.Key);
                    if (read.Status == InspectorReadStatus.Populated && !read.ObservedSelection.IsEmpty)
                    {
                        observedDisplays.Add(read.ObservedSelection);
                    }
                }

                historyToUse = RotationHistoryState.BuildSeeded(
                    _state.CurrentLiveSelection,
                    observedDisplays,
                    IsSelectionEligibleForRotation,
                    Math.Max(_state.Settings.MaxRotationHistory, participating.Count));
            }

            string planId = NextOperationId();
            var plan = AssignmentPlanner.CreateHistoryPlan(
                planId,
                _state.Generation,
                _nextCommitSeq++,
                participating,
                historyToUse,
                incomingSelection: null,
                IsSelectionEligibleForRotation,
                _state.Settings.MaxRotationHistory);

            var outcome = _planExecutor.ExecutePlan(_state, plan, _editorSessionId, _managementSessionId);
            HandlePlanOutcome(outcome, flashedWindows);
        }

        private void HandlePlanOutcome(PlanExecutionOutcome outcome, HashSet<WindowKey> flashedWindows)
        {
            if (outcome == null) return;

            switch (outcome.Kind)
            {
                case PlanExecutionOutcomeKind.Committed:
                {
                    ApplyReduce(new PlanCommittedEvent(
                        outcome.Plan.PlanId,
                        _state.Generation,
                        outcome.Plan,
                        outcome.Plan.Sequence,
                        outcome.VerifiedLocks));

                    foreach (var t in outcome.Plan.Targets)
                    {
                        if (!t.KeepCurrentDisplay)
                        {
                            flashedWindows.Add(t.WindowKey);
                        }
                    }
                    break;
                }

                case PlanExecutionOutcomeKind.PendingVerification:
                    break;

                case PlanExecutionOutcomeKind.AbortedNoWrite:
                    if (!string.IsNullOrEmpty(outcome.Error))
                    {
                        ApplyReduce(new StatusNoticeEvent(outcome.Plan?.PlanId ?? NextOperationId(), _state.Generation, outcome.Error));
                    }
                    break;

                case PlanExecutionOutcomeKind.FailedCompensated:
                    ApplyReduce(new PlanFailedEvent(
                        outcome.Plan?.PlanId ?? NextOperationId(),
                        _state.Generation,
                        outcome.Error,
                        outcome.UpdatedLeases,
                        outcome.AllLeasesRestored,
                        outcome.FaultedWindowKey));
                    break;
            }
        }

        private bool RunReconciliation(long now, HashSet<WindowKey> flashedWindows)
        {
            string opId = NextOperationId();
            bool changed = false;

            var observed = _inspectorAdapter.EnumerateInspectors();
            bool inReloadGrace = _reloadGraceUntilMs > 0 && now < _reloadGraceUntilMs;
            if (_reloadGraceUntilMs > 0 && now >= _reloadGraceUntilMs)
            {
                _reloadGraceUntilMs = -1;
            }

            // Detect lock changes made outside Manager commands BEFORE standard sync overwrites state.
            // - Unlock of a participating window while user is interacting with that Inspector (focused/hovered) -> manual unlock via standard padlock.
            // - Unlock without such evidence -> unknown lock drop (e.g. tracker rebuild) -> recover.
            // - Re-lock of a manually unlocked window -> returns it to rotation.
            var unknownDrops = new List<WindowKey>();
            var standardUiUnlocks = new List<WindowKey>();
            var standardUiRelocks = new List<WindowKey>();
            if (_state.LifecycleState == RuntimeLifecycleState.Running)
            {
                var obsMap = new Dictionary<WindowKey, ObservedWindowSnapshot>();
                for (int i = 0; i < observed.Count; i++) obsMap[observed[i].Key] = observed[i];

                for (int i = 0; i < _state.Windows.Count; i++)
                {
                    var w = _state.Windows[i];
                    if (!obsMap.TryGetValue(w.Key, out var obs) || !obs.IsLockReadable)
                    {
                        continue;
                    }

                    if (w.CanParticipate && !obs.ObservedLocked)
                    {
                        if (_inspectorAdapter.IsUserInteractingWithInspector(w.Key))
                        {
                            standardUiUnlocks.Add(w.Key);
                        }
                        else
                        {
                            unknownDrops.Add(w.Key);
                        }
                    }
                    else if (w.IsManaged && w.IsAlive && w.IsManuallyUnlocked && obs.ObservedLocked)
                    {
                        standardUiRelocks.Add(w.Key);
                    }
                }
            }

            var prevWindows = _state.Windows;
            ApplyReduce(new WindowsSynchronizedEvent(opId, _state.Generation, observed, inReloadGrace));
            if (!ReferenceEquals(prevWindows, _state.Windows))
            {
                changed = true;
            }

            for (int i = 0; i < standardUiUnlocks.Count; i++)
            {
                var key = standardUiUnlocks[i];
                _planExecutor.InvalidateWindowCache(key);
                LockLease updatedLease = _state.Leases.TryGetValue(key, out var existingLease)
                    ? existingLease.WithWrite(false)
                    : null;
                ApplyReduceAndRunEffects(new ManualLockChangedEvent(opId, _state.Generation, key, newLocked: false, updatedLease), flashedWindows);
                changed = true;
            }

            for (int i = 0; i < standardUiRelocks.Count; i++)
            {
                var key = standardUiRelocks[i];
                _planExecutor.InvalidateWindowCache(key);
                LockLease updatedLease = _state.Leases.TryGetValue(key, out var existingLease)
                    ? existingLease.WithWrite(true)
                    : null;
                ApplyReduceAndRunEffects(new ManualLockChangedEvent(opId, _state.Generation, key, newLocked: true, updatedLease), flashedWindows);
                changed = true;
            }

            for (int i = 0; i < unknownDrops.Count; i++)
            {
                ApplyReduceAndRunEffects(new UnknownLockDropDetectedEvent(opId, _state.Generation, unknownDrops[i]), flashedWindows);
                changed = true;
            }

            return changed;
        }

        private void RecoverUnknownLockDrop(WindowKey key, HashSet<WindowKey> flashedWindows)
        {
            string opId = NextOperationId();
            _inspectorAdapter.InvalidateCache();
            _planExecutor.InvalidateWindowCache(key);

            var win = _state.FindWindow(key);
            if (win == null || !win.CanParticipate || !_inspectorAdapter.IsWindowAlive(key, win.IdentityEvidence))
            {
                return;
            }

            _logger.Info(opId, _state.Generation, key, "UnknownLockDropRecovery", "Re-applying lock and confirmed assignment after unexpected unlock (e.g. tracker rebuild).");

            AdapterWriteResult res;
            if (win.AssignedSelection != null && !win.AssignedSelection.IsEmpty && _selectionAdapter.AreAllTargetsAlive(win.AssignedSelection.GetOrderedKeys()))
            {
                res = _inspectorAdapter.WriteTargetsAndLock(
                    key,
                    win.AssignedSelection.GetOrderedKeys(),
                    win.AssignedSelection.ActiveTarget,
                    locked: true);
            }
            else
            {
                res = _inspectorAdapter.WriteLockOnly(key, true);
            }

            if (res.Status != WindowWriteStatus.Failed)
            {
                var updated = _state.Windows.Select(w => w.Key == key ? w.With(observedLocked: true) : w).ToArray();
                _state = _state.With(windows: updated, statusNotice: "Recovered unexpected lock drop on Inspector #" + win.FixedDisplayNumber);
                flashedWindows.Add(key);
            }
            else
            {
                _logger.Error(opId, _state.Generation, key, "UnknownLockDropRecoveryFailed", res.Error);
            }
        }

        private void SynchronizeWindows(string opId)
        {
            var observed = _inspectorAdapter.EnumerateInspectors();
            bool inReloadGrace = _reloadGraceUntilMs > 0 && _clock.NowMs < _reloadGraceUntilMs;
            ApplyReduce(new WindowsSynchronizedEvent(opId, _state.Generation, observed, inReloadGrace));
        }

        private void RestoreSessionLayoutIntoState(SessionLayoutDto layout)
        {
            var restored = new List<WindowRecord>();
            int nextNum = Math.Max(1, layout.NextFixedDisplayNumber);
            for (int i = 0; i < layout.Windows.Count; i++)
            {
                var e = layout.Windows[i];
                if (string.IsNullOrEmpty(e.WindowKey)) continue;
                nextNum = Math.Max(nextNum, e.FixedDisplayNumber + 1);
                restored.Add(new WindowRecord(
                    new WindowKey(e.WindowKey),
                    e.FixedDisplayNumber,
                    e.RoleOrder,
                    e.IsManaged,
                    isAlive: true,
                    isExplicitlyExcluded: e.IsExplicitlyExcluded,
                    isManuallyUnlocked: e.IsManuallyUnlocked,
                    isFaultIsolated: false,
                    faultReason: string.Empty,
                    observedLocked: false,
                    assignedSelection: null,
                    assignmentLabel: AssignmentLabel.None,
                    entityGeneration: 1,
                    observedRepresentativeName: string.Empty,
                    identityEvidence: e.IdentityEvidence));
            }

            if (restored.Count > 0)
            {
                _state = _state.With(windows: restored, nextFixedDisplayNumber: nextNum);
            }
        }

        private void RecoverPendingJournalOnStartup(string opId)
        {
            var load = _persistenceStore.LoadRecoveryJournal();
            if (!load.Succeeded || load.Value == null || load.Value.Leases == null || load.Value.Leases.Count == 0)
            {
                return;
            }

            var journal = load.Value;
            if (!string.Equals(journal.EditorSessionId, _editorSessionId, StringComparison.Ordinal))
            {
                _persistenceStore.ClearRecoveryJournal();
                ApplyReduce(new StatusNoticeEvent(
                    opId,
                    _state.Generation,
                    notice: "Previous Editor session ended with unrestored Inspector locks. Please verify Inspector lock states manually."));
                return;
            }

            var leaseDict = new Dictionary<WindowKey, LockLease>();
            for (int i = 0; i < journal.Leases.Count; i++)
            {
                var domainLease = journal.Leases[i].ToDomain();
                if (domainLease.RestorationStatus == LeaseRestorationStatus.Acquired
                    || domainLease.RestorationStatus == LeaseRestorationStatus.RestoreFailed)
                {
                    leaseDict[domainLease.WindowKey] = domainLease;
                }
            }

            if (leaseDict.Count == 0)
            {
                _persistenceStore.ClearRecoveryJournal();
                return;
            }

            var (updated, allRestored, err) = _planExecutor.RestoreLeases(
                leaseDict,
                _state.Generation,
                opId,
                _editorSessionId,
                journal.ManagementSessionId);

            if (!allRestored)
            {
                _state = _state.With(
                    lifecycleState: RuntimeLifecycleState.Faulted,
                    leases: updated,
                    lastError: err,
                    statusNotice: "Incomplete lock recovery from previous reload; click Retry Restore.");
            }
            else
            {
                _state = _state.With(statusNotice: "Recovered locks from previous reload (Off).");
            }
        }

        private bool IsSelectionEligibleForRotation(SelectionSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEmpty) return false;
            var filter = SelectionFilter.Evaluate(snapshot, _state.Settings);
            if (!filter.IsAllowed) return false;
            return _selectionAdapter.AreAllTargetsAlive(snapshot.GetOrderedKeys());
        }

        private SaveResult SaveRecoveryJournal(IReadOnlyDictionary<WindowKey, LockLease> leases)
        {
            var journal = new RecoveryJournalDto
            {
                EditorSessionId = _editorSessionId,
                ManagementSessionId = _managementSessionId,
                Generation = _state.Generation,
                Leases = leases.Values.Select(LeaseJournalEntryDto.FromDomain).ToList()
            };
            return _persistenceStore.SaveRecoveryJournalImmediate(journal);
        }

        private void QueuePersistSettings()
        {
            var dto = SettingsDto.FromDomain(_projectId, _state.Settings);
            var res = _persistenceStore.QueueSaveSettings(dto, _state.Settings.SettingsRevision);
            if (res.BlockedByNewerSchema)
            {
                _state = _state.With(persistenceWriteBlocked: true, statusNotice: "Saving paused: newer schema detected.");
            }
            else
            {
                _persistDirtyAtMs = _clock.NowMs;
            }
        }

        private void PersistSessionLayoutNow()
        {
            var dto = new SessionLayoutDto
            {
                EditorSessionId = _editorSessionId,
                NextFixedDisplayNumber = _state.NextFixedDisplayNumber,
                Windows = _state.Windows.Select(w => new SessionWindowLayoutEntryDto
                {
                    WindowKey = w.Key.Value,
                    IdentityEvidence = w.IdentityEvidence,
                    FixedDisplayNumber = w.FixedDisplayNumber,
                    RoleOrder = w.RoleOrder,
                    IsManaged = w.IsManaged,
                    IsExplicitlyExcluded = w.IsExplicitlyExcluded,
                    IsManuallyUnlocked = w.IsManuallyUnlocked
                }).ToList()
            };
            _sessionLayoutStore.SaveSessionLayout(dto);
        }

        private void SyncOverlays(IReadOnlyCollection<WindowKey> flashedWindows = null)
        {
            if (_overlayPresenter == null) return;
            if (!_state.Settings.ShowInspectorOverlays)
            {
                _overlayPresenter.ClearAllOverlays();
                return;
            }
            _overlayPresenter.SyncOverlays(_state, _clock.NowMs, flashedWindows ?? Array.Empty<WindowKey>());
        }

        private void NotifyStateChanged()
        {
            StateChanged?.Invoke();
        }

        private string NextOperationId() => "op-" + (_nextOpId++);

        private static bool IsWritePlanCommand(string kind)
        {
            return kind == "SelectionSnapshot"
                || kind == "ReorderWindow"
                || kind == "UpdateSettings"
                || kind == "ResetSettings"
                || kind == "ResetLayout";
        }
    }
}
