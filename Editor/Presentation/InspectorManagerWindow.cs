using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.InspectorManagerV2.Presentation
{
    public interface IManagerWindowHost
    {
        InspectorManagerCoordinator Coordinator { get; }
        void RegisterWindow(EditorWindow window);
        void UnregisterWindowOnDestroy(EditorWindow window);
    }

    /// <summary>
    /// Compact UI Toolkit (UXML/USS) EditorWindow implementing the dennokoworks floating design system.
    /// Displays single-line high-density managed Inspector rows; Detailed Settings live in the header's ⋮ menu.
    /// </summary>
    public sealed class InspectorManagerWindow : EditorWindow
    {
        private const string THEME_USS_GUID  = "a1d8c4f0e72b4a918c3e5f6d10b2a401";
        private const string BLOCKS_USS_GUID = "a1d8c4f0e72b4a918c3e5f6d10b2a402";
        private const string WINDOW_USS_GUID = "a1d8c4f0e72b4a918c3e5f6d10b2a403";
        private const string UXML_GUID       = "a1d8c4f0e72b4a918c3e5f6d10b2a404";

        private const string LockOnIconNames  = "IN LockButton on|d_InspectorLock|InspectorLock";
        private const string LockOffIconNames = "IN LockButton|d_Unlocked|Unlocked";

        private const string ToolName = "Inspector Manager V2";

        public static Func<IManagerWindowHost> HostProvider;

        private static readonly int[] s_historyCapacityPresets = { 10, 20, 30, 50, 100, 200 };

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
        private bool _subscribed;
        private bool _uiBuilt;
        private bool _suppressControlCallbacks;

        private DennokoAppBar _appBar;
        private DennokoStatus _status;

        private VisualElement _faultBanner;
        private Label _faultBannerText;
        private Button _retryRestoreButton;
        private HelpBox _warningHelpBox;
        private VisualElement _runningBanner;
        private Label _runningBannerState;
        private Label _runningBannerDetail;

        private Label _managedCardTitle;
        private Button _refreshButton;
        private Button _resetLayoutButton;
        private VisualElement _managedList;
        private VisualElement _managedEmpty;
        private Label _managedEmptyMessage;
        private Button _managedEmptyAdd;

        private VisualElement _externalCard;
        private Label _externalCardTitle;
        private VisualElement _externalList;

        private Label _footerInfo;
        private Button _addInspectorButton;
        private Button _primaryToggleButton;

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

            RefreshView();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(ToolName);
            minSize = new Vector2(260f, 200f);
            EnsureHostAttached();
        }

        private void OnDisable()
        {
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

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();

            root.AddToClassList("dennoko-root");
            root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            root.style.flexGrow = 1;

            DennokoUIFont.Apply(root);

            AddStyleSheet(root, THEME_USS_GUID);
            AddStyleSheet(root, BLOCKS_USS_GUID);
            AddStyleSheet(root, WINDOW_USS_GUID);

            var uxml = LoadAssetByGuid<VisualTreeAsset>(UXML_GUID);
            if (uxml == null)
            {
                root.Add(new Label("UXML Asset が見つかりません。GUID を確認してください: " + UXML_GUID));
                return;
            }

            uxml.CloneTree(root);
            BindElements(root);
            _uiBuilt = true;

            EnsureHostAttached();
            RefreshView();
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

        private void OnRuntimeStateChanged()
        {
            if (_uiBuilt)
            {
                RefreshView();
            }
        }

        private void BindElements(VisualElement root)
        {
            _status = new DennokoStatus(root.Q<Label>("status-label"), readyText: "停止中 (Off)");

            _appBar = new DennokoAppBar(root, ToolName)
            {
                ResetAll = () => _host?.Coordinator.EnqueueResetSettings(),
                AddToolMenuItems = PopulateHeaderSettingsMenu
            };
            _appBar.LanguageChanged += english =>
            {
                if (_suppressControlCallbacks || _host == null) return;
                var cur = _host.Coordinator.CurrentState.Settings;
                string nextLang = english ? "en" : "ja";
                if (!string.Equals(cur.Language, nextLang, StringComparison.OrdinalIgnoreCase))
                {
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(language: nextLang));
                }
            };

            _faultBanner = root.Q<VisualElement>("fault-banner");
            _faultBannerText = root.Q<Label>("fault-banner-text");
            _retryRestoreButton = root.Q<Button>("retry-restore-button");
            if (_retryRestoreButton != null)
            {
                _retryRestoreButton.clicked += () => _host?.Coordinator.EnqueueRetryRestore();
            }

            _warningHelpBox = root.Q<HelpBox>("warning-helpbox");

            _runningBanner = root.Q<VisualElement>("running-banner");
            _runningBannerState = root.Q<Label>("running-banner-state");
            _runningBannerDetail = root.Q<Label>("running-banner-detail");

            _managedCardTitle = root.Q<Label>("managed-card-title");
            _refreshButton = root.Q<Button>("refresh-button");
            _resetLayoutButton = root.Q<Button>("reset-layout-button");
            DennokoIcons.Apply(_refreshButton, DennokoIcons.Refresh, "↻", "Inspector 一覧を再検出");
            DennokoIcons.Apply(_resetLayoutButton, "d_RotateTool|RotateTool", "↺", "配置・除外をリセット");
            if (_refreshButton != null) _refreshButton.clicked += () => _host?.Coordinator.EnqueueSynchronizeWindows();
            if (_resetLayoutButton != null) _resetLayoutButton.clicked += () => _host?.Coordinator.EnqueueResetLayout();

            _managedList = root.Q<VisualElement>("managed-list");
            _managedEmpty = root.Q<VisualElement>("managed-empty");
            _managedEmptyMessage = root.Q<Label>("managed-empty-message");
            _managedEmptyAdd = root.Q<Button>("managed-empty-add");
            if (_managedEmptyAdd != null) _managedEmptyAdd.clicked += () => _host?.Coordinator.EnqueueAddInspector();

            _externalCard = root.Q<VisualElement>("external-card");
            _externalCardTitle = root.Q<Label>("external-card-title");
            _externalList = root.Q<VisualElement>("external-list");

            _footerInfo = root.Q<Label>("footer-info");
            _addInspectorButton = root.Q<Button>("add-inspector-button");
            _primaryToggleButton = root.Q<Button>("primary-toggle-button");

            if (_addInspectorButton != null)
            {
                _addInspectorButton.clicked += () => _host?.Coordinator.EnqueueAddInspector();
            }

            if (_primaryToggleButton != null)
            {
                _primaryToggleButton.clicked += () =>
                {
                    if (_host == null) return;
                    var lifecycle = _host.Coordinator.CurrentState.LifecycleState;
                    if (lifecycle == RuntimeLifecycleState.Running || lifecycle == RuntimeLifecycleState.Starting)
                    {
                        _host.Coordinator.EnqueueStopRotation(StopReason.UserRequested);
                    }
                    else
                    {
                        _host.Coordinator.EnqueueStartRotation();
                    }
                };
            }
        }

        private void PopulateHeaderSettingsMenu(GenericMenu menu)
        {
            if (_host == null) return;
            var settings = _host.Coordinator.CurrentState.Settings;
            string lang = settings.Language;

            // 操作項目
            menu.AddItem(new GUIContent(Localization.Get(lang, "MenuRefresh")), false, () => _host?.Coordinator.EnqueueSynchronizeWindows());
            menu.AddItem(new GUIContent(Localization.Get(lang, "MenuResetLayout")), false, () => _host?.Coordinator.EnqueueResetLayout());
            menu.AddSeparator(string.Empty);

            // 詳細設定トグル群
            menu.AddItem(
                new GUIContent(Localization.Get(lang, "RecordSceneObjects")),
                settings.RecordSceneObjects,
                () =>
                {
                    var cur = _host.Coordinator.CurrentState.Settings;
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(recordSceneObjects: !cur.RecordSceneObjects));
                });

            menu.AddItem(
                new GUIContent(Localization.Get(lang, "RecordAssets")),
                settings.RecordAssets,
                () =>
                {
                    var cur = _host.Coordinator.CurrentState.Settings;
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(recordAssets: !cur.RecordAssets));
                });

            menu.AddItem(
                new GUIContent(Localization.Get(lang, "ShowOverlays")),
                settings.ShowInspectorOverlays,
                () =>
                {
                    var cur = _host.Coordinator.CurrentState.Settings;
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(showInspectorOverlays: !cur.ShowInspectorOverlays));
                });

            // 履歴保持数サブメニュー
            string capMenuRoot = Localization.Get(lang, "MaxRotHistory") + " (" + settings.MaxRotationHistory + ")/";
            for (int i = 0; i < s_historyCapacityPresets.Length; i++)
            {
                int preset = s_historyCapacityPresets[i];
                menu.AddItem(
                    new GUIContent(capMenuRoot + preset),
                    settings.MaxRotationHistory == preset,
                    () =>
                    {
                        var cur = _host.Coordinator.CurrentState.Settings;
                        _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(maxRotationHistory: preset));
                    });
            }

            // カテゴリ別更新ブロックサブメニュー
            int blockedCount = settings.BlockedCategories.Count;
            string blockMenuRoot = (blockedCount > 0
                ? Localization.Format(lang, "MenuBlockedCategoriesCount", blockedCount)
                : Localization.Get(lang, "BlockedCategoriesHeader")) + "/";

            for (int i = 0; i < s_configurableCategories.Length; i++)
            {
                var cat = s_configurableCategories[i];
                bool isBlocked = settings.BlockedCategories.Contains(cat);
                menu.AddItem(
                    new GUIContent(blockMenuRoot + Localization.CategoryLabel(lang, cat)),
                    isBlocked,
                    () =>
                    {
                        var cur = _host.Coordinator.CurrentState.Settings;
                        var nextSet = new HashSet<ObjectCategory>(cur.BlockedCategories);
                        if (nextSet.Contains(cat)) nextSet.Remove(cat);
                        else nextSet.Add(cat);
                        _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(blockedCategories: nextSet));
                    });
            }

            if (blockedCount > 0)
            {
                menu.AddSeparator(blockMenuRoot);
                menu.AddItem(
                    new GUIContent(blockMenuRoot + Localization.Get(lang, "MenuClearBlockedCategories")),
                    false,
                    () =>
                    {
                        var cur = _host.Coordinator.CurrentState.Settings;
                        _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(blockedCategories: Array.Empty<ObjectCategory>()));
                    });
            }
        }

        private void RefreshView()
        {
            if (!_uiBuilt || _host == null) return;

            var snapshot = _host.Coordinator.GetUiSnapshot();
            var state = snapshot.State;
            var settings = state.Settings;
            string lang = settings.Language;
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

            _suppressControlCallbacks = true;
            try
            {
                _appBar?.SetEnglish(isEn);

                bool showFault = state.LifecycleState == RuntimeLifecycleState.Faulted || state.HasIncompleteRestorations;
                DennokoBlockUI.Show(_faultBanner, showFault);
                if (showFault && _faultBannerText != null)
                {
                    _faultBannerText.text = string.IsNullOrEmpty(state.LastError)
                        ? Localization.Get(lang, "StatusFaulted")
                        : state.LastError;
                }
                if (_retryRestoreButton != null) _retryRestoreButton.text = Localization.Get(lang, "RetryRestore");

                string warningText = null;
                if (!state.Capabilities.SupportsRotation) warningText = Localization.Get(lang, "RotationUnavailable");
                else if (state.PersistenceWriteBlocked) warningText = Localization.Get(lang, "PersistenceBlocked");
                DennokoBlockUI.Show(_warningHelpBox, !string.IsNullOrEmpty(warningText));
                if (_warningHelpBox != null && !string.IsNullOrEmpty(warningText)) _warningHelpBox.text = warningText;

                bool isRunning = state.LifecycleState == RuntimeLifecycleState.Running;
                DennokoBlockUI.Show(_runningBanner, isRunning);
                if (_runningBannerState != null) _runningBannerState.text = Localization.Get(lang, "StatusRunning");
                if (_runningBannerDetail != null) _runningBannerDetail.text = Localization.Get(lang, "RunningBannerDetail");

                var managed = state.GetManagedAliveWindowsOrdered();
                var participating = state.GetParticipatingWindowsOrdered();
                var external = state.Windows.Where(w => !w.IsManaged && w.IsAlive).OrderBy(w => w.FixedDisplayNumber).ToArray();

                if (_managedCardTitle != null)
                {
                    _managedCardTitle.text = Localization.Format(lang, "ManagedInspectors", managed.Count);
                    _managedCardTitle.tooltip = Localization.Get(lang, "HistoryMruHint");
                }
                if (_refreshButton != null) _refreshButton.tooltip = Localization.Get(lang, "RefreshTooltip");
                if (_resetLayoutButton != null) _resetLayoutButton.tooltip = Localization.Get(lang, "ResetLayoutTooltip");

                RebuildManagedList(state, managed, participating, lang);

                DennokoBlockUI.Show(_externalCard, external.Length > 0);
                if (_externalCardTitle != null)
                {
                    _externalCardTitle.text = Localization.Format(lang, "ExternalInspectors", external.Length);
                    _externalCardTitle.tooltip = Localization.Get(lang, "ExternalHint");
                }
                RebuildExternalList(external, lang);

                if (_footerInfo != null)
                {
                    _footerInfo.text = Localization.Format(lang, "FooterSummary", participating.Count, managed.Count);
                }

                if (_addInspectorButton != null)
                {
                    _addInspectorButton.text = Localization.Get(lang, "AddInspector");
                    _addInspectorButton.tooltip = Localization.Get(lang, "AddInspectorTooltip");
                    _addInspectorButton.SetEnabled(state.Capabilities.CanCreateWindow);
                }

                if (_primaryToggleButton != null)
                {
                    bool active = state.LifecycleState == RuntimeLifecycleState.Running || state.LifecycleState == RuntimeLifecycleState.Starting;
                    _primaryToggleButton.text = Localization.Get(lang, active ? "Stop" : "Start");
                    bool canClick = active || ((state.LifecycleState == RuntimeLifecycleState.Off || state.LifecycleState == RuntimeLifecycleState.Suspended)
                        && state.Capabilities.SupportsRotation);
                    _primaryToggleButton.SetEnabled(canClick);
                }

                UpdateStatusBar(snapshot, state, lang);
            }
            finally
            {
                _suppressControlCallbacks = false;
            }
        }

        private void RebuildManagedList(
            ManagerState state,
            IReadOnlyList<WindowRecord> managed,
            IReadOnlyList<WindowRecord> participating,
            string lang)
        {
            if (_managedList == null) return;
            _managedList.Clear();

            bool isEmpty = managed.Count == 0;
            DennokoBlockUI.Show(_managedEmpty, isEmpty);
            if (_managedEmptyMessage != null) _managedEmptyMessage.text = Localization.Get(lang, "NoManagedInspectors");
            if (_managedEmptyAdd != null)
            {
                _managedEmptyAdd.text = Localization.Get(lang, "AddInspector");
                _managedEmptyAdd.SetEnabled(state.Capabilities.CanCreateWindow);
            }

            if (isEmpty) return;

            var roleSlotMap = new Dictionary<WindowKey, int>();
            for (int i = 0; i < participating.Count; i++)
            {
                roleSlotMap[participating[i].Key] = i;
            }

            for (int i = 0; i < managed.Count; i++)
            {
                var w = managed[i];
                int rowIndex = i;
                var capturedKey = w.Key;

                var row = new VisualElement();
                row.AddToClassList("imv2-row");

                if (w.IsFaultIsolated) row.AddToClassList("imv2-row--fault");
                else if (w.IsExplicitlyExcluded) row.AddToClassList("imv2-row--excluded");
                else if (w.IsManuallyUnlocked) row.AddToClassList("imv2-row--unlocked");
                else if (w.CanParticipate && state.LifecycleState == RuntimeLifecycleState.Running) row.AddToClassList("imv2-row--participating");

                var numLabel = new Label("#" + w.FixedDisplayNumber);
                numLabel.AddToClassList("imv2-row__num");
                row.Add(numLabel);

                var badge = new Label();
                badge.AddToClassList("imv2-row__badge");
                if (w.IsFaultIsolated)
                {
                    badge.text = Localization.Get(lang, "BadgeFault");
                    badge.AddToClassList("imv2-row__badge--fault");
                }
                else if (w.IsExplicitlyExcluded)
                {
                    badge.text = Localization.Get(lang, "BadgeExcluded");
                    badge.AddToClassList("imv2-row__badge--excluded");
                }
                else if (w.IsManuallyUnlocked)
                {
                    badge.text = Localization.Get(lang, "BadgeUnlocked");
                    badge.AddToClassList("imv2-row__badge--unlocked");
                }
                else if (w.CanParticipate && roleSlotMap.TryGetValue(w.Key, out int slot))
                {
                    bool isLatest = slot == 0;
                    badge.text = isLatest ? Localization.Get(lang, "RoleLatest") : ("-" + slot);
                    if (isLatest) badge.AddToClassList("imv2-row__badge--latest");
                }
                else
                {
                    badge.text = "-";
                }
                row.Add(badge);

                bool hasAssigned = w.AssignedSelection != null && !w.AssignedSelection.IsEmpty;
                string targetName = hasAssigned
                    ? w.AssignedSelection.BuildDisplaySummary()
                    : (string.IsNullOrEmpty(w.ObservedRepresentativeName) ? Localization.Get(lang, "EmptyTarget") : w.ObservedRepresentativeName);

                var targetLabel = new Label(targetName);
                targetLabel.AddToClassList("imv2-row__target");
                if (!hasAssigned && string.IsNullOrEmpty(w.ObservedRepresentativeName))
                {
                    targetLabel.AddToClassList("imv2-row__target--dim");
                }
                targetLabel.tooltip = BuildRowTooltip(w, targetName, lang);
                targetLabel.RegisterCallback<ClickEvent>(_ => _host?.Coordinator.EnqueueFocusInspector(capturedKey));
                row.Add(targetLabel);

                var actions = new VisualElement();
                actions.AddToClassList("imv2-row__actions");

                var upBtn = new Button(() => _host?.Coordinator.EnqueueReorderWindow(capturedKey, rowIndex - 1))
                {
                    text = "▲",
                    tooltip = Localization.Get(lang, "BtnUpTooltip")
                };
                upBtn.AddToClassList("imv2-row-micro-btn");
                upBtn.SetEnabled(rowIndex > 0);
                actions.Add(upBtn);

                var downBtn = new Button(() => _host?.Coordinator.EnqueueReorderWindow(capturedKey, rowIndex + 1))
                {
                    text = "▼",
                    tooltip = Localization.Get(lang, "BtnDownTooltip")
                };
                downBtn.AddToClassList("imv2-row-micro-btn");
                downBtn.SetEnabled(rowIndex < managed.Count - 1);
                actions.Add(downBtn);

                var lockBtn = new Button(() => _host?.Coordinator.EnqueueToggleLock(capturedKey));
                lockBtn.AddToClassList("dennoko-icon-button");
                lockBtn.AddToClassList("imv2-row-lock-btn");
                if (w.ObservedLocked) lockBtn.AddToClassList("imv2-row-lock-btn--on");
                DennokoIcons.Apply(
                    lockBtn,
                    w.ObservedLocked ? LockOnIconNames : LockOffIconNames,
                    w.ObservedLocked ? "鍵" : "開",
                    Localization.Get(lang, w.ObservedLocked ? "LockOnTooltip" : "LockOffTooltip"));
                lockBtn.SetEnabled(state.Capabilities.CanWriteLock);
                actions.Add(lockBtn);

                bool isExcluded = w.IsExplicitlyExcluded;
                bool canClose = state.Capabilities.CanCloseWindow;
                var menuBtn = new Button();
                menuBtn.AddToClassList("dennoko-icon-button");
                menuBtn.AddToClassList("imv2-row-micro-btn");
                DennokoIcons.Apply(menuBtn, DennokoIcons.Menu, "⋮", Localization.Get(lang, "RowMenuTooltip"));
                menuBtn.clicked += () => ShowRowMenu(menuBtn, capturedKey, isExcluded, canClose, lang);
                actions.Add(menuBtn);

                row.Add(actions);
                _managedList.Add(row);
            }
        }

        private void ShowRowMenu(VisualElement anchor, WindowKey key, bool isExcluded, bool canClose, string lang)
        {
            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent(Localization.Get(lang, isExcluded ? "RowMenuInclude" : "RowMenuExclude")),
                false,
                () => _host?.Coordinator.EnqueueSetExcluded(key, !isExcluded));
            menu.AddItem(
                new GUIContent(Localization.Get(lang, "RowMenuFocus")),
                false,
                () => _host?.Coordinator.EnqueueFocusInspector(key));

            if (canClose)
            {
                menu.AddSeparator(string.Empty);
                menu.AddItem(
                    new GUIContent(Localization.Get(lang, "RowMenuClose")),
                    false,
                    () => _host?.Coordinator.EnqueueCloseInspector(key));
            }

            menu.DropDown(anchor.worldBound);
        }

        private void RebuildExternalList(WindowRecord[] external, string lang)
        {
            if (_externalList == null) return;
            _externalList.Clear();

            for (int i = 0; i < external.Length; i++)
            {
                var ext = external[i];
                var capturedKey = ext.Key;

                var row = new VisualElement();
                row.AddToClassList("imv2-row");

                var badge = new Label("EXT");
                badge.AddToClassList("imv2-row__badge");
                row.Add(badge);

                string repName = string.IsNullOrEmpty(ext.ObservedRepresentativeName) ? "(Inspector)" : ext.ObservedRepresentativeName;
                var nameLabel = new Label(repName);
                nameLabel.AddToClassList("imv2-row__target");
                nameLabel.tooltip = repName + " — " + Localization.Get(lang, "RowMenuFocus");
                nameLabel.RegisterCallback<ClickEvent>(_ => _host?.Coordinator.EnqueueFocusInspector(capturedKey));
                row.Add(nameLabel);

                var adoptBtn = new Button(() => _host?.Coordinator.EnqueueAdoptInspector(capturedKey))
                {
                    text = Localization.Get(lang, "Adopt")
                };
                adoptBtn.AddToClassList("imv2-row-action-btn");
                row.Add(adoptBtn);

                _externalList.Add(row);
            }
        }

        private void UpdateStatusBar(UiSnapshot snapshot, ManagerState state, string lang)
        {
            if (_status == null) return;

            if (snapshot.HasPendingVerification)
            {
                _status.Set(Localization.Get(lang, "PendingVerifyBanner"), DennokoStatusType.Busy);
                return;
            }

            if (state.LifecycleState == RuntimeLifecycleState.Faulted)
            {
                _status.Set(Localization.Get(lang, "StatusFaulted"), DennokoStatusType.Error);
                return;
            }

            if (!string.IsNullOrEmpty(state.StatusNotice)
                && state.StatusNotice != "Running"
                && state.StatusNotice != "Off")
            {
                var statusType = !string.IsNullOrEmpty(state.LastError)
                    ? DennokoStatusType.Warning
                    : DennokoStatusType.Info;
                _status.Set(state.StatusNotice, statusType);
                return;
            }

            _status.Set(Localization.LifecycleLabel(lang, state.LifecycleState), DennokoStatusType.Info);
        }

        private static string BuildRowTooltip(WindowRecord w, string targetName, string lang)
        {
            string status;
            if (w.IsFaultIsolated) status = Localization.Get(lang, "StateFaultIsolated");
            else if (w.IsExplicitlyExcluded) status = Localization.Get(lang, "StateExcluded");
            else if (w.IsManuallyUnlocked) status = Localization.Get(lang, "StateManuallyUnlocked");
            else status = Localization.Get(lang, "StateParticipating");

            string extra = string.Empty;
            if (w.AssignmentLabel == AssignmentLabel.InitialDisplay)
                extra = " · " + Localization.Get(lang, "LabelInitialDisplay");
            else if (w.AssignmentLabel == AssignmentLabel.HistoryUnassigned)
                extra = " · " + Localization.Get(lang, "LabelHistoryUnassigned");

            if (w.IsFaultIsolated && !string.IsNullOrEmpty(w.FaultReason))
                extra += "\n" + w.FaultReason;

            return "#" + w.FixedDisplayNumber + " " + targetName + "\n" + status + extra;
        }

        private static void AddStyleSheet(VisualElement root, string guid)
        {
            var uss = LoadAssetByGuid<StyleSheet>(guid);
            if (uss != null) root.styleSheets.Add(uss);
            else Debug.LogWarning("[InspectorManagerWindow] USS が見つかりません。GUID を確認してください: " + guid);
        }

        private static T LoadAssetByGuid<T>(string guid) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }
    }
}
