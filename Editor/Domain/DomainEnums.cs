namespace DennokoWorks.InspectorManagerV2.Domain
{
    public enum RuntimeLifecycleState
    {
        Off = 0,
        Starting = 1,
        Running = 2,
        Paused = 3,
        Suspended = 4,
        Stopping = 5,
        Faulted = 6
    }

    public enum RotationMode
    {
        History = 0,
        Cycle = 1
    }

    public enum SelectionSource
    {
        UserSelection = 0,
        HistoryNavigation = 1,
        SessionSeed = 2,
        RecoveryObservation = 3
    }

    public enum ObjectCategory
    {
        Unknown = 0,
        GameObject = 1,
        Component = 2,
        Prefab = 3,
        Material = 4,
        Texture = 5,
        Shader = 6,
        Audio = 7,
        Animation = 8,
        Script = 9,
        ScriptableObject = 10,
        Folder = 11,
        Scene = 12,
        Mesh = 13,
        Font = 14,
        OtherAsset = 15,
        ClassificationFailed = 16
    }

    public enum AssignmentLabel
    {
        None = 0,
        Assigned = 1,
        InitialDisplay = 2,
        HistoryUnassigned = 3
    }

    public enum LeaseRestorationStatus
    {
        None = 0,
        Acquired = 1,
        Restored = 2,
        RestoreFailed = 3,
        WindowDisposed = 4
    }

    public enum FavoriteResolutionState
    {
        Resolved = 0,
        TemporaryUnsaved = 1,
        SceneUnloaded = 2,
        DeletedOrMissing = 3,
        ResolutionFailed = 4
    }

    public enum SelectionFilterOutcome
    {
        Allowed = 0,
        EmptySelection = 1,
        BlockedByRecordScope = 2,
        BlockedByCategory = 3,
        BlockedByClassificationFailure = 4
    }

    public enum WindowWriteStatus
    {
        Skipped = 0,
        Written = 1,
        Verified = 2,
        PresentationPending = 3,
        Failed = 4
    }

    public enum StopReason
    {
        UserRequested = 0,
        AssemblyReload = 1,
        PlayModeTransition = 2,
        LastManagerWindowClosed = 3,
        EditorQuitting = 4,
        ExecutionFailure = 5,
        QueueOverflow = 6,
        CapabilityLost = 7
    }
}
