# 競賽平台 — Clean Architecture 實務範例

[English](README.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

> 以 Robert C. Martin《Clean Architecture》為架構依據，從零開發的公開教學範例：競賽、排行榜、個人歷史紀錄與資料防護。**不宣稱已具備生產環境等級的防作弊能力。**

## 專案目標

參賽者經過身分驗證後報名競賽，提交附有可信證據的成績。系統保存決策歷史、產出可預測的排名，並提供個人歷史查詢。非法輸入、越權、重送、並發、過期與外部服務失效都必須是設計與測試的一部分。

**最重要的規則：原始碼依賴只能向內。** 業務政策不認識 HTTP、資料庫、框架、Logger 或傳輸技術。最外層的 Composition Root 負責組裝。

## 兩張核心架構圖

### 1. Clean Architecture 四圈與依賴規則

![原創四圈架構圖：Entities、Use Cases、Interface Adapters、Frameworks & Drivers，程式依賴向內](docs/assets/architecture-rings.svg)

- **Entities（企業業務規則）**：穩定且與技術無關的競賽規則與不變條件。
- **Use Cases（應用業務規則）**：報名、成績驗證、歷史與排行榜等應用政策。
- **Interface Adapters（介面轉接器）**：Controller、Presenter、Gateway Adapter 和 View Model。
- **Frameworks & Drivers（框架與驅動）**：ASP.NET Core、EF Core、PostgreSQL、Telemetry。

圖中箭頭代表**原始碼相依**，不等同於實際呼叫方向。

### 2. 典型請求與回應流程

![原創典型流程圖：Controller、Input Boundary、Use Case Interactor、Output Boundary、Presenter、Gateway](docs/assets/request-boundaries.svg)

1. **Controller** 將 HTTP 輸入轉成簡單的 **Input Data**，呼叫 **Input Boundary**。
2. **Use Case Interactor** 執行應用規則，並使用 **Entities**。
3. Interactor 依賴應用層定義的 **Gateway 介面**，由外層 PostgreSQL Adapter 實作。
4. Interactor 將 **Output Data** 送至 **Output Boundary**，由外層 **Presenter** 實作。
5. Presenter 建立 **View Model**，Web Host 負責 HTTP Response。核心不依賴 ASP.NET Core 與 EF Core。

執行期控制流程可能向外呼叫具體實作，但編譯期**依賴方向仍向內**，這是依賴反轉的要點。

## 專案與職責

| 圈層 | 專案 | 責任 |
| --- | --- | --- |
| Entities | `Competition.Domain` | 業務規則、不變條件；無框架依賴 |
| Use Cases | `Competition.Application` | Interactor、Input/Output Boundary、簡單資料模型與 Ports |
| Interface Adapters | `Competition.Adapters` | Presenter、View Model、介面轉換 |
| Frameworks & Drivers | `Competition.Infrastructure`、`Competition.Web` | PostgreSQL、EF Core、ASP.NET Core、Logging、Telemetry |
| Tests | `Competition.*.Tests` | 驗證規則、邊界、失敗情境與禁止依賴 |

Repository **介面**由需要它的內圈持有；EF Core **實作**位於外圈。Domain 不應注入 `ILogger`，Use Case 也不能傳遞 HTTP DTO。

## 功能與資料可信度

| Use Case | 規則 | 防護 |
| --- | --- | --- |
| Create Competition | 競賽期間、計分政策 | 時間區間無效、識別碼衝突 |
| Register Participant | 身分與報名資格 | 越權、報名截止、重複報名 |
| Submit Result | 可信證據先於正式成績 | 假成績、重播、冪等鍵衝突 |
| Get Leaderboard | 明確排序與平手規則 | 分頁範圍、即時與結算排名 |
| Get Personal History | 不覆寫的成績決策歷程 | 權限、來源鏈、並發 |
| Correct Result | 有權限且附理由的更正 | 版本衝突、交易失敗與稽核 |

**不能直接相信玩家自行上傳的分數。** 認證證明「誰送出的」，不證明「分數是真的」；正式接受結果須有可信機構提供的證據或伺服器可獨立驗證的規則。簽章金鑰不能傳給玩家。

**Log 不等於業務稽核。** 結構化 Log 和 Trace 用來追查執行狀態；稽核紀錄保存決策。不可把 Token、密鑰或整份不可信輸入寫入 Log。

## 測試與驗收

- Domain 單元測試：有效／無效狀態、邊界值、作弊輸入、平手排名。
- Interactor 測試：嚴格 Port Fakes，驗證授權、冪等與 Output Boundary。
- 架構測試：**真正檢查依賴方向**，違規必須測試失敗。
- PostgreSQL 整合測試：使用真實容器驗證 Unique Constraint、Transaction、Concurrency；不以 InMemory 資料庫取代。
- HTTP 測試：經認證的請求與錯誤回應。
- 負向控制：刻意製造錯誤，證明驗證器會失敗。
- 公開掃描：採 Repository 外維護的禁止識別字清單，不將機密清單寫進公開程式碼。

未取得 CI 成功證據之前，README **不宣稱**所有測試已通過。

## 開發

需要 .NET 10 SDK；PostgreSQL 整合測試需要 Docker。建立方案後由 Repository 根目錄執行 `dotnet test`。資料庫連線與正式認證設定經由環境變數注入，**不提交憑證**。

## 書中原則與程式碼對照

| 參照章節 | 專案落點 |
| --- | --- |
| Ch. 20 Business Rules | Domain 業務規則與不變條件 |
| Ch. 22 The Clean Architecture | 向內依賴、Input/Output Ports、Presenter 與 Gateway |
| Ch. 23 Presenters and Humble Objects | Presenter 處理 View Model，Interactor 不做格式化 |
| Ch. 26 The Main Component | Web Host 組裝具體實作 |
| Ch. 28 The Test Boundary | 使用架構邊界建立可隔離的測試 |
| Ch. 30–32 | Database、Web 和 Frameworks 是可替換細節 |

**出處：** Robert C. Martin，《Clean Architecture: A Craftsman's Guide to Software Structure and Design》（Pearson，2017）；另參考 [作者原始文章](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)。兩張 SVG 是本專案新繪製的原理示意圖，**不是出版圖片複製品**。資料庫、Outbox、防作弊、可觀測性與技術選型是本範例的工程決策，**不是作者在書中強制指定**。

## 範圍邊界

採用單體模組化架構示範明確分層。沒有可信的成績來源就無法保證玩家沒有作弊；不能保證跨系統訊息真正 Exactly Once；也不將任何特定框架誤認為書中規定。以可執行、可驗證的證據優先。

## 目前實作的 HTTP 契約

| 路由 | 授權 | 行為 |
| --- | --- | --- |
| `POST /api/competitions` | OIDC 登入並具有 competition-admin 角色 | 建立具期間與計分上限的競賽 |
| `POST /api/competitions/{id}/participants` | OIDC 登入 | 依已驗證身分報名，不採用請求自行宣稱的參賽者身分 |
| `POST /api/competitions/{id}/results` | OIDC 登入、限流 | 必須傳入 `Idempotency-Key` 和 `OpaqueEvidence`；只有可信證據能寫入成績 |
| `POST /api/competitions/{id}/results/{resultId}/corrections` | competition-admin 角色 | 寫入保留版本與理由的稽核更正 |
| `GET /api/competitions/{id}/leaderboard` | 公開 | 依最新有效成績（含更正）產生排名 |
| `GET /api/competitions/{id}/me/history` | OIDC 登入 | 僅查詢本人原始成績與更正歷程 |
| `GET /health` | 公開 | 僅代表程序存活，**不代表資料庫可用** |

### 成績來源與完整性

**可信收據 Adapter** 會驗證 HMAC 簽章，綁定競賽、參賽者、分數、證據識別碼及簽發／到期時間。簽章密鑰只能由**獨立可信的成績裁定端**保管，絕不能交給玩家、瀏覽器或手機 App。簽章只能證明聲明來自特定簽發端，**不能單獨證明遊戲過程沒有作弊**；本範例不包含完整遊戲伺服器或證據簽發器。

冪等鍵與請求證據綁定。PostgreSQL Adapter 在**同一交易**寫入正式成績、冪等收據、業務稽核紀錄，以及待發送的版本化 Outbox 事件。事件成功入庫**不代表外部發送成功**。唯一約束保護重播與並發寫入。更正以新增版本與稽核決策方式處理，不覆蓋原始成績。排行榜採最新有效版本，個人歷史保留所有版本。

越權、無效證據、相同冪等鍵卻不同內容，以及過期的更正版本，都不能冒充成功。這些行為使用真正 PostgreSQL Docker 容器進行整合測試，而不是僅用 Mock。

### Log、Trace 與正式稽核

- **Operational Log**：只記錄 HTTP 結果、處理時間、Trace 關聯及穩定的結果代碼，不記錄證據原文、JWT 或簽章密鑰。
- **Tracing / Metrics**：OpenTelemetry 只配置在最外層 Web Driver；預設**不對外傳送**，設定 `Telemetry__ExportOtlp=true` 並提供 Collector 端點才會啟用。
- **Business Audit**：成績接受與更正歷程寫入 PostgreSQL，和業務交易一起 Commit。Log **不能取代**正式稽核紀錄。
- **CA 邊界**：Entities 與 Interactors 不引用 `ILogger` 或 OpenTelemetry。

## 本地執行

需要 .NET 10 SDK、Docker，以及**另外配置的 OIDC 身分提供者**。本範例不提供預設管理者帳號、提交至 Git 的密鑰或略過驗證的捷徑。

1. 在本機設定強密碼 `POSTGRES_PASSWORD`，可複製 `.env.example` 為不追蹤的 `.env`。
2. 執行 `docker compose up -d postgres`。
3. 透過環境變數提供：

   - `ConnectionStrings__Competitions`：包含本機 PostgreSQL 密碼的連線字串
   - `Authentication__Authority`：HTTPS OIDC 簽發者
   - `Authentication__Audience`：Token 必須符合的 API Audience
   - `TrustedAuthority__SigningKeyBase64`：至少 32 個隨機位元組的 Base64，**只與可信成績裁定端共享**
   - `Database__BootstrapOnStart=true`：**僅限 Development**，只供建立全新範例 Schema，不代表正式 Migration

4. 執行 `dotnet run --project src/Competition.Web/Competition.Web.csproj --environment Development`。
5. 使用 `GET /health` 確認程序存活；其他受保護 API 需使用正式驗證的 Access Token。

提交的 `OpaqueEvidence` 必須由可信端簽發；自行修改 JSON 不會獲得有效成績。請勿將 Token、密碼、真實位址或密鑰放入 Git／Log。

## 完整驗證指令

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

整合測試使用真正的暫時性 PostgreSQL 容器，驗證交易、並發提交、重播、唯一約束、同時更正以及資料隔離。CI 將整合測試與不依賴資料庫的單元／架構測試分開執行。

## 明確保留的限制

本專案是**教學型模組化單體**，不是完整的正式上線產品。目前不內建 OIDC 提供者、可信遊戲裁定端、正式資料庫 Migration、完整反作弊機制、賽季結算排名快照、正式 Webhook 接收端、Exactly-once 訊息處理、Dead Letter 管理者重送工具或跨系統可靠投遞驗收。正式使用前，報名／成績提交與截止時間之間的競態也需要更嚴格的交易內驗證。必須由成功執行的 CI 提供測試證據；CI 綠燈不等於所有安全性或書中原則都已取得最終簽核。**書中架構規則**與**本範例採用的工程實作策略**明確分開。

## 可選啟用的 Outbox 送達流程與限制

本範例現在已在最外層 Web Driver 加入**可選啟用的 HTTPS Webhook Dispatcher**，並由 PostgreSQL 保存投遞狀態：

- 設定 `Outbox__Enabled=true` 及 `Outbox__WebhookUrl=https://your-trusted-receiver.example/events` 才會開啟投遞；預設完全不對外發送。
- PostgreSQL 使用 `FOR UPDATE SKIP LOCKED`、限時租約與隨機租約 Token，在多個 Worker 同時處理時避免領到相同待處理事件。
- 送出的 JSON Envelope 包含固定 `EventId`、`EventType`，以及 `Idempotency-Key` Header。**接收端必須持久化事件 ID 來防止重複處理**。
- HTTP 2xx 才能確認投遞；408、429、5xx 與網路失敗會重試，其他 4xx 視為永久失敗。
- 暫時失敗使用有上限的退避時間；連續五次失敗進入 **Dead Letter**，需要營運者檢查。
- 若接收端已處理成功、但發送端在保存確認之前當機，租約到期後仍**可能再次投遞**。這是 **At-least-once 投遞嘗試**，不是 Exactly-once 保證。
- 不跟隨 HTTP Redirect、只接受操作人員設定的 HTTPS 目標，也不將敏感資料或外部回應原文寫進 Log。

**開發資料庫提醒：** `EnsureCreated` 只能建立新資料庫 Schema，無法更新原本已存在的表。新欄位若尚未建立，必須在可拋棄的開發資料庫重建；有價值的資料不能直接清除。正式 Migration 尚待完成。

目前已有 PostgreSQL 真實租約競爭、接手、過期 Token 拒絕、退避與 Dead Letter，以及 HTTP Handler 回應分類測試。但**尚未驗證真實外部接收端，也還沒有完整的人工作業重送介面**。

