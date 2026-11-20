using System;
using DennokoWorks.InspectorManagerV2.Domain;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Classifies UnityEngine.Object instances into ObjectCategory.
    /// Section 5.5: Classification failures are surfaced distinctly as ClassificationFailed.
    /// </summary>
    public static class UnityCategoryClassifier
    {
        public static (ObjectCategory category, bool isAsset) Classify(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return (ObjectCategory.ClassificationFailed, false);
            }

            try
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                bool isAsset = !string.IsNullOrEmpty(assetPath);

                if (isAsset && obj is DefaultAsset && AssetDatabase.IsValidFolder(assetPath))
                {
                    return (ObjectCategory.Folder, true);
                }

                if (obj is SceneAsset)
                {
                    return (ObjectCategory.Scene, true);
                }

                if (obj is MonoScript)
                {
                    return (ObjectCategory.Script, true);
                }

                if (obj is GameObject)
                {
                    if (isAsset || PrefabUtility.IsPartOfPrefabAsset(obj))
                    {
                        return (ObjectCategory.Prefab, true);
                    }
                    return (ObjectCategory.GameObject, false);
                }

                if (obj is Component)
                {
                    if (isAsset || PrefabUtility.IsPartOfPrefabAsset(obj))
                    {
                        return (ObjectCategory.Prefab, true);
                    }
                    return (ObjectCategory.Component, false);
                }

                if (obj is Material) return (ObjectCategory.Material, isAsset);
                if (obj is Texture) return (ObjectCategory.Texture, isAsset);
                if (obj is Shader) return (ObjectCategory.Shader, isAsset);
                if (obj is AudioClip) return (ObjectCategory.Audio, isAsset);
                if (obj is AnimationClip || obj is RuntimeAnimatorController) return (ObjectCategory.Animation, isAsset);
                if (obj is Mesh) return (ObjectCategory.Mesh, isAsset);
                if (obj is Font) return (ObjectCategory.Font, isAsset);
                if (obj is ScriptableObject) return (ObjectCategory.ScriptableObject, isAsset);

                return (isAsset ? ObjectCategory.OtherAsset : ObjectCategory.Unknown, isAsset);
            }
            catch (Exception)
            {
                return (ObjectCategory.ClassificationFailed, false);
            }
        }
    }
}
