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

    public sealed class SettingsDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public long SettingsRevision { get; set; } = 1;
        public string Language { get; set; } = "ja";
        public int MaxRotationHistory { get; set; } = 20;
        public bool RecordSceneObjects { get; set; } = true;
        public bool RecordAssets { get; set; } = true;
        public bool ShowInspectorOverlays { get; set; } = true;
        public List<string> BlockedCategories { get; set; } = new List<string>();

        public static SettingsDto FromDomain(string projectId, ManagerSettings settings)
        {
            return new SettingsDto
            {
                ProjectId = projectId ?? string.Empty,
                SettingsRevision = settings.SettingsRevision,
                Language = settings.Language,
                MaxRotationHistory = settings.MaxRotationHistory,
                RecordSceneObjects = settings.RecordSceneObjects,
                RecordAssets = settings.RecordAssets,
                ShowInspectorOverlays = settings.ShowInspectorOverlays,
                BlockedCategories = settings.BlockedCategories.Select(c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList()
            };
        }

        public ManagerSettings ToDomainSettings()
        {
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
                Language,
                MaxRotationHistory,
                RecordSceneObjects,
                RecordAssets,
                ShowInspectorOverlays,
                blocked);
        }

        public Dictionary<string, object> ToJsonDictionary()
        {
            return new Dictionary<string, object>
            {
                ["projectId"] = ProjectId ?? string.Empty,
                ["settingsRevision"] = SettingsRevision,
                ["language"] = Language ?? "ja",
                ["maxRotationHistory"] = MaxRotationHistory,
                ["recordSceneObjects"] = RecordSceneObjects,
                ["recordAssets"] = RecordAssets,
                ["showInspectorOverlays"] = ShowInspectorOverlays,
                ["blockedCategories"] = BlockedCategories ?? new List<string>()
            };
        }

        public static SettingsDto FromJsonDictionary(Dictionary<string, object> dict)
        {
            var dto = new SettingsDto
            {
                ProjectId = SimpleJson.GetString(dict, "projectId"),
                SettingsRevision = SimpleJson.GetLong(dict, "settingsRevision", 1),
                Language = SimpleJson.GetString(dict, "language", "ja"),
                MaxRotationHistory = SimpleJson.GetInt(dict, "maxRotationHistory", 20),
                RecordSceneObjects = SimpleJson.GetBool(dict, "recordSceneObjects", true),
                RecordAssets = SimpleJson.GetBool(dict, "recordAssets", true),
                ShowInspectorOverlays = SimpleJson.GetBool(dict, "showInspectorOverlays", true),
                BlockedCategories = new List<string>()
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

            return dto;
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
