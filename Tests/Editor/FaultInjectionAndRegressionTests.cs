using System;
using System.IO;
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

            adapter.Get("w2").FailWriteTargets = true;
            DomainLogicTests.SelectAndTick(coord, sel, "C");

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
            var (coord, adapter, _, persist, _, _) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);

            adapter.Get("w2").FailWriteLock = true;
            coord.EnqueueStopRotation();
            coord.Tick();

            Assert.AreEqual(RuntimeLifecycleState.Faulted, coord.CurrentState.LifecycleState);
            Assert.IsTrue(coord.CurrentState.HasIncompleteRestorations);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "w1 should already be restored.");
            Assert.IsTrue(adapter.Get("w2").IsLocked, "w2 restore failed.");
            Assert.IsNotNull(persist.StoredJournal, "Journal must remain while restoration is incomplete.");

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

            coord.EnqueueStopRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(coord.GetUiSnapshot().HasPendingVerification);

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
        public void StandardPadlockUnlock_WhileInspectorFocused_IsManualUnlock_NotRelocked_AndPadlockRelockRejoins()
        {
            var (coord, adapter, sel, _, _, clock) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            sel.RegisterObject("A");
            coord.EnqueueStartRotation();
            coord.Tick();
            DomainLogicTests.SelectAndTick(coord, sel, "A");
            var key = new WindowKey("w1");

            adapter.FocusedKey = key;
            adapter.Get("w1").IsLocked = false;
            clock.Advance(400);
            coord.Tick();

            var w1 = coord.CurrentState.FindWindow(key);
            Assert.IsTrue(w1.IsManuallyUnlocked, "Unlock while focused must be treated as manual unlock.");
            Assert.IsFalse(w1.CanParticipate);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "Coordinator must NOT re-lock a manual padlock unlock.");

            adapter.FocusedKey = null;
            clock.Advance(400);
            coord.Tick();
            Assert.IsFalse(adapter.Get("w1").IsLocked);

            adapter.Get("w1").IsLocked = true;
            clock.Advance(400);
            coord.Tick();
            w1 = coord.CurrentState.FindWindow(key);
            Assert.IsFalse(w1.IsManuallyUnlocked, "Standard UI re-lock must clear manual unlock.");
            Assert.IsTrue(w1.CanParticipate);
        }

        [Test]
        public void UnreadableLockState_IsNotTreatedAsLockDrop()
        {
            var (coord, adapter, sel, _, _, clock) = DomainLogicTests.CreateHarness(1, initialLocked: false);
            sel.RegisterObject("A");
            coord.EnqueueStartRotation();
            coord.Tick();
            DomainLogicTests.SelectAndTick(coord, sel, "A");

            adapter.Get("w1").IsLocked = false;
            adapter.Get("w1").FailRead = true;
            clock.Advance(400);
            coord.Tick();

            Assert.IsFalse(adapter.Get("w1").IsLocked, "No recovery write may be issued for an unreadable lock state.");
            Assert.IsFalse(coord.CurrentState.FindWindow(new WindowKey("w1")).IsManuallyUnlocked);
        }

        [Test]
        public void FaultIsolatedWindow_RejoinsRotation_OnNextStart()
        {
            var (coord, adapter, sel, _, _, _) = DomainLogicTests.CreateHarness(3, initialLocked: false);
            sel.RegisterObject("A");
            sel.RegisterObject("B");
            sel.RegisterObject("C");
            coord.EnqueueStartRotation();
            coord.Tick();
            DomainLogicTests.SelectAndTick(coord, sel, "A");
            DomainLogicTests.SelectAndTick(coord, sel, "B");

            adapter.Get("w2").FailWriteTargets = true;
            DomainLogicTests.SelectAndTick(coord, sel, "C");
            Assert.IsTrue(coord.CurrentState.FindWindow(new WindowKey("w2")).IsFaultIsolated);

            adapter.Get("w2").FailWriteTargets = false;
            coord.EnqueueStartRotation();
            coord.Tick();

            var w2 = coord.CurrentState.FindWindow(new WindowKey("w2"));
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);
            Assert.IsFalse(w2.IsFaultIsolated, "A new session must clear fault isolation from the previous session.");
            Assert.IsTrue(w2.CanParticipate);
        }

        [Test]
        public void ManualUnlockFromPreviousSession_IsClearedOnNextStart()
        {
            var (coord, adapter, _, _, _, _) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            var key = new WindowKey("w1");
            coord.EnqueueStartRotation();
            coord.Tick();

            coord.EnqueueToggleLock(key);
            coord.Tick();
            Assert.IsTrue(coord.CurrentState.FindWindow(key).IsManuallyUnlocked);

            coord.EnqueueStopRotation();
            coord.Tick();

            coord.EnqueueToggleLock(key);
            coord.Tick();
            Assert.IsTrue(adapter.Get("w1").IsLocked);

            coord.EnqueueStartRotation();
            coord.Tick();
            var w1 = coord.CurrentState.FindWindow(key);
            Assert.IsFalse(w1.IsManuallyUnlocked, "Manual unlock is session-scoped and must not leak into the next session.");
            Assert.IsTrue(w1.CanParticipate);
        }

        [Test]
        public void ExclusionWhileOff_DoesNotWriteLock_AndStopRestoresUnlocked()
        {
            var (coord, adapter, _, _, _, _) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            var key = new WindowKey("w1");

            coord.EnqueueSetExcluded(key, true);
            coord.Tick();
            Assert.IsTrue(coord.CurrentState.FindWindow(key).IsExplicitlyExcluded);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "Exclusion while Off must not write an un-leased lock.");

            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.IsTrue(adapter.Get("w1").IsLocked, "Start locks excluded windows under a lease.");
            Assert.IsFalse(coord.CurrentState.Leases[key].OriginalLocked);

            coord.EnqueueStopRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Off, coord.CurrentState.LifecycleState);
            Assert.IsFalse(adapter.Get("w1").IsLocked, "Stop must restore the user's original unlocked state.");
        }

        [Test]
        public void InitialLockedDisplayDiffersFromGlobalSelection_CacheDoesNotSkipRequiredWrite()
        {
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

            Assert.Greater(w1.WriteTargetCallCount, writesBeforeStart, "Must NOT skip writing Y because initial cache comes from actual locked target X.");
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
                "proj", adapter, sel, new FakePersistenceStore(),
                new FakeSessionLayoutStore(), clock, logger, maxQueueSize: 4, commandsPerTickBudget: 1);
            coord.Initialize();

            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);

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
            var (coord, _, _, _, _, _) = DomainLogicTests.CreateHarness(2);
            var modified = coord.CurrentState.Settings.WithNextRevision(
                language: "en",
                maxRotationHistory: 40,
                recordSceneObjects: false,
                recordAssets: false,
                showInspectorOverlays: false,
                blockedCategories: new[] { ObjectCategory.Folder, ObjectCategory.Material });

            coord.EnqueueUpdateSettings(modified, true);
            coord.Tick();
            long revBeforeReset = coord.CurrentState.Settings.SettingsRevision;

            coord.EnqueueResetSettings();
            coord.Tick();

            var s = coord.CurrentState.Settings;
            Assert.AreEqual(revBeforeReset + 1, s.SettingsRevision);
            Assert.AreEqual("ja", s.Language);
            Assert.AreEqual(20, s.MaxRotationHistory);
            Assert.IsTrue(s.RecordSceneObjects);
            Assert.IsTrue(s.RecordAssets);
            Assert.IsTrue(s.ShowInspectorOverlays);
            Assert.AreEqual(0, s.BlockedCategories.Count);
        }

        [Test]
        public void ReloadBoundary_PreservesLayout_RestoresLeases_DoesNotAutoRestart_AndDoesNotRollbackExplicitReorder()
        {
            var (coord, adapter, sel, persist, session, clock) = DomainLogicTests.CreateHarness(2, initialLocked: false);
            coord.EnqueueReorderWindow(new WindowKey("w2"), 0);
            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.IsTrue(adapter.Get("w1").IsLocked);
            Assert.IsTrue(adapter.Get("w2").IsLocked);

            coord.HandleLifecycleBoundaryStop(StopReason.AssemblyReload, suspendMode: true);
            Assert.IsFalse(adapter.Get("w1").IsLocked);
            Assert.IsFalse(adapter.Get("w2").IsLocked);

            var coordAfter = new InspectorManagerCoordinator(
                "test-proj", adapter, sel, persist, session, clock, new FakeLogger(),
                reloadGraceMs: 1000, reconcileIntervalMs: 100);
            coordAfter.Initialize();

            Assert.AreEqual(RuntimeLifecycleState.Off, coordAfter.CurrentState.LifecycleState, "Must remain Off after Assembly Reload.");
            var ordered = coordAfter.CurrentState.GetManagedAliveWindowsOrdered();
            Assert.AreEqual(2, ordered[0].FixedDisplayNumber);

            // Explicit reorder while reload grace is active must not be rolled back after grace ends!
            coordAfter.EnqueueReorderWindow(new WindowKey("w1"), 0);
            coordAfter.Tick();
            clock.Advance(1200);
            coordAfter.Tick();

            Assert.AreEqual(1, coordAfter.CurrentState.GetManagedAliveWindowsOrdered()[0].FixedDisplayNumber);
        }

        [Test]
        public void CrossEditorRestart_RecoveryJournalDoesNotModifyLocksByTemporaryId()
        {
            var clock = new FakeClock();
            var sel = new FakeSelectionAdapter();
            var adapter = new FakeInspectorAdapter { SelectionBacking = sel };
            adapter.AddFakeWindow("w1", locked: true);

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

            var coord = new InspectorManagerCoordinator("proj", adapter, sel, persist, session, clock, new FakeLogger());
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
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var dto1 = new SettingsDto { ProjectId = "proj-1", Language = "en", SettingsRevision = 2 };
                    Assert.IsTrue(store.QueueSaveSettings(dto1, 2).Succeeded);
                    var immediate = store.LoadSettings();
                    Assert.AreEqual("en", immediate.Value.Language, "Unflushed queued write must be immediately visible to Load.");
                    Assert.IsTrue(store.FlushSettings().Succeeded);

                    var dto2 = new SettingsDto { ProjectId = "proj-1", Language = "ja", MaxRotationHistory = 77, SettingsRevision = 3 };
                    store.QueueSaveSettings(dto2, 3);
                    Assert.IsTrue(store.FlushSettings().Succeeded);

                    var stale = new SettingsDto { ProjectId = "proj-1", Language = "en", MaxRotationHistory = 11, SettingsRevision = 1 };
                    store.QueueSaveSettings(stale, 1);
                    store.FlushSettings();
                    Assert.AreEqual(77, store.LoadSettings().Value.MaxRotationHistory);
                }

                string primaryPath = Path.Combine(tempDir, "settings_v1.json");
                File.WriteAllText(primaryPath, "{corrupted-json!!!");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettings();
                    Assert.IsTrue(loaded.Succeeded);
                    Assert.IsTrue(loaded.UsedBackup);
                    Assert.AreEqual("en", loaded.Value.Language);
                }

                string backupPath = Path.Combine(tempDir, "settings_v1.json.bak");
                File.WriteAllText(backupPath, "{also-corrupted");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettings();
                    Assert.IsFalse(loaded.Succeeded);
                    Assert.IsTrue(loaded.FellBackToDefault);
                    Assert.IsTrue(Directory.GetFiles(tempDir, "*.corrupt.*").Length >= 2, "Corrupt files must be quarantined.");
                }

                File.WriteAllText(primaryPath, "{\"schemaVersion\":99,\"revision\":1,\"checksum\":\"abc\",\"payload\":{}}");
                using (var store = new AtomicJsonFileStore(tempDir, "proj-1"))
                {
                    var loaded = store.LoadSettings();
                    Assert.IsTrue(loaded.NewerSchemaDetected);
                    Assert.IsTrue(store.IsWriteBlockedByNewerSchema);
                    var saveAttempt = store.QueueSaveSettings(new SettingsDto(), 10);
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
