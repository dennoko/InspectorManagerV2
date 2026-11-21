using System;
using System.Collections.Generic;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.Presentation
{
    /// <summary>
    /// Japanese (ja) and English (en) UI localization table for Inspector Manager V2.
    /// </summary>
    public static class Localization
    {
        private static readonly Dictionary<string, (string Ja, string En)> s_table = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["WindowTitle"] = ("Inspector Manager V2", "Inspector Manager V2"),
            ["Start"] = ("開始", "Start"),
            ["Stop"] = ("終了", "Stop"),
            ["RetryRestore"] = ("復元を再試行", "Retry Restore"),
            ["AddInspector"] = ("+ 追加", "+ Add"),
            ["AddInspectorTooltip"] = ("新しい Inspector を追加して管理対象にする", "Create and manage a new Inspector"),
            ["RefreshTooltip"] = ("Inspector 一覧を再検出", "Refresh Inspector list"),
            ["ResetLayoutTooltip"] = ("並び順・除外状態を初期値に戻す", "Reset order and exclusions"),
            ["MenuRefresh"] = ("Inspector 一覧を再検出", "Refresh Inspector List"),
            ["MenuResetLayout"] = ("配置・除外状態をリセット", "Reset Order & Exclusions"),

            ["StatusOff"] = ("停止中 (Off)", "Off"),
            ["StatusStarting"] = ("開始処理中…", "Starting..."),
            ["StatusRunning"] = ("稼働中", "Running"),
            ["StatusSuspended"] = ("安全停止中 (再読込/Play)", "Suspended"),
            ["StatusStopping"] = ("終了処理中…", "Stopping..."),
            ["StatusFaulted"] = ("障害・復元未完了", "Faulted (Incomplete Restore)"),
            ["RunningBannerDetail"] = ("履歴モード (MRU)", "History Mode (MRU)"),

            ["ManagedInspectors"] = ("管理対象 Inspector ({0})", "Managed Inspectors ({0})"),
            ["ExternalInspectors"] = ("外部 Inspector ({0})", "External Inspectors ({0})"),
            ["NoManagedInspectors"] = ("管理対象の Inspector がありません", "No managed Inspectors."),
            ["ExternalHint"] = ("外部で開かれた Inspector は自動ロックしません。「管理に追加」で対象へ含めます。", "Externally opened Inspectors are never auto-locked. Click Manage to adopt."),
            ["HistoryMruHint"] = ("「-1」「-2」は異なる選択の最近使用順 (MRU) を表します。行クリックで該当 Inspector を表示します。", "'-1', '-2' represent distinct selections in MRU order. Click a row to focus that Inspector."),
            ["Adopt"] = ("+ 管理", "+ Manage"),
            ["FooterSummary"] = ("参加 {0}/{1}", "Active {0}/{1}"),

            ["RoleLatest"] = ("最新", "Latest"),
            ["BadgeExcluded"] = ("除外", "Excl"),
            ["BadgeUnlocked"] = ("解除", "Open"),
            ["BadgeFault"] = ("故障", "Err"),
            ["StateParticipating"] = ("参加中", "Active"),
            ["StateExcluded"] = ("除外中", "Excluded"),
            ["StateManuallyUnlocked"] = ("手動解除", "Unlocked"),
            ["StateFaultIsolated"] = ("故障隔離", "Faulted"),
            ["LabelInitialDisplay"] = ("開始時表示", "Initial Display"),
            ["LabelHistoryUnassigned"] = ("履歴未割当", "Unassigned"),
            ["EmptyTarget"] = ("(未表示)", "(Empty)"),

            ["LockOnTooltip"] = ("ロック中（クリックで解除）", "Locked (click to unlock)"),
            ["LockOffTooltip"] = ("ロック解除中（クリックでロック）", "Unlocked (click to lock)"),
            ["RowMenuTooltip"] = ("その他の操作", "More actions"),
            ["RowMenuExclude"] = ("ローテーションから除外", "Exclude from Rotation"),
            ["RowMenuInclude"] = ("ローテーションへ復帰", "Include in Rotation"),
            ["RowMenuFocus"] = ("Inspector を表示", "Focus Inspector"),
            ["RowMenuClose"] = ("ロックを復元して閉じる", "Restore Lock & Close"),
            ["BtnUpTooltip"] = ("優先順を上げる", "Move up"),
            ["BtnDownTooltip"] = ("優先順を下げる", "Move down"),

            ["AdvancedSettings"] = ("詳細設定", "Advanced Settings"),
            ["MaxRotHistory"] = ("履歴保持数", "History Capacity"),
            ["RecordSceneObjects"] = ("シーン内オブジェクトを記録", "Record Scene Objects"),
            ["RecordAssets"] = ("アセットを記録", "Record Assets"),
            ["ShowOverlays"] = ("各 Inspector にオーバーレイを表示", "Show Inspector Overlays"),
            ["BlockedCategoriesHeader"] = ("カテゴリ別更新ブロック", "Category Update Blocking"),
            ["MenuBlockedCategoriesCount"] = ("カテゴリ別更新ブロック ({0})", "Blocked Categories ({0})"),
            ["MenuClearBlockedCategories"] = ("ブロックをすべて解除", "Clear All Blocks"),

            ["PendingVerifyBanner"] = ("Inspector の反映を検証中…", "Verifying Inspector update..."),
            ["RotationUnavailable"] = ("Unity 内部 API の能力不足のため履歴自動割当を利用できません。", "Auto-assignment disabled due to missing Unity Inspector API capabilities."),
            ["PersistenceBlocked"] = ("設定ファイルが新しいスキーマ版のため上書き保存を停止しています。", "Saving paused because settings file uses a newer schema version.")
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
