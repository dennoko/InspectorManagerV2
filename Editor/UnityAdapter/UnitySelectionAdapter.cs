using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Read-only UnityEditor.Selection snapshot adapter.
    /// Never mutates global Selection.
    /// </summary>
    public sealed class UnitySelectionAdapter : ISelectionAdapter
    {
        public static ObjectItemSnapshot CreateItemSnapshot(UnityEngine.Object obj)
        {
            if (obj == null) return null;

            var key = UnityObjectIdHelper.GetObjectRefKey(obj);
            if (!key.IsValid) return null;

            var (category, isAsset) = UnityCategoryClassifier.Classify(obj);
            return new ObjectItemSnapshot(
                key,
                SafeGetName(obj),
                obj.GetType().Name,
                category,
                isAsset);
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
                    var item = CreateItemSnapshot(obj);
                    if (item != null && seen.Add(item.Key))
                    {
                        items.Add(item);
                    }
                }
            }

            ObjectRefKey? activeKey = null;
            if (activeObj != null)
            {
                var activeItem = CreateItemSnapshot(activeObj);
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

        private static string SafeGetName(UnityEngine.Object obj)
        {
            try
            {
                return string.IsNullOrEmpty(obj.name) ? "(" + obj.GetType().Name + ")" : obj.name;
            }
            catch
            {
                return "(Unnamed)";
            }
        }
    }
}
