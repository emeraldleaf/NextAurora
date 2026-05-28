# Messaging transport selection — a decision guide

> **Portable reference, not NextAurora-specific.** This is a reusable decision
> guide for choosing a messaging transport on any system: Redis Pub/Sub, Redis
> Streams, RabbitMQ, Azure Service Bus, AWS SNS+SQS, Kafka / Azure Event Hubs /
> AWS Kinesis. NextAurora's own choice appears at the end as a worked example.
> Lift this doc into another repo and the guide still applies.

---

## Step 0 — Do you need a broker at all?

Before choosing a transport, check you need one. A broker buys you **temporal
decoupling** (producer and consumer don't have to be up at the same time) and
**load levelling** (a spike becomes a queue, not a cascade). If you don't need
either — a synchronous request/response where the caller must have the answer
now — use a direct call (HTTP/gRPC), not a broker. And for "run this slightly
later, same process," an in-process queue or a DB-backed outbox row can be
enough. Reach for a broker when work must survive process restarts, fan out to
multiple consumers, or absorb bursts.

---

## The decision axes (ask in this order)

1. **Durability — can you afford to lose a message?**
   - *No loss tolerable* → you need a **durable** transport (persists until the
     consumer acks). That's everything below *except* Redis Pub/Sub.
   - *Loss is fine* (ephemeral signal, benign fallback) → **Redis Pub/Sub** is
     the cheapest, lowest-latency option.
2. **Replay / retention — do you need to re-read history?**
   - *Yes* (replay from an offset, add a new consumer that reads all history,
     multiple consumers at independent positions, an ordered audit log) → you
     need a **log/stream**: Kafka, Azure Event Hubs, AWS Kinesis, or Redis
     Streams.
   - *No* (consume once, ack, move on) → a **queue/topic** broker: RabbitMQ,
     Azure Service Bus, AWS SNS+SQS.
3. **Ordering — do you need strict per-key order?**
   - Kafka (per-partition), Azure Service Bus (sessions), AWS SQS FIFO.
4. **Throughput — what sustained rate?**
   - Millions/sec sustained → Kafka-class log. Thousands/sec and below →
     any durable queue handles it.
5. **Operational model — managed or self-hosted? Cloud lock-in?**
   - Cloud-native managed: Azure Service Bus (Azure), SNS+SQS (AWS), Event Hubs
     / Kinesis (managed streams).
   - Portable / self-hostable: RabbitMQ, Kafka, NATS, Redis. (Managed variants
     exist too: CloudAMQP, Confluent, Amazon MQ/MSK.)
6. **Existing infrastructure — what do you already run?**
   - Already on a cloud → its native bus is the least-friction durable choice.
   - Already run Redis + modest needs → Redis Streams avoids a new broker.
   - **Already have a durable bus → do NOT add a second messaging system.**
     Use the one you have.
7. **Latency — how tight?**
   - Redis Pub/Sub is lowest (in-memory, no persistence). Disk-backed durable
     transports add the persistence cost; usually irrelevant outside hot paths.

---

## Comparison matrix

| Transport | Durable? | Replay? | Ordering | Throughput | Hosting | Best for |
|---|---|---|---|---|---|---|
| **Redis Pub/Sub** | ❌ fire-and-forget | ❌ | ❌ | High | Self/managed Redis | Ephemeral fanout where loss is OK — cache-invalidation backplane, presence, live ticks |
| **Redis Streams** | ✅ (per Redis persistence) | ✅ | Per-stream | Moderate | Self/managed Redis | "Lightweight Kafka" — durable log + replay when you already run Redis |
| **RabbitMQ** | ✅ | ❌ (core; Streams plugin exists) | Per-queue | High | Self-host / CloudAMQP / Amazon MQ | General durable work queues + flexible routing, portable |
| **Azure Service Bus** | ✅ | ❌ (use Event Hubs to replay) | Sessions | High | Managed (Azure) | Azure-native durable pub/sub + workflow orchestration, dedup, scheduled delivery |
| **AWS SNS + SQS** | ✅ | ❌ (use Kinesis to replay) | SQS FIFO | High | Managed (AWS) | AWS-native durable pub/sub (SNS fanout → SQS queues) |
| **Kafka / Event Hubs / Kinesis** | ✅ | ✅ | Per-partition | Very high | Self (Kafka) / managed (Confluent, Event Hubs, Kinesis, MSK) | High-throughput event streaming, event sourcing, analytics/CDC, replay |

---

## Per-transport notes

**Redis Pub/Sub** — at-most-once, no persistence. A subscriber that's down when
a message publishes misses it forever. Lowest latency. *Use it* for ephemeral
fanout where a miss is benign: cache-invalidation backplanes (a missed
invalidation just means stale-until-TTL), online-presence, live dashboard
ticks. *Don't use it* for anything that can't lose a message.

**Redis Streams** — append-only log with consumer groups, offsets, and `XACK`.
Durable to the extent Redis is configured to persist (AOF/RDB) — weaker than a
dedicated log if Redis is running cache-style with persistence off. *Use it* as
a lightweight durable queue/stream when you already run Redis and don't want
Kafka's operational weight, at modest-to-moderate throughput. *Don't* overload a
cache-role Redis with a heavy streaming workload that competes for memory, and
don't expect Kafka-grade throughput.

**RabbitMQ** — durable queues + exchanges (direct/topic/fanout/headers),
at-least-once, dead-letter queues, mature and portable. The classic
general-purpose broker. *Use it* for durable work queues and pub/sub with
flexible routing when you want to stay cloud-portable. Core model is queues
(consumed = gone, no offset rewind); RabbitMQ Streams (3.9+) adds a replayable
log if you specifically need it, but reach for a real log if streaming is the
point.

**Azure Service Bus** — managed, durable queues + topics/subscriptions,
at-least-once, sessions (FIFO/ordering), duplicate detection, scheduled and
deferred delivery, DLQ. *Use it* for Azure-native durable pub/sub and workflow
orchestration. It's a broker, not a log — to replay/event-source on Azure,
that's **Event Hubs**.

**AWS SNS + SQS** — managed. SNS is the pub/sub fanout topic; SQS is the durable
queue. SNS→SQS together = durable pub/sub. At-least-once, SQS FIFO for ordering,
DLQ. *Use it* for AWS-native durable messaging. Not a replay log — that's
**Kinesis** (or MSK for Kafka).

**Kafka / Azure Event Hubs / AWS Kinesis** — durable, partitioned, append-only
logs. Replay from any offset, consumer groups at independent positions, ordering
per partition, very high throughput. *Use it* for event streaming, event
sourcing, CDC/analytics pipelines, and genuine replay needs. Heaviest to operate
(self-hosted Kafka) — prefer a managed variant (Confluent, Event Hubs, Kinesis,
MSK) unless you have the ops appetite. *Overkill* below real scale; "we might
need replay someday" is not a reason to start here.

---

## Common mistakes

- **"Pub/sub loses messages, so use a stream."** Conflates two axes. Loss
  depends on **durability** (durable queue/topic doesn't lose); replay is a
  *separate* capability. A durable queue + an outbox on the publish side +
  idempotent consumers already prevents loss. Reach for a stream for *replay*,
  not for *no-loss*. (The misconception usually comes from **Redis Pub/Sub**
  specifically, which *is* non-durable — but that's a property of Redis Pub/Sub,
  not of pub/sub as a pattern.)
- **Adding a stream "in case we need replay someday."** YAGNI. Streams carry the
  highest ops/cost. Add one when a concrete replay/retention/event-sourcing need
  lands, not preemptively.
- **Running two messaging systems.** If you already have a durable bus, don't
  bolt on Redis Pub/Sub (or anything else) for messaging — use the bus. The one
  legitimate exception is a *different job*: e.g. Redis Pub/Sub as a cache
  backplane while the bus carries domain events.
- **Chasing exactly-once.** End-to-end exactly-once is largely a myth. The
  practical guarantee is **at-least-once + idempotent consumers** — design
  handlers to tolerate duplicates (dedup key, status guard, upsert).
- **Forgetting the producer-side gap.** No transport saves you if the producer
  crashes after the DB commit but before publishing. That's the **transactional
  outbox**'s job (persist the event in the same transaction as the entity write,
  dispatch with retry) — orthogonal to queue-vs-stream.

---

## .NET note — the transport is usually swappable

With a messaging abstraction like **Wolverine** or **MassTransit**, the
transport is a pluggable adapter. The handlers, the outbox, retry/idempotency,
and context propagation are transport-agnostic; only a small registration block
changes (`UseAzureServiceBus` → `UseRabbitMq` → `UseAmazonSqs` → `UseKafka`).
So the transport choice is **low-stakes and reversible** — pick what fits today,
swap later for the cost of a config change, not a rewrite. Don't over-agonize
the decision; do avoid running two systems at once.

---

## Worked example — NextAurora's choice

NextAurora is an event-driven e-commerce saga (Order → Payment → Shipping →
Notification). Applying the guide:

- **Durability: required** (can't lose a payment event) → durable transport.
- **Replay: not needed** (no event-sourcing; the hand-rolled `EventLogs` replay
  table was deliberately deleted — any future replay rides Wolverine's own
  message store) → a **queue/topic broker**, not a stream.
- **Hosting:** Azure Service Bus in dev (Aspire-managed emulator); **RabbitMQ**
  in the Hetzner + Dokploy deployment (co-located container, no cross-cloud
  seam) — see [full-saga-deployment-plan.md](full-saga-deployment-plan.md) D3.
- **Producer-side gap:** closed by the **Wolverine transactional outbox**.
- **Duplicates:** handled by **idempotent handlers** (at-least-once delivery).
- **Redis:** used *only* as the HybridCache L2 tier — **not** for messaging. Its
  one legitimate future pub/sub role is the HybridCache cross-replica
  invalidation backplane (a loss-tolerant fanout; see
  [STATUS.md "If we deploy multi-replica"](STATUS.md)) — never as a saga
  transport.
- **Streams:** none. The durable bus + outbox + idempotency stack already
  prevents loss; there's no replay/retention need to justify Kafka/Event Hubs.

Net: **durable pub/sub (ASB dev / RabbitMQ deploy) + transactional outbox +
idempotent consumers** is the correct shape — it can't lose a message and
tolerates the duplicates at-least-once delivery implies. See CLAUDE.md
"Communication Patterns" for the in-repo rule, including "Durability ≠ replay —
don't reach for a stream just to avoid losing messages."
