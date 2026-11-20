#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using DennokoWorks.InspectorManagerV2.UnityAdapter;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.Tests
{
    /// <summary>
    /// Real Unity Editor EditMode integration tests (Section 12.3 & Section 13 Technical PoC).
    /// Runs inside Unity 2022.3.22f1 and Unity 6 EditMode Test Runner using isolated temporary Inspector windows.
    /// </summary>
    [TestFixture]
    public sealed class UnityIntegrationTests
    {
        [Test]
        public void InspectorReflectionAdapter_ProbesCapabilities_ReadOnly_AndSupportsRotation()
        {
            var resolver = new UnityObjectResolver();
            var adapter = new InspectorReflectionAdapter(resolver, "test-unity-session");
            var caps = adapter.ProbeCapabilities();

            Assert.IsTrue(caps.CanEnumerateAndIdentify, caps.DiagnosticSummary);
            Assert.IsTrue(caps.CanReadLock, caps.DiagnosticSummary);
            Assert.IsTrue(caps.CanWriteLock, caps.DiagnosticSummary);
            Assert.IsTrue(caps.CanReadTargets, caps.DiagnosticSummary);
            Assert.IsTrue(caps.CanWriteTargets, caps.DiagnosticSummary);
            Assert.IsTrue(caps.SupportsRotation, caps.DiagnosticSummary);
        }

        [Test]
        public void TechnicalPoC_AssignMultiSelectionWithoutChangingGlobalSelection_AndRestoreLock()
        {
            var resolver = new UnityObjectResolver();
            var adapter = new InspectorReflectionAdapter(resolver, "test-unity-session");

            var goA = new GameObject("IMV2_PoC_A");
            var goB = new GameObject("IMV2_PoC_B");
            var goOther = new GameObject("IMV2_PoC_GlobalSelection");
            WindowKey createdKey = default;
            string createdEvidence = string.Empty;
            var prevSelection = Selection.objects;

            try
            {
                Selection.objects = new UnityEngine.Object[] { goOther };

                // Create isolated verification Inspector (Section 8.1 & 13 #1)
                var created = adapter.CreateInspectorWindow();
                Assert.IsTrue(created.Succeeded, created.Error);
                createdKey = created.WindowKey;
                createdEvidence = created.IdentityEvidence;

                // Assign multi-selection {goA, goB} to the isolated Inspector WITHOUT changing Selection.objects!
                var keyA = UnityObjectIdHelper.GetObjectRefKey(goA);
                var keyB = UnityObjectIdHelper.GetObjectRefKey(goB);
                var writeRes = adapter.WriteTargetsAndLock(createdKey, new[] { keyA, keyB }, keyA, locked: true);

                Assert.AreNotEqual(WindowWriteStatus.Failed, writeRes.Status, writeRes.Error);
                Assert.IsTrue(writeRes.ObservedLocked);

                // Global Selection MUST remain untouched!
                Assert.AreEqual(1, Selection.objects.Length);
                Assert.AreEqual(goOther, Selection.objects[0], "Inspector assignment must NOT mutate global Selection.");

                // Read back locked target set (must contain exact multi-selection {goA, goB}, NOT Component editors!)
                var readBack = adapter.ReadInspectorState(createdKey);
                Assert.AreEqual(InspectorReadStatus.Populated, readBack.Status, readBack.Error);
                Assert.IsTrue(readBack.ObservedLocked);
                var expectedKey = SelectionComparisonKey.Create(new[] { keyA, keyB }, keyA);
                Assert.IsTrue(expectedKey.TargetSetEquals(readBack.ObservedSelection.ComparisonKey),
                    "Read-back locked target set must match assigned multi-selection exactly.");

                // Restore lock to original unlocked state
                var unlockRes = adapter.WriteLockOnly(createdKey, false);
                Assert.AreEqual(WindowWriteStatus.Verified, unlockRes.Status, unlockRes.Error);
                Assert.IsFalse(unlockRes.ObservedLocked);
            }
            finally
            {
                if (createdKey.IsValid)
                {
                    adapter.CloseInspectorWindow(createdKey, createdEvidence);
                }
                Selection.objects = prevSelection;
                UnityEngine.Object.DestroyImmediate(goA);
                UnityEngine.Object.DestroyImmediate(goB);
                UnityEngine.Object.DestroyImmediate(goOther);
            }
        }

        [Test]
        public void UnityCategoryClassifier_ClassifiesGameObjectAndMaterialAndFolder()
        {
            var go = new GameObject("IMV2_Classify_GO");
            var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader"));
            try
            {
                var (goCat, goAsset) = UnityCategoryClassifier.Classify(go);
                Assert.AreEqual(ObjectCategory.GameObject, goCat);
                Assert.IsFalse(goAsset);

                var (matCat, _) = UnityCategoryClassifier.Classify(mat);
                Assert.AreEqual(ObjectCategory.Material, matCat);

                var folderObj = AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets");
                if (folderObj != null)
                {
                    var (fCat, fAsset) = UnityCategoryClassifier.Classify(folderObj);
                    Assert.AreEqual(ObjectCategory.Folder, fCat);
                    Assert.IsTrue(fAsset);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public void UnityObjectResolver_MissingSubasset_DoesNotFallbackToMainAsset()
        {
            var resolver = new UnityObjectResolver();
            // Pick any existing asset (e.g., this test script or folder) and ask for a non-existent localFileId
            string anyGuid = AssetDatabase.AssetPathToGUID("Assets/dennokoworks/InspectorManagerV2/Editor/Domain/Identifiers.cs");
            if (string.IsNullOrEmpty(anyGuid))
            {
                Assert.Pass("Test asset GUID not yet imported in batchmode; skipped.");
                return;
            }

            var fakeSubassetPid = new PersistentObjectId(
                PersistentObjectKind.Asset,
                anyGuid,
                localFileId: 999999999999L,
                globalObjectIdString: string.Empty,
                sceneGuid: string.Empty);

            var entry = new FavoriteEntry(
                "fav-missing-sub",
                fakeSubassetPid,
                "DeletedSubassetMesh",
                "Mesh",
                0,
                isTemporary: false,
                temporaryRuntimeKey: null,
                resolutionState: FavoriteResolutionState.ResolutionFailed,
                resolutionDetail: string.Empty);

            var res = resolver.Resolve(entry);
            Assert.AreEqual(FavoriteResolutionState.DeletedOrMissing, res.State,
                "Missing subasset must NEVER fall back to resolving the main asset.");
        }
    }
}
#endif
