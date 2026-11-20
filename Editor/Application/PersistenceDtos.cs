using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Application
{
    public static class PersistenceSchema
    {
        public const int CurrentSettingsSchemaVersion = 1;
        public const int CurrentRecoverySchemaVersion = 1;
    }

    public sealed class SettingsAndFavoritesDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public long SettingsRevision { get; set; } = 1;
        public string Mode { get; set; } = "History";
        public string Language { get; set; } = "ja";
        public int MaxNavigationHistory { get; set; } = 50;
        public int MaxRotationHistory { get; set; } = 20;
        public bool RecordSceneObjects { get; set; } = true;
        public bool RecordAssets { get; set; } = true;
        public bool AutoFocusUpdatedInspector { get; set; }
        public bool AutoCleanupMissingFavorites { get; set; }
        public bool ShowInspectorOverlays { get; set; } = true;
        public List<string> BlockedCategories { get; set; } = new List<string>();
        public List<FavoriteEntryDto> Favorites { get; set; } = new List<FavoriteEntryDto>();

        public static SettingsAndFavoritesDto FromDomain(string projectId, ManagerSettings settings, IReadOnlyList<FavoriteEntry> favorites)
        {
            var dto = new SettingsAndFavoritesDto
            {
                ProjectId = projectId ?? string.Empty,
                SettingsRevision = settings.SettingsRevision,
                Mode = settings.Mode.ToString(),
                Language = settings.Language,
                MaxNavigationHistory = settings.MaxNavigationHistory,
                MaxRotationHistory = settings.MaxRotationHistory,
                RecordSceneObjects = settings.RecordSceneObjects,
                RecordAssets = settings.RecordAssets,
                AutoFocusUpdatedInspector = settings.AutoFocusUpdatedInspector,
                AutoCleanupMissingFavorites = settings.AutoCleanupMissingFavorites,
                ShowInspectorOverlays = settings.ShowInspectorOverlays,
                BlockedCategories = settings.BlockedCategories.Select(c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Favorites = new List<FavoriteEntryDto>()
            };

            if (favorites != null)
            {
                for (int i = 0; i < favorites.Count; i++)
                {
                    var fav = favorites[i];
                    // Section 7: Unsaved temporary objects are never promised across restarts.
                    if (fav.IsTemporary && fav.PersistentId.IsEmpty)
                    {
                        continue;
                    }

                    dto.Favorites.Add(new FavoriteEntryDto
                    {
                        FavoriteId = fav.FavoriteId,
                        Kind = fav.PersistentId.Kind.ToString(),
                        AssetGuid = fav.PersistentId.AssetGuid,
                        LocalFileId = fav.PersistentId.LocalFileId,
                        GlobalObjectIdString = fav.PersistentId.GlobalObjectIdString,
                        SceneGuid = fav.PersistentId.SceneGuid,
                        DisplayNameCache = fav.DisplayNameCache,
                        TypeNameCache = fav.TypeNameCache,
                        SortOrder = i
                    });
                }
            }

            return dto;
        }

        public ManagerSettings ToDomainSettings()
        {
            RotationMode parsedMode = Enum.TryParse(Mode, true, out RotationMode m) ? m : RotationMode.History;
            var blocked = new List<ObjectCategory>();
            if (BlockedCategories != null)
            {
                for (int i = 0; i < BlockedCategories.Count; i++)
                {
                    if (Enum.TryParse(BlockedCategories[i], true, out ObjectCategory cat))
                    {
                        blocked.Add(cat);
                    }
                }
            }

            return new ManagerSettings(
                SettingsRevision <= 0 ? 1 : SettingsRevision,
                parsedMode,
                Language,
                MaxNavigationHistory,
                MaxRotationHistory,
                RecordSceneObjects,
                RecordAssets,
                AutoFocusUpdatedInspector,
                AutoCleanupMissingFavorites,
                ShowInspectorOverlays,
                blocked);
        }

        public IReadOnlyList<FavoriteEntry> ToDomainFavorites()
        {
            if (Favorites == null || Favorites.Count == 0)
            {
                return Array.Empty<FavoriteEntry>();
            }

            var list = new List<FavoriteEntry>(Favorites.Count);
            foreach (var f in Favorites.OrderBy(x => x.SortOrder))
            {
                PersistentObjectKind kind = Enum.TryParse(f.Kind, true, out PersistentObjectKind k) ? k : PersistentObjectKind.None;
                var pid = new PersistentObjectId(kind, f.AssetGuid, f.LocalFileId, f.GlobalObjectIdString, f.SceneGuid);
                list.Add(new FavoriteEntry(
                    string.IsNullOrEmpty(f.FavoriteId) ? Guid.NewGuid().ToString("N") : f.FavoriteId,
                    pid,
                    f.DisplayNameCache,
                    f.TypeNameCache,
                    list.Count,
                    isTemporary: kind == PersistentObjectKind.Temporary,
                    temporaryRuntimeKey: null,
                    resolutionState: FavoriteResolutionState.ResolutionFailed,
                    resolutionDetail: "Pending resolution"));
            }
            return list;
        }

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["projectId"] = ProjectId ?? string.Empty,
                ["settingsRevision"] = SettingsRevision,
                ["mode"] = Mode ?? "History",
                ["language"] = Language ?? "ja",
                ["maxNavigationHistory"] = MaxNavigationHistory,
                ["maxRotationHistory"] = MaxRotationHistory,
                ["recordSceneObjects"] = RecordSceneObjects,
                ["recordAssets"] = RecordAssets,
                ["autoFocusUpdatedInspector"] = AutoFocusUpdatedInspector,
                ["autoCleanupMissingFavorites"] = AutoCleanupMissingFavorites,
                ["showInspectorOverlays"] = ShowInspectorOverlays,
                ["blockedCategories"] = BlockedCategories ?? new List<string>(),
                ["favorites"] = (Favorites ?? new List<FavoriteEntryDto>()).Select(f => f.ToJsonDictionary()).ToList()
            };
        }

        public static SettingsAndFavoritesDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            var dto = new SettingsAndFavoritesDto
            {
                ProjectId = SimpleJson.GetString(dict, "projectId"),
                SettingsRevision = SimpleJson.GetLong(dict, "settingsRevision", 1),
                Mode = SimpleJson.GetString(dict, "mode", "History"),
                Language = SimpleJson.GetString(dict, "language", "ja"),
                MaxNavigationHistory = SimpleJson.GetInt(dict, "maxNavigationHistory", 50),
                MaxRotationHistory = SimpleJson.GetInt(dict, "maxRotationHistory", 20),
                RecordSceneObjects = SimpleJson.GetBool(dict, "recordSceneObjects", true),
                RecordAssets = SimpleJson.GetBool(dict, "recordAssets", true),
                AutoFocusUpdatedInspector = SimpleJson.GetBool(dict, "autoFocusUpdatedInspector", false),
                AutoCleanupMissingFavorites = SimpleJson.GetBool(dict, "autoCleanupMissingFavorites", false),
                ShowInspectorOverlays = SimpleJson.GetBool(dict, "showInspectorOverlays", true),
                BlockedCategories = new List<string>(),
                Favorites = new List<FavoriteEntryDto>()
            };

            var cats = SimpleJson.GetList(dict, "blockedCategories");
            if (cats != null)
            {
                for (int i = 0; i < cats.Count; i++)
                {
                    if (cats[i] is string s && !string.IsNullOrEmpty(s))
                    {
                        dto.BlockedCategories.Add(s);
                    }
                }
            }

            var favs = SimpleJson.GetList(dict, "favorites");
            if (favs != null)
            {
                for (int i = 0; i < favs.Count; i++)
                {
                    if (favs[i] is Dictionary<string, object> fd)
                    {
                        dto.Favorites.Add(FavoriteEntryDto.FromJsonDictionary(fd));
                    }
                }
            }

            return dto;
        }
    }

    public sealed class FavoriteEntryDto
    {
        public string FavoriteId { get; set; } = string.Empty;
        public string Kind { get; set; } = "None";
        public string AssetGuid { get; set; } = string.Empty;
        public long LocalFileId { get; set; }
        public string GlobalObjectIdString { get; set; } = string.Empty;
        public string SceneGuid { get; set; } = string.Empty;
        public string DisplayNameCache { get; set; } = string.Empty;
        public string TypeNameCache { get; set; } = string.Empty;
        public int SortOrder { get; set; }

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["favoriteId"] = FavoriteId ?? string.Empty,
                ["kind"] = Kind ?? "None",
                ["assetGuid"] = AssetGuid ?? string.Empty,
                ["localFileId"] = LocalFileId,
                ["globalObjectIdString"] = GlobalObjectIdString ?? string.Empty,
                ["sceneGuid"] = SceneGuid ?? string.Empty,
                ["displayNameCache"] = DisplayNameCache ?? string.Empty,
                ["typeNameCache"] = TypeNameCache ?? string.Empty,
                ["sortOrder"] = SortOrder
            };
        }

        public static FavoriteEntryDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            return new FavoriteEntryDto
            {
                FavoriteId = SimpleJson.GetString(dict, "favoriteId"),
                Kind = SimpleJson.GetString(dict, "kind", "None"),
                AssetGuid = SimpleJson.GetString(dict, "assetGuid"),
                LocalFileId = SimpleJson.GetLong(dict, "localFileId", 0),
                GlobalObjectIdString = SimpleJson.GetString(dict, "globalObjectIdString"),
                SceneGuid = SimpleJson.GetString(dict, "sceneGuid"),
                DisplayNameCache = SimpleJson.GetString(dict, "displayNameCache"),
                TypeNameCache = SimpleJson.GetString(dict, "typeNameCache"),
                SortOrder = SimpleJson.GetInt(dict, "sortOrder", 0)
            };
        }
    }

    public sealed class RecoveryJournalDto
    {
        public string EditorSessionId { get; set; } = string.Empty;
        public string ManagementSessionId { get; set; } = string.Empty;
        public long Generation { get; set; }
        public List<LeaseJournalEntryDto> Leases { get; set; } = new List<LeaseJournalEntryDto>();

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["editorSessionId"] = EditorSessionId ?? string.Empty,
                ["managementSessionId"] = ManagementSessionId ?? string.Empty,
                ["generation"] = Generation,
                ["leases"] = (Leases ?? new List<LeaseJournalEntryDto>()).Select(l => l.ToJsonDictionary()).ToList()
            };
        }

        public static RecoveryJournalDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            var dto = new RecoveryJournalDto
            {
                EditorSessionId = SimpleJson.GetString(dict, "editorSessionId"),
                ManagementSessionId = SimpleJson.GetString(dict, "managementSessionId"),
                Generation = SimpleJson.GetLong(dict, "generation", 0),
                Leases = new List<LeaseJournalEntryDto>()
            };

            var list = SimpleJson.GetList(dict, "leases");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Dictionary<string, object> ld)
                    {
                        dto.Leases.Add(LeaseJournalEntryDto.FromJsonDictionary(ld));
                    }
                }
            }
            return dto;
        }
    }

    public sealed class LeaseJournalEntryDto
    {
        public string LeaseId { get; set; } = string.Empty;
        public string WindowKey { get; set; } = string.Empty;
        public string IdentityEvidence { get; set; } = string.Empty;
        public bool OriginalLocked { get; set; }
        public bool LastWrittenLocked { get; set; }
        public bool HasWritten { get; set; }
        public string RestorationStatus { get; set; } = "Acquired";

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["leaseId"] = LeaseId ?? string.Empty,
                ["windowKey"] = WindowKey ?? string.Empty,
                ["identityEvidence"] = IdentityEvidence ?? string.Empty,
                ["originalLocked"] = OriginalLocked,
                ["lastWrittenLocked"] = LastWrittenLocked,
                ["hasWritten"] = HasWritten,
                ["restorationStatus"] = RestorationStatus ?? "Acquired"
            };
        }

        public static LeaseJournalEntryDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            return new LeaseJournalEntryDto
            {
                LeaseId = SimpleJson.GetString(dict, "leaseId"),
                WindowKey = SimpleJson.GetString(dict, "windowKey"),
                IdentityEvidence = SimpleJson.GetString(dict, "identityEvidence"),
                OriginalLocked = SimpleJson.GetBool(dict, "originalLocked", false),
                LastWrittenLocked = SimpleJson.GetBool(dict, "lastWrittenLocked", false),
                HasWritten = SimpleJson.GetBool(dict, "hasWritten", false),
                RestorationStatus = SimpleJson.GetString(dict, "restorationStatus", "Acquired")
            };
        }

        public static LeaseJournalEntryDto FromDomain(LockLease lease)
        {
            return new LeaseJournalEntryDto
            {
                LeaseId = lease.LeaseId,
                WindowKey = lease.WindowKey.Value,
                IdentityEvidence = lease.IdentityEvidence,
                OriginalLocked = lease.OriginalLocked,
                LastWrittenLocked = lease.LastWrittenLocked,
                HasWritten = lease.HasWritten,
                RestorationStatus = lease.RestorationStatus.ToString()
            };
        }

        public LockLease ToDomain()
        {
            LeaseRestorationStatus st = Enum.TryParse(RestorationStatus, true, out LeaseRestorationStatus parsed)
                ? parsed
                : LeaseRestorationStatus.Acquired;

            return new LockLease(
                LeaseId,
                new WindowKey(WindowKey),
                IdentityEvidence,
                OriginalLocked,
                LastWrittenLocked,
                HasWritten,
                st,
                string.Empty);
        }
    }

    public sealed class SessionLayoutDto
    {
        public string EditorSessionId { get; set; } = string.Empty;
        public int NextFixedDisplayNumber { get; set; } = 1;
        public List<SessionWindowLayoutEntryDto> Windows { get; set; } = new List<SessionWindowLayoutEntryDto>();

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["editorSessionId"] = EditorSessionId ?? string.Empty,
                ["nextFixedDisplayNumber"] = NextFixedDisplayNumber,
                ["windows"] = (Windows ?? new List<SessionWindowLayoutEntryDto>()).Select(w => w.ToJsonDictionary()).ToList()
            };
        }

        public static SessionLayoutDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            var dto = new SessionLayoutDto
            {
                EditorSessionId = SimpleJson.GetString(dict, "editorSessionId"),
                NextFixedDisplayNumber = SimpleJson.GetInt(dict, "nextFixedDisplayNumber", 1),
                Windows = new List<SessionWindowLayoutEntryDto>()
            };

            var list = SimpleJson.GetList(dict, "windows");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Dictionary<string, object> wd)
                    {
                        dto.Windows.Add(SessionWindowLayoutEntryDto.FromJsonDictionary(wd));
                    }
                }
            }
            return dto;
        }
    }

    public sealed class SessionWindowLayoutEntryDto
    {
        public string WindowKey { get; set; } = string.Empty;
        public string IdentityEvidence { get; set; } = string.Empty;
        public int FixedDisplayNumber { get; set; }
        public int RoleOrder { get; set; }
        public bool IsManaged { get; set; }
        public bool IsExplicitlyExcluded { get; set; }
        public bool IsManuallyUnlocked { get; set; }

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["windowKey"] = WindowKey ?? string.Empty,
                ["identityEvidence"] = IdentityEvidence ?? string.Empty,
                ["fixedDisplayNumber"] = FixedDisplayNumber,
                ["roleOrder"] = RoleOrder,
                ["isManaged"] = IsManaged,
                ["isExplicitlyExcluded"] = IsExplicitlyExcluded,
                ["isManuallyUnlocked"] = IsManuallyUnlocked
            };
        }

        public static SessionWindowLayoutEntryDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            return new SessionWindowLayoutEntryDto
            {
                WindowKey = SimpleJson.GetString(dict, "windowKey"),
                IdentityEvidence = SimpleJson.GetString(dict, "identityEvidence"),
                FixedDisplayNumber = SimpleJson.GetInt(dict, "fixedDisplayNumber", 1),
                RoleOrder = SimpleJson.GetInt(dict, "roleOrder", 0),
                IsManaged = SimpleJson.GetBool(dict, "isManaged", true),
                IsExplicitlyExcluded = SimpleJson.GetBool(dict, "isExplicitlyExcluded", false),
                IsManuallyUnlocked = SimpleJson.GetBool(dict, "isManuallyUnlocked", false)
            };
        }
    }
}
