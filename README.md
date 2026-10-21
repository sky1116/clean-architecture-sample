# Competition Platform — Clean Architecture Reference

[English](README.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

> An executable, security-conscious competition, leaderboard and participant-history reference for **Robert C. Martin's Clean Architecture**. Built from scratch for public education. **Not a production-ready anti-cheat service.**

## What is demonstrated?

A competition accepts authenticated participants, validates submissions and trusted evidence, records immutable score decisions, computes rankings and preserves personal history. Realistic failure scenarios—invalid input, authorization, duplicate requests, concurrent updates, stale projections and operational failures—are first-class design requirements.

**Architectural rule:** source-code dependencies point inward. Policy is independent of HTTP, databases, frameworks, loggers, clocks and transports. The outermost composition root creates the runtime object graph.

## The two essential diagrams

### 1. The dependency rule

![An original four-ring illustration of entities, use cases, interface adapters and frameworks/drivers with inward-only source dependencies](docs/assets/architecture-rings.svg)

Business rules live in **Entities** and **Use Cases**. **Interface Adapters** translate data and invoke input/output boundaries. **Frameworks & Drivers** are replaceable details. The arrows describe **source dependencies**, not necessarily runtime flow.

### 2. A typical request/response scenario

![An original controller, input boundary, interactor, output boundary, presenter and gateway dependency/control-flow diagram](docs/assets/request-boundaries.svg)

1. A **Controller** transforms an HTTP request into simple **Input Data** and invokes an **Input Boundary**.
2. A **Use Case Interactor** performs application policy and invokes **Entities** as needed.
3. The interactor depends on an **application-owned Gateway interface**; an outer adapter implements it using PostgreSQL.
4. The interactor sends **Output Data** to an **Output Boundary**, implemented by a **Presenter**.
5. The Presenter creates a **View Model**. The web host converts it to HTTP. Neither entities nor use cases know about ASP.NET Core or EF Core.

**Dependency inversion:** control may call outward through interface implementations at runtime, while *source references remain pointed inward*.

## Strict boundaries

| Circle | Project | Permitted knowledge |
| --- | --- | --- |
| Enterprise business rules | `Competition.Domain` | Domain types and BCL only |
| Application business rules | `Competition.Application` | Domain, application-owned ports and request/response data |
| Interface adapters | `Competition.Adapters` | Application boundaries, view models and HTTP-independent presentation |
| Frameworks and drivers | `Competition.Infrastructure`, `Competition.Web` | EF Core, PostgreSQL, ASP.NET Core, logging and OpenTelemetry |
| Tests | `Competition.*.Tests` | Verify outcomes, failure semantics and forbidden dependencies |

Boundaries are **not** repositories named after frameworks. A repository *interface* belongs to the layer that needs it; its EF Core *implementation* belongs outside. An entity must never request an `ILogger` or receive an HTTP DTO.

## Essential use cases and defenses

| Use case | Rule | Failure coverage |
| --- | --- | --- |
| Create competition | UTC enrollment/competition window and scoring policy | Invalid range and duplicate identifier |
| Register participant | Authenticated subject and eligible competition | Unauthorized, closed enrollment, duplicate registration |
| Submit result | Verify trusted proof before acceptance | Fabricated score, replay, payload/key conflict, cross-participant submission |
| Get leaderboard | Deterministic rank and tie rules | Pagination bounds, finalized vs. live rankings |
| Get personal history | Append-only decisions and corrections | Authorization, correction lineage, concurrent updates |
| Correct result | Authorized audited correction | Version conflict, reason required, transactional projection |

**Trust boundary:** a client-supplied score is never assumed to be true. Authentication establishes a caller's identity, **not proof of gameplay**. The example's accepted results require evidence produced by a trusted authority or an independently verifiable rule. Never expose server signing secrets to players.

**Observability is not auditing.** Structured operational logs and traces describe execution; business audit records preserve decisions. Do not log tokens, signing secrets or unbounded user input.

## Proof, not slogans

Quality gates cover:
- Domain examples including malicious/boundary inputs, state transitions and ties.
- Interactor tests with strict port fakes asserting authorization, idempotency and output-boundary calls.
- Architecture tests that **fail** for forbidden source dependencies.
- PostgreSQL integration tests using an actual container for uniqueness, transactions and concurrency.
- End-to-end request/response cases using authenticated identities.
- Negative controls proving the verifiers themselves detect deliberately invalid changes.
- A publication scan that rejects configured prohibited identifiers without committing the confidential list.

Tests are reported honestly. Until a CI run succeeds, this README does **not** claim that build or integration checks have passed.

## Local development

Requires .NET 10 SDK and Docker for PostgreSQL integration tests. Follow the workflow in `.github/workflows/ci.yml`; run `dotnet test` from the repository root after the solution is established. For a runnable application, supply a PostgreSQL connection string and a real authentication configuration through environment variables; do not commit credentials.

## Book-to-code correspondence

| Source | Meaning in this example |
| --- | --- |
| *Clean Architecture*, Ch. 20, Business Rules | Framework-free domain policy and invariants |
| Ch. 22, The Clean Architecture | Inward dependencies, simple boundary data, ports, presenters and gateways |
| Ch. 23, Presenters and Humble Objects | View formatting belongs to the Presenter, not the Interactor |
| Ch. 26, The Main Component | The web host assembles concrete implementations |
| Ch. 28, The Test Boundary | Test independently across architectural seams |
| Chs. 30–32 | Database, web and frameworks remain replaceable external details |

**Attribution:** inspired by Robert C. Martin, *Clean Architecture: A Craftsman's Guide to Software Structure and Design* (Pearson, 2017), especially the dependency rule and the typical scenario. See [Martin's original article](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html). Both SVGs are newly drawn explanatory diagrams, **not reproductions of published artwork**. Product/framework selection, concurrency controls, threat mitigations and outbox design are engineering decisions for this sample, **not mandates attributed to the book**.

## Non-goals and guarantees

This is a **modular monolith** teaching explicit boundaries. It does not promise proof that a player truly achieved a score without a trusted evidence source, guarantee exactly-once message delivery, or claim that a particular technology is required by the book. Working examples and verified behavior take priority over speculative abstractions.

## Implemented HTTP contract

| Route | Authentication | Outcome |
| --- | --- | --- |
| `POST /api/competitions` | Valid OIDC token, competition-admin role | Creates a bounded, immutable competition policy |
| `POST /api/competitions/{id}/participants` | Valid OIDC token | Registers the authenticated participant, not a submitted participant identifier |
| `POST /api/competitions/{id}/results` | Valid OIDC token, rate limited | Requires `Idempotency-Key` header and `OpaqueEvidence`; accepts only verified evidence |
| `POST /api/competitions/{id}/results/{resultId}/corrections` | competition-admin role | Applies an audited, version-checked correction |
| `GET /api/competitions/{id}/leaderboard` | Public | Ranking is computed from current effective results (including corrections) |
| `GET /api/competitions/{id}/me/history` | Valid OIDC token | Only returns the authenticated participant's original decisions and corrections |
| `GET /health` | Public | Process liveness only; **not** database readiness |

### Result provenance and integrity

The sample's **trusted-receipt adapter** checks an HMAC signature over encoded authority claims (competition, participant, points, evidence identifier, issued/expiry timestamps). The signing secret must belong **only to an independent trusted authority**, never the browser, player or mobile game. A signed claim attests to the issuer, **not independently to the correctness of gameplay**; this sample deliberately does not include a complete game server or evidence issuer.

The request key is bound to the submitted proof. The PostgreSQL adapter writes the accepted result, idempotency receipt, business audit and **pending versioned outbox event** in one transaction. Persisting an event does **not** mean it was delivered externally. Unique constraints on the receipt and evidence ID defend against replay, including concurrent calls. A correction appends a revision plus an audit decision without overwriting the original result; an effective-score query considers the newest revision. Personal history returns original and correction entries.

Failures are not silently converted to success: unauthorized requests are rejected by HTTP authorization and Use Case guards; invalid evidence never reaches persistence; duplicate keys with different payloads conflict; stale correction revisions conflict. PostgreSQL integration tests exercise these behaviors with an actual Docker database.

### Operational logs, traces and business audit

- **Operational logs:** HTTP completion, duration, status, trace correlation and stable outcome code; never raw proof, JWT or signing keys.
- **Tracing and metrics:** OpenTelemetry is configured exclusively in the Web driver. Export through OTLP is **disabled by default** and opt-in with `Telemetry__ExportOtlp=true` plus exporter endpoint configuration.
- **Business audit:** PostgreSQL's append-only accepted-result and correction decision records are committed with their business transaction. An operational log is **not** a substitute for this audit.
- **Separation:** Entities and Interactors neither depend on `ILogger` nor import OpenTelemetry libraries.

## Try it locally

Prerequisites: .NET 10 SDK, Docker for PostgreSQL, and a **separate OIDC identity provider** issuing valid access tokens. The sample does not include a default admin login, an in-repository signing key or a developer bypass for authentication.

1. Set a strong `POSTGRES_PASSWORD` locally (for example in an untracked `.env`; copy `.env.example` first).
2. Run `docker compose up -d postgres`.
3. Provide the following **environment variables** outside source control:

   - `ConnectionStrings__Competitions`: PostgreSQL connection string using the locally chosen password
   - `Authentication__Authority`: HTTPS OIDC issuer URL
   - `Authentication__Audience`: API audience expected in validated tokens
   - `TrustedAuthority__SigningKeyBase64`: base64 of at least 32 random secret bytes, shared **only with the trusted result issuer**
   - `Database__BootstrapOnStart=true`: **Development only**, applies the four checked-in versioned EF Core migrations to a fresh/migration-managed database; never applies automatically outside Development

4. Run `dotnet run --project src/Competition.Web/Competition.Web.csproj --environment Development`.
5. Call `GET /health` to check liveness; authenticate against your configured issuer for protected endpoints.

The `OpaqueEvidence` in submissions must be a valid **authority-issued receipt**; fabricating JSON client-side is intentionally rejected. `Database__BootstrapOnStart` is forbidden outside Development. Keep real tokens, secrets and private host addresses out of source control and logs.

## Run every verification gate

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

The integration suite starts disposable real PostgreSQL containers and covers transactions, parallel submissions, replay, unique constraints, concurrent corrections, rollback classification and participant/competition isolation. CI runs it **separately** from deterministic unit/architecture tests.

## Explicit limitations and next work

This project is an educational modular monolith, **not a finished production service**. In particular, it does not bundle an OIDC provider, game authority/issuer, comprehensive anti-cheat checks, historical finalized leaderboard snapshots, a production-proven external webhook receiver, exactly-once event processing, a full production operations console, or fault-tolerant multi-system delivery. Enrollment and result writes now recheck Domain policies inside PostgreSQL transactions using a trusted server-side UTC clock. The policy decision is timed at the in-transaction check, **not** at database commit. Stronger commit-time/DB-clock guarantees and multi-writer scheduling changes require additional production design. Only a passing CI run supports claims about executed tests, and a green CI check does not establish complete security or book-level conformity. This separation between **the book's dependency principles** and **our engineering choices** is intentional.

## Opt-in Outbox delivery: semantics and operations

The sample now implements an **optional HTTPS webhook dispatcher** in the outer Web driver, backed by a replaceable HTTP sink and PostgreSQL delivery state:

- Enable by setting `Outbox__Enabled=true` and `Outbox__WebhookUrl=https://your-trusted-receiver.example/events`. No network delivery occurs when disabled.
- A PostgreSQL claim transaction uses `FOR UPDATE SKIP LOCKED`, a time-limited lease and a random lease token. Multiple workers can compete without intentionally claiming the same pending row.
- The dispatcher posts a versioned JSON envelope including stable `EventId`, `EventType` and an `Idempotency-Key` header. **The receiver must persistently deduplicate `EventId`**.
- Successful HTTP 2xx permits the worker to acknowledge the row; HTTP 408 / 429 / 5xx and transport errors are retryable. Other 4xx responses are terminal.
- Transient failures use increasing bounded backoff; after five unsuccessful claims the event enters a **Dead Letter** state for explicit operator review.
- If a process fails after the receiver accepts an event but before the acknowledgment commits, the lease expires and the event may be **delivered again**. Therefore this design provides **at-least-once attempts**, never guaranteed exactly-once delivery.
- No redirects are followed; the operator-controlled target must be HTTPS. Raw tokens, full response bodies and exception details are not logged.

**Versioned migration note:** The project has four EF Core migrations: `BaseCompetition`, `DurableOutbox`, `InboxProjection`, and `OperatorRedriveAudit`. The integration suite checks fresh installation, upgrading from the first version without losing existing records, rerunning migrations, and model-snapshot drift. The Development-only `Database__BootstrapOnStart=true` flag now calls `MigrateAsync`, **not** `EnsureCreated`. An older database previously created with `EnsureCreated` has no EF migration history and must be backed up and explicitly baselined/migrated; do not blindly run migrations or delete valuable data. Production schema changes require a reviewed deployment step, not web-host startup.

Test evidence includes real PostgreSQL `SKIP LOCKED` competition, lease recovery, wrong-token rejection, retry/dead-letter behavior, and an HTTP fake-handler verifying delivery classification. This does **not** demonstrate a separately deployed production receiver or a production operations console with monitoring/approvals.


## Verified full-stack sample journey

The integration test `AuthorizedHttpJourneyTests` now executes a **real ASP.NET Core HTTP request pipeline against a real PostgreSQL Testcontainers database**, with locally generated, cryptographically valid test JWTs and trusted HMAC receipts. It covers competition creation, enrollment, first submission, replay under the same idempotency key, leaderboard, personal history, administrator correction, and durable audit/outbox state.

This is a test-only issuer and signature authority, **not an external OIDC provider or production game verification service**. The validated CI evidence is [workflow run 39380576833](https://github.com/sky1116/clean-architecture-sample/actions/runs/39380576833): 67 tests passed, including 13 PostgreSQL integration tests, plus the documentation gate. Counts are specific to that revision and may change as tests are added.


## Write-time admission policy and concurrency

Two checks serve different purposes: **Use Cases** reject clearly invalid submissions early, while **PostgreSQL Gateways** re-read the authoritative competition and enrollment state **inside the write transaction** and reuse the **Domain Entity methods**, not copied date/score formulas. In particular:

- Existing accepted requests or registrations remain idempotently readable after a competition closes; a new attempt does not silently create new state.
- Newly registering after `StartsAt`, submitting before `StartsAt` or after `EndsAt`, and submitting without an enrollment are rejected in the transaction, leaving results, receipts, audit entries and outbox records unchanged.
- The accepted policy time is the trusted `IUtcClock` reading during the transaction, **not HTTP arrival or transaction commit time**. The example treats competition rules and participant enrollment as immutable after creation. Do not claim that this gives a strict database-commit-time cutoff or protects against external writers changing the competition table.
- PostgreSQL container tests cover closed registration, same-user concurrency, accepted replay after cutoff, expired result writes, and missing enrollment.

These are application/Domain policies; PostgreSQL is an **outer implementation detail** used to atomically enforce them, not a second source of business rules.


## Migration and transaction-cutoff verification (revision-specific)

[GitHub Actions run 39386303105](https://github.com/sky1116/clean-architecture-sample/actions/runs/39386303105) passed **76 tests** at this revision: **22 real PostgreSQL integration tests** plus 54 unit/architecture/web/adapter tests, and the three-language documentation gate. The PostgreSQL tests include upgrading from the base migration without data loss, reapplying migrations, EF model-snapshot comparison, deadline checks inside transactions, repeated accepted requests after closure, and concurrent enrollment. These counts are evidence for this revision, not a guarantee for future changes.


## Inbound webhook and durable Inbox reference

This project now includes a **sample receiver endpoint**, `POST /api/integrations/result-events`, disabled by default. Enable it only for trusted server-to-server integration with `Inbox__Enabled=true` and a separately provisioned `Inbox__WebhookSigningKeyBase64` (at least 32 random bytes). Configure the sending side with `Outbox__WebhookSigningKeyBase64` using the **same trusted secret** and an operator-controlled HTTPS `Outbox__WebhookUrl`. Neither key belongs in source control or a player client.

The sender signs the **exact outgoing JSON bytes** with HMAC-SHA256; the receiver verifies the `X-Webhook-Signature: v1=...` header **before parsing** the body. It also checks `X-Event-Id`, `X-Event-Type`, schema version, event identities and an 8 KiB payload limit. Any missing, incorrect or modified signature is rejected. The receiver is an educational integration endpoint in the modular monolith, **not an independently deployed production receiver**.

The inward-facing `ReceiveResultEventInteractor` delegates atomic persistence through `IResultInboxPort`; the outer PostgreSQL adapter commits an **Inbox receipt and independent `ReceivedResults` projection in one transaction**. The authoritative `Results` table and public leaderboard are not updated by inbound webhook data. Original results and corrections are revisioned: same `EventId` + same body is a harmless duplicate; same ID with changed body is a conflict; an out-of-order correction returns HTTP 503 so the sender can retry. Revisions must arrive in order, and persistent missing dependencies can eventually dead-letter upstream.

The real HTTP/PostgreSQL test checks a valid signature, rejection of forged/tampered requests, duplicate submissions, conflicting payloads, out-of-order dependencies, disabled endpoint and oversized input. PostgreSQL integration tests additionally validate simultaneous deliveries, restart replay, unique constraints and projection consistency. The receiver's Inbox is **not** an assertion of end-to-end exactly-once messaging or independent external network validation.

[Verified Inbox HTTP/PostgreSQL CI, 94 tests](https://github.com/sky1116/clean-architecture-sample/actions/runs/39390851699): 34 PostgreSQL integration tests plus 60 other tests and documentation checks. This count is revision-specific.


## Authorized Dead Letter redrive and audit

An operator may explicitly opt in with `Outbox__RecoveryEnabled=true` (disabled by default) and call `POST /api/operations/outbox/{eventId}/redrive` with a validated JWT carrying the `competition-admin` role and a JSON `Reason` of 8–300 characters. Recovery is separate from enabling background dispatch.

The **Application Interactor** checks administrator authority and a meaningful reason through an application-owned port; the **PostgreSQL implementation** takes a row lock, rejects already published/claimed/non-dead-letter events, atomically requeues one eligible event, and persists an `OutboxRecoveryAudit` containing the actor identity, reason, previous attempt count, and timestamp. Parallel operators cannot requeue the same event twice. It does **not** bypass webhook signing or promise that a repaired receiver will accept the next attempt.

The Inbox correction-revision transition has been moved into the framework-independent `ReceivedResultPolicy` in **Domain**. The PostgreSQL adapter loads the current read-side state, asks the Domain policy for the next state, and commits that state and an Inbox receipt together; the adapter no longer duplicates the revision acceptance formula.

The admin API and PostgreSQL tests use verified test JWTs, real routing, real transactions, denial of unauthorized callers, one-time replay, published-event safeguards and durable audit records. An operational dashboard, approval workflow, key rotation and external production deployment remain outside the sample.


## Architecture audit checkpoint — book principle versus verified implementation

This is a **code-and-test audit checkpoint**, not a statement that a book, a real infrastructure deployment or all future modifications have been exhaustively certified. Chapter references are design correspondences to Martin's book, not quotations or claims that the book mandates these packages or programming conventions.

| Book concept | Source location | What this repository actually checks |
| --- | --- | --- |
| Dependency Rule (Ch. 22) | `Competition.Domain`, `Competition.Application`, `Competition.Adapters`, `Competition.Infrastructure`, `Competition.Web` | `ProjectGraphPolicy` checks the exact allowed project-reference graph and unexpected source projects; tests inject an actual forbidden project reference and an inner-layer package into the **same validator**. Compiled inner assemblies are also inspected for outer references. |
| Entities and enterprise policy (Ch. 20) | `Competition`, `RankingPolicy`, `ReceivedResultPolicy`, `CorrectionRevisionPolicy` | Business rules live in the framework-free Domain project; Domain tests cover ranking, windows, score bounds, and revision transitions. PostgreSQL gateways invoke Domain rules rather than creating separate correction-version policies. |
| Application Use Cases and inversion (Ch. 22) | `UseCases/*`, `Ports/*` | Interactors depend on inward, application-owned interfaces and Domain; test discovery checks **all** named input/output boundaries and flags new contracts left out of verification. |
| Controller → Input Boundary (Ch. 22) | `CompetitionController`, `Program.cs` | The Controller delegates to input interfaces rather than concrete interactors. HTTP endpoints translate transport data and validated identities in the outer Web driver. |
| Output Boundary → Presenter → View Model (Ch. 23) | `RestPresenter`, `ApiViewModel` | Reflective contract tests reject Domain entities or persistence rows in input/output DTOs, and check that the adapter-owned Presenter implements every declared output interface. |
| Database and framework detail (Chs. 30–32) | `Postgres*Gateway`, `CompetitionDbContext`, `Migrations/*` | EF Core/Npgsql live in the outer Infrastructure project; actual PostgreSQL tests check atomic receipts, concurrent updates, Inbox/Outbox and schema upgrades. |
| Main component and test boundary (Chs. 26, 28) | `Program.cs`, `Competition.*.Tests`, CI | The Web application composes implementations. HTTP TestServer, domain, application, architecture and PostgreSQL tests run as separate CI gates. |

**Behavioral correction found during audit:** a prior result or registration is now looked up before time-window validation. An exact accepted result replay is read-only even if the proof has since expired; a fresh late request or the same request key with different content remains rejected. The PostgreSQL write transaction still checks the Domain window and enrollment for **new** results. This separation is covered by use-case and real-HTTP tests.

**Honest gate scope:** Direct project XML, compiled assembly references, discovered boundary names and DTO structures are tested; these are **not** an exhaustive static analysis of arbitrary MSBuild imports, reflection/dynamic assembly loads, secret handling, deployment behavior or the complete text of the book. The example also does not prove gameplay authenticity, exactly-once cross-service delivery, or an exact database-commit-time cutoff. The draft PR must be independently reviewed before merge; green CI is evidence for the checked revision, **not unconditional architectural sign-off**.

