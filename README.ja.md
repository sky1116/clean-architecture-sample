# 競技プラットフォーム — Clean Architecture 実践サンプル

[English](README.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

> Robert C. Martin 著『Clean Architecture』の原則に基づき、公開教育用に一から実装する大会管理・ランキング・個人履歴のサンプルです。**本番運用可能な不正対策システムであるとは主張しません。**

## 目的

認証された参加者が大会に登録し、検証可能な証拠を伴う結果を提出します。システムは判定履歴を保持し、決定的な順位を計算して個人履歴を公開します。無効な入力、不正操作、権限逸脱、再送、競合、期限切れ、外部サービス障害を最初から扱います。

**最優先原則：ソースコードの依存関係は内側だけを向く。** ビジネスルールは HTTP、データベース、フレームワーク、Logger に依存しません。オブジェクトの組み立ては最外層の Composition Root が担当します。

## 基本となる二つの図

### 1. 同心円と依存性ルール

![独自に描いた Clean Architecture 四層図：依存関係は内側に向かう](docs/assets/architecture-rings.svg)

- **Entities（企業全体のビジネスルール）**：技術に依存しない競技ルールと不変条件。
- **Use Cases（アプリケーションのビジネスルール）**：登録、結果検証、ランキング、履歴。
- **Interface Adapters（インターフェースアダプター）**：Controller、Presenter、Gateway 実装、View Model。
- **Frameworks & Drivers（フレームワークとドライバー）**：ASP.NET Core、EF Core、PostgreSQL、Telemetry。

図中の矢印は**ソースコードの依存関係**を示し、実行時の制御フローと同じ意味ではありません。

### 2. 典型的なリクエスト／レスポンス

![Controller、Input Boundary、Interactor、Output Boundary、Presenter、Gateway の独自設計フロー図](docs/assets/request-boundaries.svg)

1. **Controller** は HTTP データを単純な **Input Data** に変換し、**Input Boundary** を呼び出します。
2. **Use Case Interactor** はアプリケーションの規則を実行し、必要に応じて **Entities** を利用します。
3. Interactor は内側で定義された **Gateway interface** に依存します。PostgreSQL Adapter が外側で実装します。
4. Interactor は **Output Data** を **Output Boundary** に渡します。外側の **Presenter** が実装します。
5. Presenter は **View Model** を生成します。Web Host が HTTP 応答を作成し、Use Case は HTTP と EF Core を知りません。

**依存性逆転**により、実行時にはインターフェースを通じて外側の実装を呼び出しても、ソースコードの依存方向は内側に保たれます。

## プロジェクト構成

| 層 | プロジェクト | 責務 |
| --- | --- | --- |
| Entities | `Competition.Domain` | 業務ルールと不変条件、フレームワーク非依存 |
| Use Cases | `Competition.Application` | Interactor、Input/Output Boundary、Data、Port |
| Interface Adapters | `Competition.Adapters` | Presenter と View Model |
| Frameworks & Drivers | `Competition.Infrastructure`、`Competition.Web` | PostgreSQL、EF Core、ASP.NET Core、ログとテレメトリ |
| Tests | `Competition.*.Tests` | 正常／異常／権限／依存性の実証 |

Repository の**インターフェース**は必要とする内側の層で定義し、EF Core による**実装**は外側に置きます。Domain は `ILogger` や HTTP DTO に依存しません。

## ユースケースと防御

| Use Case | 規則 | 異常・不正対策 |
| --- | --- | --- |
| Create Competition | 開催期間と採点方針 | 不正な期間、識別子の重複 |
| Register Participant | 本人と参加資格 | 権限不足、締切、重複登録 |
| Submit Result | 信頼できる証拠の検証 | 虚偽スコア、リプレイ、冪等キー競合 |
| Get Leaderboard | 決定的な順位と同点処理 | ページ制限、暫定／確定順位 |
| Get Personal History | 不変の判定履歴 | 権限、訂正履歴、競合 |
| Correct Result | 権限のある訂正と監査 | バージョン競合、理由の欠落、トランザクション |

**クライアントから送られたスコアを信頼してはいけません。** 認証は送信者の識別であり、実際の競技結果の証明ではありません。信頼できる権威による証拠か、サーバー側で独立検証可能なルールが必要です。署名鍵をプレイヤーへ配布しません。

**ログと監査記録は別物です。** 構造化ログと Trace は運用調査用、ビジネス監査は判定履歴用です。秘密情報や無制限のユーザー入力をログへ出力しません。

## テストと品質ゲート

- Domain：境界値、状態遷移、攻撃的な入力、同点処理。
- Interactor：厳密な Port Fake による認可、冪等性、Output Boundary の検証。
- Architecture：不正な依存関係を**失敗として検出**するテスト。
- PostgreSQL integration：本物の Testcontainers による一意制約、トランザクション、競合の検証。InMemory で代用しません。
- HTTP：認証付き操作とエラー応答。
- Negative control：意図的な不具合を検出できることの証明。
- 公開前スキャン：社外管理の禁止識別子一覧を参照し、その一覧自体は公開しません。

CI の成功を確認するまでは、**全テスト PASS と主張しません**。

## 開発環境

.NET 10 SDK が必要です。PostgreSQL の統合テストには Docker を使います。Solution 作成後、リポジトリ直下から `dotnet test` を実行します。接続文字列や認証情報は環境変数で供給し、コミットしません。

## 書籍との対応表

| 原則の参照先 | 実装上の対応 |
| --- | --- |
| Ch. 20 Business Rules | Domain と不変条件 |
| Ch. 22 The Clean Architecture | 内向き依存、Input/Output Boundary、Presenter、Gateway |
| Ch. 23 Presenters and Humble Objects | Presenter が View Model を形成 |
| Ch. 26 The Main Component | Web Host が構成を組み立てる |
| Ch. 28 The Test Boundary | 境界ごとの独立した検証 |
| Ch. 30–32 | DB・Web・Framework は外側の詳細 |

**参照元：** Robert C. Martin『Clean Architecture: A Craftsman's Guide to Software Structure and Design』（Pearson、2017年）、[著者の原文記事](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)。二つの SVG は概念を説明するため**独自に作成した図**であり、出版物の画像の転載ではありません。技術選定、不正対策、Outbox、Telemetry は本サンプルの設計判断であり、書籍が具体的な製品を指定したものではありません。

## 非目標

明確な境界を持つモジュラーモノリスを扱います。信頼できる証拠がない限りスコアの真実性を保証できず、メッセージ配送の Exactly Once も保証しません。実装とテストで確認できる事実を優先します。

## 実装済みの HTTP 契約

| ルート | 認証／認可 | 実装された動作 |
| --- | --- | --- |
| `POST /api/competitions` | OIDC + competition-admin ロール | 開催期間・得点上限を持つ大会を作成 |
| `POST /api/competitions/{id}/participants` | OIDC | 検証済みの本人として参加登録 |
| `POST /api/competitions/{id}/results` | OIDC + レート制限 | `Idempotency-Key` と `OpaqueEvidence` が必要 |
| `POST /api/competitions/{id}/results/{resultId}/corrections` | competition-admin ロール | バージョン検証付きの監査可能な訂正 |
| `GET /api/competitions/{id}/leaderboard` | 公開 | 訂正を反映した有効得点から順位を算出 |
| `GET /api/competitions/{id}/me/history` | OIDC | 本人の原本と訂正履歴のみ表示 |
| `GET /health` | 公開 | プロセスの生存のみ確認。**DB の正常性を保証しません** |

### 記録の信頼性と整合性

**信頼できる証明書（receipt）の Adapter** は HMAC 署名を検証し、大会、参加者、得点、証明 ID、発行／有効期限を紐付けます。署名秘密鍵は**独立した信頼できる判定サーバーのみ**が保持し、プレイヤー・ブラウザー・モバイルアプリに渡してはいけません。署名が証明するのは発行元であり、**ゲーム内の不正行為がなかったこと自体ではありません**。本サンプルにゲームの判定サーバーや証拠の発行機能は含まれません。

冪等キーは要求の証拠と結び付きます。PostgreSQL Adapter は承認された結果、冪等処理の記録、業務監査履歴、配信待ちのバージョン付き Outbox イベントを**同じトランザクション**で保存します。記録済みでも**外部への配信完了は意味しません**。一意制約で再送と並行実行を防御します。訂正は原本を上書きせず、版数と監査イベントを追加します。ランキングには最新版を反映し、個人履歴にはすべての版を残します。

権限のない要求、不正な証拠、同じキーで異なる内容、古い訂正版数は成功扱いにしません。実際の PostgreSQL Docker コンテナーで統合テストを実施します。

### ログ・トレース・業務監査

- **運用ログ**：HTTP の結果、処理時間、Trace 相関、安定した結果コードのみ。証拠原文、JWT、署名鍵を出力しません。
- **Tracing / Metrics**：OpenTelemetry は Web Driver のみで構成し、外部送信は**既定で無効**。`Telemetry__ExportOtlp=true` と Collector の設定で有効化します。
- **業務監査**：結果承認と訂正の履歴は PostgreSQL にトランザクションとともに保存します。運用ログは監査記録の代わりにはなりません。
- **CA 境界**：Entities と Interactors は `ILogger` や OpenTelemetry に依存しません。

## ローカルで実行する

.NET 10 SDK、Docker、**外部の OIDC ID プロバイダー**が必要です。初期管理者、リポジトリに保存された署名鍵、認証を回避する仕組みは用意しません。

1. 安全な `POSTGRES_PASSWORD` をローカルで設定します。追跡されない `.env` は `.env.example` から作成できます。
2. `docker compose up -d postgres` を実行します。
3. 次の値を**環境変数**として設定します。

   - `ConnectionStrings__Competitions`：ローカル PostgreSQL への接続文字列
   - `Authentication__Authority`：HTTPS の OIDC Issuer URL
   - `Authentication__Audience`：検証対象の API Audience
   - `TrustedAuthority__SigningKeyBase64`：最低 32 バイトのランダムな秘密情報を Base64 化したもの。**信頼された判定端末以外と共有しません**
   - `Database__BootstrapOnStart=true`：**Development 専用**の初期スキーマ作成。正式な Migration ではありません

4. `dotnet run --project src/Competition.Web/Competition.Web.csproj --environment Development` を実行します。
5. `GET /health` でプロセスの生存を確認します。保護対象 API は検証済み Access Token が必要です。

`OpaqueEvidence` は信頼された判定元が発行した署名付き証明でなければなりません。クライアント側で任意の JSON を作っても承認されません。トークン、鍵、パスワードや私的なホスト名を Git やログに含めないでください。

## 全検証コマンド

```bash
dotnet test tests/Competition.Domain.Tests/Competition.Domain.Tests.csproj -c Release
dotnet test tests/Competition.Application.Tests/Competition.Application.Tests.csproj -c Release
dotnet test tests/Competition.Adapters.Tests/Competition.Adapters.Tests.csproj -c Release
dotnet test tests/Competition.Infrastructure.Tests/Competition.Infrastructure.Tests.csproj -c Release
dotnet test tests/Competition.Web.Tests/Competition.Web.Tests.csproj -c Release
dotnet test tests/Competition.Architecture.Tests/Competition.Architecture.Tests.csproj -c Release
dotnet test tests/Competition.Integration.Tests/Competition.Integration.Tests.csproj -c Release
python3 scripts/check_publication.py
```

統合テストは実際の一時 PostgreSQL コンテナーを使用し、トランザクション、並行登録、再送、一意制約、訂正競合、情報の分離を確認します。CI では単体／アーキテクチャテストと DB 統合テストを別ジョブに分けています。

## 明示的な限界と今後の課題

このプロジェクトは**教育用モジュラーモノリス**であり、本番稼働が完成しているわけではありません。OIDC 提供者、信頼されたゲーム判定／発行サービス、本番 DB Migration、完全な不正検知、確定時点のランキング Snapshot、本番向け Webhook 受信サービス、Exactly-once 処理、Dead Letter の運用者再送機能、分散システム間の配送保証は含まれません。参加登録や提出締切の競合については、本番導入の前にトランザクション内の追加対策が必要です。テスト結果は成功した CI の実行記録に基づいてのみ主張できます。緑の CI は完全な安全性や書籍への完全な適合証明ではありません。**書籍の依存性ルール**と**本サンプル独自の実務的な選択**を明確に区別しています。

## オプトイン Outbox 配信と保証範囲

最外層 Web Driver に**任意で有効化できる HTTPS Webhook Dispatcher**を追加しました。配信状態は PostgreSQL に保存されます。

- `Outbox__Enabled=true` と `Outbox__WebhookUrl=https://your-trusted-receiver.example/events` を指定した場合のみ外部へ送信します。既定では送信しません。
- PostgreSQL の `FOR UPDATE SKIP LOCKED`、期限付き Lease、ランダムな Lease Token を利用し、複数 Worker による同一イベントの同時獲得を避けます。
- バージョン付き JSON Envelope、安定した `EventId`、`EventType`、`Idempotency-Key` Header を送信します。**受信側は EventId の重複を永続的に排除する必要があります**。
- HTTP 2xx のみ配信成功として記録します。408、429、5xx とネットワーク障害はリトライ、それ以外の 4xx は恒久的失敗と扱います。
- 一時的障害では上限付きの指数バックオフを利用し、五回の失敗で **Dead Letter** へ移動し、運用担当者の調査を待ちます。
- 受信先の処理後、確認保存前に送信側が停止した場合は Lease 期限後に**再配信される可能性があります**。これは **At-least-once の試行**であり、Exactly-once ではありません。
- リダイレクトを追わず、管理者が設定した HTTPS 宛先だけを利用します。秘密情報や HTTP 応答本文はログへ出力しません。

**開発データベースについて：** `EnsureCreated` は空の Schema 作成専用です。既存データベースに Outbox 列を追加することはできません。破棄可能な開発環境以外では不用意にリセットしないでください。正式な Migration は今後の作業です。

実際の PostgreSQL による Lease 競合、期限切れ回復、無効 Token の拒否、リトライ／Dead Letter、HTTP Handler の応答分類をテストしています。ただし**実際の外部受信サービスへの送信テスト、および手動再実行の運用画面は未実装**です。


## 検証済みのフルスタック HTTP シナリオ

`AuthorizedHttpJourneyTests` は**実際の ASP.NET Core HTTP パイプラインと PostgreSQL Testcontainers**を使用します。テスト専用の正しく署名された JWT と信頼済み HMAC 証明によって、大会作成、参加登録、結果提出、同じ冪等キーでの再送、ランキング、個人履歴、管理者による訂正、監査および Outbox の永続化を一連の API 呼び出しで検証します。

これは**ローカルテスト専用の発行者**であり、外部 OIDC サービスまたは実際のゲーム判定サーバーの検証ではありません。実行証拠：[GitHub Actions 39380576833](https://github.com/sky1116/clean-architecture-sample/actions/runs/39380576833)。そのリビジョンでは**67 件のテスト（PostgreSQL 統合テスト 13 件）**とドキュメント検証が成功しました。件数は今後の変更で増減します。

