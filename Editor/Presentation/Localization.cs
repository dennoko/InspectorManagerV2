using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Presentation
{
    /// <summary>
    /// Complete Japanese (ja) and English (en) UI localization table (Section 14.3).
    /// </summary>
    public static class Localization
    {
        private static readonly Dictionary<string, (string Ja, string En)> s_table = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["WindowTitle"] = ("Inspector Manager V2", "Inspector Manager V2"),
            ["Start"] = ("開始", "Start"),
            ["Stop"] = ("終了", "Stop"),
            ["Pause"] = ("一時停止", "Pause"),
            ["Resume"] = ("再開", "Resume"),
            ["RetryRestore"] = ("復元を再試行", "Retry Restore"),
            ["AddInspector"] = ("+ Inspector追加", "+ Add Inspector"),
            ["LockAll"] = ("一括ロック ({0})", "Lock All ({0})"),
            ["UnlockAll"] = ("一括解除 ({0})", "Unlock All ({0})"),
            ["ModeHistory"] = ("履歴モード", "History Mode"),
            ["ModeCycle"] = ("サイクルモード", "Cycle Mode"),
            ["ModeHistoryHint"] = ("※「1つ前」は異なる選択の最近使用順 (MRU) を表します。", "* 'Previous' shows distinct selections in MRU order."),
            ["ModeCycleHint"] = ("※ 更新成功が最も古い参加Inspectorを1つ更新します。", "* Updates the participating Inspector with the oldest update."),
            ["Back"] = ("◀ 戻る", "◀ Back"),
            ["Forward"] = ("進む ▶", "Forward ▶"),

            ["TabInspectors"] = ("Inspector一覧", "Inspectors"),
            ["TabHistory"] = ("選択履歴", "History"),
            ["TabFavorites"] = ("お気に入り", "Favorites"),
            ["TabSettings"] = ("設定・診断", "Settings & Diagnostics"),

            ["StatusOff"] = ("停止 (Off)", "Off"),
            ["StatusStarting"] = ("開始処理中", "Starting"),
            ["StatusRunning"] = ("稼働中", "Running"),
            ["StatusPaused"] = ("一時停止中", "Paused"),
            ["StatusSuspended"] = ("安全停止中 (再読込/Play)", "Suspended"),
            ["StatusStopping"] = ("終了処理中", "Stopping"),
            ["StatusFaulted"] = ("障害・復元未完了", "Faulted (Incomplete Restore)"),

            ["ManagedInspectors"] = ("管理対象 Inspector ({0})", "Managed Inspectors ({0})"),
            ["ExternalInspectors"] = ("外部で開かれた Inspector ({0}) — 自動ロックしません", "External Inspectors ({0}) — Not auto-locked"),
            ["NoManagedInspectors"] = ("管理対象のInspectorがありません。「+ Inspector追加」または下の外部Inspectorから管理に追加してください。", "No managed Inspectors. Click '+ Add Inspector' or adopt an external Inspector below."),
            ["Adopt"] = ("管理に追加", "Manage"),
            ["ResetLayout"] = ("配置・除外をリセット", "Reset Layout"),
            ["Refresh"] = ("再検出", "Refresh"),

            ["RoleLatest"] = ("最新", "Latest"),
            ["RoleCycleNext"] = ("次候補", "Next"),
            ["RoleCycle"] = ("サイクル", "Cycle"),
            ["StateParticipating"] = ("参加中", "Active"),
            ["StateExcluded"] = ("除外中", "Excluded"),
            ["StateManuallyUnlocked"] = ("手動解除", "Unlocked"),
            ["StateFaultIsolated"] = ("故障隔離", "Faulted"),
            ["LabelInitialDisplay"] = ("開始時表示", "Initial Display"),
            ["LabelHistoryUnassigned"] = ("履歴未割当", "History Unassigned"),

            ["BtnLock"] = ("🔒", "🔒"),
            ["BtnUnlock"] = ("🔓", "🔓"),
            ["BtnExclude"] = ("除外", "Exclude"),
            ["BtnInclude"] = ("復帰", "Include"),
            ["BtnFocus"] = ("表示", "Focus"),
            ["BtnClose"] = ("×", "×"),
            ["ManualUnlockHint"] = ("※ 管理中の手動ロック解除にはManagerまたはCtrl+L/オーバーレイを使用してください。", "* Use Manager, Ctrl+L, or Overlay to manually unlock during rotation."),

            ["HistoryEmpty"] = ("選択履歴はまだありません（現在のEditorセッション内のみ保持されます）。", "No selection history yet (kept for the current Editor session only)."),
            ["ClearHistory"] = ("履歴をクリア", "Clear History"),
            ["DragHint"] = ("クリックで選択移動 / ドラッグでUnityへドロップ可能", "Click to navigate / Drag to drop into Unity"),

            ["AddCurrentFavorite"] = ("現在の選択をお気に入りに追加", "Add Current Selection"),
            ["FavoriteDropZone"] = ("ここにGameObjectまたはアセットをドラッグ＆ドロップして登録", "Drag & Drop GameObjects or Assets here to add"),
            ["FavoritesEmpty"] = ("お気に入りは登録されていません。", "No favorites registered."),
            ["FavResolved"] = ("解決済み", "Resolved"),
            ["FavTemporary"] = ("一時登録(未保存)", "Temporary (Unsaved)"),
            ["FavSceneUnloaded"] = ("シーン未ロード", "Scene Unloaded"),
            ["FavMissing"] = ("欠落/削除", "Missing"),
            ["FavFailed"] = ("解決失敗", "Failed"),
            ["BtnSelect"] = ("選択", "Select"),
            ["BtnPing"] = ("強調", "Ping"),
            ["BtnRemove"] = ("削除", "Remove"),

            ["SettingsHeader"] = ("基本設定", "General Settings"),
            ["Language"] = ("表示言語 / Language", "Language / 表示言語"),
            ["MaxNavHistory"] = ("選択履歴の最大件数", "Max Navigation History"),
            ["MaxRotHistory"] = ("ローテーション履歴の最大件数", "Max Rotation History"),
            ["RecordSceneObjects"] = ("シーン内オブジェクトを記録", "Record Scene Objects"),
            ["RecordAssets"] = ("アセットを記録", "Record Assets"),
            ["AutoFocus"] = ("更新時にInspectorへ自動フォーカス（連続選択中は奪わない）", "Auto-focus updated Inspector (respects continuous selection)"),
            ["ShowOverlays"] = ("各Inspectorにオーバーレイを表示", "Show Inspector Overlays"),
            ["AutoCleanupFavs"] = ("削除済みお気に入りを自動クリーンアップ（未ロードシーンは除外）", "Auto-cleanup deleted favorites (never removes unloaded scenes)"),
            ["BlockedCategoriesHeader"] = ("カテゴリ別更新ブロック", "Category Update Blocking"),
            ["BlockedCategoriesHint"] = ("選択中の有効対象すべてがブロック対象の場合のみ更新を抑止します（例：フォルダ+GameObjectの混合選択は全体を表示）。", "Blocks assignment only when ALL selected targets are blocked (e.g. Folder + GameObject mixed selection passes through)."),
            ["ResetSettings"] = ("設定を既定値に戻す", "Reset Settings to Defaults"),

            ["DiagnosticsHeader"] = ("診断・能力・メトリクス", "Diagnostics, Capabilities & Metrics"),
            ["CopyDiagnostics"] = ("診断情報をコピー", "Copy Diagnostics"),
            ["ClearLogs"] = ("ログをクリア", "Clear Logs"),
            ["PendingVerifyBanner"] = ("検証待機中 (PresentationPending)...", "Awaiting Inspector verification (PresentationPending)..."),
            ["RotationUnavailable"] = ("内部API能力不足のため自動ローテーションは無効です。履歴・お気に入りは継続利用できます。", "Auto-rotation disabled due to missing internal API capabilities. History & Favorites remain available."),
            ["PersistenceBlocked"] = ("保存ファイルが新しいスキーマ版のため上書き保存を停止しています。", "Saving paused because storage file uses a newer schema version.")
        };

        public static string Get(string lang, string key)
        {
            if (s_table.TryGetValue(key, out var pair))
            {
                return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? pair.En : pair.Ja;
            }
            return key;
        }

        public static string Format(string lang, string key, params object[] args)
        {
            return string.Format(Get(lang, key), args);
        }

        public static string LifecycleLabel(string lang, RuntimeLifecycleState state)
        {
            switch (state)
            {
                case RuntimeLifecycleState.Off: return Get(lang, "StatusOff");
                case RuntimeLifecycleState.Starting: return Get(lang, "StatusStarting");
                case RuntimeLifecycleState.Running: return Get(lang, "StatusRunning");
                case RuntimeLifecycleState.Paused: return Get(lang, "StatusPaused");
                case RuntimeLifecycleState.Suspended: return Get(lang, "StatusSuspended");
                case RuntimeLifecycleState.Stopping: return Get(lang, "StatusStopping");
                case RuntimeLifecycleState.Faulted: return Get(lang, "StatusFaulted");
                default: return state.ToString();
            }
        }

        public static string CategoryLabel(string lang, ObjectCategory category)
        {
            bool en = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
            switch (category)
            {
                case ObjectCategory.GameObject: return "GameObject";
                case ObjectCategory.Component: return en ? "Component" : "コンポーネント";
                case ObjectCategory.Prefab: return "Prefab";
                case ObjectCategory.Material: return en ? "Material" : "マテリアル";
                case ObjectCategory.Texture: return en ? "Texture" : "テクスチャ";
                case ObjectCategory.Shader: return en ? "Shader" : "シェーダー";
                case ObjectCategory.Audio: return en ? "Audio" : "オーディオ";
                case ObjectCategory.Animation: return en ? "Animation" : "アニメーション";
                case ObjectCategory.Script: return en ? "Script" : "スクリプト";
                case ObjectCategory.ScriptableObject: return "ScriptableObject";
                case ObjectCategory.Folder: return en ? "Folder" : "フォルダ";
                case ObjectCategory.Scene: return en ? "Scene" : "シーン";
                case ObjectCategory.Mesh: return en ? "Mesh" : "メッシュ";
                case ObjectCategory.Font: return en ? "Font" : "フォント";
                case ObjectCategory.OtherAsset: return en ? "Other Asset" : "その他アセット";
                default: return category.ToString();
            }
        }
    }
}
