---
id: wiki-powerbi-report-specifications
title: "A Power BI report travels as its specification, and the semantic layer keeps the ones git does not"
type: decision
summary: "Why pbix-extract stayed put while its output moved into committed specs and the semantic layer, with uploads read by an isolated service."
keywords:
  - powerbi
  - pbix
  - pbix.yaml
  - report specification
  - semantic layer
  - upload
  - kept extraction
  - pbix-extractor
  - sandbox
  - managed sync
  - data loss
sourceRefs:
  - src/SqlFlow.Lineage/Collection/ReportSpecs.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/PbixExtractTool.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.ControlPlane/Api/SemanticReportEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ReportExtractionClient.cs
  - src/SqlFlow.PbixExtractor/ExtractionEndpoint.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.PowerBi.cs
  - Dockerfile.pbix-extractor
  - deploy/compose/docker-compose.yml
rawRefs:
  - POWERAI.md
referenceRefs:
  - flow-subscribers
  - concept-semantic-layer
  - cli-control-plane
related:
  - wiki-powerbi-model-entity-resolution
  - wiki-semantic-layer-is-the-allow-list
updated: 2026-09-16
---

# A Power BI report travels as its specification, and the semantic layer keeps the ones git does not

## The failure that forced it

Every catalog sync deletes a repo's subscriber report and model rows and rebuilds them from what it collected. The
rebuild only had one input: running `tools/pbix-extract` over the `.pbix` a subscriber declares. Two things are
routinely missing where a sync runs:

- the extractor, which the control plane deliberately never carries (a `.pbix` is untrusted input for a
  memory-unsafe decoder, see [the model entity decision](powerbi-model-entity-resolution.md) and POWERAI.md's
  security posture section), and
- the `.pbix` itself, which is large and usually git-ignored (the sample under `samples/powerbi/` is).

So a developer's `sqlflow db sync` wrote a report's pages and model, and the managed sync's next poll, running in
the control plane against a fresh clone, rebuilt them from nothing. The semantic layer's `reportModels` went empty
with no error, only a warning in the sync trace.

## What was decided

The question was put as "should the extractor move to the semantic layer". The answer taken was: the TOOL stays
where it is, and its OUTPUT becomes the unit that moves and is stored.

1. **The specification is a first-class artifact.** `ReportSpecs` names it once: a `<report>.pbix.yaml` a subscriber
   may declare instead of the `.pbix`, written by `sqlflow powerbi extract`. Its label is the `.pbix` file name, so a
   subscriber switching from the report to its committed specification keeps every stored key.
2. **The semantic layer stores the specifications git does not carry** (`CatalogSemanticReportSpec`), with two
   origins. An `upload` is a person's and stays until a person removes it. An `extracted` row is the copy a sync
   keeps of a declared `.pbix` it could read, served by any later sync that cannot read it. Neither is part of the
   repo-scoped rows a sync rebuilds, so the derived rows are now always rebuilt from something.
3. **A GUI upload is read by an isolated service, not the control plane.** `SqlFlow.PbixExtractor` is its own image
   with no catalog, warehouse, or git credential, on a network only the control plane can reach, behind a shared
   key. The control plane streams the upload through and validates the answer as it validates any upload. This is
   the "separate sandboxed job" POWERAI.md had named as the right shape, built as a small always-on service rather
   than an on-demand job.
4. **Every path ends at one store.** The GUI extracts, shows the result, then calls the store endpoint;
   `sqlflow powerbi publish` calls the same endpoint; the CLI's `extract` uses the local tool when a machine has one
   and the service when it does not. There is one reader of a specification (`PbixExtractTool.Parse`) and one
   canonical form (`ReportSpecs.Normalize`), used by the collector, the store, the service, and the CLI.

## What was rejected, and why

- **Moving extraction into the semantic layer wholesale.** A visual's rendered SQL feeding `TSqlLineageExtractor`
  into consumption edges is lineage: it is repo-scoped, rebuilt each pass, and must keep working for the offline
  `sqlflow lineage` command, which has no catalog at all. Only the model and structure needed a durable home.
- **Keeping the old rows when a sync could not extract.** The catalog already preserves previously-derived edges for
  an unreachable server (`DegradedServers`), and the same trick was considered for reports. It was rejected because
  a report's queries, edges, pages, visuals and model rows would each need a partial-preservation rule, where a kept
  specification rebuilds all of them through the one existing path.
- **Uploading the `.pbix` and parsing it in the control plane.** The simplest upload, and exactly what the security
  posture exists to prevent: the process holding catalog credentials and warehouse reach would run the decoder.
- **Running uploads on the compute workers.** The worker already runs untrusted-ish work, but it holds every data
  source credential the estate's flows reference, which is the wrong neighbour for a hostile file.
- **An on-demand job per upload.** Stronger isolation per file, but it needs a job runner in every deployment
  target and makes an upload wait on a cold start. A single small service with a slot limit, a read-only root, and
  no credentials gives the same blast radius for this workload.
- **Committed specifications only, no upload.** Right for reports that belong to a repository, and it is the
  recommended form, but it asks every report owner for a git workflow and a C toolchain. The CLI's remote extraction
  and the GUI upload remove both requirements.

## Consequences to keep in mind

- **A kept extraction is not self-cleaning in every case.** It is dropped when its subscriber disappears, when the
  repository commits a specification for it, or when a sync that CAN read the declared directory no longer finds
  it. A sync that cannot tell (the path is missing, or a directory is empty because its reports are git-ignored)
  keeps it, by design.
- **An upload never disappears on its own.** A subscriber removed from `subscribers.yaml` leaves its uploads listed
  as not served until someone deletes them; the GUI and `sqlflow powerbi list` mark them.
- **The consumption side became a lineage input.** A fingerprint of every subscriber library and specification a
  sync read (`CatalogRepo.SubscriberInputHash`) now decides, alongside the flow hashes, whether a sync recomputes.
  Before this, an edited `subscribers.yaml` with no flow change was silently not applied until a forced sync.
- **The canonical form is what is stored, not what was sent.** Free text is passed through the credential redaction
  subscriber SQL takes, and anything the reader does not consume is dropped. A stored specification reads back
  exactly as it was validated, and the same report extracted locally or in the service stores identically (the
  specification name is derived from the report label, not from the temporary file the service writes).
