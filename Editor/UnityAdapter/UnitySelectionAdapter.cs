using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Encapsulates UnityEditor.Selection access and drag-and-drop initiation (Section 5, 7).
    /// </summary>
    public sealed class UnitySelectionAdapter : ISelectionAdapter
    {
        private readonly UnityObjectResolver _resolver;

        public UnitySelectionAdapter(UnityObjectResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public SelectionSnapshot CaptureCurrentSelection(long sequence, long generation, SelectionSource source)
        {
            var rawObjects = Selection.objects;
            var activeObj = Selection.activeObject;

            if ((rawObjects == null || rawObjects.Length == 0) && activeObj == null)
            {
                return SelectionSnapshot.Empty;
            }

            var items = new List<ObjectItemSnapshot>();
            var seen = new HashSet<ObjectRefKey>();

            if (rawObjects != null)
            {
                for (int i = 0; i < rawObjects.Length; i++)
                {
                    var obj = rawObjects[i];
                    if (obj == null) continue;
                    var item = _resolver.CreateItemSnapshot(obj);
                    if (item != null && seen.Add(item.Key))
                    {
                        items.Add(item);
                    }
                }
            }

            ObjectRefKey? activeKey = null;
            if (activeObj != null)
            {
                var activeItem = _resolver.CreateItemSnapshot(activeObj);
                if (activeItem != null)
                {
                    activeKey = activeItem.Key;
                    if (seen.Add(activeItem.Key))
                    {
                        items.Insert(0, activeItem);
                    }
                }
            }

            if (items.Count == 0)
            {
                return SelectionSnapshot.Empty;
            }

            return new SelectionSnapshot(items, activeKey, sequence, generation, source);
        }

        public bool TrySetGlobalSelection(IReadOnlyList<ObjectRefKey> targets, ObjectRefKey? activeTarget, out string error)
        {
            error = string.Empty;
            if (targets == null || targets.Count == 0)
            {
                error = "Target selection is empty.";
                return false;
            }

            // Section 7: Resolve ALL targets first; never shrink partial multi-selection!
            var resolved = new UnityEngine.Object[targets.Count];
            UnityEngine.Object activeObj = null;

            for (int i = 0; i < targets.Count; i++)
            {
                var obj = UnityObjectIdHelper.ResolveObject(targets[i]);
                if (obj == null)
                {
                    error = "One or more target objects could not be resolved.";
                    return false;
                }

                resolved[i] = obj;
                if (activeTarget.HasValue && targets[i] == activeTarget.Value)
                {
                    activeObj = obj;
                }
            }

            try
            {
                Selection.objects = resolved;
                if (activeObj != null)
                {
                    Selection.activeObject = activeObj;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool AreAllTargetsAlive(IReadOnlyList<ObjectRefKey> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (UnityObjectIdHelper.ResolveObject(targets[i]) == null)
                {
                    return false;
                }
            }
            return true;
        }

        public bool IsTargetAlive(ObjectRefKey key)
        {
            return UnityObjectIdHelper.ResolveObject(key) != null;
        }

        public bool PingObject(ObjectRefKey key)
        {
            var obj = UnityObjectIdHelper.ResolveObject(key);
            if (obj == null) return false;
            EditorGUIUtility.PingObject(obj);
            return true;
        }

        public bool TryStartDrag(IReadOnlyList<ObjectRefKey> targets, string title, out string error)
        {
            error = string.Empty;
            if (targets == null || targets.Count == 0)
            {
                error = "No objects to drag.";
                return false;
            }

            // Section 7: Resolve ALL references before starting drag; abort if any missing!
            var resolved = new UnityEngine.Object[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                var obj = UnityObjectIdHelper.ResolveObject(targets[i]);
                if (obj == null)
                {
                    error = "Cannot start drag: one or more objects no longer exist.";
                    return false;
                }
                resolved[i] = obj;
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = resolved;
            DragAndDrop.StartDrag(string.IsNullOrEmpty(title) ? resolved[0].name : title);
            return true;
        }
    }
}
