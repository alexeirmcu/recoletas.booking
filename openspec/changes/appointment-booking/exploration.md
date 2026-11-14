## Exploration: Greenfield appointment booking service

> **Note (superseded):** this exploration is a historical record of the initial options analysis.
> Final decisions differ — see `proposal.md` and `design.md`: **PostgreSQL** (not SQLite), **.NET 10**
> (not .NET 8), and a **layered** architecture with **five** projects (not four) and **controllers**
> (not minimal API).
>
> The sections below preserve the assumptions and recommendation from that initial analysis; they
> do not describe the implemented project or its selected design.

### Project State at the Time of Exploration
At the time this analysis was written, the project was greenfield: no application code, schemas, or tests existed. The initial `openspec/config.yaml` had not yet settled the stack or test tooling; .NET, Node.js, and Java were available, while Go was not. The only infrastructure hint was an empty `sqlserverdb/` directory. Those initial conditions were superseded by the implementation described in `proposal.md` and `design.md`.

### Areas Anticipated at the Time
- `openspec/changes/appointment-booking/exploration.md` — recorded the initial options and recommendation.
- The initial analysis anticipated a future API/domain, database repository, expiry and outbox workers, tests, startup script, and README. Those areas have since been implemented with the selected PostgreSQL design.

### Approaches

#### 1. Stack
| Option | Pros | Cons | Effort / startup |
|---|---|---|---|
| **.NET 8, compact Clean Architecture** | Available here; strong async/background-worker and transaction support; mature testing; SQLite provider supports local persistence. Clear separation can keep booking rules testable. | Requires the .NET 8 SDK on the reviewer machine; full Clean Architecture scaffolding can consume a 6–8 hour budget. | **Recommended; Medium.** Keep a small API/domain/infrastructure split; `dotnet run` can restore and launch without Docker. |
| Node.js + TypeScript | Available; quick HTTP/API iteration and strong test tooling; async workers are straightforward. | Event-loop concurrency does not itself make booking safe; database transactions/constraints are still essential. TypeScript/runtime tooling adds setup choices. | Low–Medium; local `npm` setup, but SQLite driver and worker behavior need deliberate validation. |
| Java + Spring | Mature transactions, background jobs, and test ecosystem; familiar enterprise stack. | More framework/configuration overhead for this timebox; embedded DB choices and production-like DB parity need care. | Medium–High; Maven/Gradle plus JDK, no clear startup advantage here. |

#### 2. Persistence and concurrency
| Option | Concurrency correctness | Startup / tradeoffs |
|---|---|---|
| **SQLite, transaction + conditional claim + unique live-booking constraint** | Recommended. Make the slot row the authoritative state; claim with a conditional `UPDATE` that succeeds only if available (or its lease is expired), update appointment state and insert the outbox event in the same transaction. A database uniqueness constraint on live claims is a second invariant. SQLite serializes writers, so competing requests/processes cannot both commit a claim; handle busy/locked retries. Do not rely on application mutexes or `SELECT FOR UPDATE` (SQLite does not provide that row-lock model). | Embedded, file-backed, no Docker or external service; easiest one-command review. Single-writer throughput is a deliberate demo-scale limit. |
| RDBMS server (e.g. SQL Server/PostgreSQL) with row locks/unique constraints | Strong production choice; row-level locking and transactional constraints suit high contention. | Requires server setup or Docker (availability unknown), undermining low-friction startup; `sqlserverdb/` is only an empty-directory hint. |
| In-memory state | Can be made atomic within one process. | Lost on restart, fails across multiple processes/instances, and cannot durably couple release to notification. Reject. |
| Redis / document DB | Redis scripts/transactions or document conditional writes can enforce atomic claims. | Adds a service and operational setup; the team must still design durable history, outbox, and cross-record transitions. Not the simplest reviewer run. |

#### 3. Hold expiry
| Option | Pros | Cons / decision |
|---|---|---|
| **DB-backed expiry polling plus lazy validity checks** | A startup-resumed worker finds due holds in batches, expires them transactionally, and writes a release event to the outbox. Booking/agenda paths also treat `expires_at <= now` as expired, so worker delay cannot make an expired hold appear valid. | Small polling delay and lease/retry logic. **Recommended combination** for prompt promotion plus correctness. |
| In-process timer per hold | Simple for a tiny in-memory demo. | Lost on restart, timer volume/lifecycle complexity, and still requires transactional DB checks. Reject as source of truth. |
| Broker delayed messages | Prompt wake-up and scales with a broker. | Broker dependency and duplicate/stale delayed events; unnecessary for this one-command build. |
| Lazy expiry only | No worker required. | Waitlist promotion can stall until another request touches the slot; insufficient alone. |

#### 4. Asynchronous waitlist messaging
| Option | Delivery guarantees and tradeoffs |
|---|---|
| **SQLite transactional outbox + background poller/handler** | Recommended. Commit a slot release and uniquely keyed outbox event atomically; a separate asynchronous worker leases and processes events. This is durable asynchronous messaging without another service and resumes after restart. Delivery is **at least once**, not exactly once: deduplicate by message ID, make offer creation idempotent, and ignore stale per-slot sequence/generation values. Retry transient failures with backoff; after a bounded retry count move to a persistent dead-letter state for inspection/replay. Process each slot's events in sequence; unrelated slots can proceed concurrently. A simulated log/webhook can itself be duplicated if a crash occurs after sending but before acknowledgement—deduplicate at the receiver where possible. |
| RabbitMQ / Kafka / Redis Streams | Real broker features, independent consumers, retries/retention, and better multi-instance scaling. | Adds Docker/service startup and configuration (Docker is unknown); still needs an outbox for atomic DB-to-broker publication, idempotent consumers, and ordering policy. Better when deployment requirements justify it, not for this timebox. |
| In-process channel/event | Lowest setup and asynchronous within one process. | Volatile across crash/restart and not durable messaging; does not satisfy the release-to-notification reliability goal by itself. Reject. |

#### 5. Waitlist offer acceptance window
| Approach | Behavior and tradeoffs |
|---|---|
| **Exclusive expiring offer represented as a slot lease** | A release event is handled asynchronously. In one DB transaction, select the earliest eligible waiter by stable join sequence and conditionally change the slot from available to `Offered` with patient, offer deadline, and generation; mark that waiter offered and enqueue `OfferCreated`. The slot's exclusive-claim constraint prevents ordinary booking or another worker from taking it. Accept only via conditional update matching slot, patient, generation, and `offer_expires_at > now`; acceptance converts it to confirmed. On timeout/cancel, atomically expire the offer, advance the waiter, and enqueue the next offer. Duplicate events are no-ops against the same release/offer generation. | Recommended: simple state machine and race-safe acceptance; requires a worker and explicit idempotency. If a waiter is invalid/ineligible, record the skip and continue FIFO. |
| Non-exclusive notification, claim on acceptance | Avoids reserving while the patient decides. | Several patients may be notified or race to accept; conflicts with “first patient on the list” and risks disappointing users. Reject. |

### Initial Recommendation (Superseded)
Use .NET 8 with a deliberately small Clean Architecture boundary, a file-backed SQLite database, and `dotnet run` as the local startup path (assuming the reviewer has the .NET 8 SDK). Enforce booking exclusivity in SQLite—not in process—with conditional state transitions, a unique constraint for live claims, and short transactions. Use a DB-polling worker for hold/offer expiry and a transactional outbox for durable asynchronous release processing and simulated notifications. Model waitlist offers as exclusive, expiring slot leases; order by a persisted FIFO sequence, and make every handler idempotent. This meets the brief without requiring Docker or a separately installed broker while keeping a credible path to a broker-backed deployment later.

### Risks
- SQLite's single-writer throughput is intentionally limited; verify contention/retry behavior with concurrent integration tests and move to a server RDBMS before multi-instance/high-volume deployment.
- At-least-once delivery permits duplicate notification attempts; receiver-side idempotency and stale-generation checks are required, and external side effects cannot be exactly-once without cooperation from the receiver.
- Polling adds bounded delay; define intervals, lease recovery, retry limits, and dead-letter visibility. Keep all deadlines in UTC and use an injectable clock in tests.
- `dotnet run` assumes an installed .NET 8 SDK. Confirm the target runtime/tooling and provide a concise README startup path; Docker cannot be assumed.
- Authentication, authorization, real patient data, multi-center tenancy, production deployment, and regulatory controls are outside this demo's scope and must not be implied by synthetic-data handling.

### Original Exploration Outcome (Superseded)
At the end of the initial exploration, the recommendation was to proceed with a single-process .NET 8 + SQLite + transactional outbox design. The subsequent proposal and design superseded that recommendation; the selected implementation uses .NET 10 and PostgreSQL.

---

> Historical status metadata below records the recommendation at the time of this exploration, not the current project state.

**Status**: success  
**Executive Summary**: Compared greenfield stacks and state, expiry, and messaging designs. Recommend .NET 8 + SQLite with DB-enforced exclusive claims, polling expiry, and a transactional outbox for durable at-least-once asynchronous waitlist offers.  
**Artifacts**: `openspec/changes/appointment-booking/exploration.md`  
**Next Recommended**: sdd-propose  
**Risks**: SQLite is single-writer and demo-scale; outbox delivery is at-least-once, requiring idempotent handlers and duplicate-aware notification behavior.  
**Skill Resolution**: paths-injected — sdd-explore, sdd-phase-common, openspec-convention
