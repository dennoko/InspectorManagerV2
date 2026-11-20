using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using DennokoWorks.InspectorManagerV2.Presentation;
using DennokoWorks.InspectorManagerV2.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.Composition
{
    /// <summary>
    /// Composition Root (Section 3 & 9).
    /// Explicitly assembles the single runtime instance and wires Unity lifecycle ingress and Manager windows.
    /// Avoids global Service Locator or mutable Event Bus anti-patterns.
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

        public static InspectorManagerCoordinator GetCoordinatorForDiagnostics()
        {
            EnsureInitialized();
            return s_host.Coordinator;
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
            var logger = new UnityDiagnosticLogger(clock);
            var sessionStore = new UnitySessionStateStore();
            string editorSessionId = sessionStore.GetEditorSessionId();

            var resolver = new UnityObjectResolver();
            var selectionAdapter = new UnitySelectionAdapter(resolver);
            var inspectorAdapter = new InspectorReflectionAdapter(resolver, editorSessionId);
            var persistenceStore = new AtomicJsonFileStore(storageDir, projectId);

            InspectorManagerCoordinator coordinator = null;
            var overlayManager = new UnityOverlayManager(inspectorAdapter, key => coordinator?.EnqueueToggleLock(key));

            coordinator = new InspectorManagerCoordinator(
                projectId,
                inspectorAdapter,
                selectionAdapter,
                resolver,
                persistenceStore,
                sessionStore,
                clock,
                logger,
                overlayManager);

            var ingress = new UnityLifecycleIngress(coordinator);

            s_host = new RuntimeHost(coordinator, selectionAdapter, ingress);
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
            public ISelectionAdapter SelectionAdapter { get; }
            public Func<UnityEngine.Object, ObjectRefKey> GetObjectRefKey => UnityObjectIdHelper.GetObjectRefKey;

            public RuntimeHost(
                InspectorManagerCoordinator coordinator,
                ISelectionAdapter selectionAdapter,
                UnityLifecycleIngress ingress)
            {
                Coordinator = coordinator;
                SelectionAdapter = selectionAdapter;
                _ingress = ingress;
            }

            public void RegisterWindow(EditorWindow window)
            {
                if (window != null)
                {
                    _openWindows.Add(window);
                }
            }

            public void UnregisterWindowOnDestroy(EditorWindow window)
            {
                if (window != null)
                {
                    _openWindows.Remove(window);
                }

                // Prune any already-destroyed window references.
                _openWindows.RemoveWhere(w => w == null);

                // Section 6.2 & 9: When the LAST Manager window is closed by the user (NOT reload/play/quit),
                // stop rotation and restore all surviving Inspector leases (no hidden background rotation).
                if (!_disposed && !s_isInLifecycleBoundary && _openWindows.Count == 0)
                {
                    Coordinator.HandleLifecycleBoundaryStop(StopReason.LastManagerWindowClosed, suspendMode: false);
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
