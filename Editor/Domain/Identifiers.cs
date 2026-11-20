using System;
using System.Collections.Generic;
using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    /// <summary>
    /// Session-scoped identifier for an Inspector window instance.
    /// Never assumed to remain identical across Editor restarts.
    /// </summary>
    public readonly struct WindowKey : IEquatable<WindowKey>, IComparable<WindowKey>
    {
        public string Value { get; }

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public WindowKey(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool Equals(WindowKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is WindowKey other && Equals(other);

        public override int GetHashCode() => Value != null ? StringComparer.Ordinal.GetHashCode(Value) : 0;

        public int CompareTo(WindowKey other) => string.Compare(Value, other.Value, StringComparison.Ordinal);

        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(WindowKey left, WindowKey right) => left.Equals(right);
        public static bool operator !=(WindowKey left, WindowKey right) => !left.Equals(right);
    }

    /// <summary>
    /// Session-scoped identifier for a live UnityEngine.Object reference.
    /// </summary>
    public readonly struct ObjectRefKey : IEquatable<ObjectRefKey>, IComparable<ObjectRefKey>
    {
        public string Value { get; }

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public ObjectRefKey(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool Equals(ObjectRefKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ObjectRefKey other && Equals(other);

        public override int GetHashCode() => Value != null ? StringComparer.Ordinal.GetHashCode(Value) : 0;

        public int CompareTo(ObjectRefKey other) => string.Compare(Value, other.Value, StringComparison.Ordinal);

        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(ObjectRefKey left, ObjectRefKey right) => left.Equals(right);
        public static bool operator !=(ObjectRefKey left, ObjectRefKey right) => !left.Equals(right);
    }

    public enum PersistentObjectKind
    {
        None = 0,
        Asset = 1,
        SceneObject = 2,
        Temporary = 3
    }

    /// <summary>
    /// Persistent project-scoped object reference independent of temporary runtime IDs.
    /// Assets store GUID + LocalFileId + GlobalObjectId; saved scene objects store SceneGuid + GlobalObjectId.
    /// </summary>
    public readonly struct PersistentObjectId : IEquatable<PersistentObjectId>
    {
        public PersistentObjectKind Kind { get; }
        public string AssetGuid { get; }
        public long LocalFileId { get; }
        public string GlobalObjectIdString { get; }
        public string SceneGuid { get; }

        public bool IsEmpty => Kind == PersistentObjectKind.None;

        public PersistentObjectId(
            PersistentObjectKind kind,
            string assetGuid,
            long localFileId,
            string globalObjectIdString,
            string sceneGuid)
        {
            Kind = kind;
            AssetGuid = assetGuid ?? string.Empty;
            LocalFileId = localFileId;
            GlobalObjectIdString = globalObjectIdString ?? string.Empty;
            SceneGuid = sceneGuid ?? string.Empty;
        }

        public static PersistentObjectId Empty => new PersistentObjectId(PersistentObjectKind.None, string.Empty, 0, string.Empty, string.Empty);

        public bool Equals(PersistentObjectId other)
        {
            return Kind == other.Kind
                && string.Equals(AssetGuid, other.AssetGuid, StringComparison.Ordinal)
                && LocalFileId == other.LocalFileId
                && string.Equals(GlobalObjectIdString, other.GlobalObjectIdString, StringComparison.Ordinal)
                && string.Equals(SceneGuid, other.SceneGuid, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is PersistentObjectId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(AssetGuid ?? string.Empty);
                hash = (hash * 397) ^ LocalFileId.GetHashCode();
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(GlobalObjectIdString ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(SceneGuid ?? string.Empty);
                return hash;
            }
        }

        public static bool operator ==(PersistentObjectId left, PersistentObjectId right) => left.Equals(right);
        public static bool operator !=(PersistentObjectId left, PersistentObjectId right) => !left.Equals(right);
    }

    /// <summary>
    /// Canonical selection identity: deduplicated target set (order-independent) + active target.
    /// Reordering targets does not create a new selection, whereas changing the active target does.
    /// </summary>
    public sealed class SelectionComparisonKey : IEquatable<SelectionComparisonKey>
    {
        public static readonly SelectionComparisonKey Empty = new SelectionComparisonKey(Array.Empty<ObjectRefKey>(), null);

        public IReadOnlyList<ObjectRefKey> SortedUniqueTargets { get; }
        public ObjectRefKey? ActiveTarget { get; }
        public bool IsEmpty => SortedUniqueTargets.Count == 0;

        private readonly string _canonicalString;
        private readonly int _hashCode;

        private SelectionComparisonKey(ObjectRefKey[] sortedUniqueTargets, ObjectRefKey? activeTarget)
        {
            SortedUniqueTargets = sortedUniqueTargets;
            ActiveTarget = activeTarget;

            if (sortedUniqueTargets.Length == 0)
            {
                _canonicalString = "<empty>";
            }
            else
            {
                var targetsPart = string.Join(",", sortedUniqueTargets.Select(k => k.Value));
                var activePart = activeTarget.HasValue ? activeTarget.Value.Value : string.Empty;
                _canonicalString = targetsPart + "|active:" + activePart;
            }

            _hashCode = StringComparer.Ordinal.GetHashCode(_canonicalString);
        }

        public static SelectionComparisonKey Create(IEnumerable<ObjectRefKey> targets, ObjectRefKey? activeTarget)
        {
            if (targets == null)
            {
                return Empty;
            }

            var set = new HashSet<ObjectRefKey>();
            foreach (var key in targets)
            {
                if (key.IsValid)
                {
                    set.Add(key);
                }
            }

            if (set.Count == 0)
            {
                return Empty;
            }

            var sorted = set.ToArray();
            Array.Sort(sorted);

            ObjectRefKey? normalizedActive = activeTarget.HasValue && activeTarget.Value.IsValid && set.Contains(activeTarget.Value)
                ? activeTarget
                : (ObjectRefKey?)sorted[0];

            return new SelectionComparisonKey(sorted, normalizedActive);
        }

        /// <summary>
        /// Compares target sets ignoring the active target (useful when an adapter cannot read back active target).
        /// </summary>
        public bool TargetSetEquals(SelectionComparisonKey other)
        {
            if (other == null) return false;
            if (SortedUniqueTargets.Count != other.SortedUniqueTargets.Count) return false;
            for (int i = 0; i < SortedUniqueTargets.Count; i++)
            {
                if (!SortedUniqueTargets[i].Equals(other.SortedUniqueTargets[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public bool Equals(SelectionComparisonKey other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null) return false;
            return string.Equals(_canonicalString, other._canonicalString, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is SelectionComparisonKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

        public override string ToString() => _canonicalString;

        public static bool operator ==(SelectionComparisonKey left, SelectionComparisonKey right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            return left.Equals(right);
        }

        public static bool operator !=(SelectionComparisonKey left, SelectionComparisonKey right) => !(left == right);
    }
}
