using System;
using System.IO;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using NUnit.Framework;

namespace DennokoWorks.InspectorManagerV2.Tests
{
    [TestFixture]
    public sealed class FaultInjectionAndRegressionTests
    {
        [Test]
        public void WriteFailsOnSecondOfThreeInspectors_CompensatesFirstInReverse_RestoresAllLeases_AndStopsRotation()
        {
            var (coord, adapter, sel, persist, _, _) = DomainLogicTests.CreateHarness(3, initialLocked: false);
            sel.RegisterObject("A");
            sel.RegisterObject("B");
            sel.RegisterObject("C");

            coord.EnqueueStartRotation();
            coord.Tick();

            DomainLogicTests.SelectAndTick(coord, sel, "A");
            DomainLogicTests.SelectAndTick(coord, sel, "B");
            Assert.AreEqual("B", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual("A", adapter.Get("w2").LockedTargets[0].Value);

            // Inject failure on 2nd window during next 3-window History update (C -> w1=C, w2=B [FAILS], w3=A)
            adapter.Get("w2").FailWriteTargets = true;
            DomainLogicTests.SelectAndTick(coord, sel, "C");

            // Must stop auto-rotation cleanly, restore all leases (originalLocked = false), and NOT commit C!
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "All leases must be restored to originalLocked=false.");
            Assert.IsFalse(adapter.Get("w2").IsLocked);
            Assert.IsFalse(adapter.Get("w3").IsLocked);
            Assert.IsTrue(coord.CurrentState.FindWindow(new WindowKey("w2")).IsFaultIsolated);
            Assert.IsNull(persist.StoredJournal, "Cleared recovery journal once all leases restored.");
        }

        [Test]
        public void LeaseRestoreFails_TransitionsToFaulted_AndRetryRestoreTransitionsToOffNeverRunning()
        {
            var (coord, adapter, sel, persist, _, _) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);

            // Inject lock write failure on w2 during Stop -> must transition to Faulted with incomplete restoration!
            adapter.Get("w2").FailWriteLock = true;
            coord.EnqueueStopRotation();
            coord.Tick();

            Assert.AreEqual(RuntimeLifecycleState.Faulted, coord.CurrentState.LifecycleState);
            Assert.IsTrue(coord.CurrentState.HasIncompleteRestorations);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "w1 should already be restored.");
            Assert.IsTrue(adapter.Get("w2").IsLocked, "w2 restore failed.");
            Assert.IsNotNull(persist.StoredJournal, "Journal must remain while restoration is incomplete.");

            // Clear fault and click Retry Restore -> transitions to Off (NEVER automatically back to Running!)
            adapter.Get("w2").FailWriteLock = false;
            coord.EnqueueRetryRestore();
            coord.Tick();

            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState, "RetryRestore must transition to Off, never Running.");
            Assert.IsFalse(coord.CurrentState.HasIncompleteRestorations);
            Assert.IsFalse(adapter.Get("w2").IsLocked);
            Assert.IsNull(persist.StoredJournal);
        }

        [Test]
        public void WindowClosedMidCompensation_SurvivingWindowsStillRestored_WithoutException()
        {
            var (coord, adapter, sel, _, _, _) = DomainLogicTests.CreateHarness(3, initialLocked: false);
            sel.RegisterObject("A");
            sel.RegisterObject("B");

            coord.EnqueueStartRotation();
            coord.Tick();
            DomainLogicTests.SelectAndTick(coord, sel, "A");

            // Close w1 right when w2 fails during multi-window write!
            adapter.Get("w2").FailWriteTargets = true;
            adapter.Get("w2").OnBeforeWrite = () => adapter.Get("w1").IsAlive = false;

            Assert.DoesNotThrow(() => DomainLogicTests.SelectAndTick(coord, sel, "B"));
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(adapter.Get("w2").IsLocked);
            Assert.IsFalse(adapter.Get("w3").IsLocked);
        }

        [Test]
        public void ReadOnlyJournalStorage_PreventsLeaseAcquisition_AndDoesNotMutateInspectorLocks()
        {
            var (coord, adapter, _, persist, _, _) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            persist.FailJournalSaves = true;

            coord.EnqueueStartRotation();
            coord.Tick();

            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.AreEqual(0, coord.CurrentState.Leases.Count);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "Must NOT lock any Inspector if recovery journal save fails.");
            Assert.IsFalse(adapter.Get("w2").IsLocked);
        }

        [Test]
        public void StopBeforePendingVerificationCompletes_DiscardsStaleDelayedVerification()
        {
            var (coord, adapter, sel, _, _, clock) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            sel.RegisterObject("A");
            coord.EnqueueStartRotation();
            coord.Tick();

            adapter.Get("w1").ReturnPendingOnWrite = true;
            adapter.Get("w1").DeferredVerifyStatus = WindowWriteStatus.PresentationPending;

            DomainLogicTests.SelectAndTick(coord, sel, "A");
            Assert.IsTrue(coord.GetUiSnapshot().HasPendingVerification);
            Assert.AreEqual(0, coord.CurrentState.RotationHistory.Entries.Count, "Must not commit before verification completes.");

            // User stops before delayed verification finishes!
            coord.EnqueueStopRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(coord.GetUiSnapshot().HasPendingVerification);

            // Even if delayed ticks arrive later, must NOT re-lock or advance state (Section 14.5 #5)!
            adapter.Get("w1").DeferredVerifyStatus = WindowWriteStatus.Verified;
            clock.Advance(400);
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(adapter.Get("w1").IsLocked);
        }

        [Test]
        public void UnknownLockDrop_FromTrackerRebuild_RecoversLockWithoutMarkingManualUnlock()
        {
            var (coord, adapter, sel, _, _, clock) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            sel.RegisterObject("A");
            coord.EnqueueStartRotation();
            coord.Tick();
            DomainLogicTests.SelectAndTick(coord, sel, "A");

            // Simulate Unity tracker recreation dropping lock without any Manager/Shortcut command (Section 14.5 #2)
            adapter.Get("w1").IsLocked = false;
            adapter.Get("w1").LockedTargets.Clear();

            clock.Advance(400);
            coord.Tick();

            var w1State = coord.CurrentState.FindWindow(new WindowKey("w1"));
            Assert.IsFalse(w1State.IsManuallyUnlocked, "Tracker rebuild lock drop must NOT be treated as manual unlock.");
            Assert.IsTrue(w1State.CanParticipate);
            Assert.IsTrue(adapter.Get("w1").IsLocked, "Coordinator must re-lock the window.");
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value, "Coordinator must restore confirmed assignment.");
        }

        [Test]
        public void InitialLockedDisplayDiffersFromGlobalSelection_CacheDoesNotSkipRequiredWrite()
        {
            // Section 14.5 #6: Inspector #1 is already locked to X while global Selection is Y.
            var (coord, adapter, sel, _, _, _) = DomainLogicTests.CreateHarness(1, initialLocked: false);
            sel.RegisterObject("X");
            sel.RegisterObject("Y");

            var w1 = adapter.Get("w1");
            w1.IsLocked = true;
            w1.LockedTargets.Add(new ObjectRefKey("X"));

            sel.CurrentSelection = sel.BuildSnapshot("Y");
            coord.OnSelectionChangedImmediate();
            coord.Tick();

            int writesBeforeStart = w1.WriteTargetCallCount;
            coord.EnqueueStartRotation();
            coord.Tick();

            Assert.Greater(w1.WriteTargetCallCount, writesBeforeStart, "Must NOT skip writing Y because initial cache must come from actual locked target X, not Selection Y.");
            Assert.AreEqual("Y", w1.LockedTargets[0].Value);
        }

        [Test]
        public void QueueOverflow_StopsAutoRotationAndNotifiesUser()
        {
            var clock = new FakeClock();
            var logger = new FakeLogger();
            var sel = new FakeSelectionAdapter();
            var adapter = new FakeInspectorAdapter { SelectionBacking = sel };
            adapter.AddFakeWindow("w1", false);
            sel.RegisterObject("A");
            sel.RegisterObject("B");

            var coord = new InspectorManagerCoordinator(
                "proj", adapter, sel, new FakeFavoriteResolver(sel), new FakePersistenceStore(),
                new FakeSessionLayoutStore(), clock, logger, maxQueueSize: 4, commandsPerTickBudget: 1);
            coord.Initialize();

            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);

            // Flood queue beyond maxQueueSize=4 without ticking
            for (int i = 0; i < 6; i++)
            {
                coord.EnqueueSelectionSnapshot(sel.BuildSnapshot(i % 2 == 0 ? "A" : "B"));
            }

            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState, "Queue overflow must stop auto-rotation safely.");
            Assert.IsFalse(adapter.Get("w1").IsLocked, "Queue overflow stop must restore locks.");
            StringAssert.Contains("Queue", coord.CurrentState.StatusNotice);
        }

        [Test]
        public void SettingsReset_AtomicallyAppliesSingleSettingsRevisionAcrossAllOptions()
        {
            // Section 14.5 #10
            var (coord, _, _, _, _, _) = DomainLogicTests.CreateHarness(2);
            var modified = coord.CurrentState.Settings.WithNextRevision(
                mode: RotationMode.Cycle,
                language: "en",
                maxNavigationHistory: 120,
                maxRotationHistory: 40,
                recordSceneObjects: false,
                recordAssets: false,
                blockedCategories: new[] { ObjectCategory.Folder, ObjectCategory.Material });

            coord.EnqueueUpdateSettings(modified, true);
            coord.Tick();
            long revBeforeReset = coord.CurrentState.Settings.SettingsRevision;

            coord.EnqueueResetSettings();
            coord.Tick();

            var s = coord.CurrentState.Settings;
            Assert.AreEqual(revBeforeReset + 1, s.SettingsRevision);
            Assert.AreEqual(RotationMode.History, s.Mode);
            Assert.AreEqual("ja", s.Language);
            Assert.AreEqual(50, s.MaxNavigationHistory);
            Assert.IsTrue(s.RecordSceneObjects);
            Assert.IsTrue(s.RecordAssets);
            Assert.AreEqual(0, s.BlockedCategories.Count);
        }

        [Test]
        public void ReloadBoundary_PreservesLayout_RestoresLeases_DoesNotAutoRestart_AndDoesNotRollbackExplicitModeChange()
        {
            // Section 9 & Section 14.5 #3
            var (coord, adapter, sel, persist, session, clock) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            coord.EnqueueReorderWindow(new WindowKey("w2"), 0);
            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.IsTrue(adapter.Get("w1").IsLocked);
            Assert.IsTrue(adapter.Get("w2").IsLocked);

            // Simulate Assembly Reload boundary
            coord.HandleLifecycleBoundaryStop(StopReason.AssemblyReload, suspendMode: true);
            Assert.IsFalse(adapter.Get("w1").IsLocked);
            Assert.IsFalse(adapter.Get("w2").IsLocked);

            // Simulate post-reload new coordinator in same Editor session
            var coordAfter = new InspectorManagerCoordinator(
                "test-proj", adapter, sel, new FakeFavoriteResolver(sel), persist, session, clock, new FakeLogger(),
                reloadGraceMs: 1000, reconcileIntervalMs: 100);
            coordAfter.Initialize();

            Assert.AreEqual(RuntimeLifecycleState.Off, coordAfter.CurrentState.LifecycleState, "Must remain Off after Assembly Reload.");
            // Layout preserved (#2 still first in role order)
            var ordered = coordAfter.CurrentState.GetManagedAliveWindowsOrdered();
            Assert.AreEqual(2, ordered[0].FixedDisplayNumber);

            // User explicitly switches to Cycle mode while reload grace is active -> subsequent reconciliation MUST NOT roll it back!
            coordAfter.EnqueueUpdateSettings(coordAfter.CurrentState.Settings.WithNextRevision(mode: RotationMode.Cycle), true);
            coordAfter.Tick();
            clock.Advance(1200);
            coordAfter.Tick();

            Assert.AreEqual(RotationMode.Cycle, coordAfter.CurrentState.Settings.Mode);
            Assert.AreEqual(2, coordAfter.CurrentState.GetManagedAliveWindowsOrdered()[0].FixedDisplayNumber);
        }

        [Test]
        public void CrossEditorRestart_RecoveryJournalDoesNotModifyLocksByTemporaryId()
        {
            var clock = new FakeClock();
            var sel = new FakeSelectionAdapter();
            var adapter = new FakeInspectorAdapter { SelectionBacking = sel };
            adapter.AddFakeWindow("w1", locked: true); // Pretend user locked w1 manually in new Editor launch

            var persist = new FakePersistenceStore
            {
                StoredJournal = new RecoveryJournalDto
                {
                    EditorSessionId = "OLD-EDITOR-SESSION",
                    ManagementSessionId = "mgmt-1",
                    Generation = 5,
                    Leases =
                    {
                        new LeaseJournalEntryDto
                        {
                            LeaseId = "l1",
                            WindowKey = "w1",
                            IdentityEvidence = "OLD-EDITOR-SESSION|InspectorWindow|w1",
                            OriginalLocked = false,
                            RestorationStatus = "Acquired"
                        }
                    }
                }
            };
            var session = new FakeSessionLayoutStore { EditorSessionId = "NEW-EDITOR-SESSION" };

            var coord = new InspectorManagerCoordinator("proj", adapter, sel, new FakeFavoriteResolver(sel), persist, session, clock, new FakeLogger());
            coord.Initialize();

            Assert.IsTrue(adapter.Get("w1").IsLocked, "MUST NOT overwrite Inspector lock across Editor restarts using temporary runtime IDs.");
            StringAssert.Contains("manually", coord.CurrentState.StatusNotice);
        }

        [Test]
        public void AtomicJsonFileStore_HandlesBackupFallback_CorruptionQuarantine_NewerSchemaBlock_AndReadYourOwnWrites()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "imv2_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                // 1. Atomic write + read-your-own-writes before flush
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var dto1 = new SettingsAndFavoritesDto { ProjectId = "proj-1", Language = "en", SettingsRevision = 2 };
                    Assert.IsTrue(store.QueueSaveSettingsAndFavorites(dto1, 2).Succeeded);
                    var immediate = store.LoadSettingsAndFavorites();
                    Assert.AreEqual("en", immediate.Value.Language, "Unflushed queued write must be immediately visible to Load.");
                    Assert.IsTrue(store.FlushSettingsAndFavorites().Succeeded);

                    // Second flush creates backup of revision 2
                    var dto2 = new SettingsAndFavoritesDto { ProjectId = "proj-1", Language = "ja", MaxNavigationHistory = 77, SettingsRevision = 3 };
                    store.QueueSaveSettingsAndFavorites(dto2, 3);
                    Assert.IsTrue(store.FlushSettingsAndFavorites().Succeeded);

                    // Stale revision 1 must not overwrite revision 3
                    var stale = new SettingsAndFavoritesDto { ProjectId = "proj-1", Language = "en", MaxNavigationHistory = 11, SettingsRevision = 1 };
                    store.QueueSaveSettingsAndFavorites(stale, 1);
                    store.FlushSettingsAndFavorites();
                    Assert.AreEqual(77, store.LoadSettingsAndFavorites().Value.MaxNavigationHistory);
                }

                // 2. Corrupt primary -> falls back to backup (revision 2, Language="en")
                string primaryPath = Path.Combine(tempDir, "settings_v1.json");
                File.WriteAllText(primaryPath, "{corrupted-json!!!");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettingsAndFavorites();
                    Assert.IsTrue(loaded.Succeeded);
                    Assert.IsTrue(loaded.UsedBackup);
                    Assert.AreEqual("en", loaded.Value.Language);
                }

                // 3. Corrupt BOTH primary and backup -> quarantines both & falls back to default
                string backupPath = Path.Combine(tempDir, "settings_v1.json.bak");
                File.WriteAllText(backupPath, "{also-corrupted");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettingsAndFavorites();
                    Assert.IsFalse(loaded.Succeeded);
                    Assert.IsTrue(loaded.FellBackToDefault);
                    Assert.IsTrue(Directory.GetFiles(tempDir, "*.corrupt.*").Length >= 2, "Corrupt files must be quarantined.");
                }

                // 4. Newer schema version -> refuses to overwrite & blocks writes
                File.WriteAllText(primaryPath, "{\"schemaVersion\":99,\"revision\":1,\"checksum\":\"abc\",\"payload\":{}}");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettingsAndFavorites();
                    Assert.IsTrue(loaded.NewerSchemaDetected);
                    Assert.IsTrue(store.IsWriteBlockedByNewerSchema);
                    var saveAttempt = store.QueueSaveSettingsAndFavorites(new SettingsAndFavoritesDto(), 10);
                    Assert.IsFalse(saveAttempt.Succeeded);
                    Assert.IsTrue(saveAttempt.BlockedByNewerSchema);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
    }
}
