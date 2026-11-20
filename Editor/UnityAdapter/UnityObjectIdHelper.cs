using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Cross-version Unity object runtime identity helper supporting both Unity 2022.3.22f1 (InstanceID)
    /// and Unity 6 / 6000.3+ (EntityId) without casting EntityId to int or relying on GetHashCode().
    /// </summary>
    public static class UnityObjectIdHelper
    {
#if UNITY_6000_3_OR_NEWER
        private readonly struct NativeHandle
        {
            public readonly EntityId Id;
            public NativeHandle(EntityId id) => Id = id;
        }
#else
        private readonly struct NativeHandle
        {
            public readonly int Id;
            public NativeHandle(int id) => Id = id;
        }
#endif

        private static readonly Dictionary<string, NativeHandle> s_objectHandles = new Dictionary<string, NativeHandle>(StringComparer.Ordinal);
        private static readonly Dictionary<string, NativeHandle> s_windowHandles = new Dictionary<string, NativeHandle>(StringComparer.Ordinal);

        public static void ClearCaches()
        {
            s_objectHandles.Clear();
            s_windowHandles.Clear();
        }

        public static ObjectRefKey GetObjectRefKey(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return default;
            }

#if UNITY_6000_3_OR_NEWER
            EntityId id = obj.GetEntityId();
            if (!id.IsValid())
            {
                return default;
            }
            string raw = "obj:" + id.ToString();
            s_objectHandles[raw] = new NativeHandle(id);
            return new ObjectRefKey(raw);
#else
            int id = obj.GetInstanceID();
            if (id == 0)
            {
                return default;
            }
            string raw = "obj:" + id.ToString();
            s_objectHandles[raw] = new NativeHandle(id);
            return new ObjectRefKey(raw);
#endif
        }

        public static UnityEngine.Object ResolveObject(ObjectRefKey key)
        {
            if (!key.IsValid)
            {
                return null;
            }

            if (s_objectHandles.TryGetValue(key.Value, out var handle))
            {
#if UNITY_6000_3_OR_NEWER
                return EditorUtility.EntityIdToObject(handle.Id);
#else
                return EditorUtility.InstanceIDToObject(handle.Id);
#endif
            }

#if !UNITY_6000_3_OR_NEWER
            if (key.Value.StartsWith("obj:", StringComparison.Ordinal)
                && int.TryParse(key.Value.Substring(4), out int parsedId)
                && parsedId != 0)
            {
                var resolved = EditorUtility.InstanceIDToObject(parsedId);
                if (resolved != null)
                {
                    s_objectHandles[key.Value] = new NativeHandle(parsedId);
                }
                return resolved;
            }
#endif
            return null;
        }

        public static WindowKey GetWindowKey(EditorWindow window)
        {
            if (window == null)
            {
                return default;
            }

#if UNITY_6000_3_OR_NEWER
            EntityId id = window.GetEntityId();
            if (!id.IsValid())
            {
                return default;
            }
            string raw = "win:" + id.ToString();
            s_windowHandles[raw] = new NativeHandle(id);
            return new WindowKey(raw);
#else
            int id = window.GetInstanceID();
            if (id == 0)
            {
                return default;
            }
            string raw = "win:" + id.ToString();
            s_windowHandles[raw] = new NativeHandle(id);
            return new WindowKey(raw);
#endif
        }

        public static string BuildWindowIdentityEvidence(string editorSessionId, EditorWindow window)
        {
            if (window == null) return string.Empty;
            var key = GetWindowKey(window);
            return (editorSessionId ?? string.Empty) + "|" + window.GetType().FullName + "|" + key.Value;
        }

        public static EditorWindow ResolveWindow(WindowKey key)
        {
            if (!key.IsValid)
            {
                return null;
            }

            if (s_windowHandles.TryGetValue(key.Value, out var handle))
            {
#if UNITY_6000_3_OR_NEWER
                return EditorUtility.EntityIdToObject(handle.Id) as EditorWindow;
#else
                return EditorUtility.InstanceIDToObject(handle.Id) as EditorWindow;
#endif
            }

#if !UNITY_6000_3_OR_NEWER
            if (key.Value.StartsWith("win:", StringComparison.Ordinal)
                && int.TryParse(key.Value.Substring(4), out int parsedId)
                && parsedId != 0)
            {
                var win = EditorUtility.InstanceIDToObject(parsedId) as EditorWindow;
                if (win != null)
                {
                    s_windowHandles[key.Value] = new NativeHandle(parsedId);
                }
                return win;
            }
#endif
            return null;
        }
    }
}
