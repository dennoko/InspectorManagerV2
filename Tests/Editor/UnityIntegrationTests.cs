#if UNITY_EDITOR
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;
using DennokoWorks.InspectorManagerV2.UnityAdapter;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.Tests
{
    /// <summary>
    /// Real Unity Editor EditMode integration tests for Unity 2022.3.22f1 and Unity 6.
    /// </summary>
    [TestFixture]
    public sealed class UnityIntegrationTests
    {
        [Test]
        public void InspectorReflectionAdapter_ProbesCapabilities_ReadOnly_AndSupportsRotation()
        {
            var adapter = new InspectorReflectionAdapter("test-unity-session");
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
            var adapter = new InspectorReflectionAdapter("test-unity-session");

            var goA = new GameObject("IMV2_PoC_A");
            var goB = new GameObject("IMV2_PoC_B");
            var goOther = new GameObject("IMV2_PoC_GlobalSelection");
            WindowKey createdKey = default;
            string createdEvidence = string.Empty;
            var prevSelection = Selection.objects;

            try
            {
                Selection.objects = new UnityEngine.Object[] { goOther };

                var created = adapter.CreateInspectorWindow();
                Assert.IsTrue(created.Succeeded, created.Error);
                createdKey = created.WindowKey;
                createdEvidence = created.IdentityEvidence;

                var keyA = UnityObjectIdHelper.GetObjectRefKey(goA);
                var keyB = UnityObjectIdHelper.GetObjectRefKey(goB);
                var writeRes = adapter.WriteTargetsAndLock(createdKey, new[] { keyA, keyB }, keyA, locked: true);

                Assert.AreNotEqual(WindowWriteStatus.Failed, writeRes.Status, writeRes.Error);
                Assert.IsTrue(writeRes.ObservedLocked);

                Assert.AreEqual(1, Selection.objects.Length);
                Assert.AreEqual(goOther, Selection.objects[0], "Inspector assignment must NOT mutate global Selection.");

                var readBack = adapter.ReadInspectorState(createdKey);
                Assert.AreEqual(InspectorReadStatus.Populated, readBack.Status, readBack.Error);
                Assert.IsTrue(readBack.ObservedLocked);
                var expectedKey = SelectionComparisonKey.Create(new[] { keyA, keyB }, keyA);
                Assert.IsTrue(expectedKey.TargetSetEquals(readBack.ObservedSelection.ComparisonKey),
                    "Read-back locked target set must match assigned multi-selection exactly.");

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
    }
}
#endif
