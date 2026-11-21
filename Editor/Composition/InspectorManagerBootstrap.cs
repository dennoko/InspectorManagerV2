using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using DennokoWorks.InspectorManagerV2.Presentation;
using DennokoWorks.InspectorManagerV2.UnityAdapter;
using UnityEditor;

namespace DennokoWorks.InspectorManagerV2.Composition
{
    /// <summary>
    /// Composition Root.
    /// Explicitly assembles the single History-mode runtime instance and wires Unity lifecycle ingress and Manager windows.
    /// </summary>
    [InitializeOnLoad]
    public static class InspectorManagerBootstrap
    {
        private static RuntimeHost s_host;
        private static bool s_isInLifecycleBoundary;

        static InspectorManagerBootstrap()
        {
            InitializeRuntime();
        }

        [MenuItem("Window/DennokoWorks/Inspector Manager V2")]
        public static void OpenWindow()
        {
            EnsureInitialized();
            var window = EditorWindow.GetWindow<InspectorManagerWindow>();
            window.AttachHost(s_host);
            window.Show();
        }

        private static void EnsureInitialized()
        {
            if (s_host == null)
            {
                InitializeRuntime();
            }
        }

        private static void InitializeRuntime()
        {
            if (s_host != null)
            {
                s_host.Dispose();
                s_host = null;
            }

            s_isInLifecycleBoundary = false;

            string projectId = UnityProjectPaths.GetProjectId();
            string storageDir = UnityProjectPaths.GetProjectStorageDirectory(projectId);

            var clock = new UnityMonotonicClock();
            var logger = new UnityDiagnosticLogger();
            var sessionStore = new UnitySessionStateStore();
            string editorSessionId = sessionStore.GetEditorSessionId();

            var selectionAdapter = new UnitySelectionAdapter();
            var inspectorAdapter = new InspectorReflectionAdapter(editorSessionId);
            var persistenceStore = new AtomicJsonFileStore(storageDir, projectId);

            InspectorManagerCoordinator coordinator = null;
            var overlayManager = new UnityOverlayManager(inspectorAdapter, key => coordinator?.EnqueueToggleLock(key));

            coordinator = new InspectorManagerCoordinator(
                projectId,
                inspectorAdapter,
                selectionAdapter,
                persistenceStore,
                sessionStore,
                clock,
                logger,
                overlayManager);

            var ingress = new UnityLifecycleIngress(coordinator);

            s_host = new RuntimeHost(coordinator, ingress);
            InspectorManagerWindow.HostProvider = () =>
            {
                EnsureInitialized();
                return s_host;
            };

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReloadFlag;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReloadFlag;
            EditorApplication.quitting -= OnQuittingFlag;
            EditorApplication.quitting += OnQuittingFlag;
            EditorApplication.playModeStateChanged -= OnPlayModeFlag;
            EditorApplication.playModeStateChanged += OnPlayModeFlag;

            coordinator.Initialize();
            ingress.Subscribe();

            // Attach any InspectorManagerWindow already restored by Unity's Editor layout on startup / reload.
            var existingWindows = UnityEngine.Resources.FindObjectsOfTypeAll<InspectorManagerWindow>();
            if (existingWindows != null)
            {
                for (int i = 0; i < existingWindows.Length; i++)
                {
                    if (existingWindows[i] != null)
                    {
                        existingWindows[i].AttachHost(s_host);
                    }
                }
            }
        }

        private static void OnBeforeAssemblyReloadFlag()
        {
            s_isInLifecycleBoundary = true;
        }

        private static void OnQuittingFlag()
        {
            s_isInLifecycleBoundary = true;
        }

        private static void OnPlayModeFlag(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
            {
                s_isInLifecycleBoundary = true;
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                s_isInLifecycleBoundary = false;
            }
        }

        private sealed class RuntimeHost : IManagerWindowHost, IDisposable
        {
            private readonly HashSet<EditorWindow> _openWindows = new HashSet<EditorWindow>();
            private readonly UnityLifecycleIngress _ingress;
            private bool _disposed;

            public InspectorManagerCoordinator Coordinator { get; }

            public RuntimeHost(
                InspectorManagerCoordinator coordinator,
                UnityLifecycleIngress ingress)
            {
                Coordinator = coordinator;
                _ingress = ingress;
            }

            public void RegisterWindow(EditorWindow window)
            {
                if (window == null) return;
                _openWindows.Add(window);
                bool canAutoStart = !s_isInLifecycleBoundary && !EditorApplication.isPlayingOrWillChangePlaymode;
                Coordinator.SetManagerTabOpen(true, autoStartIfIdle: canAutoStart);
            }

            public void UnregisterWindowOnDestroy(EditorWindow window)
            {
                if (window != null)
                {
                    _openWindows.Remove(window);
                }

                _openWindows.RemoveWhere(w => w == null);

                // When the LAST Manager tab is closed by the user (NOT reload/play/quit),
                // stop rotation, clear overlays, and unlock ALL managed Inspectors.
                if (!_disposed && !s_isInLifecycleBoundary && _openWindows.Count == 0)
                {
                    Coordinator.SetManagerTabOpen(false);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _ingress?.Dispose();
                Coordinator?.Dispose();
                _openWindows.Clear();
            }
        }
    }
}
