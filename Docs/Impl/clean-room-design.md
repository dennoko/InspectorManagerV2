# Inspector Manager V2 設計仕様（シンプル化改訂版）

## 1. 目的とスコープ

Inspector Manager V2 は、複数の Unity 標準 Inspector ウィンドウに対して **履歴モード（重複なし MRU）** で現在と過去の選択を自動割当し、**管理対象 Inspector の順序と状態（ロック・除外・追加・閉鎖）** を安全に管理することに特化したシンプルな Editor 拡張である。

対象環境は **Unity 2022.3.22f1** および **Unity 6（6000.0〜6000.3+ の `EntityId` / `EditorLockTracker` 対応を含む）**、.NET Standard 2.0 / 2.1。グローバル `Selection` は一切書き換えない。

### 採用する機能（コア）

| 機能 | 保証する動作 |
| --- | --- |
| 履歴モード（MRU 固定） | 参加中の管理対象 Inspector を役割順に並べ、最新・`-1`・`-2` の異なる選択集合を自動割当する。表示番号 `#N` は並び替えで変わらない |
| 複数選択 | 選択集合を欠落させず Unity 標準 Inspector へ渡す |
| 管理対象 Inspector の順序・状態管理 | 追加、外部 Inspector の明示的管理追加、並び替え、明示除外、個別ロック切替、閉じる（ロック復元後）、配置・除外リセット |
| 標準南京錠 UI との協調 | フォーカス中・ホバー中の Inspector で観測したアンロックは手動解除（離脱）として扱い、再ロックで復帰する。原因不明のロック落ち（トラッカー再生成等）のみ自動回復する |
| タブ連動ライフサイクル | ツールタブが開いている間のみ動作し、Unity 起動時やタブを開いた直後は既定で開始状態（`Running`）になる。タブを閉じた（削除した）場合は動作を停止し、管理対象 Inspector のロックをすべて解除する |
| 境界時のロック復元 | 手動「終了」、Assembly Reload、Play Mode 遷移、Editor 終了、更新失敗時は生存する同一 Inspector のロックを開始前の状態へ復元する |
| 各 Inspector オーバーレイ | `#N` バッジ・役割/状態・個別ロックボタン・タイムスタンプ更新型の更新フラッシュを表示する（詳細設定で ON/OFF 可） |
| 詳細設定（折りたたみ式） | 履歴保持数、記録対象（シーン / アセット）、オーバーレイ表示、カテゴリ別更新ブロック（15種・混合選択対応）、日本語/英語切替 |
| 内部 API 非互換時の保護 | 起動時は読取専用プローブのみ行い、必須能力が欠ける場合は自動割当を無効化して安全に待機する |

### 意図的にオミット（廃止）した機能

シンプルさと保守性を優先し、以下は仕様から除外している：

- サイクルモード（Cycle Mode）
- 選択履歴タブ・戻る/進む・履歴からのドラッグ＆ドロップ（`NavigationHistory`）
- お気に入り機能・永続参照解決（`Favorites` / `GlobalObjectId`）
- `Ctrl+L` グローバルショートカット
- 一時停止 / 再開（Pause / Resume：開始と終了のみ）
- 一括ロック / 一括解除
- 更新時の Inspector 自動フォーカス
- 診断パネル・メトリクス・ログビューア（障害時バナーのみ表示）

---

## 2. アーキテクチャ

```text
Presentation (UI Toolkit: dennokoworks-design UXML/USS)
          │ Command
Unity events → UnityLifecycleIngress → 単一の有界処理キュー (Coordinator)
                                              │
                                     StateReducer（純粋関数）
                                              │
                                       AssignmentPlan
                                              │
                                        PlanExecutor
                                     │                │
                              UnityAdapter       AtomicJsonFileStore
                                     │                │
                                     └── 確定状態 ─────┘
                                              │
                                 UiSnapshot / Overlay Sync
```

- **Domain**（`noEngineReferences: true`）: 選択集合比較、フィルタ、履歴割当計画、純粋状態遷移（`StateReducer`）。Unity API・時刻・I/O・UI 依存を持たない。
- **Application**（`noEngineReferences: true`）: 単一有界キュー（`InspectorManagerCoordinator`）、書込み前検証・書込み省略キャッシュ・`PresentationPending` 遅延検証・逆順補償＋Lease復元（`PlanExecutor`）、原子的永続化（`AtomicJsonFileStore`）。
- **UnityAdapter**: `InspectorReflectionAdapter`（Unity 2022.3 / Unity 6 互換）、`UnityObjectIdHelper`（`InstanceID` / 6000.3+ `EntityId`）、`UnityCategoryClassifier`、`UnitySelectionAdapter`（読取専用）、`UnityOverlayManager`、`UnityLifecycleIngress`、`UnitySessionStateStore`、`UnityProjectPaths`。
- **Presentation**: `dennokoworks-design`（UI Toolkit / UXML / USS / `DennokoUIFont` / `DennokoAppBar` / `DennokoStatus` / `DennokoBlockUI`）によるダーク固定フローティング UI。1 画面構成で管理対象 Inspector 一覧・外部 Inspector 一覧・折りたたみ式「詳細設定（`.dennoko-details` + 要約チップ）」を備える。
- **Composition**: `[InitializeOnLoad]` Composition Root（`InspectorManagerBootstrap`）。単一ランタイムを組み立て、最後の Manager ウィンドウ閉鎖時に自動終了・ロック復元を行う。

---

## 3. 状態モデルと不変条件

### ライフサイクル状態
`Off / Starting / Running / Suspended / Stopping / Faulted`

- **Off → Starting → Running**: 全管理対象の現在ロックと実表示を読取り、`RecoveryJournal` を即時保存してからロックを取得する。一部失敗時は取得済み Lease を復元して `Off`（復元失敗が残れば `Faulted`）へ戻る。開始時に前セッションの `IsManuallyUnlocked`・`IsFaultIsolated` をクリアし、ロック済みなのに割当されないウィンドウを作らない。
- **Running → Stopping / Suspended → Off / Faulted**: 新規更新を停止し、全 `LockLease` の開始前ロックを復元する。`RetryRestore` は未復元分のみ再試行し、自動で `Running` へは戻らない。
- **明示除外と手動解除の独立**:
  - 除外は `Running` 中のみ Lease 下でロックして表示を固定する（`Off` 中はフラグのみ記録し、未管理ロックを書き込まない）。
  - 再ロックは `IsManuallyUnlocked` のみを解除し、`IsExplicitlyExcluded` は維持する。
- **常に守る条件**:
  1. Unity API 操作はすべてメインスレッドで行い、同時に実行する更新計画は 1 つだけとする（`PresentationPending` 検証中は新規書込み計画を開始しない）。
  2. 復元元ロック（`OriginalLocked`）は最初の管理開始時に 1 度だけ記録し、並び替えや手動操作で上書きしない。
  3. 古い世代（`Generation`）の遅延処理・タイムアウト結果は破棄する。
  4. `A → 空 → A` の再選択を重複通知として捨てない（空選択は割当を維持しつつ直前比較キーを空に更新する）。
  5. カテゴリブロックは有効対象すべてがブロック対象の場合のみ抑止し、1 つでも許可対象があれば混合選択集合全体を渡す。カテゴリ判定失敗（`ClassificationFailed`）は通常ブロックと区別する。
  6. 複数 Inspector 更新の途中失敗時は、既に書き換えた生存 Inspector を逆順で直前表示・ロックへ補償し、全 Lease を復元して自動割当を停止する。

---

## 4. 永続化と再読込耐性

| 対象 | 保存先 | 方針 |
| --- | --- | --- |
| 詳細設定（言語・履歴保持数・記録範囲・オーバーレイ・ブロックカテゴリ） | プロジェクト識別子別の利用者ローカル領域（`Assets/` 外） | スキーマ版＋SHA-256 検証、`.tmp` 読戻し検証→`.bak` 退避→置換。破損時は隔離して既定値、未知の新スキーマ時は上書き停止 |
| ロック復旧記録（`RecoveryJournal`） | 同上（即時同期書込み専用経路） | Lease 取得・ロック書込み前に必ず保存。保存失敗時は Lease を取得しない。同一 `EditorSessionId` 内（再読込後）のみ自動復旧し、Editor 再起動をまたぐ一時 ID でのロック変更は禁止 |
| セッション内配置（固定番号・役割順・除外状態） | `UnityEditor.SessionState` | Assembly Reload をまたいで維持し、Editor 再起動で自動消去。再読込後は `Off` から利用者が再開する |

---

## 5. 検証基準

- **純粋ロジック & ランダム不変条件テスト**（[DomainLogicTests.cs](../../Tests/Editor/DomainLogicTests.cs)）: 履歴モード MRU・固定番号維持・`A → 空 → A`・混合カテゴリフィルタ・除外と手動解除の独立性・250ステップランダム操作での不変条件維持。
- **障害注入 & 回帰テスト**（[FaultInjectionAndRegressionTests.cs](../../Tests/Editor/FaultInjectionAndRegressionTests.cs)）: 途中失敗時の逆順補償、補償中のウィンドウ閉鎖、復元失敗→`Faulted`→`RetryRestore`→`Off`、読取専用ストレージ時のLease拒否、`PresentationPending` 中断後の遅延検証破棄、トラッカー再生成ロック落ち回復、標準南京錠のフォーカス/ホバー手動解除と再ロック復帰、読取不能ロック状態の誤検知防止、キュー上限停止、再読込境界・Editor再起動境界、原子的永続化の破損/新スキーマ耐性。
- **Unity 実機 EditMode 統合テスト**（[UnityIntegrationTests.cs](../../Tests/Editor/UnityIntegrationTests.cs)）: 読取専用能力プローブ、隔離 Inspector への複数選択割当（グローバル `Selection` 非変更）・読戻し・ロック復元、カテゴリ分類。
