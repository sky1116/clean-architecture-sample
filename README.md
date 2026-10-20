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

The request key is bound to the submitted proof. The PostgreSQL adapter writes the accepted result, idempotency receipt and decision audit **in one transaction**. Unique constraints on the receipt and evidence ID defend against replay, including concurrent calls. A correction appends a revision plus an audit decision without overwriting the original result; an effective-score query considers the newest revision. Personal history returns original and correction entries.

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
   - `Database__BootstrapOnStart=true`: **Development only**, creates a new schema on startup; not a migration strategy

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

This project is an educational modular monolith, **not a finished production service**. In particular, it does not bundle an OIDC provider, game authority/issuer, versioned migrations, comprehensive anti-cheat checks, historical finalized leaderboard snapshots, an outbox/background processor or fault-tolerant distributed delivery. Registration and result expiry race windows require further transaction-bound policies before production use. Only a passing CI run supports claims about executed tests, and a green CI check does not establish complete security or book-level conformity. This separation between **the book's dependency principles** and **our engineering choices** is intentional.
