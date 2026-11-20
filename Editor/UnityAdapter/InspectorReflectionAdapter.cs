using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Capability-probed Unity Inspector reflection adapter supporting Unity 2022.3.22f1 AND Unity 6 (6000.x).
    /// All reflection lookup occurs ONCE at initialization (Section 8.1 & 14.1).
    /// Startup probe is strictly read-only and never mutates existing Inspector windows.
    /// </summary>
    public sealed class InspectorReflectionAdapter : IInspectorAdapter
    {
        private readonly UnityObjectResolver _resolver;
        private readonly string _editorSessionId;

        // Cached reflection metadata (discovered once)
        private Type _inspectorWindowType;
        private Type _propertyEditorType;

        // Enumeration
        private MethodInfo _getAllInspectorWindowsMethod;
        private MethodInfo _getInspectorsMethod;

        // Tracker (prefer PropertyEditor.tracker property over m_Tracker field per Section 14.1!)
        private PropertyInfo _trackerProperty;
        private FieldInfo _trackerField;

        // Lock read/write (supports both InspectorWindow.isLocked AND Unity 6 m_LockTracker / EditorLockTracker!)
        private PropertyInfo _inspectorIsLockedProperty;
        private FieldInfo _lockTrackerField;
        private PropertyInfo _lockTrackerIsLockedProperty;
        private MethodInfo _lockTrackerFlipLockedMethod;

        // Targets read/write (prefer combined InspectorWindow.SetObjectsLocked/GetObjectsLocked over tracker fallback!)
        private MethodInfo _windowGetObjectsLockedMethod;
        private MethodInfo _windowSetObjectsLockedMethod;
        private MethodInfo _trackerGetObjectsLockedMethod;
        private MethodInfo _trackerSetObjectsLockedMethod;

        // Rebuild / Flush (static FlushAllOptimizedGUIBlocksIfNeeded per Section 14.1!)
        private MethodInfo _rebuildContentsContainersMethod;
        private MethodInfo _flushAllOptimizedGuiBlocksStaticMethod;

        // Window enumeration cache
        private EditorWindow[] _cachedWindows;
        private AdapterCapabilities _cachedCapabilities;

        public InspectorReflectionAdapter(UnityObjectResolver resolver, string editorSessionId)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _editorSessionId = editorSessionId ?? string.Empty;
            DiscoverInternalApisOnce();
        }

        public AdapterCapabilities ProbeCapabilities()
        {
            if (_cachedCapabilities != null)
            {
                return _cachedCapabilities;
            }

            bool canEnum = _inspectorWindowType != null;
            bool canReadLock = _inspectorWindowType != null && (
                (_inspectorIsLockedProperty != null && _inspectorIsLockedProperty.CanRead)
                || (_lockTrackerField != null && _lockTrackerIsLockedProperty != null && _lockTrackerIsLockedProperty.CanRead)
                || (_trackerProperty != null || _trackerField != null));

            bool canWriteLock = _inspectorWindowType != null && (
                (_inspectorIsLockedProperty != null && _inspectorIsLockedProperty.CanWrite)
                || (_lockTrackerField != null && _lockTrackerIsLockedProperty != null && _lockTrackerIsLockedProperty.CanWrite)
                || (_lockTrackerField != null && _lockTrackerIsLockedProperty != null && _lockTrackerIsLockedProperty.CanRead && _lockTrackerFlipLockedMethod != null)
                || (_trackerProperty != null || _trackerField != null));

            bool canReadTargets = _windowGetObjectsLockedMethod != null || _trackerGetObjectsLockedMethod != null;
            bool usesCombinedWindowApi = _windowSetObjectsLockedMethod != null;
            bool canWriteTargets = usesCombinedWindowApi || (_trackerSetObjectsLockedMethod != null && canWriteLock);
            bool canCreate = _inspectorWindowType != null && typeof(EditorWindow).IsAssignableFrom(_inspectorWindowType);
            bool canClose = canEnum;

            var summaryParts = new List<string>
            {
                "Unity=" + UnityEngine.Application.unityVersion,
                "InspectorType=" + (_inspectorWindowType != null ? "OK" : "MISSING"),
                "LockPath=" + GetLockPathLabel(),
                "TargetWrite=" + (usesCombinedWindowApi ? "InspectorWindow.SetObjectsLocked" : (_trackerSetObjectsLockedMethod != null ? "ActiveEditorTracker.SetObjectsLockedByThisTracker" : "MISSING")),
                "TargetRead=" + (_windowGetObjectsLockedMethod != null ? "InspectorWindow.GetObjectsLocked" : (_trackerGetObjectsLockedMethod != null ? "ActiveEditorTracker.GetObjectsLockedByThisTracker" : "MISSING")),
                "FlushStatic=" + (_flushAllOptimizedGuiBlocksStaticMethod != null ? "OK" : "N/A")
            };

            _cachedCapabilities = new AdapterCapabilities(
                canEnumerateAndIdentify: canEnum,
                canReadLock: canReadLock,
                canWriteLock: canWriteLock,
                canReadTargets: canReadTargets,
                canWriteTargets: canWriteTargets,
                canCreateWindow: canCreate,
                canCloseWindow: canClose,
                usesWindowCombinedTargetLockApi: usesCombinedWindowApi,
                // Section 14.1: SetObjectsLocked takes a List<Object> and does not guarantee independent active-target readback.
                supportsActiveTargetSelection: false,
                // Section 14.1: Empty display compensation is achieved cleanly by unlocking back to ObservedLocked=false (or writing empty locked list).
                supportsEmptyTargetCompensation: canWriteLock,
                // Section 6.1: Standard padlock click origin is not distinguishable reliably from tracker rebuild lock drops.
                canDistinguishStandardUiManualUnlock: false,
                diagnosticSummary: string.Join("; ", summaryParts));

            return _cachedCapabilities;
        }

        public void InvalidateCache()
        {
            _cachedWindows = null;
        }

        public EditorWindow[] GetLiveInspectorWindowsSnapshot()
        {
            if (_inspectorWindowType == null)
            {
                return Array.Empty<EditorWindow>();
            }

            var windows = EnumerateRawInspectorWindows();
            _cachedWindows = windows;
            return windows;
        }

        public IReadOnlyList<ObservedWindowSnapshot> EnumerateInspectors()
        {
            var windows = GetLiveInspectorWindowsSnapshot();
            if (windows.Length == 0)
            {
                return Array.Empty<ObservedWindowSnapshot>();
            }

            // Section 14.3: Always return an immutable snapshot list, never a borrowed mutable buffer.
            var snapshots = new List<ObservedWindowSnapshot>(windows.Length);
            for (int i = 0; i < windows.Length; i++)
            {
                var win = windows[i];
                if (win == null) continue;

                var key = UnityObjectIdHelper.GetWindowKey(win);
                if (!key.IsValid) continue;

                string evidence = UnityObjectIdHelper.BuildWindowIdentityEvidence(_editorSessionId, win);
                bool lockReadable = TryReadLocked(win, out bool readLocked);
                bool locked = lockReadable && readLocked;
                string repName = ReadRepresentativeName(win);

                snapshots.Add(new ObservedWindowSnapshot(
                    key,
                    locked,
                    repName,
                    evidence,
                    isLockReadable: lockReadable));
            }

            return snapshots;
        }

        public bool IsWindowAlive(WindowKey key, string expectedIdentityEvidence = null)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null) return false;

            if (!string.IsNullOrEmpty(expectedIdentityEvidence))
            {
                string actual = UnityObjectIdHelper.BuildWindowIdentityEvidence(_editorSessionId, win);
                if (!string.Equals(actual, expectedIdentityEvidence, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public InspectorReadResult ReadInspectorState(WindowKey key)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null)
            {
                return new InspectorReadResult(InspectorReadStatus.Failed, false, SelectionSnapshot.Empty, string.Empty, string.Empty, "Inspector window not found.");
            }

            string evidence = UnityObjectIdHelper.BuildWindowIdentityEvidence(_editorSessionId, win);
            if (!TryReadLocked(win, out bool isLocked))
            {
                return new InspectorReadResult(InspectorReadStatus.Failed, false, SelectionSnapshot.Empty, string.Empty, evidence, "Unable to read lock state.");
            }

            string repName = ReadRepresentativeName(win);

            if (!isLocked)
            {
                return new InspectorReadResult(InspectorReadStatus.EmptyDisplay, false, SelectionSnapshot.Empty, repName, evidence);
            }

            var readTargetsResult = TryReadLockedTargets(win, out var lockedObjects, out bool trackerPending, out string err);
            if (!readTargetsResult)
            {
                if (trackerPending)
                {
                    return new InspectorReadResult(InspectorReadStatus.PendingTrackerReady, isLocked, SelectionSnapshot.Empty, repName, evidence, "Tracker not yet initialized.");
                }
                return new InspectorReadResult(InspectorReadStatus.Failed, isLocked, SelectionSnapshot.Empty, repName, evidence, err);
            }

            if (lockedObjects == null || lockedObjects.Count == 0)
            {
                return new InspectorReadResult(InspectorReadStatus.EmptyDisplay, isLocked, SelectionSnapshot.Empty, repName, evidence);
            }

            var items = new List<ObjectItemSnapshot>(lockedObjects.Count);
            for (int i = 0; i < lockedObjects.Count; i++)
            {
                var obj = lockedObjects[i];
                if (obj == null) continue;
                var item = _resolver.CreateItemSnapshot(obj);
                if (item != null)
                {
                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                return new InspectorReadResult(InspectorReadStatus.EmptyDisplay, isLocked, SelectionSnapshot.Empty, repName, evidence);
            }

            var selection = new SelectionSnapshot(items, items[0].Key, 0, 0, SelectionSource.RecoveryObservation);
            return new InspectorReadResult(InspectorReadStatus.Populated, isLocked, selection, selection.BuildDisplaySummary(), evidence);
        }

        public AdapterWriteResult WriteLockOnly(WindowKey key, bool locked)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, "Inspector window not found.");
            }

            try
            {
                if (!TryWriteLocked(win, locked, out string writeErr))
                {
                    return new AdapterWriteResult(WindowWriteStatus.Failed, false, writeErr);
                }

                FlushOptimizedGuiBlocksSafe();
                win.Repaint();

                if (TryReadLocked(win, out bool verifiedLock) && verifiedLock == locked)
                {
                    return new AdapterWriteResult(WindowWriteStatus.Verified, verifiedLock);
                }

                return new AdapterWriteResult(WindowWriteStatus.Failed, !locked, "Lock state read-back mismatch.");
            }
            catch (Exception ex)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, ex.Message);
            }
        }

        public AdapterWriteResult WriteTargetsAndLock(
            WindowKey key,
            IReadOnlyList<ObjectRefKey> targets,
            ObjectRefKey? activeTarget,
            bool locked)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, "Inspector window not found.");
            }

            if (targets == null || targets.Count == 0)
            {
                return WriteLockOnly(key, locked);
            }

            // Resolve ALL target objects without shrinking multi-selection.
            var resolvedList = new List<UnityEngine.Object>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                var obj = UnityObjectIdHelper.ResolveObject(targets[i]);
                if (obj == null)
                {
                    return new AdapterWriteResult(WindowWriteStatus.Failed, false, "Target object disappeared before Inspector write.");
                }
                resolvedList.Add(obj);
            }

            // Ensure tracker exists via PropertyEditor.tracker property (Section 14.1: prevents first-update failure on fresh Inspectors).
            var tracker = TryGetTracker(win, ensureCreated: true);

            try
            {
                if (_windowSetObjectsLockedMethod != null)
                {
                    // Preferred combined window API (Section 14.1): sets targets and lock together.
                    _windowSetObjectsLockedMethod.Invoke(win, new object[] { resolvedList });
                    if (locked && TryReadLocked(win, out bool curLock) && !curLock)
                    {
                        TryWriteLocked(win, true, out _);
                    }
                }
                else if (_trackerSetObjectsLockedMethod != null)
                {
                    if (tracker == null)
                    {
                        return new AdapterWriteResult(WindowWriteStatus.Failed, false, "ActiveEditorTracker unavailable on Inspector.");
                    }

                    // Tracker fallback path: explicitly set lock alongside targets.
                    TryWriteLocked(win, locked, out _);
                    _trackerSetObjectsLockedMethod.Invoke(tracker, new object[] { resolvedList });
                    tracker.isLocked = locked;
                }
                else
                {
                    return new AdapterWriteResult(WindowWriteStatus.Failed, false, "No compatible SetObjectsLocked API available.");
                }

                // Rebuild & flush sequence (Section 14.1).
                if (tracker != null)
                {
                    tracker.ForceRebuild();
                }

                if (_rebuildContentsContainersMethod != null)
                {
                    try
                    {
                        _rebuildContentsContainersMethod.Invoke(win, null);
                    }
                    catch
                    {
                        // Optional container rebuild may not be ready on hidden tabs; verified below.
                    }
                }

                FlushOptimizedGuiBlocksSafe();
                win.Repaint();

                // Immediate read-back verification.
                var verify = VerifyTargetsAndLock(key, targets, locked);
                if (verify.Status == WindowWriteStatus.Verified)
                {
                    return new AdapterWriteResult(WindowWriteStatus.Verified, verify.ObservedLocked);
                }

                // Section 14.1: If write succeeded without exception but read-back is still settling (e.g. hidden tab),
                // report PresentationPending so the coordinator verifies on the next Editor update tick with timeout.
                return new AdapterWriteResult(WindowWriteStatus.PresentationPending, verify.ObservedLocked, verify.Error);
            }
            catch (TargetInvocationException tie)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception ex)
            {
                return new AdapterWriteResult(WindowWriteStatus.Failed, false, ex.Message);
            }
        }

        public AdapterVerifyResult VerifyTargetsAndLock(WindowKey key, IReadOnlyList<ObjectRefKey> expectedTargets, bool expectedLocked)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null)
            {
                return new AdapterVerifyResult(WindowWriteStatus.Failed, false, "Inspector window disappeared during verification.");
            }

            if (!TryReadLocked(win, out bool actualLocked))
            {
                return new AdapterVerifyResult(WindowWriteStatus.Failed, false, "Unable to read lock state during verification.");
            }

            if (actualLocked != expectedLocked)
            {
                return new AdapterVerifyResult(WindowWriteStatus.PresentationPending, actualLocked, "Lock state not yet matching expected value.");
            }

            if (!expectedLocked || expectedTargets == null || expectedTargets.Count == 0)
            {
                return new AdapterVerifyResult(WindowWriteStatus.Verified, actualLocked);
            }

            if (!TryReadLockedTargets(win, out var actualObjects, out bool trackerPending, out string readErr))
            {
                if (trackerPending)
                {
                    return new AdapterVerifyResult(WindowWriteStatus.PresentationPending, actualLocked, "Tracker pending initialization.");
                }
                return new AdapterVerifyResult(WindowWriteStatus.Failed, actualLocked, readErr);
            }

            var expectedSet = new HashSet<ObjectRefKey>(expectedTargets);
            var actualSet = new HashSet<ObjectRefKey>();
            if (actualObjects != null)
            {
                for (int i = 0; i < actualObjects.Count; i++)
                {
                    if (actualObjects[i] != null)
                    {
                        var k = UnityObjectIdHelper.GetObjectRefKey(actualObjects[i]);
                        if (k.IsValid) actualSet.Add(k);
                    }
                }
            }

            if (expectedSet.SetEquals(actualSet))
            {
                return new AdapterVerifyResult(WindowWriteStatus.Verified, actualLocked);
            }

            return new AdapterVerifyResult(
                WindowWriteStatus.PresentationPending,
                actualLocked,
                "Locked target set mismatch (expected " + expectedSet.Count + ", observed " + actualSet.Count + ").");
        }

        public AdapterWindowCreateResult CreateInspectorWindow()
        {
            if (_inspectorWindowType == null)
            {
                return new AdapterWindowCreateResult(false, default, false, string.Empty, false, "InspectorWindow type unavailable.");
            }

            try
            {
                var created = ScriptableObject.CreateInstance(_inspectorWindowType) as EditorWindow;
                if (created == null)
                {
                    return new AdapterWindowCreateResult(false, default, false, string.Empty, false, "Failed to instantiate InspectorWindow.");
                }

                created.Show();
                // Trigger lazy tracker creation so first update never fails on fresh Inspector (Section 14.5 #1).
                TryGetTracker(created, ensureCreated: true);

                InvalidateCache();
                var key = UnityObjectIdHelper.GetWindowKey(created);
                string evidence = UnityObjectIdHelper.BuildWindowIdentityEvidence(_editorSessionId, created);
                bool locked = TryReadLocked(created, out bool cur) && cur;

                return new AdapterWindowCreateResult(
                    succeeded: key.IsValid,
                    windowKey: key,
                    observedLocked: locked,
                    identityEvidence: evidence,
                    confirmedEmptyNewWindow: true);
            }
            catch (Exception ex)
            {
                return new AdapterWindowCreateResult(false, default, false, string.Empty, false, ex.Message);
            }
        }

        public AdapterResult CloseInspectorWindow(WindowKey key, string expectedIdentityEvidence)
        {
            var win = FindLiveInspectorWindow(key);
            if (win == null)
            {
                return AdapterResult.Ok();
            }

            if (!string.IsNullOrEmpty(expectedIdentityEvidence))
            {
                string actual = UnityObjectIdHelper.BuildWindowIdentityEvidence(_editorSessionId, win);
                if (!string.Equals(actual, expectedIdentityEvidence, StringComparison.Ordinal))
                {
                    return AdapterResult.Fail("Refused to close window: identity mismatch.");
                }
            }

            try
            {
                win.Close();
                InvalidateCache();
                return AdapterResult.Ok();
            }
            catch (Exception ex)
            {
                return AdapterResult.Fail(ex.Message);
            }
        }

        public WindowKey? GetFocusedInspectorKey()
        {
            var focused = EditorWindow.focusedWindow;
            if (focused == null || _inspectorWindowType == null)
            {
                return null;
            }

            if (!_inspectorWindowType.IsInstanceOfType(focused))
            {
                return null;
            }

            var key = UnityObjectIdHelper.GetWindowKey(focused);
            return key.IsValid ? (WindowKey?)key : null;
        }

        public bool IsUserInteractingWithInspector(WindowKey key)
        {
            if (!key.IsValid || _inspectorWindowType == null)
            {
                return false;
            }

            var focused = EditorWindow.focusedWindow;
            if (focused != null && _inspectorWindowType.IsInstanceOfType(focused) && UnityObjectIdHelper.GetWindowKey(focused) == key)
            {
                return true;
            }

            var hovered = EditorWindow.mouseOverWindow;
            return hovered != null && _inspectorWindowType.IsInstanceOfType(hovered) && UnityObjectIdHelper.GetWindowKey(hovered) == key;
        }

        public bool IsSelectionSourceContinuous()
        {
            // Section 14.3: Avoid stealing keyboard focus when user is actively navigating Hierarchy or Project browser.
            var focused = EditorWindow.focusedWindow;
            if (focused == null) return false;
            string name = focused.GetType().Name;
            return string.Equals(name, "SceneHierarchyWindow", StringComparison.Ordinal)
                || string.Equals(name, "ProjectBrowser", StringComparison.Ordinal);
        }

        public void FocusInspector(WindowKey key)
        {
            var win = FindLiveInspectorWindow(key);
            if (win != null)
            {
                win.Focus();
            }
        }

        public EditorWindow FindLiveInspectorWindow(WindowKey key)
        {
            if (!key.IsValid || _inspectorWindowType == null)
            {
                return null;
            }

            // Fast lookup via UnityObjectIdHelper first.
            var resolved = UnityObjectIdHelper.ResolveWindow(key);
            if (resolved != null && _inspectorWindowType.IsInstanceOfType(resolved))
            {
                return resolved;
            }

            // Fallback scan of live Inspector windows.
            var windows = _cachedWindows ?? EnumerateRawInspectorWindows();
            for (int i = 0; i < windows.Length; i++)
            {
                var w = windows[i];
                if (w != null && UnityObjectIdHelper.GetWindowKey(w) == key)
                {
                    return w;
                }
            }

            return null;
        }

        private void DiscoverInternalApisOnce()
        {
            var editorAssembly = typeof(Editor).Assembly;
            _inspectorWindowType = editorAssembly.GetType("UnityEditor.InspectorWindow");
            _propertyEditorType = editorAssembly.GetType("UnityEditor.PropertyEditor");

            const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags anyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            if (_inspectorWindowType != null)
            {
                var getAll = _inspectorWindowType.GetMethod("GetAllInspectorWindows", anyStatic, null, Type.EmptyTypes, null);
                if (getAll != null && getAll.ReturnType.IsArray && typeof(EditorWindow).IsAssignableFrom(getAll.ReturnType.GetElementType()))
                {
                    _getAllInspectorWindowsMethod = getAll;
                }

                var getInspectors = _inspectorWindowType.GetMethod("GetInspectors", anyStatic, null, Type.EmptyTypes, null);
                if (getInspectors != null && typeof(System.Collections.IList).IsAssignableFrom(getInspectors.ReturnType))
                {
                    _getInspectorsMethod = getInspectors;
                }

                var isLockedProp = _inspectorWindowType.GetProperty("isLocked", anyInstance);
                if (isLockedProp != null && isLockedProp.PropertyType == typeof(bool))
                {
                    _inspectorIsLockedProperty = isLockedProp;
                }

                // Unity 2023.2 / Unity 6 EditorLockTracker support
                var lockTrackerField = _inspectorWindowType.GetField("m_LockTracker", anyInstance);
                if (lockTrackerField != null)
                {
                    var ltType = lockTrackerField.FieldType;
                    var ltIsLocked = ltType.GetProperty("isLocked", anyInstance);
                    if (ltIsLocked != null && ltIsLocked.PropertyType == typeof(bool))
                    {
                        _lockTrackerField = lockTrackerField;
                        _lockTrackerIsLockedProperty = ltIsLocked;
                        _lockTrackerFlipLockedMethod = ltType.GetMethod("FlipLocked", anyInstance, null, Type.EmptyTypes, null);
                    }
                }

                var listObjType = typeof(List<UnityEngine.Object>);
                var getLocked = _inspectorWindowType.GetMethod("GetObjectsLocked", anyInstance, null, new[] { listObjType }, null);
                if (getLocked != null && getLocked.ReturnType == typeof(void))
                {
                    _windowGetObjectsLockedMethod = getLocked;
                }

                var setLocked = _inspectorWindowType.GetMethod("SetObjectsLocked", anyInstance, null, new[] { listObjType }, null);
                if (setLocked != null && setLocked.ReturnType == typeof(void))
                {
                    _windowSetObjectsLockedMethod = setLocked;
                }

                // Section 14.1: FlushAllOptimizedGUIBlocksIfNeeded is STATIC!
                var flushStatic = _inspectorWindowType.GetMethod("FlushAllOptimizedGUIBlocksIfNeeded", anyStatic, null, Type.EmptyTypes, null);
                if (flushStatic != null && flushStatic.ReturnType == typeof(void))
                {
                    _flushAllOptimizedGuiBlocksStaticMethod = flushStatic;
                }
            }

            // PropertyEditor base class (or InspectorWindow directly)
            Type trackerOwner = _propertyEditorType ?? _inspectorWindowType;
            if (trackerOwner != null)
            {
                var tProp = trackerOwner.GetProperty("tracker", anyInstance);
                if (tProp != null && tProp.CanRead && typeof(ActiveEditorTracker).IsAssignableFrom(tProp.PropertyType))
                {
                    _trackerProperty = tProp;
                }

                var tField = trackerOwner.GetField("m_Tracker", anyInstance);
                if (tField != null && typeof(ActiveEditorTracker).IsAssignableFrom(tField.FieldType))
                {
                    _trackerField = tField;
                }

                var rebuild = trackerOwner.GetMethod("RebuildContentsContainers", anyInstance, null, Type.EmptyTypes, null);
                if (rebuild != null && rebuild.ReturnType == typeof(void))
                {
                    _rebuildContentsContainersMethod = rebuild;
                }
            }

            // ActiveEditorTracker target methods
            var trackerType = typeof(ActiveEditorTracker);
            var objListType = typeof(List<UnityEngine.Object>);
            var tGetLocked = trackerType.GetMethod("GetObjectsLockedByThisTracker", anyInstance, null, new[] { objListType }, null);
            if (tGetLocked != null && tGetLocked.ReturnType == typeof(void))
            {
                _trackerGetObjectsLockedMethod = tGetLocked;
            }

            var tSetLocked = trackerType.GetMethod("SetObjectsLockedByThisTracker", anyInstance, null, new[] { objListType }, null);
            if (tSetLocked != null && tSetLocked.ReturnType == typeof(void))
            {
                _trackerSetObjectsLockedMethod = tSetLocked;
            }
        }

        private EditorWindow[] EnumerateRawInspectorWindows()
        {
            if (_inspectorWindowType == null)
            {
                return Array.Empty<EditorWindow>();
            }

            try
            {
                if (_getAllInspectorWindowsMethod != null)
                {
                    var arr = _getAllInspectorWindowsMethod.Invoke(null, null) as Array;
                    if (arr != null)
                    {
                        var list = new List<EditorWindow>(arr.Length);
                        foreach (var item in arr)
                        {
                            if (item is EditorWindow ew && ew != null)
                            {
                                list.Add(ew);
                            }
                        }
                        return list.ToArray();
                    }
                }

                if (_getInspectorsMethod != null)
                {
                    if (_getInspectorsMethod.Invoke(null, null) is System.Collections.IList rawList)
                    {
                        var list = new List<EditorWindow>(rawList.Count);
                        foreach (var item in rawList)
                        {
                            if (item is EditorWindow ew && ew != null)
                            {
                                list.Add(ew);
                            }
                        }
                        return list.ToArray();
                    }
                }
            }
            catch
            {
                // Fall back to Resources.FindObjectsOfTypeAll below.
            }

            var all = Resources.FindObjectsOfTypeAll(_inspectorWindowType);
            if (all == null || all.Length == 0)
            {
                return Array.Empty<EditorWindow>();
            }

            var fallback = new List<EditorWindow>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is EditorWindow ew && ew != null)
                {
                    fallback.Add(ew);
                }
            }
            return fallback.ToArray();
        }

        private ActiveEditorTracker TryGetTracker(EditorWindow win, bool ensureCreated)
        {
            if (win == null) return null;
            try
            {
                // Prefer PropertyEditor.tracker property (creates tracker lazily if ensureCreated)
                if (ensureCreated && _trackerProperty != null)
                {
                    return _trackerProperty.GetValue(win, null) as ActiveEditorTracker;
                }

                if (_trackerField != null)
                {
                    var existing = _trackerField.GetValue(win) as ActiveEditorTracker;
                    if (existing != null || !ensureCreated)
                    {
                        return existing;
                    }
                }

                if (_trackerProperty != null)
                {
                    return _trackerProperty.GetValue(win, null) as ActiveEditorTracker;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        private bool TryReadLocked(EditorWindow win, out bool isLocked)
        {
            isLocked = false;
            if (win == null) return false;

            try
            {
                if (_inspectorIsLockedProperty != null && _inspectorIsLockedProperty.CanRead)
                {
                    isLocked = (bool)_inspectorIsLockedProperty.GetValue(win, null);
                    return true;
                }

                if (_lockTrackerField != null && _lockTrackerIsLockedProperty != null && _lockTrackerIsLockedProperty.CanRead)
                {
                    var lt = _lockTrackerField.GetValue(win);
                    if (lt != null)
                    {
                        isLocked = (bool)_lockTrackerIsLockedProperty.GetValue(lt, null);
                        return true;
                    }
                }

                var tracker = TryGetTracker(win, ensureCreated: false);
                if (tracker != null)
                {
                    isLocked = tracker.isLocked;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private bool TryWriteLocked(EditorWindow win, bool locked, out string error)
        {
            error = string.Empty;
            if (win == null)
            {
                error = "Window is null.";
                return false;
            }

            try
            {
                if (_inspectorIsLockedProperty != null && _inspectorIsLockedProperty.CanWrite)
                {
                    _inspectorIsLockedProperty.SetValue(win, locked, null);
                    return true;
                }

                if (_lockTrackerField != null && _lockTrackerIsLockedProperty != null)
                {
                    var lt = _lockTrackerField.GetValue(win);
                    if (lt != null)
                    {
                        if (_lockTrackerIsLockedProperty.CanWrite)
                        {
                            _lockTrackerIsLockedProperty.SetValue(lt, locked, null);
                            return true;
                        }
                        if (_lockTrackerIsLockedProperty.CanRead && _lockTrackerFlipLockedMethod != null)
                        {
                            bool cur = (bool)_lockTrackerIsLockedProperty.GetValue(lt, null);
                            if (cur != locked)
                            {
                                _lockTrackerFlipLockedMethod.Invoke(lt, null);
                            }
                            return true;
                        }
                    }
                }

                var tracker = TryGetTracker(win, ensureCreated: true);
                if (tracker != null)
                {
                    tracker.isLocked = locked;
                    tracker.ForceRebuild();
                    return true;
                }

                error = "No compatible lock write path available.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool TryReadLockedTargets(EditorWindow win, out List<UnityEngine.Object> objects, out bool trackerPending, out string error)
        {
            objects = new List<UnityEngine.Object>();
            trackerPending = false;
            error = string.Empty;

            try
            {
                if (_windowGetObjectsLockedMethod != null)
                {
                    _windowGetObjectsLockedMethod.Invoke(win, new object[] { objects });
                    return true;
                }

                if (_trackerGetObjectsLockedMethod != null)
                {
                    var tracker = TryGetTracker(win, ensureCreated: false);
                    if (tracker == null)
                    {
                        trackerPending = true;
                        error = "Tracker not yet created.";
                        return false;
                    }
                    _trackerGetObjectsLockedMethod.Invoke(tracker, new object[] { objects });
                    return true;
                }

                error = "No compatible GetObjectsLocked API available.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private string ReadRepresentativeName(EditorWindow win)
        {
            // Section 14.1: activeEditors is ONLY used for UI representative display name, NEVER for exact target set readback!
            try
            {
                var tracker = TryGetTracker(win, ensureCreated: false);
                if (tracker != null)
                {
                    var editors = tracker.activeEditors;
                    if (editors != null && editors.Length > 0 && editors[0] != null && editors[0].target != null)
                    {
                        return editors[0].target.name;
                    }
                }
            }
            catch
            {
                // Non-critical representative name lookup.
            }
            return string.Empty;
        }

        private void FlushOptimizedGuiBlocksSafe()
        {
            if (_flushAllOptimizedGuiBlocksStaticMethod == null) return;
            try
            {
                _flushAllOptimizedGuiBlocksStaticMethod.Invoke(null, null);
            }
            catch
            {
                // Best-effort GUI cache flush.
            }
        }

        private string GetLockPathLabel()
        {
            if (_inspectorIsLockedProperty != null && _inspectorIsLockedProperty.CanWrite)
                return "InspectorWindow.isLocked";
            if (_lockTrackerField != null && _lockTrackerIsLockedProperty != null)
                return "InspectorWindow.m_LockTracker";
            if (_trackerProperty != null || _trackerField != null)
                return "ActiveEditorTracker.isLocked";
            return "MISSING";
        }
    }
}
