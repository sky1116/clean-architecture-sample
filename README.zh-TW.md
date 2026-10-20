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
