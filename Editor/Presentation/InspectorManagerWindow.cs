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
    /// UI Toolkit (UXML/USS) EditorWindow implementing the dennokoworks floating design system.
    /// Displays History-mode managed Inspector order/state management and collapsible Advanced Settings.
    /// Never mutates Unity Inspectors or saves files synchronously from UI callbacks; only enqueues commands.
    /// </summary>
    public sealed class InspectorManagerWindow : EditorWindow
    {
        private const string THEME_USS_GUID  = "a1d8c4f0e72b4a918c3e5f6d10b2a401";
        private const string BLOCKS_USS_GUID = "a1d8c4f0e72b4a918c3e5f6d10b2a402";
        private const string WINDOW_USS_GUID = "a1d8c4f0e72b4a918c3e5f6d10b2a403";
        private const string UXML_GUID       = "a1d8c4f0e72b4a918c3e5f6d10b2a404";

        private const string ToolName = "Inspector Manager V2";

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
        private bool _subscribed;
        private bool _uiBuilt;
        private bool _suppressControlCallbacks;

        // Shared components
        private DennokoAppBar _appBar;
        private DennokoStatus _status;

        // Bound UXML elements
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
        private Label _historyMruHint;

        private VisualElement _externalCard;
        private Label _externalCardTitle;
        private VisualElement _externalList;
        private Label _externalHint;

        private VisualElement _advancedDetails;
        private Label _advancedDetailsTitle;
        private VisualElement _advancedDetailsChips;
        private SliderInt _maxHistorySlider;
        private Toggle _recordSceneToggle;
        private Toggle _recordAssetsToggle;
        private Toggle _showOverlaysToggle;
        private Label _blockedCategoriesTitle;
        private Label _blockedCategoriesHint;
        private VisualElement _blockedCategoriesGrid;
        private readonly Dictionary<ObjectCategory, Toggle> _categoryToggles = new Dictionary<ObjectCategory, Toggle>();

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
            minSize = new Vector2(360f, 360f);
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

            // テーマ非依存ルート設定 (dennokoworks-design 絶対規則)
            root.AddToClassList("dennoko-root");
            root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            root.style.flexGrow = 1;

            // 標準フォント適用 (メイリオ SDF + 自己修復)
            DennokoUIFont.Apply(root);

            // USS ロード順: Theme -> Blocks -> Window
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
                AddToolMenuItems = menu =>
                {
                    string lang = _host != null ? _host.Coordinator.CurrentState.Settings.Language : "ja";
                    menu.AddItem(new GUIContent(Localization.Get(lang, "MenuRefresh")), false, () => _host?.Coordinator.EnqueueSynchronizeWindows());
                    menu.AddItem(new GUIContent(Localization.Get(lang, "MenuResetLayout")), false, () => _host?.Coordinator.EnqueueResetLayout());
                }
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
            _historyMruHint = root.Q<Label>("history-mru-hint");

            _externalCard = root.Q<VisualElement>("external-card");
            _externalCardTitle = root.Q<Label>("external-card-title");
            _externalList = root.Q<VisualElement>("external-list");
            _externalHint = root.Q<Label>("external-hint");

            // 詳細設定 (.dennoko-details: 既定折りたたみ＋要約チップ)
            _advancedDetails = root.Q<VisualElement>("advanced-details");
            _advancedDetailsTitle = root.Q<Label>("advanced-details-title");
            _advancedDetailsChips = root.Q<VisualElement>("advanced-details-chips");
            DennokoBlockUI.BindDetails(_advancedDetails, open: false);

            _maxHistorySlider = root.Q<SliderInt>("max-history-slider");
            _recordSceneToggle = root.Q<Toggle>("record-scene-toggle");
            _recordAssetsToggle = root.Q<Toggle>("record-assets-toggle");
            _showOverlaysToggle = root.Q<Toggle>("show-overlays-toggle");
            _blockedCategoriesTitle = root.Q<Label>("blocked-categories-title");
            _blockedCategoriesHint = root.Q<Label>("blocked-categories-hint");
            _blockedCategoriesGrid = root.Q<VisualElement>("blocked-categories-grid");

            _maxHistorySlider?.RegisterValueChangedCallback(evt =>
            {
                if (_suppressControlCallbacks || _host == null) return;
                var cur = _host.Coordinator.CurrentState.Settings;
                if (evt.newValue != cur.MaxRotationHistory)
                {
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(maxRotationHistory: evt.newValue));
                }
            });

            _recordSceneToggle?.RegisterValueChangedCallback(evt =>
            {
                if (_suppressControlCallbacks || _host == null) return;
                var cur = _host.Coordinator.CurrentState.Settings;
                if (evt.newValue != cur.RecordSceneObjects)
                {
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(recordSceneObjects: evt.newValue));
                }
            });

            _recordAssetsToggle?.RegisterValueChangedCallback(evt =>
            {
                if (_suppressControlCallbacks || _host == null) return;
                var cur = _host.Coordinator.CurrentState.Settings;
                if (evt.newValue != cur.RecordAssets)
                {
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(recordAssets: evt.newValue));
                }
            });

            _showOverlaysToggle?.RegisterValueChangedCallback(evt =>
            {
                if (_suppressControlCallbacks || _host == null) return;
                var cur = _host.Coordinator.CurrentState.Settings;
                if (evt.newValue != cur.ShowInspectorOverlays)
                {
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(showInspectorOverlays: evt.newValue));
                }
            });

            BuildCategoryToggles();

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

        private void BuildCategoryToggles()
        {
            if (_blockedCategoriesGrid == null) return;
            _blockedCategoriesGrid.Clear();
            _categoryToggles.Clear();

            for (int i = 0; i < s_configurableCategories.Length; i++)
            {
                var cat = s_configurableCategories[i];
                var toggle = new Toggle(Localization.CategoryLabel("ja", cat));
                toggle.AddToClassList("imv2-category-toggle");
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressControlCallbacks || _host == null) return;
                    var cur = _host.Coordinator.CurrentState.Settings;
                    var nextSet = new HashSet<ObjectCategory>(cur.BlockedCategories);
                    if (evt.newValue) nextSet.Add(cat);
                    else nextSet.Remove(cat);
                    _host.Coordinator.EnqueueUpdateSettings(cur.WithNextRevision(blockedCategories: nextSet));
                });
                _categoryToggles[cat] = toggle;
                _blockedCategoriesGrid.Add(toggle);
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

                // バナー類
                bool showFault = state.LifecycleState == RuntimeLifecycleState.Faulted || state.HasIncompleteRestorations;
                DennokoBlockUI.Show(_faultBanner, showFault);
                if (showFault && _faultBannerText != null)
                {
                    _faultBannerText.text = string.IsNullOrEmpty(state.LastError)
                        ? Localization.Get(lang, "StatusFaulted")
                        : state.LastError;
                }
                if (_retryRestoreButton != null)
                {
                    _retryRestoreButton.text = Localization.Get(lang, "RetryRestore");
                }

                string warningText = null;
                if (!state.Capabilities.SupportsRotation)
                {
                    warningText = Localization.Get(lang, "RotationUnavailable");
                }
                else if (state.PersistenceWriteBlocked)
                {
                    warningText = Localization.Get(lang, "PersistenceBlocked");
                }
                DennokoBlockUI.Show(_warningHelpBox, !string.IsNullOrEmpty(warningText));
                if (_warningHelpBox != null && !string.IsNullOrEmpty(warningText))
                {
                    _warningHelpBox.text = warningText;
                }

                bool isRunning = state.LifecycleState == RuntimeLifecycleState.Running;
                DennokoBlockUI.Show(_runningBanner, isRunning);
                if (_runningBannerState != null) _runningBannerState.text = Localization.Get(lang, "StatusRunning");
                if (_runningBannerDetail != null) _runningBannerDetail.text = Localization.Get(lang, "RunningBannerDetail");

                // 管理対象 / 外部 Inspector 一覧
                var managed = state.GetManagedAliveWindowsOrdered();
                var participating = state.GetParticipatingWindowsOrdered();
                var external = state.Windows.Where(w => !w.IsManaged && w.IsAlive).OrderBy(w => w.FixedDisplayNumber).ToArray();

                if (_managedCardTitle != null)
                {
                    _managedCardTitle.text = Localization.Format(lang, "ManagedInspectors", managed.Count);
                }
                if (_refreshButton != null) _refreshButton.tooltip = Localization.Get(lang, "RefreshTooltip");
                if (_resetLayoutButton != null) _resetLayoutButton.tooltip = Localization.Get(lang, "ResetLayoutTooltip");
                if (_historyMruHint != null) _historyMruHint.text = Localization.Get(lang, "HistoryMruHint");

                RebuildManagedList(state, managed, participating, lang);

                DennokoBlockUI.Show(_externalCard, external.Length > 0);
                if (_externalCardTitle != null)
                {
                    _externalCardTitle.text = Localization.Format(lang, "ExternalInspectors", external.Length);
                }
                if (_externalHint != null) _externalHint.text = Localization.Get(lang, "ExternalHint");
                RebuildExternalList(external, lang);

                // 詳細設定コントロール同期 & 要約チップ更新
                SyncAdvancedSettings(settings, lang);

                // フッター & ステータスバー
                if (_footerInfo != null)
                {
                    _footerInfo.text = Localization.Format(lang, "FooterSummary", participating.Count, managed.Count);
                }

                if (_addInspectorButton != null)
                {
                    _addInspectorButton.text = Localization.Get(lang, "AddInspector");
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
                var row = new VisualElement();
                row.AddToClassList("imv2-row");

                if (w.IsFaultIsolated) row.AddToClassList("imv2-row--fault");
                else if (w.IsExplicitlyExcluded) row.AddToClassList("imv2-row--excluded");
                else if (w.IsManuallyUnlocked) row.AddToClassList("imv2-row--unlocked");
                else if (w.CanParticipate && state.LifecycleState == RuntimeLifecycleState.Running) row.AddToClassList("imv2-row--participating");

                var numLabel = new Label("#" + w.FixedDisplayNumber);
                numLabel.AddToClassList("imv2-row__num");
                row.Add(numLabel);

                string roleText = "-";
                bool isLatest = false;
                if (w.CanParticipate && roleSlotMap.TryGetValue(w.Key, out int slot))
                {
                    isLatest = slot == 0;
                    roleText = isLatest ? Localization.Get(lang, "RoleLatest") : ("-" + slot);
                }
                var roleLabel = new Label(roleText);
                roleLabel.AddToClassList("imv2-row__role");
                if (isLatest) roleLabel.AddToClassList("imv2-row__role--latest");
                row.Add(roleLabel);

                var mainCol = new VisualElement();
                mainCol.AddToClassList("imv2-row__main");

                string targetName = w.AssignedSelection != null && !w.AssignedSelection.IsEmpty
                    ? w.AssignedSelection.BuildDisplaySummary()
                    : (string.IsNullOrEmpty(w.ObservedRepresentativeName) ? Localization.Get(lang, "EmptyTarget") : w.ObservedRepresentativeName);

                var targetLabel = new Label(targetName);
                targetLabel.AddToClassList("imv2-row__target");
                mainCol.Add(targetLabel);

                string statusPart = GetWindowStatusText(w, lang);
                if (w.AssignmentLabel == AssignmentLabel.InitialDisplay)
                {
                    statusPart += " · " + Localization.Get(lang, "LabelInitialDisplay");
                }
                else if (w.AssignmentLabel == AssignmentLabel.HistoryUnassigned)
                {
                    statusPart += " · " + Localization.Get(lang, "LabelHistoryUnassigned");
                }
                if (w.IsFaultIsolated && !string.IsNullOrEmpty(w.FaultReason))
                {
                    statusPart += " · " + w.FaultReason;
                }

                var subLabel = new Label(statusPart);
                subLabel.AddToClassList("imv2-row__sub");
                if (w.IsFaultIsolated) subLabel.AddToClassList("imv2-row__sub--error");
                else if (w.IsExplicitlyExcluded || w.IsManuallyUnlocked) subLabel.AddToClassList("imv2-row__sub--warning");
                mainCol.Add(subLabel);

                row.Add(mainCol);

                var actions = new VisualElement();
                actions.AddToClassList("imv2-row__actions");

                var capturedKey = w.Key;

                var upBtn = new Button(() => _host?.Coordinator.EnqueueReorderWindow(capturedKey, rowIndex - 1))
                {
                    text = "▲",
                    tooltip = Localization.Get(lang, "BtnUpTooltip")
                };
                upBtn.AddToClassList("imv2-row-btn");
                upBtn.SetEnabled(rowIndex > 0);
                actions.Add(upBtn);

                var downBtn = new Button(() => _host?.Coordinator.EnqueueReorderWindow(capturedKey, rowIndex + 1))
                {
                    text = "▼",
                    tooltip = Localization.Get(lang, "BtnDownTooltip")
                };
                downBtn.AddToClassList("imv2-row-btn");
                downBtn.SetEnabled(rowIndex < managed.Count - 1);
                actions.Add(downBtn);

                var lockBtn = new Button(() => _host?.Coordinator.EnqueueToggleLock(capturedKey))
                {
                    text = Localization.Get(lang, w.ObservedLocked ? "BtnLock" : "BtnUnlock")
                };
                lockBtn.AddToClassList("imv2-row-btn");
                if (w.ObservedLocked) lockBtn.AddToClassList("imv2-row-btn--lock-on");
                lockBtn.SetEnabled(state.Capabilities.CanWriteLock);
                actions.Add(lockBtn);

                bool nextExcluded = !w.IsExplicitlyExcluded;
                var exBtn = new Button(() => _host?.Coordinator.EnqueueSetExcluded(capturedKey, nextExcluded))
                {
                    text = Localization.Get(lang, w.IsExplicitlyExcluded ? "BtnInclude" : "BtnExclude")
                };
                exBtn.AddToClassList("imv2-row-btn");
                if (w.IsExplicitlyExcluded) exBtn.AddToClassList("imv2-row-btn--excluded");
                actions.Add(exBtn);

                var focusBtn = new Button(() => _host?.Coordinator.EnqueueFocusInspector(capturedKey))
                {
                    text = Localization.Get(lang, "BtnFocus")
                };
                focusBtn.AddToClassList("imv2-row-btn");
                actions.Add(focusBtn);

                var closeBtn = new Button(() => _host?.Coordinator.EnqueueCloseInspector(capturedKey))
                {
                    text = "×",
                    tooltip = Localization.Get(lang, "BtnCloseTooltip")
                };
                closeBtn.AddToClassList("imv2-row-btn");
                closeBtn.SetEnabled(state.Capabilities.CanCloseWindow);
                actions.Add(closeBtn);

                row.Add(actions);
                _managedList.Add(row);
            }
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
                badge.AddToClassList("imv2-row__num");
                row.Add(badge);

                var mainCol = new VisualElement();
                mainCol.AddToClassList("imv2-row__main");
                var nameLabel = new Label(string.IsNullOrEmpty(ext.ObservedRepresentativeName) ? "(Inspector)" : ext.ObservedRepresentativeName);
                nameLabel.AddToClassList("imv2-row__target");
                mainCol.Add(nameLabel);
                row.Add(mainCol);

                var actions = new VisualElement();
                actions.AddToClassList("imv2-row__actions");

                var adoptBtn = new Button(() => _host?.Coordinator.EnqueueAdoptInspector(capturedKey))
                {
                    text = Localization.Get(lang, "Adopt")
                };
                adoptBtn.AddToClassList("imv2-row-btn");
                actions.Add(adoptBtn);

                var focusBtn = new Button(() => _host?.Coordinator.EnqueueFocusInspector(capturedKey))
                {
                    text = Localization.Get(lang, "BtnFocus")
                };
                focusBtn.AddToClassList("imv2-row-btn");
                actions.Add(focusBtn);

                row.Add(actions);
                _externalList.Add(row);
            }
        }

        private void SyncAdvancedSettings(ManagerSettings settings, string lang)
        {
            if (_advancedDetailsTitle != null) _advancedDetailsTitle.text = Localization.Get(lang, "AdvancedSettings");

            if (_maxHistorySlider != null)
            {
                _maxHistorySlider.label = Localization.Get(lang, "MaxRotHistory");
                _maxHistorySlider.SetValueWithoutNotify(settings.MaxRotationHistory);
            }
            if (_recordSceneToggle != null)
            {
                _recordSceneToggle.text = Localization.Get(lang, "RecordSceneObjects");
                _recordSceneToggle.SetValueWithoutNotify(settings.RecordSceneObjects);
            }
            if (_recordAssetsToggle != null)
            {
                _recordAssetsToggle.text = Localization.Get(lang, "RecordAssets");
                _recordAssetsToggle.SetValueWithoutNotify(settings.RecordAssets);
            }
            if (_showOverlaysToggle != null)
            {
                _showOverlaysToggle.text = Localization.Get(lang, "ShowOverlays");
                _showOverlaysToggle.SetValueWithoutNotify(settings.ShowInspectorOverlays);
            }
            if (_blockedCategoriesTitle != null) _blockedCategoriesTitle.text = Localization.Get(lang, "BlockedCategoriesHeader");
            if (_blockedCategoriesHint != null) _blockedCategoriesHint.text = Localization.Get(lang, "BlockedCategoriesHint");

            foreach (var kvp in _categoryToggles)
            {
                kvp.Value.text = Localization.CategoryLabel(lang, kvp.Key);
                kvp.Value.SetValueWithoutNotify(settings.BlockedCategories.Contains(kvp.Key));
            }

            // 閉じている間の要約チップ (.dennoko-summary-chip: 初期値と異なる項目だけ表示)
            if (_advancedDetailsChips != null)
            {
                _advancedDetailsChips.Clear();
                if (settings.BlockedCategories.Count > 0)
                {
                    _advancedDetailsChips.Add(DennokoBlockUI.Chip(null, Localization.Format(lang, "ChipBlocked", settings.BlockedCategories.Count)));
                }
                if (!settings.RecordSceneObjects)
                {
                    _advancedDetailsChips.Add(DennokoBlockUI.Chip(null, Localization.Get(lang, "ChipSceneOff")));
                }
                if (!settings.RecordAssets)
                {
                    _advancedDetailsChips.Add(DennokoBlockUI.Chip(null, Localization.Get(lang, "ChipAssetsOff")));
                }
                if (!settings.ShowInspectorOverlays)
                {
                    _advancedDetailsChips.Add(DennokoBlockUI.Chip(null, Localization.Get(lang, "ChipOverlayOff")));
                }
                if (settings.MaxRotationHistory != 20)
                {
                    _advancedDetailsChips.Add(DennokoBlockUI.Chip(null, Localization.Format(lang, "ChipHistory", settings.MaxRotationHistory)));
                }
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

            string lifecycleText = Localization.LifecycleLabel(lang, state.LifecycleState);
            _status.Set(lifecycleText, DennokoStatusType.Info);
        }

        private static string GetWindowStatusText(WindowRecord w, string lang)
        {
            if (w.IsFaultIsolated) return Localization.Get(lang, "StateFaultIsolated");
            if (w.IsExplicitlyExcluded) return Localization.Get(lang, "StateExcluded");
            if (w.IsManuallyUnlocked) return Localization.Get(lang, "StateManuallyUnlocked");
            return Localization.Get(lang, "StateParticipating");
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
