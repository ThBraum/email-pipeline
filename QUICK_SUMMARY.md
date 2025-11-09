
For recruiters and non‑experts (TL;DR)
-------------------------------------

- What this is: a small system that receives requests to send emails and processes them reliably in the background. It’s designed to be fault‑tolerant and observable (easy to monitor).
- Why it matters: in real products you don’t want to block the user while sending emails. You queue the work, process it asynchronously, and can retry if something fails.
- How it works in 30 seconds:
  1) A client calls the API to send an email.
  2) The API saves a record in the database and puts a message in a queue (RabbitMQ) instead of sending the email right away.
  3) A background worker (service) will read from that queue, send the email via SMTP, and update the status in the database.
  4) Redis avoids duplicates when the same request is sent twice (idempotency).
  5) Everything is instrumented so we can see health and metrics in monitoring tools (OpenTelemetry + Prometheus).

If you only look at one thing, check the diagram below and the endpoint `POST /emails` in the API.

<p align="center">
  <img src="docs/email-pipeline.en.jpeg" alt="Email Pipeline architecture" width="820" />
  <br/>
  <sub>Solid arrows = implemented · Dashed arrows = planned/not yet in code</sub>
</p>

#### Frequently Asked Questions:

- How do I run it locally with Docker?

  1) From the repo root, start everything:

     ```zsh
     docker compose up --build
     ```
  2) Open these URLs:
     - API health: http://localhost:8080/health
     - RabbitMQ (UI): http://localhost:15672 (user: guest / pass: guest)
     - Redis: tcp://localhost:6379
     - PostgreSQL: localhost:5432 (db: emails, user: postgres, pass: postgres)
     - Mailhog (SMTP/Web): http://localhost:8025
     - Prometheus (metrics UI): http://localhost:9090
  3) Send a sample request to enqueue an email (idempotency key optional):

     ```zsh
     curl -X POST http://localhost:8080/emails \
       -H 'Content-Type: application/json' \
       -d '{
         "to":"user@example.com",
         "subject":"Hello",
         "body":"This is a test",
         "idempotencyKey":"demo-123"
       }'
     ```

- Do we need to configure SMTP for development?

  - For development the stack uses Mailhog, already defined in `docker-compose.yml`.
  - Environment variables consumed by the Worker (when the consumer is implemented):
    - `Smtp__Host=mailhog`
    - `Smtp__Port=1025`
    - `Smtp__From=no-reply@example.com`
  - You can swap Mailhog for a real SMTP by changing these env vars in Docker or `appsettings`.

- What if I want to use a real SMTP provider (not Mailhog) in production with my own sender address?

  You can point the Worker to any SMTP provider (SendGrid SMTP relay, Amazon SES SMTP, Gmail/Workspace SMTP, etc.). In containers, set these variables (compose uses `.env`):

  ```env
  # Required
  Smtp__Host=smtp.yourprovider.com
  Smtp__Port=587              # 465 for SMTPS/SSL, 587 for STARTTLS
  Smtp__From=no-reply@yourdomain.com

  # If your Worker adds auth options later, include:
  Smtp__User=apikey-or-username
  Smtp__Password=your-secret
  Smtp__EnableSsl=true        # or false if using STARTTLS negotiated by the client
  ```

  Steps to go live safely:
  1) Create/verify your sender domain and address at the provider (SPF, DKIM, DMARC).
  2) Put credentials in a secure store (secrets manager / CI variables) and inject as env vars.
  3) Set `Smtp__From` to your custom address and deploy. Keep Mailhog disabled in production.
  4) Observe the API/Worker health and metrics. Add retries/backoff and a dead-letter queue if you expect spikes.

  Note: This repo currently stubs the Worker (no consumer). To actually send emails in prod, implement the MassTransit consumer in `EmailService.Worker` and have it read the SMTP settings above. Mailhog is only for development/local testing.

- Is `idempotencyKey` required, and how do I obtain its value?

  - It is optional. If you provide it, the API uses Redis to guarantee the same logical request isn’t processed twice.
  - Generate a random UUID per logical email on the client side (e.g., `uuidv4()`) and send it as `idempotencyKey`.
  - If omitted, the request still works, but automatic de‑duplication won’t be applied on the server.

-- How can I monitor health and metrics using OpenTelemetry + Prometheus tools? Step-by-step

  1) Health: open `http://localhost:8080/health` (checks DB, Redis, RabbitMQ).
  2) Metrics: the API exports metrics via OTLP to the OpenTelemetry Collector, and Prometheus scrapes the Collector.
  3) Open Prometheus UI: `http://localhost:9090` and try basic queries like `http_requests_duration_seconds_count` (ASP.NET) or `process_cpu_seconds_total` (runtime). Metric names vary by OTel/ASP.NET version.
  4) Traces: they are sent to the Collector; to visualize traces you can add a UI like Jaeger/Tempo (not included by default in this stack).

- Default credentials for PostgreSQL and RabbitMQ

  - PostgreSQL: host `localhost`, port `5432`, database `emails`, user `postgres`, password `postgres`.
  - RabbitMQ UI: `http://localhost:15672` with user `guest`, password `guest`. AMQP port `5672`.

Additional notes
----------------

- Current state: the API path is fully working (health checks, DB migrations, Redis, publish to RabbitMQ, telemetry). The Worker is a stub and does not consume messages yet; in the diagram, this is the dashed arrow from RabbitMQ to Worker.
- Key files to peek:
  - API endpoint and wiring: `src/EmailService.Api/Program.cs`
  - Contracts (message shape): `src/EmailService.Contracts/EmailContracts.cs`
  - Data model/EF Core: `src/EmailService.Infrastructure/Entities.cs`
- Next small steps (nice improvements):
  1) Implement MassTransit consumer in the Worker and send via SMTP (Mailhog in dev).
  2) Record send attempts in PostgreSQL and expose a summarized status in `GET /emails/{id}`.
  3) Add Worker OpenTelemetry (traces/metrics) and error‑rate counters in Prometheus.
