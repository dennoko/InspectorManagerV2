using System.Linq;

namespace DennokoWorks.InspectorManagerV2.Domain
{
    public readonly struct SelectionFilterResult
    {
        public SelectionFilterOutcome Outcome { get; }
        public string Reason { get; }
        public bool IsAllowed => Outcome == SelectionFilterOutcome.Allowed;

        public SelectionFilterResult(SelectionFilterOutcome outcome, string reason)
        {
            Outcome = outcome;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Evaluates whether a selection snapshot may be assigned to managed Inspectors.
    /// Section 5.5: Blocks only when ALL valid targets are blocked; if at least one target is permitted,
    /// the entire original selection set (including blocked items like Folders) is passed through.
    /// Classification failure is reported distinctly from normal category blocking.
    /// </summary>
    public static class SelectionFilter
    {
        public static SelectionFilterResult Evaluate(SelectionSnapshot snapshot, ManagerSettings settings)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return new SelectionFilterResult(SelectionFilterOutcome.EmptySelection, "Empty selection");
            }

            if (settings == null)
            {
                return new SelectionFilterResult(SelectionFilterOutcome.Allowed, string.Empty);
            }

            bool hasAnyClassificationFailure = false;
            bool hasAnyScopeAllowed = false;
            bool hasAnyCategoryAllowed = false;

            for (int i = 0; i < snapshot.OrderedTargets.Count; i++)
            {
                var item = snapshot.OrderedTargets[i];
                if (item.ClassificationFailed)
                {
                    hasAnyClassificationFailure = true;
                    continue;
                }

                bool scopeOk = item.IsAsset ? settings.RecordAssets : settings.RecordSceneObjects;
                if (!scopeOk)
                {
                    continue;
                }

                hasAnyScopeAllowed = true;

                bool categoryBlocked = settings.BlockedCategories != null && settings.BlockedCategories.Contains(item.Category);
                if (!categoryBlocked)
                {
                    hasAnyCategoryAllowed = true;
                }
            }

            if (hasAnyClassificationFailure)
            {
                return new SelectionFilterResult(
                    SelectionFilterOutcome.BlockedByClassificationFailure,
                    "Category classification failed; assignment suppressed.");
            }

            if (!hasAnyScopeAllowed)
            {
                return new SelectionFilterResult(
                    SelectionFilterOutcome.BlockedByRecordScope,
                    "Selection suppressed by record scope setting.");
            }

            if (!hasAnyCategoryAllowed)
            {
                return new SelectionFilterResult(
                    SelectionFilterOutcome.BlockedByCategory,
                    "All selected objects belong to blocked categories.");
            }

            return new SelectionFilterResult(SelectionFilterOutcome.Allowed, string.Empty);
        }
    }
}
