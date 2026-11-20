using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Tests
{
    internal sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; } = 1000;
        public void Advance(long ms) => NowMs += ms;
    }

    internal sealed class FakeLogger : IDiagnosticLogger
    {
        private readonly List<DiagnosticLogEntry> _entries = new List<DiagnosticLogEntry>();
        public void Info(string operationId, long generation, WindowKey? windowKey, string stage, string message)
            => _entries.Add(new DiagnosticLogEntry(0, operationId, generation, windowKey, stage, message, false));
        public void Error(string operationId, long generation, WindowKey? windowKey, string stage, string message)
            => _entries.Add(new DiagnosticLogEntry(0, operationId, generation, windowKey, stage, message, true));
        public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries() => _entries.ToArray();
        public void Clear() => _entries.Clear();
    }

    internal sealed class FakeSessionLayoutStore : ISessionLayoutStore
    {
        public string EditorSessionId { get; set; } = "session-1";
        public SessionLayoutDto StoredLayout { get; set; }

        public string GetEditorSessionId() => EditorSessionId;
        public SessionLayoutDto LoadSessionLayout() => StoredLayout;
        public void SaveSessionLayout(SessionLayoutDto dto) => StoredLayout = dto;
        public void ClearSessionLayout() => StoredLayout = null;
    }

    internal sealed class FakePersistenceStore : IPersistenceStore
    {
        public bool IsWriteBlockedByNewerSchema { get; set; }
        public bool FailJournalSaves { get; set; }
        public bool FailSettingsSaves { get; set; }
        public SettingsAndFavoritesDto StoredSettings { get; set; } = new SettingsAndFavoritesDto();
        public RecoveryJournalDto StoredJournal { get; set; }
        public int JournalSaveCount { get; private set; }
        public int SettingsFlushCount { get; private set; }

        public LoadResult<SettingsAndFavoritesDto> LoadSettingsAndFavorites()
        {
            if (IsWriteBlockedByNewerSchema)
            {
                return new LoadResult<SettingsAndFavoritesDto>(false, new SettingsAndFavoritesDto(), fellBackToDefault: true, newerSchemaDetected: true, notice: "Newer schema");
            }
            return new LoadResult<SettingsAndFavoritesDto>(true, StoredSettings ?? new SettingsAndFavoritesDto());
        }

        public SaveResult QueueSaveSettingsAndFavorites(SettingsAndFavoritesDto dto, long revision)
        {
            if (IsWriteBlockedByNewerSchema) return new SaveResult(false, true, "Blocked");
            StoredSettings = dto;
            return SaveResult.Ok();
        }

        public SaveResult FlushSettingsAndFavorites()
        {
            if (IsWriteBlockedByNewerSchema) return new SaveResult(false, true, "Blocked");
            if (FailSettingsSaves) return SaveResult.Fail("Read-only filesystem");
            SettingsFlushCount++;
            return SaveResult.Ok();
        }

        public LoadResult<RecoveryJournalDto> LoadRecoveryJournal()
        {
            return new LoadResult<RecoveryJournalDto>(true, StoredJournal);
        }

        public SaveResult SaveRecoveryJournalImmediate(RecoveryJournalDto dto)
        {
            if (FailJournalSaves) return SaveResult.Fail("Journal storage read-only");
            JournalSaveCount++;
            StoredJournal = dto;
            return SaveResult.Ok();
        }

        public void ClearRecoveryJournal() => StoredJournal = null;
        public void Dispose() { }
    }

    internal sealed class FakeWindowInstance
    {
        public WindowKey Key;
        public string IdentityEvidence;
        public bool IsAlive = true;
        public bool IsLocked;
        public List<ObjectRefKey> LockedTargets = new List<ObjectRefKey>();
        public ObjectRefKey? ActiveTarget;
        public bool TrackerPending { get; set; }
        public bool FailRead { get; set; }
        public bool FailWriteTargets { get; set; }
        public bool FailWriteLock { get; set; }
        public bool ReturnPendingOnWrite { get; set; }
        public WindowWriteStatus DeferredVerifyStatus = WindowWriteStatus.Verified;
        public Action OnBeforeWrite;
        public int WriteTargetCallCount;
        public int WriteLockCallCount;
    }

    internal sealed class FakeInspectorAdapter : IInspectorAdapter
    {
        public AdapterCapabilities Capabilities { get; set; } = AdapterCapabilities.Full();
        public List<FakeWindowInstance> Windows { get; } = new List<FakeWindowInstance>();
        public FakeSelectionAdapter SelectionBacking { get; set; }
        public WindowKey? FocusedKey { get; set; }
        public WindowKey? HoveredKey { get; set; }
        public bool ContinuousSelection { get; set; }
        public int NextCreatedId = 100;
        public int FocusCallCount { get; private set; }

        public FakeWindowInstance AddFakeWindow(string key, bool locked = false, params ObjectRefKey[] initialLockedTargets)
        {
            var win = new FakeWindowInstance
            {
                Key = new WindowKey(key),
                IdentityEvidence = "session-1|InspectorWindow|" + key,
                IsAlive = true,
                IsLocked = locked,
                LockedTargets = initialLockedTargets != null ? initialLockedTargets.ToList() : new List<ObjectRefKey>()
            };
            Windows.Add(win);
            return win;
        }

        public FakeWindowInstance Get(string key) => Windows.FirstOrDefault(w => w.Key.Value == key);

        public AdapterCapabilities ProbeCapabilities() => Capabilities;

        public IReadOnlyList<ObservedWindowSnapshot> EnumerateInspectors()
        {
            return Windows.Where(w => w.IsAlive).Select(w => new ObservedWindowSnapshot(
                w.Key,
                w.IsLocked,
                w.LockedTargets.Count > 0 ? w.LockedTargets[0].Value : string.Empty,
                w.IdentityEvidence,
                BuildSelectionFromKeys(w.LockedTargets, w.ActiveTarget),
                isLockReadable: !w.FailRead)).ToArray();
        }

        public bool IsWindowAlive(WindowKey key, string expectedIdentityEvidence = null)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null) return false;
            if (!string.IsNullOrEmpty(expectedIdentityEvidence) && !string.Equals(w.IdentityEvidence, expectedIdentityEvidence, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        public InspectorReadResult ReadInspectorState(WindowKey key)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null || w.FailRead)
            {
                return new InspectorReadResult(InspectorReadStatus.Failed, false, SelectionSnapshot.Empty, string.Empty, string.Empty, "Injected read failure");
            }
            if (w.TrackerPending)
            {
                return new InspectorReadResult(InspectorReadStatus.PendingTrackerReady, w.IsLocked, SelectionSnapshot.Empty, string.Empty, w.IdentityEvidence, "Tracker pending");
            }
            if (!w.IsLocked || w.LockedTargets.Count == 0)
            {
                return new InspectorReadResult(InspectorReadStatus.EmptyDisplay, w.IsLocked, SelectionSnapshot.Empty, string.Empty, w.IdentityEvidence);
            }
            var sel = BuildSelectionFromKeys(w.LockedTargets, w.ActiveTarget);
            return new InspectorReadResult(InspectorReadStatus.Populated, w.IsLocked, sel, sel.BuildDisplaySummary(), w.IdentityEvidence);
        }

        public AdapterWriteResult WriteLockOnly(WindowKey key, bool locked)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null || w.FailWriteLock)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, w != null && w.IsLocked, "Injected lock write failure");
            }
            w.WriteLockCallCount++;
            w.IsLocked = locked;
            if (!locked) w.LockedTargets.Clear();
            return new AdapterWriteResult(WindowWriteStatus.Verified, w.IsLocked);
        }

        public AdapterWriteResult WriteTargetsAndLock(WindowKey key, IReadOnlyList<ObjectRefKey> targets, ObjectRefKey? activeTarget, bool locked)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, "Window closed");
            }

            w.OnBeforeWrite?.Invoke();
            w.WriteTargetCallCount++;

            if (w.FailWriteTargets)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, w.IsLocked, "Injected target write failure");
            }

            w.IsLocked = locked;
            w.LockedTargets = targets != null ? targets.ToList() : new List<ObjectRefKey>();
            w.ActiveTarget = activeTarget;

            if (w.ReturnPendingOnWrite)
            {
                return new AdapterWriteResult(WindowWriteStatus.PresentationPending, w.IsLocked, "Pending presentation");
            }

            return new AdapterWriteResult(WindowWriteStatus.Verified, w.IsLocked);
        }

        public AdapterVerifyResult VerifyTargetsAndLock(WindowKey key, IReadOnlyList<ObjectRefKey> expectedTargets, bool expectedLocked)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null) return new AdapterVerifyResult(WindowWriteStatus.Failed, false, "Window closed");
            return new AdapterVerifyResult(w.DeferredVerifyStatus, w.IsLocked, w.DeferredVerifyStatus == WindowWriteStatus.Failed ? "Injected verify failure" : string.Empty);
        }

        public AdapterWindowCreateResult CreateInspectorWindow()
        {
            if (!Capabilities.CanCreateWindow)
            {
                return new AdapterWindowCreateResult(false, default, false, string.Empty, false, "Cannot create");
            }
            string id = "win-new-" + (NextCreatedId++);
            var win = AddFakeWindow(id, locked: false);
            return new AdapterWindowCreateResult(true, win.Key, false, win.IdentityEvidence, confirmedEmptyNewWindow: true);
        }

        public AdapterResult CloseInspectorWindow(WindowKey key, string expectedIdentityEvidence)
        {
            var w = Windows.FirstOrDefault(x => x.Key == key && x.IsAlive);
            if (w == null) return AdapterResult.Ok();
            if (!string.IsNullOrEmpty(expectedIdentityEvidence) && !string.Equals(w.IdentityEvidence, expectedIdentityEvidence, StringComparison.Ordinal))
            {
                return AdapterResult.Fail("Identity mismatch");
            }
            w.IsAlive = false;
            return AdapterResult.Ok();
        }

        public WindowKey? GetFocusedInspectorKey() => FocusedKey;
        public bool IsUserInteractingWithInspector(WindowKey key) => FocusedKey == key || HoveredKey == key;
        public bool IsSelectionSourceContinuous() => ContinuousSelection;
        public void FocusInspector(WindowKey key) => FocusCallCount++;
        public void InvalidateCache() { }

        private SelectionSnapshot BuildSelectionFromKeys(List<ObjectRefKey> keys, ObjectRefKey? active)
        {
            if (keys == null || keys.Count == 0) return SelectionSnapshot.Empty;
            var items = keys.Select(k => SelectionBacking != null && SelectionBacking.Objects.TryGetValue(k, out var item)
                ? item
                : new ObjectItemSnapshot(k, k.Value, "GameObject", ObjectCategory.GameObject, false, PersistentObjectId.Empty)).ToArray();
            return new SelectionSnapshot(items, active ?? items[0].Key, 0, 0, SelectionSource.RecoveryObservation);
        }
    }

    internal sealed class FakeSelectionAdapter : ISelectionAdapter
    {
        public Dictionary<ObjectRefKey, ObjectItemSnapshot> Objects { get; } = new Dictionary<ObjectRefKey, ObjectItemSnapshot>();
        public SelectionSnapshot CurrentSelection { get; set; } = SelectionSnapshot.Empty;
        public bool EmitSelectionChangedOnSet { get; set; } = true;
        public Action OnGlobalSelectionChanged;
        public int SetGlobalSelectionCallCount { get; private set; }

        public ObjectItemSnapshot RegisterObject(string id, string name = null, ObjectCategory category = ObjectCategory.GameObject, bool isAsset = false)
        {
            var key = new ObjectRefKey(id);
            var item = new ObjectItemSnapshot(key, name ?? id, category.ToString(), category, isAsset, new PersistentObjectId(
                isAsset ? PersistentObjectKind.Asset : PersistentObjectKind.SceneObject,
                isAsset ? "guid-" + id : string.Empty,
                isAsset ? 100 : 0,
                "gid-" + id,
                isAsset ? string.Empty : "scene-1"));
            Objects[key] = item;
            return item;
        }

        public void DeleteObject(string id) => Objects.Remove(new ObjectRefKey(id));

        public SelectionSnapshot BuildSnapshot(params string[] ids)
        {
            if (ids == null || ids.Length == 0) return SelectionSnapshot.Empty;
            var items = ids.Select(id => Objects[new ObjectRefKey(id)]).ToArray();
            return new SelectionSnapshot(items, items[0].Key, 0, 0, SelectionSource.UserSelection);
        }

        public SelectionSnapshot BuildSnapshotWithActive(string activeId, params string[] ids)
        {
            var items = ids.Select(id => Objects[new ObjectRefKey(id)]).ToArray();
            return new SelectionSnapshot(items, new ObjectRefKey(activeId), 0, 0, SelectionSource.UserSelection);
        }

        public SelectionSnapshot CaptureCurrentSelection(long sequence, long generation, SelectionSource source)
        {
            if (CurrentSelection == null || CurrentSelection.IsEmpty) return SelectionSnapshot.Empty;
            return new SelectionSnapshot(CurrentSelection.OrderedTargets, CurrentSelection.ActiveTarget, sequence, generation, source);
        }

        public bool TrySetGlobalSelection(IReadOnlyList<ObjectRefKey> targets, ObjectRefKey? activeTarget, out string error)
        {
            error = string.Empty;
            if (!AreAllTargetsAlive(targets))
            {
                error = "Missing object";
                return false;
            }
            SetGlobalSelectionCallCount++;
            var items = targets.Select(k => Objects[k]).ToArray();
            CurrentSelection = new SelectionSnapshot(items, activeTarget ?? items[0].Key, 0, 0, SelectionSource.UserSelection);
            if (EmitSelectionChangedOnSet)
            {
                OnGlobalSelectionChanged?.Invoke();
            }
            return true;
        }

        public bool AreAllTargetsAlive(IReadOnlyList<ObjectRefKey> targets)
            => targets != null && targets.Count > 0 && targets.All(k => Objects.ContainsKey(k));

        public bool IsTargetAlive(ObjectRefKey key) => Objects.ContainsKey(key);
        public bool PingObject(ObjectRefKey key) => Objects.ContainsKey(key);
        public bool TryStartDrag(IReadOnlyList<ObjectRefKey> targets, string title, out string error)
        {
            error = AreAllTargetsAlive(targets) ? string.Empty : "Missing target";
            return string.IsNullOrEmpty(error);
        }
    }

    internal sealed class FakeFavoriteResolver : IFavoriteResolver
    {
        private readonly FakeSelectionAdapter _selection;
        public Dictionary<string, FavoriteResolutionState> OverrideStates { get; } = new Dictionary<string, FavoriteResolutionState>();

        public FakeFavoriteResolver(FakeSelectionAdapter selection) => _selection = selection;

        public FavoriteResolutionResult Resolve(FavoriteEntry entry)
        {
            if (OverrideStates.TryGetValue(entry.FavoriteId, out var st))
            {
                return new FavoriteResolutionResult(st, null, null, entry.DisplayNameCache, entry.TypeNameCache, st.ToString());
            }
            var match = _selection.Objects.Values.FirstOrDefault(o => o.PersistentId == entry.PersistentId);
            if (match != null)
            {
                return new FavoriteResolutionResult(FavoriteResolutionState.Resolved, match.Key, null, match.DisplayName, match.TypeName, "Resolved");
            }
            return new FavoriteResolutionResult(FavoriteResolutionState.DeletedOrMissing, null, null, entry.DisplayNameCache, entry.TypeNameCache, "Missing");
        }

        public FavoriteEntry CreateFromLiveObject(ObjectRefKey key, int sortOrder)
        {
            if (!_selection.Objects.TryGetValue(key, out var item)) return null;
            return new FavoriteEntry(Guid.NewGuid().ToString("N"), item.PersistentId, item.DisplayName, item.TypeName, sortOrder, false, null, FavoriteResolutionState.Resolved, "Resolved");
        }

        public void InvalidateCache() { }
    }
}
