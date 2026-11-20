using System;
using System.Collections.Generic;

namespace DennokoWorks.InspectorManagerV2.Domain
{
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
    /// Deduplicated MRU selection list used for History mode assignment.
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

            // Priority 1: Current live selection
            TryAdd(currentSelection, false);

            // Priority 2: Observed actual Inspector displays (marked as InitialDisplay)
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
