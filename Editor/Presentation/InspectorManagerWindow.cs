using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.Presentation
{
    public interface IManagerWindowHost
    {
        InspectorManagerCoordinator Coordinator { get; }
        ISelectionAdapter SelectionAdapter { get; }
        Func<UnityEngine.Object, ObjectRefKey> GetObjectRefKey { get; }
        void RegisterWindow(EditorWindow window);
        void UnregisterWindowOnDestroy(EditorWindow window);
    }

    /// <summary>
    /// Presentation layer EditorWindow (Section 3, 6.2, 11, 14.3).
    /// - Strictly locks one immutable UiSnapshot at EventType.Layout and reuses it across Repaint so IMGUI layout never drifts.
    /// - NEVER performs Unity Inspector writes, Selection writes, or file I/O inside OnGUI; only enqueues commands.
    /// </summary>
    public sealed class InspectorManagerWindow : EditorWindow
    {
        public static Func<IManagerWindowHost> HostProvider;

        private static readonly ObjectCategory[] s_configurableCategories =
        {
            ObjectCategory.Folder,
            ObjectCategory.Scene,
            ObjectCategory.Script,
            ObjectCategory.GameObject,
            ObjectCategory.Component,
            ObjectCategory.Prefab,
            ObjectCategory.Material,
            ObjectCategory.Texture,
            ObjectCategory.Shader,
            ObjectCategory.Audio,
            ObjectCategory.Animation,
            ObjectCategory.Mesh,
            ObjectCategory.Font,
            ObjectCategory.ScriptableObject,
            ObjectCategory.OtherAsset
        };

        private IManagerWindowHost _host;
        private UiSnapshot _frameSnapshot;
        private int _selectedTab;
        private Vector2 _scrollPos;
        private Vector2 _diagScrollPos;
        private bool _subscribed;

        // Drag state for history item drag-to-Unity
        private int _draggingHistoryIndex = -1;
        private Vector2 _dragStartMousePos;

        public void AttachHost(IManagerWindowHost host)
        {
            if (_host != null && _subscribed)
            {
                _host.Coordinator.StateChanged -= OnRuntimeStateChanged;
                _subscribed = false;
            }

            _host = host;
            if (_host != null)
            {
                _host.RegisterWindow(this);
                _host.Coordinator.StateChanged += OnRuntimeStateChanged;
                _subscribed = true;
            }
            Repaint();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Inspector Manager V2");
            minSize = new Vector2(360f, 320f);
            EnsureHostAttached();
        }

        private void OnDisable()
        {
            // Section 9: Do NOT interpret OnDisable alone as user closing the window!
            if (_host != null && _subscribed)
            {
                _host.Coordinator.StateChanged -= OnRuntimeStateChanged;
                _subscribed = false;
            }
        }

        private void OnDestroy()
        {
            if (_host != null)
            {
                if (_subscribed)
                {
                    _host.Coordinator.StateChanged -= OnRuntimeStateChanged;
                    _subscribed = false;
                }
                _host.UnregisterWindowOnDestroy(this);
            }
        }

        private void OnRuntimeStateChanged()
        {
            Repaint();
        }

        private void EnsureHostAttached()
        {
            if (_host == null && HostProvider != null)
            {
                var provided = HostProvider();
                if (provided != null)
                {
                    AttachHost(provided);
                }
            }
            else if (_host != null && !_subscribed)
            {
                _host.Coordinator.StateChanged += OnRuntimeStateChanged;
                _subscribed = true;
            }
        }

        private void OnGUI()
        {
            EnsureHostAttached();
            if (_host == null)
            {
                EditorGUILayout.HelpBox("Inspector Manager V2 runtime is initializing...", MessageType.Info);
                return;
            }

            // Section 14.3: Capture one immutable UiSnapshot at Layout and use it consistently through Repaint.
            if (Event.current.type == EventType.Layout || _frameSnapshot == null)
            {
                _frameSnapshot = _host.Coordinator.GetUiSnapshot();
            }

            var snapshot = _frameSnapshot;
            var state = snapshot.State;
            string lang = state.Settings.Language;

            DrawHeaderAndControls(snapshot, state, lang);
            DrawBanners(snapshot, state, lang);

            EditorGUILayout.Space(4);
            string[] tabs =
            {
                Localization.Get(lang, "TabInspectors"),
                Localization.Get(lang, "TabHistory") + " (" + state.NavigationHistory.Entries.Count + ")",
                Localization.Get(lang, "TabFavorites") + " (" + state.Favorites.Count + ")",
                Localization.Get(lang, "TabSettings")
            };
            _selectedTab = GUILayout.Toolbar(_selectedTab, tabs);
            EditorGUILayout.Space(4);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            switch (_selectedTab)
            {
                case 0:
                    DrawInspectorsTab(state, lang);
                    break;
                case 1:
                    DrawHistoryTab(state, lang);
                    break;
                case 2:
                    DrawFavoritesTab(state, lang);
                    break;
                case 3:
                    DrawSettingsAndDiagnosticsTab(snapshot, state, lang);
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeaderAndControls(UiSnapshot snapshot, ManagerState state, string lang)
        {
            var coordinator = _host.Coordinator;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Row 1: Lifecycle badge + Start/Stop + Pause/Resume + Back/Forward
            EditorGUILayout.BeginHorizontal();
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = GetLifecycleColor(state.LifecycleState);
            GUILayout.Label(Localization.LifecycleLabel(lang, state.LifecycleState), EditorStyles.miniButton, GUILayout.Width(130));
            GUI.backgroundColor = prevBg;

            bool canStart = (state.LifecycleState == RuntimeLifecycleState.Off || state.LifecycleState == RuntimeLifecycleState.Suspended)
                && state.Capabilities.SupportsRotation;
            bool isRunning = state.LifecycleState == RuntimeLifecycleState.Running;
            bool isPaused = state.LifecycleState == RuntimeLifecycleState.Paused;
            bool isActiveSession = isRunning || isPaused;

            using (new EditorGUI.DisabledScope(!canStart))
            {
                if (!isActiveSession && GUILayout.Button(Localization.Get(lang, "Start"), GUILayout.Height(20)))
                {
                    coordinator.EnqueueStartRotation();
                }
            }

            if (isActiveSession)
            {
                if (isRunning && GUILayout.Button(Localization.Get(lang, "Pause"), GUILayout.Height(20)))
                {
                    coordinator.EnqueuePauseRotation();
                }
                else if (isPaused && GUILayout.Button(Localization.Get(lang, "Resume"), GUILayout.Height(20)))
                {
                    coordinator.EnqueueResumeRotation();
                }

                if (GUILayout.Button(Localization.Get(lang, "Stop"), GUILayout.Height(20)))
                {
                    coordinator.EnqueueStopRotation(StopReason.UserRequested);
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!state.NavigationHistory.CanGoBack))
            {
                if (GUILayout.Button(Localization.Get(lang, "Back"), GUILayout.Width(68), GUILayout.Height(20)))
                {
                    coordinator.EnqueueNavigateBack();
                }
            }

            using (new EditorGUI.DisabledScope(!state.NavigationHistory.CanGoForward))
            {
                if (GUILayout.Button(Localization.Get(lang, "Forward"), GUILayout.Width(68), GUILayout.Height(20)))
                {
                    coordinator.EnqueueNavigateForward();
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // Row 2: Mode selector + Add Inspector + Bulk Lock/Unlock (showing target count per Section 6.1)
            EditorGUILayout.BeginHorizontal();
            int modeIdx = state.Settings.Mode == RotationMode.History ? 0 : 1;
            int nextModeIdx = GUILayout.Toolbar(modeIdx, new[]
            {
                Localization.Get(lang, "ModeHistory"),
                Localization.Get(lang, "ModeCycle")
            }, GUILayout.Width(200));

            if (nextModeIdx != modeIdx)
            {
                var nextMode = nextModeIdx == 0 ? RotationMode.History : RotationMode.Cycle;
                coordinator.EnqueueUpdateSettings(state.Settings.WithNextRevision(mode: nextMode), isExplicitLayoutChange: true);
            }

            GUILayout.FlexibleSpace();

            int managedCount = state.GetManagedAliveWindowsOrdered().Count;
            using (new EditorGUI.DisabledScope(!state.Capabilities.CanCreateWindow))
            {
                if (GUILayout.Button(Localization.Get(lang, "AddInspector"), EditorStyles.miniButton))
                {
                    coordinator.EnqueueAddInspector();
                }
            }

            using (new EditorGUI.DisabledScope(managedCount == 0 || !state.Capabilities.CanWriteLock))
            {
                if (GUILayout.Button(Localization.Format(lang, "LockAll", managedCount), EditorStyles.miniButton))
                {
                    coordinator.EnqueueBulkLock(true);
                }
                if (GUILayout.Button(Localization.Format(lang, "UnlockAll", managedCount), EditorStyles.miniButton))
                {
                    coordinator.EnqueueBulkLock(false);
                }
            }

            EditorGUILayout.EndHorizontal();

            // Mode explanation hint (Section 5.3: explicitly state MRU distinct selection semantics)
            string modeHint = state.Settings.Mode == RotationMode.History
                ? Localization.Get(lang, "ModeHistoryHint")
                : Localization.Get(lang, "ModeCycleHint");
            EditorGUILayout.LabelField(modeHint, EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawBanners(UiSnapshot snapshot, ManagerState state, string lang)
        {
            if (state.LifecycleState == RuntimeLifecycleState.Faulted || state.HasIncompleteRestorations)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(state.LastError) ? Localization.Get(lang, "StatusFaulted") : state.LastError,
                    EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(Localization.Get(lang, "RetryRestore"), GUILayout.Width(115), GUILayout.Height(24)))
                {
                    _host.Coordinator.EnqueueRetryRestore();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!state.Capabilities.SupportsRotation)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "RotationUnavailable"), MessageType.Warning);
            }

            if (state.PersistenceWriteBlocked)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "PersistenceBlocked"), MessageType.Warning);
            }

            if (snapshot.HasPendingVerification)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "PendingVerifyBanner"), MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(state.StatusNotice)
                && state.StatusNotice != "Running"
                && state.StatusNotice != "Off"
                && state.StatusNotice != "Paused")
            {
                EditorGUILayout.HelpBox(state.StatusNotice, MessageType.Info);
            }
        }

        private void DrawInspectorsTab(ManagerState state, string lang)
        {
            var coordinator = _host.Coordinator;
            var managed = state.GetManagedAliveWindowsOrdered();
            var participating = state.GetParticipatingWindowsOrdered();
            var external = state.Windows.Where(w => !w.IsManaged && w.IsAlive).OrderBy(w => w.FixedDisplayNumber).ToArray();

            // Determine role slots for participating windows
            var roleSlotMap = new Dictionary<WindowKey, int>();
            for (int i = 0; i < participating.Count; i++)
            {
                roleSlotMap[participating[i].Key] = i;
            }

            WindowKey? cycleNextKey = null;
            if (state.Settings.Mode == RotationMode.Cycle && participating.Count > 0)
            {
                cycleNextKey = participating
                    .OrderBy(w => w.LastSuccessSequence)
                    .ThenBy(w => w.RoleOrder)
                    .ThenBy(w => w.FixedDisplayNumber)
                    .First().Key;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Localization.Format(lang, "ManagedInspectors", managed.Count), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Get(lang, "Refresh"), EditorStyles.miniButton, GUILayout.Width(64)))
            {
                coordinator.EnqueueSynchronizeWindows();
            }
            if (GUILayout.Button(Localization.Get(lang, "ResetLayout"), EditorStyles.miniButton, GUILayout.Width(130)))
            {
                coordinator.EnqueueResetLayout();
            }
            EditorGUILayout.EndHorizontal();

            if (managed.Count == 0)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "NoManagedInspectors"), MessageType.Info);
            }
            else
            {
                for (int i = 0; i < managed.Count; i++)
                {
                    var w = managed[i];
                    DrawManagedWindowRow(state, w, i, managed.Count, roleSlotMap, cycleNextKey, lang);
                }
            }

            if (external.Length > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(Localization.Format(lang, "ExternalInspectors", external.Length), EditorStyles.boldLabel);
                for (int i = 0; i < external.Length; i++)
                {
                    var ext = external[i];
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    GUILayout.Label("EXT", EditorStyles.miniBoldLabel, GUILayout.Width(32));
                    GUILayout.Label(ext.ObservedLocked ? "🔒" : "🔓", GUILayout.Width(22));
                    GUILayout.Label(string.IsNullOrEmpty(ext.ObservedRepresentativeName) ? "(Inspector)" : ext.ObservedRepresentativeName, EditorStyles.label);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Localization.Get(lang, "Adopt"), EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        coordinator.EnqueueAdoptInspector(ext.Key);
                    }
                    if (GUILayout.Button(Localization.Get(lang, "BtnFocus"), EditorStyles.miniButton, GUILayout.Width(48)))
                    {
                        coordinator.EnqueueFocusInspector(ext.Key);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(Localization.Get(lang, "ManualUnlockHint"), EditorStyles.miniLabel);
        }

        private void DrawManagedWindowRow(
            ManagerState state,
            WindowRecord w,
            int index,
            int totalManaged,
            Dictionary<WindowKey, int> roleSlotMap,
            WindowKey? cycleNextKey,
            string lang)
        {
            var coordinator = _host.Coordinator;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();

            // Fixed Display Number (#N) — never changes on reorder!
            GUILayout.Label("#" + w.FixedDisplayNumber, EditorStyles.boldLabel, GUILayout.Width(30));

            // Role badge
            string roleBadge = "-";
            if (w.CanParticipate)
            {
                if (state.Settings.Mode == RotationMode.History && roleSlotMap.TryGetValue(w.Key, out int slot))
                {
                    roleBadge = slot == 0 ? Localization.Get(lang, "RoleLatest") : ("-" + slot);
                }
                else if (state.Settings.Mode == RotationMode.Cycle)
                {
                    roleBadge = cycleNextKey.HasValue && cycleNextKey.Value == w.Key
                        ? Localization.Get(lang, "RoleCycleNext")
                        : Localization.Get(lang, "RoleCycle");
                }
            }
            GUILayout.Label("[" + roleBadge + "]", EditorStyles.miniLabel, GUILayout.Width(52));

            // Participation / Exclusion / Unlock / Fault status
            string statusText = GetWindowStatusText(w, lang);
            GUILayout.Label(statusText, EditorStyles.miniLabel, GUILayout.Width(64));

            // Target display name + assignment label
            string targetSummary = w.AssignedSelection != null && !w.AssignedSelection.IsEmpty
                ? w.AssignedSelection.BuildDisplaySummary()
                : (string.IsNullOrEmpty(w.ObservedRepresentativeName) ? "(Empty)" : w.ObservedRepresentativeName);

            if (w.AssignmentLabel == AssignmentLabel.InitialDisplay)
            {
                targetSummary += " [" + Localization.Get(lang, "LabelInitialDisplay") + "]";
            }
            else if (w.AssignmentLabel == AssignmentLabel.HistoryUnassigned)
            {
                targetSummary += " [" + Localization.Get(lang, "LabelHistoryUnassigned") + "]";
            }

            GUILayout.Label(targetSummary, EditorStyles.label, GUILayout.ExpandWidth(true));

            // Reorder Up / Down (uses WindowKey!)
            using (new EditorGUI.DisabledScope(index <= 0))
            {
                if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                {
                    coordinator.EnqueueReorderWindow(w.Key, index - 1);
                }
            }
            using (new EditorGUI.DisabledScope(index >= totalManaged - 1))
            {
                if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                {
                    coordinator.EnqueueReorderWindow(w.Key, index + 1);
                }
            }

            // Lock toggle (shows verified actual ObservedLocked state!)
            using (new EditorGUI.DisabledScope(!state.Capabilities.CanWriteLock))
            {
                string lockGlyph = w.ObservedLocked ? Localization.Get(lang, "BtnLock") : Localization.Get(lang, "BtnUnlock");
                if (GUILayout.Button(lockGlyph, EditorStyles.miniButton, GUILayout.Width(28)))
                {
                    coordinator.EnqueueToggleLock(w.Key);
                }
            }

            // Explicit Exclude / Include toggle
            string exLabel = w.IsExplicitlyExcluded ? Localization.Get(lang, "BtnInclude") : Localization.Get(lang, "BtnExclude");
            if (GUILayout.Button(exLabel, EditorStyles.miniButton, GUILayout.Width(48)))
            {
                coordinator.EnqueueSetExcluded(w.Key, !w.IsExplicitlyExcluded);
            }

            // Focus
            if (GUILayout.Button(Localization.Get(lang, "BtnFocus"), EditorStyles.miniButton, GUILayout.Width(42)))
            {
                coordinator.EnqueueFocusInspector(w.Key);
            }

            // Close (restores lease before closing)
            using (new EditorGUI.DisabledScope(!state.Capabilities.CanCloseWindow))
            {
                if (GUILayout.Button(Localization.Get(lang, "BtnClose"), EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    coordinator.EnqueueCloseInspector(w.Key);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (w.IsFaultIsolated && !string.IsNullOrEmpty(w.FaultReason))
            {
                EditorGUILayout.LabelField("Fault: " + w.FaultReason, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawHistoryTab(ManagerState state, string lang)
        {
            var coordinator = _host.Coordinator;
            var entries = state.NavigationHistory.Entries;
            int cursor = state.NavigationHistory.CursorIndex;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Localization.Get(lang, "DragHint"), EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(entries.Count == 0))
            {
                if (GUILayout.Button(Localization.Get(lang, "ClearHistory"), EditorStyles.miniButton, GUILayout.Width(96)))
                {
                    coordinator.EnqueueClearNavigationHistory();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "HistoryEmpty"), MessageType.Info);
                return;
            }

            // Display newest first
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                bool isCurrent = i == cursor;
                var rowRect = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                string marker = isCurrent ? "●" : " ";
                GUILayout.Label(marker, EditorStyles.boldLabel, GUILayout.Width(16));
                GUILayout.Label("#" + (i + 1), EditorStyles.miniLabel, GUILayout.Width(28));
                GUILayout.Label(entry.BuildDisplaySummary(), isCurrent ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.ExpandWidth(true));

                if (GUILayout.Button(Localization.Get(lang, "BtnSelect"), EditorStyles.miniButton, GUILayout.Width(52)))
                {
                    coordinator.EnqueueNavigateToIndex(i);
                }

                EditorGUILayout.EndHorizontal();

                // Support drag-and-drop from history rows (Section 7: verifies ALL targets resolve before starting drag!)
                HandleHistoryRowDrag(rowRect, i, entry);
            }
        }

        private void HandleHistoryRowDrag(Rect rowRect, int index, SelectionSnapshot entry)
        {
            var evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rowRect.Contains(evt.mousePosition))
            {
                _draggingHistoryIndex = index;
                _dragStartMousePos = evt.mousePosition;
            }
            else if (evt.type == EventType.MouseDrag && _draggingHistoryIndex == index)
            {
                if ((evt.mousePosition - _dragStartMousePos).magnitude > 6f)
                {
                    _draggingHistoryIndex = -1;
                    if (_host.SelectionAdapter.TryStartDrag(entry.GetOrderedKeys(), entry.BuildDisplaySummary(), out string err))
                    {
                        evt.Use();
                    }
                    else
                    {
                        Debug.LogWarning("[InspectorManagerV2] Drag aborted: " + err);
                    }
                }
            }
            else if (evt.type == EventType.MouseUp)
            {
                _draggingHistoryIndex = -1;
            }
        }

        private void DrawFavoritesTab(ManagerState state, string lang)
        {
            var coordinator = _host.Coordinator;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Get(lang, "AddCurrentFavorite"), GUILayout.Height(22)))
            {
                coordinator.EnqueueAddFavoriteFromCurrentSelection();
            }
            if (GUILayout.Button(Localization.Get(lang, "Refresh"), GUILayout.Width(64), GUILayout.Height(22)))
            {
                coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
            }
            EditorGUILayout.EndHorizontal();

            // Drag & Drop zone to add favorites
            Rect dropRect = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, Localization.Get(lang, "FavoriteDropZone"), EditorStyles.helpBox);
            HandleFavoriteDropZone(dropRect);

            EditorGUILayout.Space(4);

            if (state.Favorites.Count == 0)
            {
                EditorGUILayout.HelpBox(Localization.Get(lang, "FavoritesEmpty"), MessageType.Info);
                return;
            }

            for (int i = 0; i < state.Favorites.Count; i++)
            {
                var fav = state.Favorites[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                string statusLabel = GetFavoriteStatusLabel(fav.ResolutionState, lang);
                GUILayout.Label("[" + statusLabel + "]", EditorStyles.miniLabel, GUILayout.Width(92));
                GUILayout.Label(fav.DisplayNameCache, EditorStyles.label, GUILayout.ExpandWidth(true));

                using (new EditorGUI.DisabledScope(i <= 0))
                {
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                    {
                        coordinator.EnqueueReorderFavorite(fav.FavoriteId, i - 1);
                    }
                }
                using (new EditorGUI.DisabledScope(i >= state.Favorites.Count - 1))
                {
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                    {
                        coordinator.EnqueueReorderFavorite(fav.FavoriteId, i + 1);
                    }
                }

                bool selectable = fav.ResolutionState == FavoriteResolutionState.Resolved || fav.ResolutionState == FavoriteResolutionState.TemporaryUnsaved;
                using (new EditorGUI.DisabledScope(!selectable))
                {
                    if (GUILayout.Button(Localization.Get(lang, "BtnSelect"), EditorStyles.miniButton, GUILayout.Width(46)))
                    {
                        coordinator.EnqueueSelectFavorite(fav.FavoriteId);
                    }
                    if (GUILayout.Button(Localization.Get(lang, "BtnPing"), EditorStyles.miniButton, GUILayout.Width(42)))
                    {
                        coordinator.EnqueuePingFavorite(fav.FavoriteId);
                    }
                }

                if (GUILayout.Button(Localization.Get(lang, "BtnRemove"), EditorStyles.miniButton, GUILayout.Width(48)))
                {
                    coordinator.EnqueueRemoveFavorite(fav.FavoriteId);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private void HandleFavoriteDropZone(Rect dropRect)
        {
            var evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        for (int i = 0; i < DragAndDrop.objectReferences.Length; i++)
                        {
                            var obj = DragAndDrop.objectReferences[i];
                            if (obj == null) continue;
                            var key = _host.GetObjectRefKey(obj);
                            if (key.IsValid)
                            {
                                _host.Coordinator.EnqueueAddFavoriteObject(key);
                            }
                        }
                    }
                    evt.Use();
                }
            }
        }

        private void DrawSettingsAndDiagnosticsTab(UiSnapshot snapshot, ManagerState state, string lang)
        {
            var coordinator = _host.Coordinator;
            var s = state.Settings;

            EditorGUILayout.LabelField(Localization.Get(lang, "SettingsHeader"), EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Language
            int langIdx = string.Equals(s.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            int nextLangIdx = EditorGUILayout.Popup(Localization.Get(lang, "Language"), langIdx, new[] { "日本語", "English" });
            if (nextLangIdx != langIdx)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(language: nextLangIdx == 0 ? "ja" : "en"));
            }

            int maxNav = EditorGUILayout.IntSlider(Localization.Get(lang, "MaxNavHistory"), s.MaxNavigationHistory, 10, 500);
            if (maxNav != s.MaxNavigationHistory)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(maxNavigationHistory: maxNav));
            }

            int maxRot = EditorGUILayout.IntSlider(Localization.Get(lang, "MaxRotHistory"), s.MaxRotationHistory, 4, 200);
            if (maxRot != s.MaxRotationHistory)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(maxRotationHistory: maxRot));
            }

            bool recScene = EditorGUILayout.ToggleLeft(Localization.Get(lang, "RecordSceneObjects"), s.RecordSceneObjects);
            if (recScene != s.RecordSceneObjects)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(recordSceneObjects: recScene));
            }

            bool recAssets = EditorGUILayout.ToggleLeft(Localization.Get(lang, "RecordAssets"), s.RecordAssets);
            if (recAssets != s.RecordAssets)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(recordAssets: recAssets));
            }

            bool autoFocus = EditorGUILayout.ToggleLeft(Localization.Get(lang, "AutoFocus"), s.AutoFocusUpdatedInspector);
            if (autoFocus != s.AutoFocusUpdatedInspector)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(autoFocusUpdatedInspector: autoFocus));
            }

            bool showOverlays = EditorGUILayout.ToggleLeft(Localization.Get(lang, "ShowOverlays"), s.ShowInspectorOverlays);
            if (showOverlays != s.ShowInspectorOverlays)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(showInspectorOverlays: showOverlays));
            }

            bool autoCleanup = EditorGUILayout.ToggleLeft(Localization.Get(lang, "AutoCleanupFavs"), s.AutoCleanupMissingFavorites);
            if (autoCleanup != s.AutoCleanupMissingFavorites)
            {
                coordinator.EnqueueUpdateSettings(s.WithNextRevision(autoCleanupMissingFavorites: autoCleanup));
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button(Localization.Get(lang, "ResetSettings"), GUILayout.Width(190)))
            {
                coordinator.EnqueueResetSettings();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(Localization.Get(lang, "BlockedCategoriesHeader"), EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(Localization.Get(lang, "BlockedCategoriesHint"), EditorStyles.wordWrappedMiniLabel);

            int cols = 3;
            for (int i = 0; i < s_configurableCategories.Length; i += cols)
            {
                EditorGUILayout.BeginHorizontal();
                for (int c = 0; c < cols && i + c < s_configurableCategories.Length; c++)
                {
                    var cat = s_configurableCategories[i + c];
                    bool isBlocked = s.BlockedCategories.Contains(cat);
                    bool nextBlocked = EditorGUILayout.ToggleLeft(Localization.CategoryLabel(lang, cat), isBlocked, GUILayout.MinWidth(100));
                    if (nextBlocked != isBlocked)
                    {
                        var nextSet = new HashSet<ObjectCategory>(s.BlockedCategories);
                        if (nextBlocked) nextSet.Add(cat);
                        else nextSet.Remove(cat);
                        coordinator.EnqueueUpdateSettings(s.WithNextRevision(blockedCategories: nextSet));
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);
            DrawDiagnosticsSection(snapshot, state, lang);
        }

        private void DrawDiagnosticsSection(UiSnapshot snapshot, ManagerState state, string lang)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Localization.Get(lang, "DiagnosticsHeader"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Get(lang, "CopyDiagnostics"), EditorStyles.miniButton, GUILayout.Width(120)))
            {
                EditorGUIUtility.systemCopyBuffer = BuildDiagnosticsReport(snapshot);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var caps = state.Capabilities;
            var m = snapshot.Metrics;
            EditorGUILayout.LabelField("State: " + state.LifecycleState + " | Gen: " + state.Generation + " | SettingsRev: " + state.Settings.SettingsRevision);
            EditorGUILayout.LabelField("Capabilities: " + caps.DiagnosticSummary, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                string.Format("SupportsRotation={0}, CombinedAPI={1}, ActiveTarget={2}, EmptyComp={3}",
                    caps.SupportsRotation,
                    caps.UsesWindowCombinedTargetLockApi,
                    caps.SupportsActiveTargetSelection,
                    caps.SupportsEmptyTargetCompensation),
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                string.Format("Metrics: Queue={0} (Peak={1}), Plans={2}, Skips={3}, LastExec={4:0.#}ms, LastLatency={5:0.#}ms",
                    m.CurrentQueueDepth, m.PeakQueueDepth, m.TotalCommittedPlans, m.TotalWriteSkips, m.LastPlanExecutionMs, m.LastSelectionToCommitMs),
                EditorStyles.miniLabel);

            if (state.Leases.Count > 0)
            {
                EditorGUILayout.LabelField("Active/Pending Leases (" + state.Leases.Count + "):", EditorStyles.miniBoldLabel);
                foreach (var kvp in state.Leases)
                {
                    var l = kvp.Value;
                    EditorGUILayout.LabelField(
                        "  " + l.WindowKey + " | orig=" + l.OriginalLocked + " | status=" + l.RestorationStatus + (string.IsNullOrEmpty(l.LastError) ? "" : " | err=" + l.LastError),
                        EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(4);
            _diagScrollPos = EditorGUILayout.BeginScrollView(_diagScrollPos, GUILayout.Height(110));
            for (int i = snapshot.RecentLogs.Count - 1; i >= 0; i--)
            {
                var log = snapshot.RecentLogs[i];
                string repeat = log.RepeatCount > 1 ? " (x" + log.RepeatCount + ")" : string.Empty;
                string line = string.Format("[{0}] g{1} {2} {3}: {4}{5}",
                    log.IsError ? "ERR" : "INF",
                    log.Generation,
                    log.Stage,
                    log.WindowKey.HasValue ? log.WindowKey.Value.Value : "-",
                    log.Message,
                    repeat);
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private static string BuildDiagnosticsReport(UiSnapshot snapshot)
        {
            var state = snapshot.State;
            var sb = new StringBuilder();
            sb.AppendLine("=== Inspector Manager V2 Diagnostics ===");
            sb.AppendLine("Unity: " + UnityEngine.Application.unityVersion);
            sb.AppendLine("Lifecycle: " + state.LifecycleState + " (Gen=" + state.Generation + ", SettingsRev=" + state.Settings.SettingsRevision + ")");
            sb.AppendLine("Capabilities: " + state.Capabilities.DiagnosticSummary);
            sb.AppendLine("SupportsRotation: " + state.Capabilities.SupportsRotation);
            sb.AppendLine("Windows: " + state.Windows.Count + ", Leases: " + state.Leases.Count);
            sb.AppendLine("Metrics: Queue=" + snapshot.Metrics.CurrentQueueDepth + ", Peak=" + snapshot.Metrics.PeakQueueDepth + ", Plans=" + snapshot.Metrics.TotalCommittedPlans + ", Skips=" + snapshot.Metrics.TotalWriteSkips);
            sb.AppendLine("--- Recent Logs ---");
            foreach (var log in snapshot.RecentLogs)
            {
                sb.AppendLine("[" + (log.IsError ? "ERR" : "INF") + "] op=" + log.OperationId + " g=" + log.Generation + " stage=" + log.Stage + " msg=" + log.Message + (log.RepeatCount > 1 ? " x" + log.RepeatCount : ""));
            }
            return sb.ToString();
        }

        private static string GetWindowStatusText(WindowRecord w, string lang)
        {
            if (w.IsFaultIsolated) return Localization.Get(lang, "StateFaultIsolated");
            if (w.IsExplicitlyExcluded) return Localization.Get(lang, "StateExcluded");
            if (w.IsManuallyUnlocked) return Localization.Get(lang, "StateManuallyUnlocked");
            return Localization.Get(lang, "StateParticipating");
        }

        private static string GetFavoriteStatusLabel(FavoriteResolutionState state, string lang)
        {
            switch (state)
            {
                case FavoriteResolutionState.Resolved: return Localization.Get(lang, "FavResolved");
                case FavoriteResolutionState.TemporaryUnsaved: return Localization.Get(lang, "FavTemporary");
                case FavoriteResolutionState.SceneUnloaded: return Localization.Get(lang, "FavSceneUnloaded");
                case FavoriteResolutionState.DeletedOrMissing: return Localization.Get(lang, "FavMissing");
                default: return Localization.Get(lang, "FavFailed");
            }
        }

        private static Color GetLifecycleColor(RuntimeLifecycleState state)
        {
            switch (state)
            {
                case RuntimeLifecycleState.Running: return new Color(0.55f, 0.95f, 0.65f);
                case RuntimeLifecycleState.Paused: return new Color(0.95f, 0.85f, 0.50f);
                case RuntimeLifecycleState.Faulted: return new Color(1.0f, 0.50f, 0.50f);
                case RuntimeLifecycleState.Suspended: return new Color(0.70f, 0.78f, 1.0f);
                default: return Color.white;
            }
        }
    }
}
