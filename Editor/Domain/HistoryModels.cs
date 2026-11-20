using System;
using System.Collections.Generic;
using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    /// <summary>
    /// Chronological user selection history supporting Back/Forward cursor navigation.
    /// Empty selections are never recorded. Branching after Back discards forward entries.
    /// </summary>
    public sealed class NavigationHistoryState
    {
        public static readonly NavigationHistoryState Empty = new NavigationHistoryState(Array.Empty<SelectionSnapshot>(), -1);

        public IReadOnlyList<SelectionSnapshot> Entries { get; }
        public int CursorIndex { get; }

        public bool CanGoBack => CursorIndex > 0 && Entries.Count > 1;
        public bool CanGoForward => CursorIndex >= 0 && CursorIndex < Entries.Count - 1;
        public SelectionSnapshot Current => CursorIndex >= 0 && CursorIndex < Entries.Count ? Entries[CursorIndex] : null;

        public NavigationHistoryState(IReadOnlyList<SelectionSnapshot> entries, int cursorIndex)
        {
            Entries = entries ?? Array.Empty<SelectionSnapshot>();
            if (Entries.Count == 0)
            {
                CursorIndex = -1;
            }
            else if (cursorIndex < 0)
            {
                CursorIndex = 0;
            }
            else if (cursorIndex >= Entries.Count)
            {
                CursorIndex = Entries.Count - 1;
            }
            else
            {
                CursorIndex = cursorIndex;
            }
        }

        public NavigationHistoryState RecordUserSelection(SelectionSnapshot snapshot, int maxEntries)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return this;
            }

            if (maxEntries < 1)
            {
                maxEntries = 1;
            }

            // Do not duplicate immediately identical cursor entry.
            if (CursorIndex >= 0 && CursorIndex < Entries.Count
                && Entries[CursorIndex].ComparisonKey == snapshot.ComparisonKey)
            {
                return this;
            }

            var list = new List<SelectionSnapshot>();
            int copyCount = CursorIndex >= 0 ? Math.Min(CursorIndex + 1, Entries.Count) : 0;
            for (int i = 0; i < copyCount; i++)
            {
                list.Add(Entries[i]);
            }

            list.Add(snapshot);

            while (list.Count > maxEntries)
            {
                list.RemoveAt(0);
            }

            return new NavigationHistoryState(list.ToArray(), list.Count - 1);
        }

        public NavigationHistoryState MoveCursorTo(int index)
        {
            if (index < 0 || index >= Entries.Count)
            {
                return this;
            }

            return new NavigationHistoryState(Entries, index);
        }

        public NavigationHistoryState TrimToCapacity(int maxEntries)
        {
            if (maxEntries < 1) maxEntries = 1;
            if (Entries.Count <= maxEntries) return this;

            int excess = Entries.Count - maxEntries;
            var trimmed = Entries.Skip(excess).ToArray();
            int newCursor = Math.Max(0, CursorIndex - excess);
            return new NavigationHistoryState(trimmed, newCursor);
        }
    }

    public sealed class RotationHistoryEntry
    {
        public SelectionSnapshot Snapshot { get; }
        public bool IsInitialDisplay { get; }

        public RotationHistoryEntry(SelectionSnapshot snapshot, bool isInitialDisplay)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            IsInitialDisplay = isInitialDisplay;
        }
    }

    /// <summary>
    /// Deduplicated MRU selection list used for History rotation mode.
    /// Re-selecting A in A -> B -> A moves A to the front and removes the rear duplicate, yielding [A, B].
    /// </summary>
    public sealed class RotationHistoryState
    {
        public static readonly RotationHistoryState Empty = new RotationHistoryState(Array.Empty<RotationHistoryEntry>());

        public IReadOnlyList<RotationHistoryEntry> Entries { get; }

        public RotationHistoryState(IReadOnlyList<RotationHistoryEntry> entries)
        {
            Entries = entries ?? Array.Empty<RotationHistoryEntry>();
        }

        public RotationHistoryState PushFrontDeduplicated(SelectionSnapshot snapshot, bool isInitialDisplay, int maxEntries)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return this;
            }

            if (maxEntries < 1)
            {
                maxEntries = 1;
            }

            var list = new List<RotationHistoryEntry>(Entries.Count + 1)
            {
                new RotationHistoryEntry(snapshot, isInitialDisplay)
            };

            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Snapshot.ComparisonKey != snapshot.ComparisonKey)
                {
                    list.Add(Entries[i]);
                }

                if (list.Count >= maxEntries)
                {
                    break;
                }
            }

            return new RotationHistoryState(list.ToArray());
        }

        public RotationHistoryState PruneInvalid(Func<SelectionSnapshot, bool> isValidWholeSet, int maxEntries)
        {
            if (isValidWholeSet == null)
            {
                return this;
            }

            var list = new List<RotationHistoryEntry>(Entries.Count);
            var seen = new HashSet<SelectionComparisonKey>();

            for (int i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                if (entry.Snapshot == null || entry.Snapshot.IsEmpty)
                {
                    continue;
                }

                if (!seen.Add(entry.Snapshot.ComparisonKey))
                {
                    continue;
                }

                // Invalid selection sets are removed as a whole set; never shrunk to partial subset.
                if (isValidWholeSet(entry.Snapshot))
                {
                    list.Add(entry);
                    if (list.Count >= maxEntries)
                    {
                        break;
                    }
                }
            }

            return new RotationHistoryState(list.ToArray());
        }

        public static RotationHistoryState BuildSeeded(
            SelectionSnapshot currentSelection,
            IReadOnlyList<SelectionSnapshot> navigationHistoryEntries,
            IReadOnlyList<SelectionSnapshot> observedDisplays,
            Func<SelectionSnapshot, bool> isEligible,
            int maxEntries)
        {
            if (maxEntries < 1) maxEntries = 1;

            var list = new List<RotationHistoryEntry>();
            var seen = new HashSet<SelectionComparisonKey>();

            void TryAdd(SelectionSnapshot snapshot, bool isInitialDisplay)
            {
                if (list.Count >= maxEntries) return;
                if (snapshot == null || snapshot.IsEmpty) return;
                if (seen.Contains(snapshot.ComparisonKey)) return;
                if (isEligible != null && !isEligible(snapshot)) return;

                seen.Add(snapshot.ComparisonKey);
                list.Add(new RotationHistoryEntry(snapshot, isInitialDisplay));
            }

            // Section 5.3 exact seed priority:
            // 1. Current selection
            TryAdd(currentSelection, false);

            // 2. Recorded valid selection history (most recent first)
            if (navigationHistoryEntries != null)
            {
                for (int i = navigationHistoryEntries.Count - 1; i >= 0; i--)
                {
                    TryAdd(navigationHistoryEntries[i], false);
                }
            }

            // 3. Observed actual Inspector displays (distinguished as InitialDisplay)
            if (observedDisplays != null)
            {
                for (int i = 0; i < observedDisplays.Count; i++)
                {
                    TryAdd(observedDisplays[i], true);
                }
            }

            return new RotationHistoryState(list.ToArray());
        }
    }
}
