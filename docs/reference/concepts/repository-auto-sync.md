---
id: concept-repository-auto-sync
title: "Automatic sync of a repository, and syncing only on request"
type: concept
summary: Each repo source syncs on its own interval or only when asked; off, it holds its commit until a trigger request or a sync-now moves it, forward only.
keywords:
  - auto sync
  - automatic sync
  - autoSync
  - sync on request
  - sync only on request
  - hold commit
  - several environments
  - central estate repository
  - promotion
  - gated sync
  - merge reaches every environment
  - ControlPlane:Bootstrap:Repositories:AutoSync
  - repos auto-sync
related:
  - concept-control-plane
  - concept-external-triggers
  - guide-github-actions
sourceRefs:
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/RepoSourceStore.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncService.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncTarget.cs
  - src/SqlFlow.ControlPlane/Background/TriggerRequestProcessor.cs
  - src/SqlFlow.ControlPlane/Api/RepoSourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.Estate.cs
  - gui/src/features/external-triggers/RepositoriesTab.tsx
  - gui/src/features/repos/autoSync.ts
---

# Automatic sync of a repository

Every repo source has its own switch, **auto sync**. On (the default), the control plane syncs the source every
`SyncIntervalSeconds`, reading the tip of its branch each time, so a merge reaches the catalog, and the runs pinned to it,
within one interval. That is what a single environment wants, and what every source did before the switch existed.

Several control planes that sync one repository (a central estate repository behind dev, test and production) want
something else: a merge to reach each environment when that environment says so, not every environment within five
minutes. Turn auto sync off on the environments that should wait, and give each a [trigger](external-triggers.md) its
merge workflow calls. Each environment then takes the merge its own trigger reports, behind whatever that workflow
requires first (a GitHub environment's reviewers, for one).

## What syncs a source that syncs only on request

With auto sync off, a source holds the commit it last synced (`LastSyncedSha`) and has no next periodic sync. What asks
for a sync decides what the sync reads:

| Who asks | What the sync reads |
| --- | --- |
| A [trigger request](external-triggers.md#trigger-requests) naming a commit | Exactly that commit, once it is on the source's branch. Nothing merged after it comes along. |
| A trigger request naming no commit | The tip of the branch. |
| A person's sync-now (GUI, `POST /repos/sources/{id}/sync`, `sqlflow repos sync`) | The tip of the branch. The GUI asks for a confirmation first, naming the commit the source holds. |
| Something that changes what a sync derives: a schema registration run, a Power BI report stored or removed, a host module's own registry (of the targets its flows deliver to, say) | The commit the source holds, again, with the full lineage recompute. Never a newer commit. |
| A change of where the source reads from (its remote, directory or branch) | The tip of the new branch, since the commit it held belongs to the old one. |
| Re-registering it with other settings (its exclusions, its interval), or enabling it again | The commit it holds, with the new settings. |

Every sync's trace says which it read and why (step `target`), and a sync of the held commit also says where the branch's
tip is, so the trace shows what a request would bring in.

## It moves forward only

A move to a commit is placed on the branch's history before anything is read (the repository's history clone, fetched at
that moment, with the source's own credential):

- a commit not in the remote, an abbreviated id that names several commits, or a commit not on the source's branch is
  refused: the sync fails with the reason, and the source keeps what it holds;
- a commit the held one already contains (an earlier merge whose request arrived late) leaves the source where it is: a
  sync never moves back;
- a commit that contains the held one is read;
- a commit on a separate line of the branch's history from the held one (neither contains the other, as a merged
  feature commit and the branch commit beside it) is refused, since reading it would drop what the held commit has. Ask
  for a commit that contains both, such as the branch's tip;
- a held commit the remote no longer has (its history was rewritten) is left behind for the commit asked for, and the
  trace says so.

Two moves asked for before a sync takes them become one: a move to the tip wins over a commit, since the tip contains
every commit on the branch, and of two commits the later request's is kept. A trigger request whose commit the sync did
not reach (another request's earlier commit answered it) asks again; a request whose commit the held one already
contains is answered at once as `current` and fires on the held commit.

## A first sync, and a failed one

A source that has never synced successfully retries on its interval until its first sync succeeds, auto sync or not, so a
deployment comes up from its configuration alone with the tip of the branch at that moment. From then on it waits to be
asked. A failed sync of a source that syncs only on request is not retried on its own: the trigger request that asked
fails with the sync's error, and asking again (re-running the workflow, a sync-now) tries again.

A move asked for while a sync runs is the next sync's: the running one answers only the moves asked for before it began.

## Turning it on or off

An administrator turns auto sync on or off, per repository, on **External triggers** (Admin), tab **Repositories**. Each
row says how the repository syncs, which commit it holds and since when, which enabled triggers sync it, and who turned
the switch last. A row warns when auto sync is off and no enabled trigger syncs the repository (a merge then reaches it
only when a person syncs it now), and notes when auto sync is on although triggers sync it (a merge then reaches it within
the interval whatever the triggers do).

- Off: the next periodic sync is cancelled. A sync already due (asked for, or a first sync still retrying) still runs, and
  reads the commit the source holds.
- On: the source is due at once, so it catches up with the tip of its branch now and then syncs on its interval. The page
  asks for a confirmation, since that brings in everything merged since.

The same switch through the API and the CLI:

```text
PUT /api/v1/repos/sources/{id}/auto-sync      { "autoSync": false }     (admin scope)
sqlflow repos auto-sync estate off
```

The answer is the source as it now stands (`autoSync`, `autoSyncUpdatedBy`, `autoSyncUpdatedUtc`); the same answer when it
already synced that way. 400 without `autoSync`, 404 for an unknown source, 409 when the configuration sets the value.
Registering a source never changes its switch: a new source syncs on its own, and an existing one keeps what it has.

## In the configuration

A deployment can pin the switch for a repository it configures:

```text
ControlPlane__Bootstrap__Repositories__estate__RemoteUrl=https://github.com/acme/estate.git
ControlPlane__Bootstrap__Repositories__estate__AutoSync=false
```

Pinned, the API refuses to change it (409, naming `ControlPlane:Bootstrap:Repositories:estate:AutoSync`) and the
Repositories tab shows it locked. Unset, the switch is an administrator's, and a start never changes it; a value the
configuration stops stating stays as it was, for an administrator to change. A configured repository registered fresh
starts with the value the configuration gives, or on.

## A directory source

A local-path source holds no commit: with auto sync off it no longer syncs on its interval, and each sync it is asked for
reads the directory as it is then. A trigger request naming a commit is refused for it, as it always was.

## Where it is kept

`catalog.RepoSource` holds the switch (`AutoSync`), the setting that pins it (`AutoSyncConfiguredBy`), who turned it on or
off and when (`AutoSyncUpdatedBy`, `AutoSyncUpdatedUtc`), and the move a request asked for (`MoveRequestedUtc`,
`MoveToCommit`, the commit or null for the tip), cleared by the outcome of the sync that answered it. `GET /repos/sources`
returns `autoSync`, `autoSyncConfiguredBy`, `autoSyncUpdatedBy` and `autoSyncUpdatedUtc` with each source.

## See also

- [The control plane's managed sync](control-plane.md#managed-git-to-catalog-sync-repo-sources)
- [External triggers](external-triggers.md)
- [Syncing and firing from GitHub](../guides/github-actions.md)
