using System;
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
            var folder = new ObjectItemSnapshot(new ObjectRefKey("obj:f"), "Folder", "DefaultAsset", ObjectCategory.Folder, true);
            var go = new ObjectItemSnapshot(new ObjectRefKey("obj:g"), "Cube", "GameObject", ObjectCategory.GameObject, false);
            var failed = new ObjectItemSnapshot(new ObjectRefKey("obj:x"), "Broken", "Object", ObjectCategory.ClassificationFailed, false);

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
            var (coord, adapter, sel, _, _, _) = CreateHarness(3);
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

            // Select A again (A -> B -> A): History mode MRU must NOT duplicate A! Candidates=[A, B].
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual(2, coord.CurrentState.RotationHistory.Entries.Count);
            Assert.AreEqual("A", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual("B", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual(AssignmentLabel.HistoryUnassigned, coord.CurrentState.FindWindow(new WindowKey("w2")).AssignmentLabel);

            // Select C -> #3=C, #1=A, #2=B
            SelectAndTick(coord, sel, "C");
            Assert.AreEqual("C", adapter.Get("w3").LockedTargets[0].Value);
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
            Assert.AreEqual("B", adapter.Get("w2").LockedTargets[0].Value);
        }

        [Test]
        public void EmptySelection_A_Empty_A_DoesNotDiscardReSelection()
        {
            var (coord, adapter, sel, _, _, _) = CreateHarness(2);
            sel.RegisterObject("A");
            sel.RegisterObject("B");

            coord.EnqueueStartRotation();
            coord.Tick();

            SelectAndTick(coord, sel, "A");
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);

            // Empty selection keeps current assignments, updates LastAcceptedSelectionKey to Empty
            sel.CurrentSelection = SelectionSnapshot.Empty;
            coord.OnSelectionChangedImmediate();
            coord.Tick();
            Assert.AreEqual(SelectionComparisonKey.Empty, coord.CurrentState.LastAcceptedSelectionKey);
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);

            // Re-selecting A after empty must NOT be dropped as duplicate!
            SelectAndTick(coord, sel, "A");
            Assert.AreEqual(new ObjectRefKey("A"), coord.CurrentState.CurrentLiveSelection.ActiveTarget);
            Assert.AreEqual("A", adapter.Get("w1").LockedTargets[0].Value);
        }

        [Test]
        public void ExplicitExclusion_AndManualUnlock_AreIndependent()
        {
            var (coord, adapter, _, _, _, _) = CreateHarness(2);
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
            Assert.IsFalse(coord.CurrentState.Leases[new WindowKey("w1")].OriginalLocked);
        }

        [Test]
        public void RandomizedStateMachine_MaintainsAllInvariants()
        {
            var rng = new Random(20260924);
            var (coord, _, sel, _, _, clock) = CreateHarness(3);
            for (int i = 0; i < 6; i++) sel.RegisterObject("O" + i);

            for (int step = 0; step < 250; step++)
            {
                int op = rng.Next(8);
                switch (op)
                {
                    case 0: coord.EnqueueStartRotation(); break;
                    case 1: coord.EnqueueStopRotation(); break;
                    case 2:
                        sel.CurrentSelection = sel.BuildSnapshot("O" + rng.Next(6));
                        coord.OnSelectionChangedImmediate();
                        break;
                    case 3:
                        sel.CurrentSelection = SelectionSnapshot.Empty;
                        coord.OnSelectionChangedImmediate();
                        break;
                    case 4:
                        coord.EnqueueToggleLock(new WindowKey("w" + (1 + rng.Next(3))));
                        break;
                    case 5:
                        coord.EnqueueSetExcluded(new WindowKey("w" + (1 + rng.Next(3))), rng.Next(2) == 0);
                        break;
                    case 6:
                        coord.EnqueueReorderWindow(new WindowKey("w" + (1 + rng.Next(3))), rng.Next(3));
                        break;
                    case 7:
                        coord.EnqueueResetLayout();
                        break;
                }

                clock.Advance(50);
                coord.Tick();

                var st = coord.CurrentState;
                Assert.Greater(st.Generation, 0);
                var fixedNums = st.Windows.Select(w => w.FixedDisplayNumber).ToArray();
                Assert.AreEqual(fixedNums.Length, fixedNums.Distinct().Count(), "FixedDisplayNumber must remain unique.");
                var rotKeys = st.RotationHistory.Entries.Select(e => e.Snapshot.ComparisonKey).ToArray();
                Assert.AreEqual(rotKeys.Length, rotKeys.Distinct().Count(), "RotationHistory must never contain duplicates.");
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

            var persist = new FakePersistenceStore();
            var session = new FakeSessionLayoutStore();

            var coord = new InspectorManagerCoordinator(
                "test-proj",
                adapter,
                sel,
                persist,
                session,
                clock,
                logger,
                commandsPerTickBudget: 16,
                reconcileIntervalMs: 300,
                persistDebounceMs: 500,
                reloadGraceMs: 1000,
                pendingVerifyTimeoutMs: 300);

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
