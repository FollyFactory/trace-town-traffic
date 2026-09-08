# Application Insights

**All three signals**, in Azure. The only setup here whose backend is not a
container on your machine, which changes three things: you need an Azure
subscription, the data costs money, and it takes a couple of minutes to show up.

```bash
echo 'APPLICATIONINSIGHTS_CONNECTION_STRING=<your connection string>' > deploy/.env
docker compose -f deploy/appinsights.yml --profile generator up -d
```

| | |
|---|---|
| Your data | Azure portal → your Application Insights resource |
| Control API | <http://localhost:8080/api/status> |

## Setting up Azure

Six commands. Everything lands in one resource group so that deleting the
group deletes the lot.

```bash
az extension add --name application-insights --upgrade
az group create --name trace-town --location uksouth

az monitor log-analytics workspace create \
  --resource-group trace-town --workspace-name trace-town-logs

az monitor app-insights component create \
  --app trace-town-ai --resource-group trace-town --location uksouth \
  --workspace trace-town-logs
```

Then **set a daily cap before you send anything**, because the generator is a
busy system and Azure bills by the gigabyte:

```bash
az monitor app-insights component billing update \
  --app trace-town-ai --resource-group trace-town --cap 1
```

One gigabyte a day is the smallest the CLI accepts — the portal will go lower,
under Application Insights → Usage and estimated costs → Daily cap. Ingestion
stops when the cap is hit and resumes the next day, which is exactly the failure
you want: an empty chart, not an invoice.

Finally, the connection string:

```bash
az monitor app-insights component show \
  --app trace-town-ai --resource-group trace-town \
  --query connectionString --output tsv
```

Put it in `deploy/.env` as `APPLICATIONINSIGHTS_CONNECTION_STRING=...`. That
file is gitignored, and reading it from there rather than exporting it keeps
the key out of your shell history.

Prefer clicking? Portal → Create a resource → Application Insights, pick
**Workspace-based** (the only kind there is now), and copy the connection
string from the Overview page. Create the Log Analytics workspace first, or let
the blade make one for you.

## What it costs

Measured rather than guessed: the exporter's payloads were captured off the
wire and weighed. Azure bills on its own measure of ingested size, so treat
these as good enough to plan with and not good enough to reconcile an invoice
against.

| `--rate` | Records/second | Per hour | Per day |
|---|---|---|---|
| `0.05` (the default here) | ~60 | ~300 MB | ~7 GB |
| `0.1` | ~120 | ~600 MB | ~14 GB |
| `1` (the example as written) | ~1,200 | ~6 GB | ~140 GB |

At around £2.30 a gigabyte that makes the default roughly £15 a day left
running, against a free grant of 5 GB a month. **This is not a thing to leave up
overnight.** Half an hour is plenty to populate a service map, and half an hour
of the default is around 150 MB — inside the free grant.

To turn it down, change `--rate` in `deploy/appinsights.yml`. Reach for that
rather than `--sample`: the exporter never sets Application Insights'
`sampleRate` field, so head-sampled traces arrive claiming to be the whole
population, and every rate you compute downstream is wrong by exactly the
sampling ratio. Rate cuts the volume honestly; sampling cuts it and lies.

## Where each signal lands

| Sent | Application Insights | Log Analytics |
|---|---|---|
| Server and consumer spans | `requests` | `AppRequests` |
| Client and producer spans | `dependencies` | `AppDependencies` |
| Logs | `traces` | `AppTraces` |
| Metrics | `customMetrics` | `AppMetrics` |

Two names for one table: the left column is what the Application Insights
**Logs** blade accepts, the right is what the Log Analytics workspace accepts.
They are the same rows. Which name you need depends on which of the two you are
querying from — and if you are querying over the API, on which resource you
pointed the URL at. Column names differ too, not just capitalisation:
`duration` is `DurationMs`, `cloud_RoleName` is `AppRoleName`,
`customDimensions` is `Properties`.

Start here, in the Logs blade of the Application Insights resource:

```kql
dependencies
| summarize calls = count(), failed = countif(success == false)
    by from = cloud_RoleName, to = target
| order by calls desc
```

Twenty rows, one per edge, and it is the whole system.

## Two corrections, and why they are not optional

`deploy/config/collector-appinsights.yaml` runs a transform processor before
the exporter. Without it the data arrives and looks fine until you try to draw
a service map out of it, at which point nothing joins to anything.

**Role names carry the group.** The exporter builds `cloud_RoleName` as
`service.namespace + "." + service.name`, and the generator sets
`service.namespace` from a service's `group`. So the services arrive as
`orders.checkout-api` — while a dependency's `target` is the bare hostname,
`checkout-api`. Every edge would point at a name no service has. Dropping the
namespace makes both ends agree.

**Database and queue dependencies have no target at all.** This is an upstream
bug: for database and messaging spans the exporter reads `client.address`, and
falls back to `network.peer.address` — but a client span carries
`server.address`. HTTP is unaffected, because it takes its target from
`url.full`, which is what makes this so easy to miss. The http edges look
perfect while every database, cache and queue edge points at the empty string.
Copying `server.address` onto `network.peer.address` fixes it.

With both applied, `examples/ecommerce.json` produces 19 services and 20 edges,
no empty targets, and the only two targets without a service of their own are
`stripe` and `sendgrid` — which is correct. They are `external`, so nothing
about them is instrumented from the inside, and an edge pointing at one is all
you will ever see.

## Reading it back with Trace Town

Ingestion above is one credential. Reading is a different one, and more of a
faff, because Application Insights API keys were **retired on 31 March 2026**.
What is left is a Microsoft Entra service principal.

```bash
WORKSPACE_ID=$(az monitor log-analytics workspace show \
  --resource-group trace-town --workspace-name trace-town-logs \
  --query id --output tsv)

az ad sp create-for-rbac --name trace-town-reader \
  --role "Monitoring Reader" --scopes "$WORKSPACE_ID"
```

That prints `tenant`, `appId` and `password` — the tenant, client and secret
Trace Town asks for, and the password is shown once. The role assignment is the
step people miss: registering an app grants it nothing, and without it every
query returns 403 while the token itself is perfectly valid.

The workspace **GUID** — different from the resource id above, and the one that
goes in the query URL — is:

```bash
az monitor log-analytics workspace show \
  --resource-group trace-town --workspace-name trace-town-logs \
  --query customerId --output tsv
```

> **Mind the table names.** Querying `https://api.loganalytics.io/v1/workspaces/<guid>`
> puts you in the Log Analytics workspace, where the tables are `AppRequests`
> and `AppDependencies` and the columns are `AppRoleName`, `Target` and
> `DurationMs`. The classic `requests` / `dependencies` names resolve only in
> the Application Insights resource's own Logs blade. A query written against
> one and run against the other fails outright rather than returning nothing,
> which is at least a loud failure.

## Stopping it

```bash
docker compose -f deploy/appinsights.yml --profile generator down
```

`down -v` has nothing to delete here — the data is in Azure. To get rid of that
as well, `az group delete --name trace-town --yes`, which removes the workspace
and the Application Insights resource together.

## When the portal stays empty

**Wait five minutes.** Azure's ingestion latency is a minute or two on a good
day. Every other backend in this repo shows a span within a second, so the
instinct that something is broken arrives long before the data does.

**Then read the collector's log**, which is where every ingestion failure is
reported and the only place it is:

```bash
docker compose -f deploy/appinsights.yml logs collector
```

A malformed connection string, a hit daily cap and a rejected batch all look
identical from outside — an empty portal — and all three say so plainly there.
