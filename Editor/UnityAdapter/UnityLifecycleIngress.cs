using System;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Centralized single-subscriber Unity Editor event ingress (Section 3 & 9).
    /// Guards subscriptions with a generation counter to prevent duplicate handlers when Domain Reload is disabled.
    /// </summary>
    public sealed class UnityLifecycleIngress : IDisposable
    {
        private static UnityLifecycleIngress s_activeInstance;
        private static long s_subscriptionGeneration;

        private readonly InspectorManagerCoordinator _coordinator;
        private readonly long _myGeneration;
        private bool _subscribed;
        private bool _disposed;

        public UnityLifecycleIngress(InspectorManagerCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            // Section 9: When Domain Reload is disabled, explicitly tear down any prior subscription generation first!
            if (s_activeInstance != null)
            {
                s_activeInstance.Unsubscribe();
            }

            s_subscriptionGeneration++;
            _myGeneration = s_subscriptionGeneration;
            s_activeInstance = this;
        }

        public static InspectorManagerCoordinator ActiveCoordinator => s_activeInstance?._coordinator;

        public void Subscribe()
        {
            if (_disposed || _subscribed) return;
            _subscribed = true;

            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;

            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.update -= OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
            if (ReferenceEquals(s_activeInstance, this))
            {
                s_activeInstance = null;
            }
        }

        private bool IsCurrentGeneration() => !_disposed && _myGeneration == s_subscriptionGeneration;

        private void OnSelectionChanged()
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.OnSelectionChangedImmediate();
        }

        private void OnEditorUpdate()
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.Tick();
        }

        private void OnBeforeAssemblyReload()
        {
            if (!IsCurrentGeneration()) return;
            // Section 9: Stop input, bump generation, flush settings, save recovery journal, restore leases, unsubscribe.
            _coordinator.HandleLifecycleBoundaryStop(StopReason.AssemblyReload, suspendMode: true);
            Unsubscribe();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (!IsCurrentGeneration()) return;

            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
            {
                // Section 9: Stop & restore leases before Play Mode transition; never reuse Edit Mode refs in Play Mode.
                _coordinator.HandleLifecycleBoundaryStop(StopReason.PlayModeTransition, suspendMode: true);
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                // Section 9: Re-enable input in Off state (never auto-restart rotation!).
                _coordinator.ResumeInputAfterPlayModeReturn();
            }
        }

        private void OnEditorQuitting()
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.HandleLifecycleBoundaryStop(StopReason.EditorQuitting, suspendMode: false);
            Unsubscribe();
        }

        private void OnProjectChanged()
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
        }

        private void OnSceneClosed(Scene scene)
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
        }

        private void OnSceneSaved(Scene scene)
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: true);
        }

        private void OnUndoRedoPerformed()
        {
            if (!IsCurrentGeneration()) return;
            _coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
        }

        internal static void NotifyAssetsImportedOrDeleted()
        {
            s_activeInstance?._coordinator.EnqueueInvalidateEnvironmentCaches(sceneSaved: false);
        }
    }

    /// <summary>
    /// Invalidates object/subasset resolution caches whenever assets are imported, deleted, or moved (Section 14.4).
    /// </summary>
    internal sealed class InspectorManagerAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            UnityLifecycleIngress.NotifyAssetsImportedOrDeleted();
        }
    }

    /// <summary>
    /// Registers Ctrl+L / Cmd+L via Unity's official Shortcut Management API (Section 11).
    /// Respects user rebinding and routes through the exact same command queue as Manager UI buttons.
    /// </summary>
    public static class UnityShortcutCommands
    {
        [Shortcut("Inspector Manager V2/Toggle Focused Inspector Lock", KeyCode.L, ShortcutModifiers.Action)]
        public static void ToggleFocusedInspectorLock()
        {
            var coordinator = UnityLifecycleIngress.ActiveCoordinator;
            if (coordinator == null) return;
            coordinator.EnqueueToggleFocusedInspectorLock();
        }
    }
}
