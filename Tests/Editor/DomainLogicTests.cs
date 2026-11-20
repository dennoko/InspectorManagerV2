using System;
using System.Collections.Generic;
using System.Linq;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using NUnit.Framework;

namespace DennokoWorks.InspectorManagerV2.Tests
{
    [TestFixture]
    public sealed class DomainLogicTests
    {
        [Test]
        public void SelectionComparisonKey_IgnoresOrderAndDuplicates_DetectsActiveChange()
        {
            var a = new ObjectRefKey("obj:1");
            var b = new ObjectRefKey("obj:2");

            var k1 = SelectionComparisonKey.Create(new[] { a, b }, a);
            var k2 = SelectionComparisonKey.Create(new[] { b, a, a }, a);
            var k3 = SelectionComparisonKey.Create(new[] { a, b }, b);

            Assert.AreEqual(k1, k2, "Reordering or duplicating targets must produce the same comparison key.");
            Assert.AreNotEqual(k1, k3, "Changing active target within the same set must produce a distinct comparison key.");
            Assert.IsTrue(k1.TargetSetEquals(k3), "TargetSetEquals should match independent of active target.");
        }

        [Test]
        public void SelectionFilter_MixedFolderAndGameObject_PassesWholeSet_WhileAllBlockedSuppresses()
        {
            var folder = new ObjectItemSnapshot(new ObjectRefKey("obj:f"), "Folder", "DefaultAsset", ObjectCategory.Folder, true, PersistentObjectId.Empty);
            var go = new ObjectItemSnapshot(new ObjectRefKey("obj:g"), "Cube", "GameObject", ObjectCategory.GameObject, false, PersistentObjectId.Empty);
            var failed = new ObjectItemSnapshot(new ObjectRefKey("obj:x"), "Broken", "Object", ObjectCategory.ClassificationFailed, false, PersistentObjectId.Empty);

            var settings = ManagerSettings.CreateDefault().WithNextRevision(blockedCategories: new[] { ObjectCategory.Folder });

            var folderOnly = new SelectionSnapshot(new[] { folder }, folder.Key, 1, 1, SelectionSource.UserSelection);
            var mixed = new SelectionSnapshot(new[] { folder, go }, go.Key, 2, 1, SelectionSource.UserSelection);
            var withFailed = new SelectionSnapshot(new[] { go, failed }, go.Key, 3, 1, SelectionSource.UserSelection);

            Assert.AreEqual(SelectionFilterOutcome.BlockedByCategory, SelectionFilter.Evaluate(folderOnly, settings).Outcome);
            Assert.AreEqual(SelectionFilterOutcome.Allowed, SelectionFilter.Evaluate(mixed, settings).Outcome);
            Assert.AreEqual(SelectionFilterOutcome.BlockedByClassificationFailure, SelectionFilter.Evaluate(withFailed, settings).Outcome);
        }

        [Test]
        public void HistoryMode_AssignsByRoleOrder_KeepsFixedNumbers_DeduplicatesMru_AndMarksInsufficientSlots()
        {
            var (coord, adapter, sel, _, _, clock) = CreateHarness(3);
            sel.RegisterObject("A");
            sel.RegisterObject("B");
            sel.RegisterObject("C");

            // Reorder #3 to front -> Role order becomes #3, #1, #2 while fixed numbers stay 3, 1, 2!
            coord.EnqueueReorderWindow(new WindowKey("w3"), 0);
            coord.Tick();

            var orderedBefore = coord.CurrentState.GetManagedAliveWindowsOrdered();
            Assert.AreEqual(3, orderedBefore[0].FixedDisplayNumber);
            Assert.AreEqual(1, orderedBefore[1].FixedDisplayNumber);
            Assert.AreEqual(2, orderedBefore[2].FixedDisplayNumber);

            coord.EnqueueStartRotation();
            coord.Tick();
            Assert.AreEqual(RuntimeLifecycleState.Running, coord.CurrentState.LifecycleState);

            // Select A -> #3 gets A, #1 and #2 are HistoryUnassigned
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual("A", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual(AssignmentLabel.HistoryUnassigned, coord.CurrentState.FindWindow(new WindowKey("w1")).AssignmentLabel);
            Assert.AreEqual(AssignmentLabel.HistoryUnassigned, coord.CurrentState.FindWindow(new WindowKey("w2")).AssignmentLabel);

            // Select B -> #3=B, #1=A, #2=HistoryUnassigned
            SelectAndTick(coord, sel, "B");
            Assert.AreEqual("B", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);

            // Select A again (A -> B -> A): History mode MRU must NOT duplicate A! Candidates=[A, B], NOT [A, B, A].
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual(2, coord.CurrentState.RotationHistory.Entries.Count);
            Assert.AreEqual("A", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual("B", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual(AssignmentLabel.HistoryUnassigned, coord.CurrentState.FindWindow(new WindowKey("w2")).AssignmentLabel);
            // NavigationHistory still preserves full A -> B -> A sequence!
            Assert.AreEqual(3, coord.CurrentState.NavigationHistory.Entries.Count);

            // Select C -> #3=C, #1=A, #2=B
            SelectAndTick(coord, sel, "C");
            Assert.AreEqual("C", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual("B", adapter.Get("w2").LockedTargets[0].Value);
        }

        [Test]
        public void CycleMode_UpdatesOldestDeterministically_AndBackForwardDoesNotAdvanceOrder()
        {
            var (coord, adapter, sel, _, _, _) = CreateHarness(2);
            sel.RegisterObject("A");
            sel.RegisterObject("B");
            sel.RegisterObject("C");

            coord.EnqueueUpdateSettings(coord.CurrentState.Settings.WithNextRevision(mode: RotationMode.Cycle), true);
            coord.EnqueueStartRotation();
            coord.Tick();

            // Select A -> w1 (oldest, tie broken by role order)
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
            long seqW1AfterA = coord.CurrentState.FindWindow(new WindowKey("w1")).LastSuccessSequence;
            Assert.Greater(seqW1AfterA, 0);

            // Select B -> w2 (sequence 0 < seqW1AfterA)
            SelectAndTick(coord, sel, "B");
            Assert.AreEqual("B", adapter.Get("w2").LockedTargets[0].Value);
            long seqW2AfterB = coord.CurrentState.FindWindow(new WindowKey("w2")).LastSuccessSequence;
            Assert.Greater(seqW2AfterB, seqW1AfterA);

            // Navigate Back to A -> updates current oldest candidate (w1) WITHOUT advancing w1's sequence!
            coord.EnqueueNavigateBack();
            coord.Tick(); // executes NavigateBack -> fires OnGlobalSelectionChanged -> enqueues SelectionSnapshot
            coord.Tick(); // processes SelectionSnapshot as HistoryNavigation
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual(seqW1AfterA, coord.CurrentState.FindWindow(new WindowKey("w1")).LastSuccessSequence,
                "Back/Forward in Cycle mode must NOT advance LastSuccessSequence.");

            // Next normal selection C still goes to w1 because its sequence did not advance!
            SelectAndTick(coord, sel, "C");
            Assert.AreEqual("C", adapter.Get("w1").LockedTargets[0].Value);
            Assert.Greater(coord.CurrentState.FindWindow(new WindowKey("w1")).LastSuccessSequence, seqW2AfterB);
        }

        [Test]
        public void EmptySelection_A_Empty_A_ReSelectsA_WithoutPollutingHistory()
        {
            var (coord, adapter, sel, _, _, _) = CreateHarness(2);
            sel.RegisterObject("A");
            sel.RegisterObject("B");

            coord.EnqueueUpdateSettings(coord.CurrentState.Settings.WithNextRevision(mode: RotationMode.Cycle), true);
            coord.EnqueueStartRotation();
            coord.Tick();

            SelectAndTick(coord, sel, "A");
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
            int navCountBeforeEmpty = coord.CurrentState.NavigationHistory.Entries.Count;

            // Empty selection
            sel.CurrentSelection = SelectionSnapshot.Empty;
            coord.OnSelectionChangedImmediate();
            coord.Tick();
            Assert.AreEqual(SelectionComparisonKey.Empty, coord.CurrentState.LastAcceptedSelectionKey);
            Assert.AreEqual(navCountBeforeEmpty, coord.CurrentState.NavigationHistory.Entries.Count, "Empty selection must not add to NavigationHistory.");

            // Re-selecting A after empty must NOT be dropped as duplicate!
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual("A", adapter.Get("w2").LockedTargets[0].Value, "A -> Empty -> A must not be discarded as duplicate.");
        }

        [Test]
        public void ExplicitExclusion_AndManualUnlock_AreIndependent_AndPreservedAcrossBulkOps()
        {
            var (coord, adapter, sel, _, _, _) = CreateHarness(2);
            coord.EnqueueStartRotation();
            coord.Tick();

            // Exclude w1 explicitly -> locked & excluded
            coord.EnqueueSetExcluded(new WindowKey("w1"), true);
            coord.Tick();
            Assert.IsTrue(coord.CurrentState.FindWindow(new WindowKey("w1")).IsExplicitlyExcluded);
            Assert.IsTrue(adapter.Get("w1").IsLocked);

            // Manually unlock w1 while excluded -> both flags true
            coord.EnqueueToggleLock(new WindowKey("w1"));
            coord.Tick();
            var w1 = coord.CurrentState.FindWindow(new WindowKey("w1"));
            Assert.IsTrue(w1.IsExplicitlyExcluded);
            Assert.IsTrue(w1.IsManuallyUnlocked);
            Assert.IsFalse(adapter.Get("w1").IsLocked);

            // Re-lock w1 -> clears IsManuallyUnlocked ONLY, MUST keep IsExplicitlyExcluded!
            coord.EnqueueToggleLock(new WindowKey("w1"));
            coord.Tick();
            w1 = coord.CurrentState.FindWindow(new WindowKey("w1"));
            Assert.IsTrue(w1.IsExplicitlyExcluded, "Re-locking must NOT clear explicit exclusion.");
            Assert.IsFalse(w1.IsManuallyUnlocked);
            Assert.IsFalse(w1.CanParticipate);

            // Bulk unlock & bulk lock must preserve explicit exclusion and original lease value (false)
            coord.EnqueueBulkLock(false);
            coord.Tick();
            Assert.IsTrue(coord.CurrentState.FindWindow(new WindowKey("w1")).IsExplicitlyExcluded);
            Assert.IsFalse(coord.CurrentState.Leases[new WindowKey("w1")].OriginalLocked);

            coord.EnqueueBulkLock(true);
            coord.Tick();
            Assert.IsTrue(coord.CurrentState.FindWindow(new WindowKey("w1")).IsExplicitlyExcluded);
            Assert.IsFalse(coord.CurrentState.Leases[new WindowKey("w1")].OriginalLocked);
        }

        [Test]
        public void NavigationBack_DuplicatePreviousNotification_AndExpiredExpectation_HandledCorrectly()
        {
            var (coord, adapter, sel, _, _, clock) = CreateHarness(2);
            sel.RegisterObject("A");
            sel.RegisterObject("B");

            coord.EnqueueStartRotation();
            coord.Tick();
            SelectAndTick(coord, sel, "A");
            SelectAndTick(coord, sel, "B");
            Assert.AreEqual(2, coord.CurrentState.NavigationHistory.Entries.Count);
            Assert.AreEqual(1, coord.CurrentState.NavigationHistory.CursorIndex);

            // Suppress automatic notification so we can simulate delayed duplicate B then A
            sel.EmitSelectionChangedOnSet = false;
            coord.EnqueueNavigateBack();
            coord.Tick();
            Assert.IsNotNull(coord.CurrentState.PendingNavigation);

            // Duplicate previous notification (B) arrives BEFORE A -> must NOT consume PendingNavigation!
            coord.EnqueueSelectionSnapshot(sel.BuildSnapshot("B"));
            coord.Tick();
            Assert.IsNotNull(coord.CurrentState.PendingNavigation, "Duplicate old selection must not consume PendingNavigation.");

            // Now simulate A notification NEVER arriving -> timeout triggers readback finalization!
            clock.Advance(500);
            coord.Tick();
            Assert.IsNull(coord.CurrentState.PendingNavigation);
            Assert.AreEqual(0, coord.CurrentState.NavigationHistory.CursorIndex);
            Assert.AreEqual(2, coord.CurrentState.NavigationHistory.Entries.Count, "Back navigation must not append new history entry.");
        }

        [Test]
        public void RandomizedStateMachine_MaintainsAllInvariants()
        {
            var rng = new Random(20260924);
            var (coord, adapter, sel, _, _, clock) = CreateHarness(3);
            for (int i = 0; i < 6; i++) sel.RegisterObject("O" + i);

            for (int step = 0; step < 250; step++)
            {
                int op = rng.Next(12);
                switch (op)
                {
                    case 0: coord.EnqueueStartRotation(); break;
                    case 1: coord.EnqueueStopRotation(); break;
                    case 2: coord.EnqueuePauseRotation(); break;
                    case 3: coord.EnqueueResumeRotation(); break;
                    case 4:
                        sel.CurrentSelection = sel.BuildSnapshot("O" + rng.Next(6));
                        coord.OnSelectionChangedImmediate();
                        break;
                    case 5:
                        sel.CurrentSelection = SelectionSnapshot.Empty;
                        coord.OnSelectionChangedImmediate();
                        break;
                    case 6:
                        coord.EnqueueToggleLock(new WindowKey("w" + (1 + rng.Next(3))));
                        break;
                    case 7:
                        coord.EnqueueSetExcluded(new WindowKey("w" + (1 + rng.Next(3))), rng.Next(2) == 0);
                        break;
                    case 8:
                        coord.EnqueueReorderWindow(new WindowKey("w" + (1 + rng.Next(3))), rng.Next(3));
                        break;
                    case 9:
                        coord.EnqueueUpdateSettings(coord.CurrentState.Settings.WithNextRevision(
                            mode: rng.Next(2) == 0 ? RotationMode.History : RotationMode.Cycle), true);
                        break;
                    case 10:
                        coord.EnqueueNavigateBack();
                        break;
                    case 11:
                        coord.EnqueueNavigateForward();
                        break;
                }

                clock.Advance(50);
                coord.Tick();

                // Check core invariants after every random step!
                var st = coord.CurrentState;
                Assert.Greater(st.Generation, 0);
                // Fixed display numbers must remain unique across all windows
                var fixedNums = st.Windows.Select(w => w.FixedDisplayNumber).ToArray();
                Assert.AreEqual(fixedNums.Length, fixedNums.Distinct().Count(), "FixedDisplayNumber must remain unique.");
                // RotationHistory must never have duplicate keys
                var rotKeys = st.RotationHistory.Entries.Select(e => e.Snapshot.ComparisonKey).ToArray();
                Assert.AreEqual(rotKeys.Length, rotKeys.Distinct().Count(), "RotationHistory must never contain duplicates.");
                // When Off cleanly, leases must be empty and all surviving windows restored to original (false)
                if (st.LifecycleState == RuntimeLifecycleState.Off)
                {
                    Assert.AreEqual(0, st.Leases.Count, "Off state must hold zero active leases.");
                    Assert.AreEqual(0, st.RotationHistory.Entries.Count, "Off state must clear session RotationHistory.");
                }
            }
        }

        internal static (InspectorManagerCoordinator coord, FakeInspectorAdapter adapter, FakeSelectionAdapter sel, FakePersistenceStore persist, FakeSessionLayoutStore session, FakeClock clock)
            CreateHarness(int initialWindowCount, bool initialLocked = false)
        {
            var clock = new FakeClock();
            var logger = new FakeLogger();
            var sel = new FakeSelectionAdapter();
            var adapter = new FakeInspectorAdapter { SelectionBacking = sel };
            for (int i = 1; i <= initialWindowCount; i++)
            {
                adapter.AddFakeWindow("w" + i, locked: initialLocked);
            }

            var favResolver = new FakeFavoriteResolver(sel);
            var persist = new FakePersistenceStore();
            var session = new FakeSessionLayoutStore();

            var coord = new InspectorManagerCoordinator(
                "test-proj",
                adapter,
                sel,
                favResolver,
                persist,
                session,
                clock,
                logger,
                commandsPerTickBudget: 16,
                reconcileIntervalMs: 300,
                persistDebounceMs: 500,
                reloadGraceMs: 1000,
                navigationTimeoutMs: 400,
                pendingVerifyTimeoutMs: 300);

            sel.OnGlobalSelectionChanged = () => coord.OnSelectionChangedImmediate();
            coord.Initialize();
            return (coord, adapter, sel, persist, session, clock);
        }

        internal static void SelectAndTick(InspectorManagerCoordinator coord, FakeSelectionAdapter sel, params string[] ids)
        {
            sel.CurrentSelection = sel.BuildSnapshot(ids);
            coord.OnSelectionChangedImmediate();
            coord.Tick();
        }
    }
}
