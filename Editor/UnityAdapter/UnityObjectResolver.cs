using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Resolves and persists Unity object references (Section 7 & 14.4).
    /// - Assets store GUID + LocalFileId + GlobalObjectId; missing subassets NEVER fall back to main asset.
    /// - Scene objects verify scene loaded state without opening scenes; unsaved objects are marked temporary
    ///   and promoted only when verified alive after scene save.
    /// - Resolution results (including failures) are cached and invalidated on project/scene/undo changes.
    /// </summary>
    public sealed class UnityObjectResolver : IFavoriteResolver
    {
        private readonly Dictionary<string, FavoriteResolutionResult> _cache = new Dictionary<string, FavoriteResolutionResult>(StringComparer.Ordinal);

        public void InvalidateCache()
        {
            _cache.Clear();
        }

        public ObjectItemSnapshot CreateItemSnapshot(UnityEngine.Object obj)
        {
            if (obj == null) return null;

            var key = UnityObjectIdHelper.GetObjectRefKey(obj);
            if (!key.IsValid) return null;

            var (category, isAsset) = UnityCategoryClassifier.Classify(obj);
            var persistentId = BuildPersistentId(obj, out _);

            return new ObjectItemSnapshot(
                key,
                SafeGetName(obj),
                obj.GetType().Name,
                category,
                isAsset,
                persistentId);
        }

        public FavoriteEntry CreateFromLiveObject(ObjectRefKey key, int sortOrder)
        {
            var obj = UnityObjectIdHelper.ResolveObject(key);
            if (obj == null)
            {
                return null;
            }

            var pid = BuildPersistentId(obj, out bool isTemporary);
            string displayName = SafeGetName(obj);
            string typeName = obj.GetType().Name;

            return new FavoriteEntry(
                favoriteId: Guid.NewGuid().ToString("N"),
                persistentId: pid,
                displayNameCache: displayName,
                typeNameCache: typeName,
                sortOrder: sortOrder,
                isTemporary: isTemporary,
                temporaryRuntimeKey: isTemporary ? (ObjectRefKey?)key : null,
                resolutionState: isTemporary ? FavoriteResolutionState.TemporaryUnsaved : FavoriteResolutionState.Resolved,
                resolutionDetail: isTemporary ? "Unsaved scene or temporary object (not persisted across restarts)." : "Resolved");
        }

        public FavoriteResolutionResult Resolve(FavoriteEntry entry)
        {
            if (entry == null)
            {
                return new FavoriteResolutionResult(FavoriteResolutionState.ResolutionFailed, null, null, string.Empty, string.Empty, "Null entry");
            }

            string cacheKey = BuildCacheKey(entry);
            if (!entry.IsTemporary && _cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            FavoriteResolutionResult result = ResolveUncached(entry);
            if (!entry.IsTemporary)
            {
                _cache[cacheKey] = result;
            }
            return result;
        }

        private FavoriteResolutionResult ResolveUncached(FavoriteEntry entry)
        {
            try
            {
                // Case 1: Temporary (unsaved scene / transient) object.
                if (entry.IsTemporary)
                {
                    if (!entry.TemporaryRuntimeKey.HasValue)
                    {
                        return new FavoriteResolutionResult(
                            FavoriteResolutionState.DeletedOrMissing,
                            null,
                            null,
                            entry.DisplayNameCache,
                            entry.TypeNameCache,
                            "Temporary object reference expired.");
                    }

                    var liveObj = UnityObjectIdHelper.ResolveObject(entry.TemporaryRuntimeKey.Value);
                    if (liveObj == null)
                    {
                        return new FavoriteResolutionResult(
                            FavoriteResolutionState.DeletedOrMissing,
                            null,
                            null,
                            entry.DisplayNameCache,
                            entry.TypeNameCache,
                            "Temporary object no longer exists.");
                    }

                    // Section 7: Promote to persistent reference ONLY if scene was saved and object is confirmed identical!
                    var upgradedPid = BuildPersistentId(liveObj, out bool stillTemp);
                    var liveKey = UnityObjectIdHelper.GetObjectRefKey(liveObj);
                    if (!stillTemp && !upgradedPid.IsEmpty)
                    {
                        return new FavoriteResolutionResult(
                            FavoriteResolutionState.Resolved,
                            liveKey,
                            upgradedPid,
                            SafeGetName(liveObj),
                            liveObj.GetType().Name,
                            "Promoted to persistent reference after save.");
                    }

                    return new FavoriteResolutionResult(
                        FavoriteResolutionState.TemporaryUnsaved,
                        liveKey,
                        null,
                        SafeGetName(liveObj),
                        liveObj.GetType().Name,
                        "Temporary unsaved object.");
                }

                // Case 2: Persistent Asset (GUID + LocalFileId + GlobalObjectId).
                if (entry.PersistentId.Kind == PersistentObjectKind.Asset)
                {
                    return ResolveAsset(entry);
                }

                // Case 3: Persistent Scene Object (SceneGuid + GlobalObjectId).
                if (entry.PersistentId.Kind == PersistentObjectKind.SceneObject)
                {
                    return ResolveSceneObject(entry);
                }

                return new FavoriteResolutionResult(
                    FavoriteResolutionState.ResolutionFailed,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Unsupported persistent reference kind.");
            }
            catch (Exception ex)
            {
                return new FavoriteResolutionResult(
                    FavoriteResolutionState.ResolutionFailed,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Resolution exception: " + ex.Message);
            }
        }

        private static FavoriteResolutionResult ResolveAsset(FavoriteEntry entry)
        {
            string guid = entry.PersistentId.AssetGuid;
            long expectedLocalId = entry.PersistentId.LocalFileId;
            string assetPath = !string.IsNullOrEmpty(guid) ? AssetDatabase.GUIDToAssetPath(guid) : string.Empty;

            if (string.IsNullOrEmpty(assetPath))
            {
                return new FavoriteResolutionResult(
                    FavoriteResolutionState.DeletedOrMissing,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Asset GUID not found in project.");
            }

            // First try GlobalObjectId exact resolution.
            if (!string.IsNullOrEmpty(entry.PersistentId.GlobalObjectIdString)
                && GlobalObjectId.TryParse(entry.PersistentId.GlobalObjectIdString, out var gid))
            {
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (resolved != null && MatchesAssetGuidAndLocalId(resolved, guid, expectedLocalId))
                {
                    return new FavoriteResolutionResult(
                        FavoriteResolutionState.Resolved,
                        UnityObjectIdHelper.GetObjectRefKey(resolved),
                        null,
                        SafeGetName(resolved),
                        resolved.GetType().Name,
                        "Resolved");
                }
            }

            // Exact scan across main asset + subassets matching BOTH GUID and LocalFileId.
            // Section 14.4 & 14.5 #9: NEVER fall back to main asset when a subasset is missing!
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                var folderObj = AssetDatabase.LoadAssetAtPath<DefaultAsset>(assetPath);
                if (folderObj != null && MatchesAssetGuidAndLocalId(folderObj, guid, expectedLocalId))
                {
                    return new FavoriteResolutionResult(
                        FavoriteResolutionState.Resolved,
                        UnityObjectIdHelper.GetObjectRefKey(folderObj),
                        null,
                        SafeGetName(folderObj),
                        folderObj.GetType().Name,
                        "Resolved");
                }
            }
            else
            {
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (allAssets != null)
                {
                    for (int i = 0; i < allAssets.Length; i++)
                    {
                        var candidate = allAssets[i];
                        if (candidate == null) continue;
                        if (MatchesAssetGuidAndLocalId(candidate, guid, expectedLocalId))
                        {
                            return new FavoriteResolutionResult(
                                FavoriteResolutionState.Resolved,
                                UnityObjectIdHelper.GetObjectRefKey(candidate),
                                null,
                                SafeGetName(candidate),
                                candidate.GetType().Name,
                                "Resolved");
                        }
                    }
                }
            }

            return new FavoriteResolutionResult(
                FavoriteResolutionState.DeletedOrMissing,
                null,
                null,
                entry.DisplayNameCache,
                entry.TypeNameCache,
                "Target asset or subasset (localFileId=" + expectedLocalId + ") was not found; main asset fallback suppressed.");
        }

        private static FavoriteResolutionResult ResolveSceneObject(FavoriteEntry entry)
        {
            string sceneGuid = entry.PersistentId.SceneGuid;
            if (!string.IsNullOrEmpty(sceneGuid) && !IsSceneGuidCurrentlyLoaded(sceneGuid))
            {
                // Section 7: Never auto-open scenes to resolve favorites!
                return new FavoriteResolutionResult(
                    FavoriteResolutionState.SceneUnloaded,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Target scene is not currently loaded.");
            }

            if (string.IsNullOrEmpty(entry.PersistentId.GlobalObjectIdString)
                || !GlobalObjectId.TryParse(entry.PersistentId.GlobalObjectIdString, out var gid))
            {
                return new FavoriteResolutionResult(
                    FavoriteResolutionState.ResolutionFailed,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Invalid GlobalObjectId for scene object.");
            }

            var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (resolved == null)
            {
                return new FavoriteResolutionResult(
                    FavoriteResolutionState.DeletedOrMissing,
                    null,
                    null,
                    entry.DisplayNameCache,
                    entry.TypeNameCache,
                    "Scene object not found in loaded scene.");
            }

            return new FavoriteResolutionResult(
                FavoriteResolutionState.Resolved,
                UnityObjectIdHelper.GetObjectRefKey(resolved),
                null,
                SafeGetName(resolved),
                resolved.GetType().Name,
                "Resolved");
        }

        public static PersistentObjectId BuildPersistentId(UnityEngine.Object obj, out bool isTemporary)
        {
            isTemporary = false;
            if (obj == null)
            {
                return PersistentObjectId.Empty;
            }

            if ((obj.hideFlags & HideFlags.DontSave) != 0)
            {
                isTemporary = true;
                return new PersistentObjectId(PersistentObjectKind.Temporary, string.Empty, 0, string.Empty, string.Empty);
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
                {
                    string gidStr = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
                    return new PersistentObjectId(PersistentObjectKind.Asset, guid, localId, gidStr, string.Empty);
                }

                string fallbackGuid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(fallbackGuid))
                {
                    string gidStr = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
                    return new PersistentObjectId(PersistentObjectKind.Asset, fallbackGuid, 0, gidStr, string.Empty);
                }
            }

            // Scene object (GameObject or Component).
            Scene? scene = null;
            if (obj is GameObject go)
            {
                scene = go.scene;
            }
            else if (obj is Component comp && comp.gameObject != null)
            {
                scene = comp.gameObject.scene;
            }

            if (scene.HasValue)
            {
                if (!scene.Value.IsValid() || string.IsNullOrEmpty(scene.Value.path))
                {
                    // Unsaved scene -> temporary!
                    isTemporary = true;
                    return new PersistentObjectId(PersistentObjectKind.Temporary, string.Empty, 0, string.Empty, string.Empty);
                }

                string sceneGuid = AssetDatabase.AssetPathToGUID(scene.Value.path);
                var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
                if (!string.IsNullOrEmpty(sceneGuid) && gid.identifierType != 0)
                {
                    return new PersistentObjectId(PersistentObjectKind.SceneObject, string.Empty, 0, gid.ToString(), sceneGuid);
                }
            }

            isTemporary = true;
            return new PersistentObjectId(PersistentObjectKind.Temporary, string.Empty, 0, string.Empty, string.Empty);
        }

        private static bool MatchesAssetGuidAndLocalId(UnityEngine.Object obj, string expectedGuid, long expectedLocalId)
        {
            if (obj == null) return false;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string actualGuid, out long actualLocalId))
            {
                return false;
            }

            if (!string.Equals(actualGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Match exact local file ID when recorded (non-zero).
            if (expectedLocalId != 0 && actualLocalId != expectedLocalId)
            {
                return false;
            }

            return true;
        }

        private static bool IsSceneGuidCurrentlyLoaded(string sceneGuid)
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                {
                    continue;
                }

                string loadedGuid = AssetDatabase.AssetPathToGUID(scene.path);
                if (string.Equals(loadedGuid, sceneGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildCacheKey(FavoriteEntry entry)
        {
            var p = entry.PersistentId;
            return entry.FavoriteId + "|" + p.Kind + "|" + p.AssetGuid + "|" + p.LocalFileId + "|" + p.GlobalObjectIdString + "|" + p.SceneGuid;
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
