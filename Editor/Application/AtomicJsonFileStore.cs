using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DennokoWorks.InspectorManagerV2.Application
{
    /// <summary>
    /// Atomic, schema-versioned, SHA-256 checksummed persistence store (Section 10 & 14.4).
    /// Supports coalesced settings/favorites writes with immediate read-your-own-writes,
    /// backup fallback, corruption quarantine, newer-schema write blocking,
    /// and a separate immediate synchronous path for LockLease recovery journals.
    /// </summary>
    public sealed class AtomicJsonFileStore : IPersistenceStore
    {
        private readonly string _directoryPath;
        private readonly string _settingsPath;
        private readonly string _settingsBackupPath;
        private readonly string _recoveryPath;
        private readonly string _recoveryBackupPath;
        private readonly string _projectId;
        private readonly object _ioLock = new object();

        private SettingsAndFavoritesDto _pendingSettings;
        private long _pendingRevision;
        private long _lastCommittedRevision;
        private bool _hasPendingSettings;
        private bool _isWriteBlockedByNewerSchema;
        private bool _disposed;

        public bool IsWriteBlockedByNewerSchema => _isWriteBlockedByNewerSchema;

        public AtomicJsonFileStore(string directoryPath, string projectId)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException("Directory path is required.", nameof(directoryPath));
            }

            _directoryPath = directoryPath;
            _projectId = projectId ?? string.Empty;
            _settingsPath = Path.Combine(directoryPath, "settings_v1.json");
            _settingsBackupPath = Path.Combine(directoryPath, "settings_v1.json.bak");
            _recoveryPath = Path.Combine(directoryPath, "recovery_journal_v1.json");
            _recoveryBackupPath = Path.Combine(directoryPath, "recovery_journal_v1.json.bak");
        }

        public LoadResult<SettingsAndFavoritesDto> LoadSettingsAndFavorites()
        {
            lock (_ioLock)
            {
                // Section 14.4: Unflushed values immediately reflected in Load.
                if (_hasPendingSettings && _pendingSettings != null)
                {
                    return new LoadResult<SettingsAndFavoritesDto>(true, _pendingSettings);
                }

                return LoadEnvelope(
                    _settingsPath,
                    _settingsBackupPath,
                    PersistenceSchema.CurrentSettingsSchemaVersion,
                    SettingsAndFavoritesDto.FromJsonDictionary,
                    () => new SettingsAndFavoritesDto { ProjectId = _projectId },
                    trackNewerSchemaBlock: true);
            }
        }

        public SaveResult QueueSaveSettingsAndFavorites(SettingsAndFavoritesDto dto, long revision)
        {
            if (dto == null) return SaveResult.Fail("DTO is null.");

            lock (_ioLock)
            {
                if (_isWriteBlockedByNewerSchema)
                {
                    return new SaveResult(false, true, "Save blocked: stored file uses a newer schema version.");
                }

                if (revision < _pendingRevision || revision < _lastCommittedRevision)
                {
                    // Section 10: Prevent stale older save from overwriting newer state.
                    return SaveResult.Ok();
                }

                _pendingSettings = dto;
                _pendingRevision = revision;
                _hasPendingSettings = true;
                return SaveResult.Ok();
            }
        }

        public SaveResult FlushSettingsAndFavorites()
        {
            lock (_ioLock)
            {
                if (_isWriteBlockedByNewerSchema)
                {
                    return new SaveResult(false, true, "Save blocked: stored file uses a newer schema version.");
                }

                if (!_hasPendingSettings || _pendingSettings == null)
                {
                    return SaveResult.Ok();
                }

                var result = WriteEnvelopeAtomic(
                    _settingsPath,
                    _settingsBackupPath,
                    PersistenceSchema.CurrentSettingsSchemaVersion,
                    _pendingRevision,
                    _pendingSettings.ToJsonDictionary());

                if (result.Succeeded)
                {
                    _lastCommittedRevision = _pendingRevision;
                    _hasPendingSettings = false;
                }

                return result;
            }
        }

        public LoadResult<RecoveryJournalDto> LoadRecoveryJournal()
        {
            lock (_ioLock)
            {
                if (!File.Exists(_recoveryPath) && !File.Exists(_recoveryBackupPath))
                {
                    return new LoadResult<RecoveryJournalDto>(true, null);
                }

                return LoadEnvelope(
                    _recoveryPath,
                    _recoveryBackupPath,
                    PersistenceSchema.CurrentRecoverySchemaVersion,
                    RecoveryJournalDto.FromJsonDictionary,
                    () => null,
                    trackNewerSchemaBlock: false);
            }
        }

        public SaveResult SaveRecoveryJournalImmediate(RecoveryJournalDto dto)
        {
            if (dto == null) return SaveResult.Fail("Recovery journal DTO is null.");

            lock (_ioLock)
            {
                // Section 10 & 14.4: Always written immediately and synchronously BEFORE acquiring any LockLease.
                return WriteEnvelopeAtomic(
                    _recoveryPath,
                    _recoveryBackupPath,
                    PersistenceSchema.CurrentRecoverySchemaVersion,
                    dto.Generation,
                    dto.ToJsonDictionary());
            }
        }

        public void ClearRecoveryJournal()
        {
            lock (_ioLock)
            {
                TryDeleteFile(_recoveryPath);
                TryDeleteFile(_recoveryBackupPath);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                FlushSettingsAndFavorites();
            }
            catch
            {
                // Best-effort flush on dispose.
            }
        }

        private LoadResult<T> LoadEnvelope<T>(
            string primaryPath,
            string backupPath,
            int currentSchemaVersion,
            Func<Dictionary<string, object>, T> mapper,
            Func<T> defaultFactory,
            bool trackNewerSchemaBlock)
        {
            bool primaryExists = File.Exists(primaryPath);
            bool backupExists = File.Exists(backupPath);

            if (!primaryExists && !backupExists)
            {
                return new LoadResult<T>(true, defaultFactory());
            }

            if (primaryExists)
            {
                var primaryRead = TryReadAndValidate(primaryPath, currentSchemaVersion, mapper);
                if (primaryRead.NewerSchema)
                {
                    if (trackNewerSchemaBlock)
                    {
                        _isWriteBlockedByNewerSchema = true;
                    }
                    return new LoadResult<T>(
                        succeeded: false,
                        value: defaultFactory(),
                        fellBackToDefault: true,
                        newerSchemaDetected: true,
                        notice: "Stored file uses newer schema version " + primaryRead.FoundSchemaVersion + "; saving paused to avoid data loss.");
                }

                if (primaryRead.Succeeded)
                {
                    if (trackNewerSchemaBlock)
                    {
                        _lastCommittedRevision = Math.Max(_lastCommittedRevision, primaryRead.Revision);
                    }
                    return new LoadResult<T>(true, primaryRead.Value);
                }
            }

            if (backupExists)
            {
                var backupRead = TryReadAndValidate(backupPath, currentSchemaVersion, mapper);
                if (backupRead.NewerSchema)
                {
                    if (trackNewerSchemaBlock)
                    {
                        _isWriteBlockedByNewerSchema = true;
                    }
                    return new LoadResult<T>(
                        succeeded: false,
                        value: defaultFactory(),
                        fellBackToDefault: true,
                        newerSchemaDetected: true,
                        notice: "Backup file uses newer schema version " + backupRead.FoundSchemaVersion + "; saving paused.");
                }

                if (backupRead.Succeeded)
                {
                    if (trackNewerSchemaBlock)
                    {
                        _lastCommittedRevision = Math.Max(_lastCommittedRevision, backupRead.Revision);
                    }
                    return new LoadResult<T>(
                        succeeded: true,
                        value: backupRead.Value,
                        usedBackup: true,
                        notice: "Primary storage file was corrupted; restored from backup.");
                }
            }

            // Section 10: Both primary and backup failed -> quarantine corrupt files and fall back to defaults.
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            if (primaryExists) TryQuarantineFile(primaryPath, stamp);
            if (backupExists) TryQuarantineFile(backupPath, stamp);

            return new LoadResult<T>(
                succeeded: false,
                value: defaultFactory(),
                fellBackToDefault: true,
                notice: "Storage files were corrupted and quarantined; defaults loaded.");
        }

        private SaveResult WriteEnvelopeAtomic(
            string primaryPath,
            string backupPath,
            int schemaVersion,
            long revision,
            Dictionary<string, object> payloadDict)
        {
            string tmpPath = primaryPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(_directoryPath);

                string payloadJson = SimpleJson.Serialize(payloadDict);
                string checksum = ComputeSha256Hex(payloadJson);

                var envelope = new Dictionary<string, object>
                {
                    ["schemaVersion"] = schemaVersion,
                    ["revision"] = revision,
                    ["checksum"] = checksum,
                    ["payload"] = payloadDict
                };

                string serialized = SimpleJson.Serialize(envelope);

                // Step 1: Write temporary file on the same filesystem.
                File.WriteAllText(tmpPath, serialized, new UTF8Encoding(false));

                // Step 2: Read-back & checksum verification before touching primary or backup.
                string verifyContent = File.ReadAllText(tmpPath, Encoding.UTF8);
                var verifyObj = SimpleJson.DeserializeObject(verifyContent);
                var verifyPayload = SimpleJson.GetObject(verifyObj, "payload");
                string verifyChecksum = SimpleJson.GetString(verifyObj, "checksum");
                if (verifyPayload == null || !string.Equals(ComputeSha256Hex(SimpleJson.Serialize(verifyPayload)), verifyChecksum, StringComparison.Ordinal))
                {
                    throw new IOException("Read-back verification failed for temporary persistence file.");
                }

                // Step 3: Preserve previous valid primary as backup and atomically replace.
                if (File.Exists(primaryPath))
                {
                    try
                    {
                        File.Replace(tmpPath, primaryPath, backupPath, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(primaryPath, backupPath, overwrite: true);
                        File.Copy(tmpPath, primaryPath, overwrite: true);
                        TryDeleteFile(tmpPath);
                    }
                    catch (IOException)
                    {
                        File.Copy(primaryPath, backupPath, overwrite: true);
                        File.Copy(tmpPath, primaryPath, overwrite: true);
                        TryDeleteFile(tmpPath);
                    }
                }
                else
                {
                    File.Move(tmpPath, primaryPath);
                }

                return SaveResult.Ok();
            }
            catch (Exception ex)
            {
                TryDeleteFile(tmpPath);
                return SaveResult.Fail("Persistence write failed: " + ex.Message);
            }
        }

        private readonly struct EnvelopeReadAttempt<T>
        {
            public bool Succeeded { get; }
            public bool NewerSchema { get; }
            public int FoundSchemaVersion { get; }
            public long Revision { get; }
            public T Value { get; }

            public EnvelopeReadAttempt(bool succeeded, bool newerSchema, int foundSchemaVersion, long revision, T value)
            {
                Succeeded = succeeded;
                NewerSchema = newerSchema;
                FoundSchemaVersion = foundSchemaVersion;
                Revision = revision;
                Value = value;
            }
        }

        private static EnvelopeReadAttempt<T> TryReadAndValidate<T>(
            string path,
            int currentSchemaVersion,
            Func<Dictionary<string, object>, T> mapper)
        {
            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                var root = SimpleJson.DeserializeObject(raw);
                int schemaVersion = SimpleJson.GetInt(root, "schemaVersion", -1);
                if (schemaVersion <= 0)
                {
                    return new EnvelopeReadAttempt<T>(false, false, schemaVersion, 0, default);
                }

                if (schemaVersion > currentSchemaVersion)
                {
                    return new EnvelopeReadAttempt<T>(false, true, schemaVersion, 0, default);
                }

                long revision = SimpleJson.GetLong(root, "revision", 0);
                string expectedChecksum = SimpleJson.GetString(root, "checksum");
                var payload = SimpleJson.GetObject(root, "payload");
                if (payload == null || string.IsNullOrEmpty(expectedChecksum))
                {
                    return new EnvelopeReadAttempt<T>(false, false, schemaVersion, 0, default);
                }

                string actualChecksum = ComputeSha256Hex(SimpleJson.Serialize(payload));
                if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    return new EnvelopeReadAttempt<T>(false, false, schemaVersion, 0, default);
                }

                T mapped = mapper(payload);
                return new EnvelopeReadAttempt<T>(true, false, schemaVersion, revision, mapped);
            }
            catch
            {
                return new EnvelopeReadAttempt<T>(false, false, -1, 0, default);
            }
        }

        private static string ComputeSha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        private static void TryQuarantineFile(string path, string stamp)
        {
            try
            {
                if (!File.Exists(path)) return;
                string dest = path + ".corrupt." + stamp;
                File.Move(path, dest);
            }
            catch
            {
                // Ignore quarantine error if filesystem is read-only.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignore cleanup error.
            }
        }
    }
}
