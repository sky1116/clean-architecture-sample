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
