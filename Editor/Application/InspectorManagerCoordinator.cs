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
        public PerformanceMetrics Metrics { get; }
        public IReadOnlyList<DiagnosticLogEntry> RecentLogs { get; }
        public bool HasPendingVerification { get; }
        public long CapturedAtMs { get; }

        public UiSnapshot(
            ManagerState state,
            PerformanceMetrics metrics,
            IReadOnlyList<DiagnosticLogEntry> recentLogs,
            bool hasPendingVerification,
            long capturedAtMs)
        {
            State = state;
            Metrics = metrics;
            RecentLogs = recentLogs;
            HasPendingVerification = hasPendingVerification;
            CapturedAtMs = capturedAtMs;
        }
    }

    /// <summary>
    /// Single-queue main-thread runtime controller (Section 3, 5, 6, 8, 9, 11, 14).
    /// All UI events, shortcuts, and Unity callbacks enqueue commands here and are executed
    /// deterministically outside IMGUI callbacks with bounded queue depth and per-tick budgets.
    /// </summary>
    public sealed class InspectorManagerCoordinator : IDisposable
    {
        public const int DefaultMaxQueueSize = 128;
        public const int DefaultCommandsPerTickBudget = 4;
        public const long DefaultReconcileIntervalMs = 350;
        public const long DefaultPersistDebounceMs = 800;
        public const long DefaultReloadGraceMs = 1500;
        public const long DefaultNavigationExpectationTimeoutMs = 400;

        private readonly IInspectorAdapter _inspectorAdapter;
        private readonly ISelectionAdapter _selectionAdapter;
        private readonly IFavoriteResolver _favoriteResolver;
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
        private readonly long _navigationTimeoutMs;

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

        // Metrics
        private double _lastSelectionToCommitMs;
        private double _lastPlanExecutionMs;
        private int _peakQueueDepth;
        private long _totalCommittedPlans;
        private long _totalWriteSkips;
        private long _lastSelectionReceivedAtMs;

        public event Action StateChanged;

        public ManagerState CurrentState => _state;

        public InspectorManagerCoordinator(
            string projectId,
            IInspectorAdapter inspectorAdapter,
            ISelectionAdapter selectionAdapter,
            IFavoriteResolver favoriteResolver,
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
            long navigationTimeoutMs = DefaultNavigationExpectationTimeoutMs,
            long pendingVerifyTimeoutMs = 500)
        {
            _projectId = projectId ?? string.Empty;
            _inspectorAdapter = inspectorAdapter ?? throw new ArgumentNullException(nameof(inspectorAdapter));
            _selectionAdapter = selectionAdapter ?? throw new ArgumentNullException(nameof(selectionAdapter));
            _favoriteResolver = favoriteResolver ?? throw new ArgumentNullException(nameof(favoriteResolver));
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
            _navigationTimeoutMs = navigationTimeoutMs > 0 ? navigationTimeoutMs : DefaultNavigationExpectationTimeoutMs;

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

            // 1. Load Settings & Favorites from project-scoped store (with schema / corruption protection).
            var loadSettings = _persistenceStore.LoadSettingsAndFavorites();
            var settings = loadSettings.Value != null ? loadSettings.Value.ToDomainSettings() : ManagerSettings.CreateDefault();
            var favorites = loadSettings.Value != null ? loadSettings.Value.ToDomainFavorites() : Array.Empty<FavoriteEntry>();

            _state = ManagerState.CreateInitial(settings, AdapterCapabilities.None())
                .With(
                    favorites: favorites,
                    persistenceWriteBlocked: loadSettings.NewerSchemaDetected || _persistenceStore.IsWriteBlockedByNewerSchema,
                    statusNotice: loadSettings.Notice);

            // 2. Read-only capability probe (Section 8.1: never mutates existing Inspectors on startup).
            var caps = _inspectorAdapter.ProbeCapabilities();
            ApplyReduce(new CapabilitiesProbedEvent(opId, _state.Generation, caps));

            // 3. Restore within-session layout (fixed numbers, role orders, exclusions) if same Editor session (Section 9).
            var sessionLayout = _sessionLayoutStore.LoadSessionLayout();
            if (sessionLayout != null && string.Equals(sessionLayout.EditorSessionId, _editorSessionId, StringComparison.Ordinal))
            {
                _reloadGraceUntilMs = _clock.NowMs + _reloadGraceMs;
                RestoreSessionLayoutIntoState(sessionLayout);
            }

            // 4. Synchronize live Inspector windows.
            SynchronizeWindows(opId);

            // 5. Check RecoveryJournal for any unrestored leases from a reload/crash (Section 9 & 10).
            RecoverPendingJournalOnStartup(opId);

            // 6. Refresh favorite resolution statuses (read-only, never auto-opens scenes!).
            RefreshFavoriteResolutionsInternal(opId);

            // 7. Capture current live selection as initial observation (NOT mutating anything).
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
            var metrics = new PerformanceMetrics(
                _lastSelectionToCommitMs,
                _lastPlanExecutionMs,
                _queue.Count,
                _peakQueueDepth,
                _totalCommittedPlans,
                _totalWriteSkips,
                GC.GetTotalMemory(false));

            return new UiSnapshot(
                _state,
                metrics,
                _logger.GetRecentEntries(),
                _planExecutor.HasPendingVerification,
                _clock.NowMs);
        }

        // --- Public Command Enqueue Entry Points (safe to call from IMGUI, shortcuts, or Unity callbacks) ---

        public void EnqueueStartRotation() => Enqueue("StartRotation", null);
        public void EnqueueStopRotation(StopReason reason = StopReason.UserRequested) => Enqueue("StopRotation", reason);
        public void EnqueuePauseRotation() => Enqueue("PauseRotation", null);
        public void EnqueueResumeRotation() => Enqueue("ResumeRotation", null);
        public void EnqueueRetryRestore() => Enqueue("RetryRestore", null);
        public void EnqueueAddInspector() => Enqueue("AddInspector", null);
        public void EnqueueAdoptInspector(WindowKey key) => Enqueue("AdoptInspector", key);
        public void EnqueueCloseInspector(WindowKey key) => Enqueue("CloseInspector", key);
        public void EnqueueToggleLock(WindowKey key) => Enqueue("ToggleLock", key);
        public void EnqueueToggleFocusedInspectorLock() => Enqueue("ToggleFocusedInspectorLock", null);
        public void EnqueueBulkLock(bool locked) => Enqueue("BulkLock", locked);
        public void EnqueueSetExcluded(WindowKey key, bool excluded) => Enqueue("SetExcluded", (key, excluded));
        public void EnqueueReorderWindow(WindowKey key, int newRoleIndex) => Enqueue("ReorderWindow", (key, newRoleIndex));
        public void EnqueueFocusInspector(WindowKey key) => Enqueue("FocusInspector", key);
        public void EnqueueNavigateBack() => Enqueue("NavigateBack", null);
        public void EnqueueNavigateForward() => Enqueue("NavigateForward", null);
        public void EnqueueNavigateToIndex(int index) => Enqueue("NavigateToIndex", index);
        public void EnqueueClearNavigationHistory() => Enqueue("ClearNavigationHistory", null);
        public void EnqueueUpdateSettings(ManagerSettings newSettings, bool isExplicitLayoutChange = false) => Enqueue("UpdateSettings", (newSettings, isExplicitLayoutChange));
        public void EnqueueResetSettings() => Enqueue("ResetSettings", null);
        public void EnqueueResetLayout() => Enqueue("ResetLayout", null);
        public void EnqueueAddFavoriteFromCurrentSelection() => Enqueue("AddFavoriteFromCurrentSelection", null);
        public void EnqueueAddFavoriteObject(ObjectRefKey key) => Enqueue("AddFavoriteObject", key);
        public void EnqueueRemoveFavorite(string favoriteId) => Enqueue("RemoveFavorite", favoriteId);
        public void EnqueueReorderFavorite(string favoriteId, int newIndex) => Enqueue("ReorderFavorite", (favoriteId, newIndex));
        public void EnqueueSelectFavorite(string favoriteId) => Enqueue("SelectFavorite", favoriteId);
        public void EnqueuePingFavorite(string favoriteId) => Enqueue("PingFavorite", favoriteId);
        public void EnqueueInvalidateEnvironmentCaches(bool sceneSaved = false) => Enqueue("InvalidateEnvironmentCaches", sceneSaved);
        public void EnqueueSynchronizeWindows() => Enqueue("SyncWindows", null);

        /// <summary>
        /// Called immediately when Unity fires Selection.selectionChanged (Section 5.1 #1).
        /// Captures an immutable SelectionSnapshot at receipt time instead of re-reading Selection later.
        /// </summary>
        public void OnSelectionChangedImmediate()
        {
            if (_isInputBlocked || _disposed) return;

            var snapshot = _selectionAdapter.CaptureCurrentSelection(
                _nextSelectionSeq++,
                _state.Generation,
                SelectionSource.UserSelection);

            _lastSelectionReceivedAtMs = _clock.NowMs;
            Enqueue("SelectionSnapshot", snapshot);
        }

        /// <summary>
        /// Allows tests or callers to push an already-captured snapshot into the single queue.
        /// </summary>
        public void EnqueueSelectionSnapshot(SelectionSnapshot snapshot)
        {
            if (_isInputBlocked || _disposed) return;
            _lastSelectionReceivedAtMs = _clock.NowMs;
            Enqueue("SelectionSnapshot", snapshot);
        }

        /// <summary>
        /// Synchronous lifecycle boundary handler for AssemblyReload, PlayMode, LastManagerWindowClosed, or EditorQuitting (Section 9).
        /// Immediately stops input, increments generation, flushes settings, and restores all surviving leases.
        /// </summary>
        public void HandleLifecycleBoundaryStop(StopReason reason, bool suspendMode)
        {
            string opId = NextOperationId();
            _isInputBlocked = suspendMode || reason == StopReason.EditorQuitting;
            _queue.Clear();
            _planExecutor.CancelPendingVerification();
            _planExecutor.InvalidateAllCaches();
            _inspectorAdapter.InvalidateCache();

            PersistSessionLayoutNow();
            _persistenceStore.FlushSettingsAndFavorites();
            _persistDirtyAtMs = -1;

            if (_state.Leases.Count > 0 || _state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused)
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
            _favoriteResolver.InvalidateCache();
            _planExecutor.InvalidateAllCaches();

            // Section 9: Never auto-restart rotation after Play Mode return; move Suspended -> Off.
            if (_state.LifecycleState == RuntimeLifecycleState.Suspended)
            {
                _state = _state.With(lifecycleState: RuntimeLifecycleState.Off, statusNotice: "Off (returned from Play Mode).");
            }

            EnqueueSynchronizeWindows();
            EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
            NotifyStateChanged();
        }

        /// <summary>
        /// Main-thread budgeted execution tick called from EditorApplication.update (NEVER from OnGUI).
        /// </summary>
        public void Tick()
        {
            if (_disposed) return;

            long now = _clock.NowMs;
            bool stateDirty = false;
            var flashedWindows = new HashSet<WindowKey>();

            // 1. Poll deferred PresentationPending verification first (Invariant 1: blocks new write plans while pending).
            if (_planExecutor.HasPendingVerification)
            {
                var pendingOutcome = _planExecutor.PollPendingVerification(_state, _editorSessionId, _managementSessionId);
                if (pendingOutcome != null)
                {
                    HandlePlanOutcome(pendingOutcome, flashedWindows);
                    stateDirty = true;
                }
            }

            // 2. Check expired ExpectedNavigationNotification (Section 5.2: finalize via readback, never wait forever).
            if (_state.PendingNavigation != null && now > _state.PendingNavigation.ExpiresAtMs)
            {
                string opId = NextOperationId();
                var observed = _selectionAdapter.CaptureCurrentSelection(_nextSelectionSeq++, _state.Generation, SelectionSource.HistoryNavigation);
                ApplyReduceAndRunEffects(new NavigationNotificationExpiredEvent(opId, _state.Generation, observed), flashedWindows);
                stateDirty = true;
            }

            // 3. Process queued commands within per-tick budget (pausing write-triggering commands while verification is pending).
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

            // 4. Low-frequency reconciliation (window changes, unknown lock drop detection, reload grace expiry).
            if (!_isInputBlocked && !_planExecutor.HasPendingVerification && (now - _lastReconcileMs >= _reconcileIntervalMs))
            {
                _lastReconcileMs = now;
                if (RunReconciliation(now, flashedWindows))
                {
                    stateDirty = true;
                }
            }

            // 5. Debounced settings & favorites persistence flush.
            if (_persistDirtyAtMs >= 0 && (now - _persistDirtyAtMs >= _persistDebounceMs))
            {
                _persistDirtyAtMs = -1;
                var saveRes = _persistenceStore.FlushSettingsAndFavorites();
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
            _persistenceStore.FlushSettingsAndFavorites();
            _overlayPresenter?.ClearAllOverlays();
            _overlayPresenter?.Dispose();
            _persistenceStore.Dispose();
        }

        private void Enqueue(string kind, object payload)
        {
            if (_disposed) return;
            if (_isInputBlocked && kind != "StopRotation") return;

            // Section 5.1: Bounded queue policy. On overflow, stop auto-rotation safely and notify user.
            if (_queue.Count >= _maxQueueSize)
            {
                string opId = NextOperationId();
                _logger.Error(opId, _state.Generation, null, "QueueOverflow", "Command queue limit reached (" + _maxQueueSize + "); stopping auto-rotation.");
                _queue.Clear();
                if (_state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused)
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
            if (_queue.Count > _peakQueueDepth)
            {
                _peakQueueDepth = _queue.Count;
            }
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

                case "PauseRotation":
                    _planExecutor.CancelPendingVerification();
                    ApplyReduce(new PauseRequestedEvent(cmd.OperationId, _state.Generation));
                    return true;

                case "ResumeRotation":
                    ApplyReduceAndRunEffects(new ResumeRequestedEvent(cmd.OperationId, _state.Generation), flashedWindows);
                    return true;

                case "RetryRestore":
                    ExecuteRetryRestore(cmd.OperationId);
                    return true;

                case "SelectionSnapshot":
                    if (cmd.Payload is SelectionSnapshot snap)
                    {
                        ApplyReduceAndRunEffects(new SelectionReceivedEvent(cmd.OperationId, _state.Generation, snap, _clock.NowMs), flashedWindows);
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

                case "ToggleFocusedInspectorLock":
                {
                    var focusedKey = _inspectorAdapter.GetFocusedInspectorKey();
                    if (focusedKey.HasValue)
                    {
                        ExecuteToggleLock(cmd.OperationId, focusedKey.Value, flashedWindows);
                        return true;
                    }
                    return false;
                }

                case "BulkLock":
                    if (cmd.Payload is bool bulkLocked)
                    {
                        ExecuteBulkLock(cmd.OperationId, bulkLocked, flashedWindows);
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

                case "NavigateBack":
                    if (_state.NavigationHistory.CanGoBack)
                    {
                        ExecuteNavigationMove(cmd.OperationId, _state.NavigationHistory.CursorIndex - 1, flashedWindows);
                        return true;
                    }
                    return false;

                case "NavigateForward":
                    if (_state.NavigationHistory.CanGoForward)
                    {
                        ExecuteNavigationMove(cmd.OperationId, _state.NavigationHistory.CursorIndex + 1, flashedWindows);
                        return true;
                    }
                    return false;

                case "NavigateToIndex":
                    if (cmd.Payload is int idx)
                    {
                        ExecuteNavigationMove(cmd.OperationId, idx, flashedWindows);
                        return true;
                    }
                    return false;

                case "ClearNavigationHistory":
                    ApplyReduce(new ClearNavigationHistoryEvent(cmd.OperationId, _state.Generation));
                    return true;

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

                case "AddFavoriteFromCurrentSelection":
                    ExecuteAddFavoriteFromCurrentSelection(cmd.OperationId);
                    return true;

                case "AddFavoriteObject":
                    if (cmd.Payload is ObjectRefKey favKey)
                    {
                        ExecuteAddFavoriteObject(cmd.OperationId, favKey);
                        return true;
                    }
                    return false;

                case "RemoveFavorite":
                    if (cmd.Payload is string favId)
                    {
                        var remaining = _state.Favorites.Where(f => !string.Equals(f.FavoriteId, favId, StringComparison.Ordinal))
                            .Select((f, i) => f.With(sortOrder: i)).ToArray();
                        ApplyReduceAndRunEffects(new FavoritesUpdatedEvent(cmd.OperationId, _state.Generation, remaining), flashedWindows);
                        return true;
                    }
                    return false;

                case "ReorderFavorite":
                    if (cmd.Payload is ValueTuple<string, int> favMove)
                    {
                        ExecuteReorderFavorite(cmd.OperationId, favMove.Item1, favMove.Item2, flashedWindows);
                        return true;
                    }
                    return false;

                case "SelectFavorite":
                    if (cmd.Payload is string selectFavId)
                    {
                        ExecuteSelectFavorite(cmd.OperationId, selectFavId);
                        return true;
                    }
                    return false;

                case "PingFavorite":
                    if (cmd.Payload is string pingFavId)
                    {
                        ExecutePingFavorite(cmd.OperationId, pingFavId);
                        return true;
                    }
                    return false;

                case "InvalidateEnvironmentCaches":
                    _inspectorAdapter.InvalidateCache();
                    _favoriteResolver.InvalidateCache();
                    _planExecutor.InvalidateAllCaches();
                    RefreshFavoriteResolutionsInternal(cmd.OperationId);
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

            // Step 1: Read pre-session original lock & actual display for ALL managed windows BEFORE writing anything.
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

            // Step 2 (Section 10): Persist RecoveryJournal BEFORE acquiring/writing any lock!
            // If journal persistence fails, refuse to start or mutate any Inspector lock.
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

            // Step 3: Lock all participating & explicitly excluded managed windows (skipping manually unlocked ones).
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
                        // Section 6.3: Partial start failure -> restore already-acquired leases!
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

            // Step 4: Seed RotationHistory per Section 5.3 priority (CurrentSelection -> NavigationHistory -> ObservedDisplays).
            int capacity = Math.Max(_state.Settings.MaxRotationHistory, managedWindows.Count);
            var seededHistory = RotationHistoryState.BuildSeeded(
                _state.CurrentLiveSelection,
                _state.NavigationHistory.Entries,
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

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused;
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
                    // Section 6.2: Only clean up created window if confirmed empty & exact same instance.
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

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused;
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

            // Section 6.2: Restore lease original lock BEFORE closing; if restore fails, DO NOT close automatically!
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

        private void ExecuteBulkLock(string opId, bool desiredLock, HashSet<WindowKey> flashedWindows)
        {
            var targets = _state.GetManagedAliveWindowsOrdered();
            var affected = new List<WindowKey>();
            var errors = new List<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var w = targets[i];
                var writeRes = _inspectorAdapter.WriteLockOnly(w.Key, desiredLock);
                if (writeRes.IsSuccess && writeRes.ObservedLocked == desiredLock)
                {
                    _planExecutor.InvalidateWindowCache(w.Key);
                    affected.Add(w.Key);
                }
                else
                {
                    errors.Add("#" + w.FixedDisplayNumber + ": " + writeRes.Error);
                }
            }

            if (affected.Count > 0)
            {
                ApplyReduceAndRunEffects(new BulkManualLockChangedEvent(opId, _state.Generation, desiredLock, affected), flashedWindows);
            }

            if (errors.Count > 0)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Some Inspectors failed during bulk lock operation.", string.Join(" | ", errors)));
            }
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

            bool isSessionActive = _state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused;
            LockLease lease = null;
            bool finalLocked = read.ObservedLocked;

            // Section 6.1: Explicit exclusion locks the Inspector to freeze its display, acquiring a lease if active.
            if (excluded)
            {
                if (isSessionActive && !_state.Leases.ContainsKey(key))
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

        private void ExecuteNavigationMove(string opId, int targetIndex, HashSet<WindowKey> flashedWindows)
        {
            if (targetIndex < 0 || targetIndex >= _state.NavigationHistory.Entries.Count)
            {
                return;
            }

            var target = _state.NavigationHistory.Entries[targetIndex];
            // Section 7: If ANY object in a multi-selection history entry disappeared, do NOT shrink to partial subset!
            if (!_selectionAdapter.AreAllTargetsAlive(target.GetOrderedKeys()))
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "History entry unavailable: one or more objects no longer exist."));
                return;
            }

            ApplyReduceAndRunEffects(
                new NavigationMoveRequestedEvent(opId, _state.Generation, targetIndex, _clock.NowMs + _navigationTimeoutMs),
                flashedWindows);
        }

        private void ExecuteAddFavoriteFromCurrentSelection(string opId)
        {
            if (_state.CurrentLiveSelection == null || _state.CurrentLiveSelection.IsEmpty)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "No active object selected to add to favorites."));
                return;
            }

            var targetKey = _state.CurrentLiveSelection.ActiveTarget ?? _state.CurrentLiveSelection.OrderedTargets[0].Key;
            ExecuteAddFavoriteObject(opId, targetKey);
        }

        private void ExecuteAddFavoriteObject(string opId, ObjectRefKey key)
        {
            if (!key.IsValid || !_selectionAdapter.IsTargetAlive(key))
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Cannot add favorite: target object is unavailable."));
                return;
            }

            var created = _favoriteResolver.CreateFromLiveObject(key, _state.Favorites.Count);
            if (created == null)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to create favorite entry."));
                return;
            }

            // Avoid duplicate favorite for identical persistent ID or temporary runtime key.
            bool duplicate = _state.Favorites.Any(f =>
                (!created.PersistentId.IsEmpty && f.PersistentId == created.PersistentId)
                || (created.IsTemporary && f.TemporaryRuntimeKey.HasValue && f.TemporaryRuntimeKey == created.TemporaryRuntimeKey));

            if (duplicate)
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Object is already in favorites."));
                return;
            }

            var nextList = _state.Favorites.Concat(new[] { created }).Select((f, i) => f.With(sortOrder: i)).ToArray();
            ApplyReduceAndRunEffects(new FavoritesUpdatedEvent(opId, _state.Generation, nextList), new HashSet<WindowKey>());
        }

        private void ExecuteReorderFavorite(string opId, string favoriteId, int newIndex, HashSet<WindowKey> flashedWindows)
        {
            var list = _state.Favorites.ToList();
            int currentIdx = list.FindIndex(f => string.Equals(f.FavoriteId, favoriteId, StringComparison.Ordinal));
            if (currentIdx < 0) return;

            int clamped = Math.Max(0, Math.Min(list.Count - 1, newIndex));
            if (clamped == currentIdx) return;

            var item = list[currentIdx];
            list.RemoveAt(currentIdx);
            list.Insert(clamped, item);
            var reindexed = list.Select((f, i) => f.With(sortOrder: i)).ToArray();
            ApplyReduceAndRunEffects(new FavoritesUpdatedEvent(opId, _state.Generation, reindexed), flashedWindows);
        }

        private void ExecuteSelectFavorite(string opId, string favoriteId)
        {
            var fav = _state.Favorites.FirstOrDefault(f => string.Equals(f.FavoriteId, favoriteId, StringComparison.Ordinal));
            if (fav == null) return;

            var res = _favoriteResolver.Resolve(fav);
            UpdateSingleFavoriteResolution(fav.FavoriteId, res);

            if (res.State == FavoriteResolutionState.Resolved && res.ResolvedRuntimeKey.HasValue)
            {
                if (!_selectionAdapter.TrySetGlobalSelection(new[] { res.ResolvedRuntimeKey.Value }, res.ResolvedRuntimeKey.Value, out string err))
                {
                    ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Failed to select favorite: " + err));
                }
            }
            else
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Favorite unavailable (" + res.State + "): " + res.Detail));
            }
        }

        private void ExecutePingFavorite(string opId, string favoriteId)
        {
            var fav = _state.Favorites.FirstOrDefault(f => string.Equals(f.FavoriteId, favoriteId, StringComparison.Ordinal));
            if (fav == null) return;

            var res = _favoriteResolver.Resolve(fav);
            UpdateSingleFavoriteResolution(fav.FavoriteId, res);

            if (res.State == FavoriteResolutionState.Resolved && res.ResolvedRuntimeKey.HasValue)
            {
                _selectionAdapter.PingObject(res.ResolvedRuntimeKey.Value);
            }
            else
            {
                ApplyReduce(new StatusNoticeEvent(opId, _state.Generation, "Favorite unavailable (" + res.State + "): " + res.Detail));
            }
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
                        RunAssignmentPlan(eval.Snapshot, eval.IsNavigationUpdate, flashedWindows);
                        break;

                    case RequestReassignHistoryEffect reassign:
                        RunHistoryReassignment(reassign.SeedFromObservedIfEmpty, flashedWindows);
                        break;

                    case RequestSetGlobalSelectionEffect setSel:
                        if (!_selectionAdapter.TrySetGlobalSelection(
                            setSel.TargetSnapshot.GetOrderedKeys(),
                            setSel.TargetSnapshot.ActiveTarget,
                            out string err))
                        {
                            _state = _state.With(clearPendingNavigation: true, statusNotice: "Navigation failed: " + err);
                        }
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

        private void RunAssignmentPlan(SelectionSnapshot incoming, bool isNavigationUpdate, HashSet<WindowKey> flashedWindows)
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
            AssignmentPlan plan;

            if (_state.Settings.Mode == RotationMode.History)
            {
                plan = AssignmentPlanner.CreateHistoryPlan(
                    planId,
                    _state.Generation,
                    seq,
                    participating,
                    _state.RotationHistory,
                    incoming,
                    IsSelectionEligibleForRotation,
                    _state.Settings.MaxRotationHistory,
                    isNavigationUpdate);
            }
            else
            {
                plan = AssignmentPlanner.CreateCyclePlan(
                    planId,
                    _state.Generation,
                    seq,
                    participating,
                    _state.RotationHistory,
                    incoming,
                    isNavigationUpdate);
            }

            long startMs = _clock.NowMs;
            var outcome = _planExecutor.ExecutePlan(_state, plan, _editorSessionId, _managementSessionId);
            _lastPlanExecutionMs = Math.Max(0, _clock.NowMs - startMs);
            HandlePlanOutcome(outcome, flashedWindows);
        }

        private void RunHistoryReassignment(bool seedFromObservedIfEmpty, HashSet<WindowKey> flashedWindows)
        {
            if (_state.LifecycleState != RuntimeLifecycleState.Running || _state.Settings.Mode != RotationMode.History)
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
                    _state.NavigationHistory.Entries,
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
                _state.Settings.MaxRotationHistory,
                isNavigationUpdate: false);

            long startMs = _clock.NowMs;
            var outcome = _planExecutor.ExecutePlan(_state, plan, _editorSessionId, _managementSessionId);
            _lastPlanExecutionMs = Math.Max(0, _clock.NowMs - startMs);
            HandlePlanOutcome(outcome, flashedWindows);
        }

        private void HandlePlanOutcome(PlanExecutionOutcome outcome, HashSet<WindowKey> flashedWindows)
        {
            if (outcome == null) return;

            _totalWriteSkips += outcome.WriteSkipCount;

            switch (outcome.Kind)
            {
                case PlanExecutionOutcomeKind.Committed:
                {
                    _totalCommittedPlans++;
                    if (_lastSelectionReceivedAtMs > 0)
                    {
                        _lastSelectionToCommitMs = Math.Max(0, _clock.NowMs - _lastSelectionReceivedAtMs);
                    }

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

                    // Section 14.3: Auto-focus respects continuous Hierarchy/Project selection and never steals input!
                    if (_state.Settings.AutoFocusUpdatedInspector
                        && outcome.Plan.FocusCandidateWindowKey.HasValue
                        && !_inspectorAdapter.IsSelectionSourceContinuous())
                    {
                        _inspectorAdapter.FocusInspector(outcome.Plan.FocusCandidateWindowKey.Value);
                    }
                    break;
                }

                case PlanExecutionOutcomeKind.PendingVerification:
                    // Waiting for next tick verification; do NOT commit UI or RotationHistory yet (Invariant #4).
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

            // Detect unknown lock drops on participating windows BEFORE standard sync overwrites state (Section 6.1 & 14.5 #2).
            var unknownDrops = new List<WindowKey>();
            if (_state.LifecycleState == RuntimeLifecycleState.Running || _state.LifecycleState == RuntimeLifecycleState.Paused)
            {
                var obsMap = new Dictionary<WindowKey, ObservedWindowSnapshot>();
                for (int i = 0; i < observed.Count; i++) obsMap[observed[i].Key] = observed[i];

                for (int i = 0; i < _state.Windows.Count; i++)
                {
                    var w = _state.Windows[i];
                    if (w.CanParticipate && obsMap.TryGetValue(w.Key, out var obs) && !obs.ObservedLocked)
                    {
                        unknownDrops.Add(w.Key);
                    }
                }
            }

            var prevWindows = _state.Windows;
            ApplyReduce(new WindowsSynchronizedEvent(opId, _state.Generation, observed, inReloadGrace));
            if (!ReferenceEquals(prevWindows, _state.Windows))
            {
                changed = true;
            }

            // Recover unknown lock drops (e.g., tracker recreation) WITHOUT marking them ManuallyUnlocked!
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
                    lastSuccessSequence: 0,
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
            // Section 9 & 10: NEVER automatically modify locks across Editor restarts using temporary runtime IDs!
            if (!string.Equals(journal.EditorSessionId, _editorSessionId, StringComparison.Ordinal))
            {
                _persistenceStore.ClearRecoveryJournal();
                ApplyReduce(new StatusNoticeEvent(
                    opId,
                    _state.Generation,
                    notice: "Previous Editor session ended with unrestored Inspector locks. Please verify Inspector lock states manually."));
                return;
            }

            // Same Editor session (e.g., after Assembly Reload): restore only verified surviving identical windows.
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

        private void RefreshFavoriteResolutionsInternal(string opId)
        {
            if (_state.Favorites.Count == 0) return;

            bool anyChanged = false;
            bool persistentUpgraded = false;
            var updated = new List<FavoriteEntry>(_state.Favorites.Count);

            for (int i = 0; i < _state.Favorites.Count; i++)
            {
                var fav = _state.Favorites[i];
                var res = _favoriteResolver.Resolve(fav);

                // Section 14.4: Auto-cleanup NEVER removes SceneUnloaded or pending items; only DeletedOrMissing if explicitly enabled.
                if (_state.Settings.AutoCleanupMissingFavorites && res.State == FavoriteResolutionState.DeletedOrMissing)
                {
                    anyChanged = true;
                    persistentUpgraded = true;
                    continue;
                }

                bool upgraded = fav.IsTemporary && res.UpgradedPersistentId.HasValue && !res.UpgradedPersistentId.Value.IsEmpty;
                var nextFav = fav.With(
                    persistentId: upgraded ? res.UpgradedPersistentId : (PersistentObjectId?)null,
                    displayNameCache: !string.IsNullOrEmpty(res.ResolvedDisplayName) ? res.ResolvedDisplayName : fav.DisplayNameCache,
                    typeNameCache: !string.IsNullOrEmpty(res.ResolvedTypeName) ? res.ResolvedTypeName : fav.TypeNameCache,
                    sortOrder: updated.Count,
                    isTemporary: upgraded ? false : fav.IsTemporary,
                    resolutionState: res.State,
                    resolutionDetail: res.Detail);

                if (upgraded) persistentUpgraded = true;
                if (nextFav.ResolutionState != fav.ResolutionState || upgraded || !string.Equals(nextFav.DisplayNameCache, fav.DisplayNameCache, StringComparison.Ordinal))
                {
                    anyChanged = true;
                }

                updated.Add(nextFav);
            }

            if (anyChanged)
            {
                _state = _state.With(favorites: updated);
                if (persistentUpgraded)
                {
                    QueuePersistSettings();
                }
            }
        }

        private void UpdateSingleFavoriteResolution(string favoriteId, FavoriteResolutionResult res)
        {
            var updated = _state.Favorites.Select(f =>
            {
                if (!string.Equals(f.FavoriteId, favoriteId, StringComparison.Ordinal)) return f;
                bool upgraded = f.IsTemporary && res.UpgradedPersistentId.HasValue && !res.UpgradedPersistentId.Value.IsEmpty;
                return f.With(
                    persistentId: upgraded ? res.UpgradedPersistentId : (PersistentObjectId?)null,
                    displayNameCache: !string.IsNullOrEmpty(res.ResolvedDisplayName) ? res.ResolvedDisplayName : f.DisplayNameCache,
                    typeNameCache: !string.IsNullOrEmpty(res.ResolvedTypeName) ? res.ResolvedTypeName : f.TypeNameCache,
                    isTemporary: upgraded ? false : f.IsTemporary,
                    resolutionState: res.State,
                    resolutionDetail: res.Detail);
            }).ToArray();

            _state = _state.With(favorites: updated);
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
            var dto = SettingsAndFavoritesDto.FromDomain(_projectId, _state.Settings, _state.Favorites);
            var res = _persistenceStore.QueueSaveSettingsAndFavorites(dto, _state.Settings.SettingsRevision);
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
                || kind == "ResumeRotation"
                || kind == "ReorderWindow"
                || kind == "UpdateSettings"
                || kind == "ResetSettings"
                || kind == "ResetLayout";
        }
    }
}
