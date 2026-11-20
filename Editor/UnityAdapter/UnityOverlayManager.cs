using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Attaches lightweight UIElements badge/control overlays onto Unity Inspector windows (Section 14.3).
    /// - Prevents duplicates via named container element.
    /// - Re-verifies parent attachment against current rootVisualElement and re-attaches if Inspector rebuilt.
    /// - Success flash updates an end timestamp only (never stacks callbacks per selection).
    /// - Lock button dispatches the standard ToggleLock command (never mutates Inspector directly).
    /// - Cleans up all elements & callbacks on dispose.
    /// </summary>
    public sealed class UnityOverlayManager : IOverlayPresenter
    {
        private const string OverlayElementName = "dennokoworks-imv2-overlay-bar";
        private const long FlashDurationMs = 450;

        private sealed class OverlayBinding
        {
            public EditorWindow Window;
            public VisualElement Container;
            public Label BadgeLabel;
            public Label StatusLabel;
            public Button LockButton;
            public WindowKey WindowKey;
        }

        private readonly InspectorReflectionAdapter _adapter;
        private readonly Action<WindowKey> _onToggleLockCommand;
        private readonly Dictionary<WindowKey, OverlayBinding> _bindings = new Dictionary<WindowKey, OverlayBinding>();
        private readonly Dictionary<WindowKey, long> _flashEndMs = new Dictionary<WindowKey, long>();
        private bool _disposed;

        public UnityOverlayManager(InspectorReflectionAdapter adapter, Action<WindowKey> onToggleLockCommand)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _onToggleLockCommand = onToggleLockCommand;
        }

        public void SyncOverlays(ManagerState state, long nowMs, IReadOnlyCollection<WindowKey> flashedWindows)
        {
            if (_disposed || state == null) return;

            if (flashedWindows != null)
            {
                foreach (var key in flashedWindows)
                {
                    // Section 14.3: Update end timestamp only; do NOT stack callbacks per selection.
                    _flashEndMs[key] = nowMs + FlashDurationMs;
                }
            }

            var aliveWindowKeys = new HashSet<WindowKey>();
            var participating = state.GetParticipatingWindowsOrdered();
            var roleIndexMap = new Dictionary<WindowKey, int>();
            for (int i = 0; i < participating.Count; i++)
            {
                roleIndexMap[participating[i].Key] = i;
            }

            for (int i = 0; i < state.Windows.Count; i++)
            {
                var record = state.Windows[i];
                if (!record.IsAlive) continue;

                var editorWindow = _adapter.FindLiveInspectorWindow(record.Key);
                if (editorWindow == null || editorWindow.rootVisualElement == null)
                {
                    continue;
                }

                aliveWindowKeys.Add(record.Key);
                var binding = EnsureOverlayAttached(record.Key, editorWindow);
                if (binding == null) continue;

                UpdateBindingVisuals(binding, state, record, roleIndexMap, nowMs);
            }

            // Detach bindings for closed windows.
            var staleKeys = new List<WindowKey>();
            foreach (var kvp in _bindings)
            {
                if (!aliveWindowKeys.Contains(kvp.Key))
                {
                    staleKeys.Add(kvp.Key);
                }
            }

            for (int i = 0; i < staleKeys.Count; i++)
            {
                RemoveBinding(staleKeys[i]);
            }
        }

        public void ClearAllOverlays()
        {
            var keys = new List<WindowKey>(_bindings.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                RemoveBinding(keys[i]);
            }
            _flashEndMs.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearAllOverlays();
        }

        private OverlayBinding EnsureOverlayAttached(WindowKey key, EditorWindow window)
        {
            var root = window.rootVisualElement;
            if (root == null) return null;

            if (_bindings.TryGetValue(key, out var existing))
            {
                // Section 14.3: Verify current rootVisualElement parent relationship and re-attach if detached on rebuild.
                if (existing.Window == window && existing.Container != null)
                {
                    if (existing.Container.parent != root)
                    {
                        RemoveNamedDuplicates(root, existing.Container);
                        root.Insert(0, existing.Container);
                    }
                    return existing;
                }

                RemoveBinding(key);
            }

            // Prevent duplicate named elements (e.g., leftovers from before Assembly Reload).
            RemoveNamedDuplicates(root, keepElement: null);

            var container = new VisualElement
            {
                name = OverlayElementName,
                pickingMode = PickingMode.Position
            };
            container.style.flexDirection = FlexDirection.Row;
            container.style.alignItems = Align.Center;
            container.style.justifyContent = Justify.SpaceBetween;
            container.style.paddingLeft = 6;
            container.style.paddingRight = 6;
            container.style.paddingTop = 2;
            container.style.paddingBottom = 2;
            container.style.borderBottomWidth = 1;
            container.style.borderBottomColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);
            container.style.backgroundColor = new Color(0.18f, 0.22f, 0.28f, 0.92f);

            var leftGroup = new VisualElement();
            leftGroup.style.flexDirection = FlexDirection.Row;
            leftGroup.style.alignItems = Align.Center;

            var badgeLabel = new Label("#?");
            badgeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            badgeLabel.style.marginRight = 6;
            badgeLabel.style.color = new Color(0.95f, 0.97f, 1f, 1f);

            var statusLabel = new Label(string.Empty);
            statusLabel.style.fontSize = 11;
            statusLabel.style.color = new Color(0.82f, 0.87f, 0.93f, 1f);

            leftGroup.Add(badgeLabel);
            leftGroup.Add(statusLabel);

            var capturedKey = key;
            var lockBtn = new Button(() => _onToggleLockCommand?.Invoke(capturedKey))
            {
                text = "Lock"
            };
            lockBtn.style.height = 18;
            lockBtn.style.fontSize = 10;
            lockBtn.style.paddingLeft = 6;
            lockBtn.style.paddingRight = 6;
            lockBtn.style.marginLeft = 4;

            container.Add(leftGroup);
            container.Add(lockBtn);

            root.Insert(0, container);

            var binding = new OverlayBinding
            {
                Window = window,
                Container = container,
                BadgeLabel = badgeLabel,
                StatusLabel = statusLabel,
                LockButton = lockBtn,
                WindowKey = key
            };

            _bindings[key] = binding;
            return binding;
        }

        private void UpdateBindingVisuals(
            OverlayBinding binding,
            ManagerState state,
            WindowRecord record,
            Dictionary<WindowKey, int> roleIndexMap,
            long nowMs)
        {
            bool isJa = string.Equals(state.Settings.Language, "ja", StringComparison.OrdinalIgnoreCase);

            string badge = record.IsManaged
                ? "#" + record.FixedDisplayNumber
                : (isJa ? "外部" : "EXT");

            string status;
            Color bgColor;

            if (!record.IsManaged)
            {
                status = isJa ? "管理外" : "Unmanaged";
                bgColor = new Color(0.22f, 0.22f, 0.22f, 0.88f);
            }
            else if (record.IsFaultIsolated)
            {
                status = isJa ? "故障隔離" : "Faulted";
                bgColor = new Color(0.45f, 0.16f, 0.16f, 0.95f);
            }
            else if (record.IsExplicitlyExcluded)
            {
                status = isJa ? "除外中" : "Excluded";
                bgColor = new Color(0.32f, 0.25f, 0.16f, 0.92f);
            }
            else if (record.IsManuallyUnlocked)
            {
                status = isJa ? "手動解除" : "Unlocked";
                bgColor = new Color(0.30f, 0.28f, 0.15f, 0.92f);
            }
            else if (state.LifecycleState == RuntimeLifecycleState.Running)
            {
                string roleText = string.Empty;
                if (state.Settings.Mode == RotationMode.History && roleIndexMap.TryGetValue(record.Key, out int slot))
                {
                    roleText = slot == 0 ? (isJa ? "最新" : "Latest") : ("-" + slot);
                }
                else if (state.Settings.Mode == RotationMode.Cycle)
                {
                    roleText = isJa ? "サイクル" : "Cycle";
                }

                if (record.AssignmentLabel == AssignmentLabel.HistoryUnassigned)
                {
                    status = roleText + (isJa ? " (履歴未割当)" : " (Unassigned)");
                }
                else if (record.AssignmentLabel == AssignmentLabel.InitialDisplay)
                {
                    status = roleText + (isJa ? " (開始時表示)" : " (Initial)");
                }
                else
                {
                    status = roleText;
                }

                bool flashing = _flashEndMs.TryGetValue(record.Key, out long endMs) && nowMs <= endMs;
                bgColor = flashing
                    ? new Color(0.16f, 0.42f, 0.26f, 0.95f)
                    : new Color(0.15f, 0.26f, 0.36f, 0.92f);
            }
            else if (state.LifecycleState == RuntimeLifecycleState.Paused)
            {
                status = isJa ? "一時停止" : "Paused";
                bgColor = new Color(0.28f, 0.24f, 0.34f, 0.92f);
            }
            else
            {
                status = isJa ? "待機中 (Off)" : "Off";
                bgColor = new Color(0.20f, 0.21f, 0.23f, 0.88f);
            }

            binding.BadgeLabel.text = badge;
            binding.StatusLabel.text = status;
            binding.LockButton.text = record.ObservedLocked
                ? (isJa ? "🔒 ロック中" : "🔒 Locked")
                : (isJa ? "🔓 解除中" : "🔓 Unlocked");
            binding.Container.style.backgroundColor = bgColor;
        }

        private void RemoveBinding(WindowKey key)
        {
            if (_bindings.TryGetValue(key, out var binding))
            {
                binding.Container?.RemoveFromHierarchy();
                _bindings.Remove(key);
            }
            _flashEndMs.Remove(key);
        }

        private static void RemoveNamedDuplicates(VisualElement root, VisualElement keepElement)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root[i];
                if (child != keepElement && string.Equals(child.name, OverlayElementName, StringComparison.Ordinal))
                {
                    child.RemoveFromHierarchy();
                }
            }
        }
    }
}
