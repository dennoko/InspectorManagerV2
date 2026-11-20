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
        public List<string> Errors { get; } = new List<string>();
        public void Info(string operationId, long generation, WindowKey? windowKey, string stage, string message) { }
        public void Error(string operationId, long generation, WindowKey? windowKey, string stage, string message)
            => Errors.Add(stage + ": " + message);
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
        public SettingsDto StoredSettings { get; set; } = new SettingsDto();
        public RecoveryJournalDto StoredJournal { get; set; }
        public int JournalSaveCount { get; private set; }
        public int SettingsFlushCount { get; private set; }

        public LoadResult<SettingsDto> LoadSettings()
        {
            if (IsWriteBlockedByNewerSchema)
            {
                return new LoadResult<SettingsDto>(false, new SettingsDto(), fellBackToDefault: true, newerSchemaDetected: true, notice: "Newer schema");
            }
            return new LoadResult<SettingsDto>(true, StoredSettings ?? new SettingsDto());
        }

        public SaveResult QueueSaveSettings(SettingsDto dto, long revision)
        {
            if (IsWriteBlockedByNewerSchema) return new SaveResult(false, true, "Blocked");
            StoredSettings = dto;
            return SaveResult.Ok();
        }

        public SaveResult FlushSettings()
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
                !w.FailRead && w.IsLocked,
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

        public bool IsUserInteractingWithInspector(WindowKey key)
            => (FocusedKey.HasValue && FocusedKey.Value == key)
            || (HoveredKey.HasValue && HoveredKey.Value == key);

        public void FocusInspector(WindowKey key) => FocusCallCount++;
        public void InvalidateCache() { }

        private SelectionSnapshot BuildSelectionFromKeys(List<ObjectRefKey> keys, ObjectRefKey? active)
        {
            if (keys == null || keys.Count == 0) return SelectionSnapshot.Empty;
            var items = keys.Select(k => SelectionBacking != null && SelectionBacking.Objects.TryGetValue(k, out var item)
                ? item
                : new ObjectItemSnapshot(k, k.Value, "GameObject", ObjectCategory.GameObject, false)).ToArray();
            return new SelectionSnapshot(items, active ?? items[0].Key, 0, 0, SelectionSource.RecoveryObservation);
        }
    }

    internal sealed class FakeSelectionAdapter : ISelectionAdapter
    {
        public Dictionary<ObjectRefKey, ObjectItemSnapshot> Objects { get; } = new Dictionary<ObjectRefKey, ObjectItemSnapshot>();
        public SelectionSnapshot CurrentSelection { get; set; } = SelectionSnapshot.Empty;

        public ObjectItemSnapshot RegisterObject(string id, string name = null, ObjectCategory category = ObjectCategory.GameObject, bool isAsset = false)
        {
            var key = new ObjectRefKey(id);
            var item = new ObjectItemSnapshot(key, name ?? id, category.ToString(), category, isAsset);
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

        public SelectionSnapshot CaptureCurrentSelection(long sequence, long generation, SelectionSource source)
        {
            if (CurrentSelection == null || CurrentSelection.IsEmpty) return SelectionSnapshot.Empty;
            return new SelectionSnapshot(CurrentSelection.OrderedTargets, CurrentSelection.ActiveTarget, sequence, generation, source);
        }

        public bool AreAllTargetsAlive(IReadOnlyList<ObjectRefKey> targets)
            => targets != null && targets.Count > 0 && targets.All(k => Objects.ContainsKey(k));
    }
}
