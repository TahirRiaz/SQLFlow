---
id: guide-lineage-graph
title: "Reading the lineage graph: scope, level, folders, stacks, waves, kinds and trace"
type: guide
summary: How the GUI's lineage graph decides what to draw, what each control of its bar changes, and how a drawing is shared as a link.
keywords:
  - lineage graph
  - lineage page
  - trace upstream downstream
  - impact analysis
  - wave range
  - folders
  - stacks
  - handovers
  - graph level
  - hide kinds
  - project-graph
  - whole repository
related:
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - concept-control-plane
  - concept-shadow-catalog
  - guide-lineage-demo
sourceRefs:
  - gui/src/features/lineage/LineageGraphPage.tsx
  - gui/src/features/lineage/graphModel.ts
  - gui/src/features/lineage/graphUrl.ts
  - gui/src/features/lineage/graphLayout.ts
  - gui/src/features/lineage/GraphControls.tsx
  - gui/src/features/lineage/GraphStatusLine.tsx
  - gui/src/features/lineage/GraphDetails.tsx
  - gui/src/features/lineage/LineageNodeCard.tsx
  - gui/unit/lineageGraph.test.ts
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
---

# Reading the lineage graph: scope, level, folders, stacks, waves, kinds and trace

A repository's lineage is hundreds of nodes: every flow, and every table, view, file and dataset a flow reads or
writes. A reader has one question at a time, so the graph at **Explore > Lineage** never draws everything it knows. It
opens on the smallest drawing that still tells the order of things, and each control of its bar widens or narrows that
drawing in one predictable way. The line under the bar says in words what is drawn and why.

Data runs top to bottom: what a node is made from is above it, what depends on it below. A level too wide for one
line is laid in several staggered lines. **Layout** turns the drawing left to right for those who prefer it.

## What opens

| You arrive by | Scope | What is drawn |
| --- | --- | --- |
| Picking a repository in **Scope** | The repository's active flows and what is downstream of them, in any repository | Flows only. With more than 30 flows in scope, each folder's flows draw as one node. |
| Picking a project (a root folder) in **Scope** | That folder's flows and what is downstream of them | The same, for the project. |
| A link or a search hit naming a flow, or an object with its repository | The whole repository | The trace of that node: only what is upstream and downstream of it. |
| A link or a search hit naming an object with no repository | The object's own neighbourhood, across every repository: its producers, what they read, and its consumers downstream | Everything, with the object selected. |

A walk downstream stops after three hops outside the scope's own flows, and a scope holds at most 500 flows. A node the
walk stopped at carries **more** under it; clicking it loads what is beyond. A scope that was cut short says so
in the line under the bar.

## The bar, left to right

The bar is in the order a drawing is made in: what to look at, how much of it, then which part.

| Control | What it changes |
| --- | --- |
| **Find** | Searches the drawing and the whole catalog. A node in scope is drawn (its folder opens, its stack unstacks), selected and centered. A flow found in the catalog opens its repository traced around it; an object opens its own neighbourhood. |
| **Scope** | A whole repository, or one of its projects. |
| **Show** | The level: how much data is drawn beside the flows. Each choice carries the number of nodes it draws for the whole scope. |
| **Draw as one** | **Folders** collapses each folder's flows into one node; **Stacks** collapses look-alike nodes. |
| **Waves** | Trims the drawing to a range of execution waves. |
| **Kinds** | The legend, and the filter: hides kinds of node. |
| **Layout** | Which way data runs: top to bottom, or left to right. |
| **Trace** | Shown while a node is traced: how many hops around it, and which side. |

### Show: the level

| Level | Draws | An arrow means |
| --- | --- | --- |
| **Flows** | Flows alone. | One flow reads what the other writes. Selecting either end names what the arrow passes through. |
| **Handovers** | Flows, and each object one flow writes and another reads. An object only one flow touches (a source file, a final table) is left out. | A flow writes or reads the object. |
| **Everything** | Every flow and every object. | The same. |
| **Data only** | Objects alone. | A flow moves data from one object to the other; on a small drawing the arrow names the flow. |

A flow's card has no room for what the level folds away. Selecting the flow lists everything it reads and writes in the
details panel, whatever the level draws, and an entry of those lists draws its node.

### Folders

With **Folders** on, a folder holding two or more flows draws as one node, named by its path, with its flows and waves
counted. An object only that folder's flows touch goes into it; an object that crosses folders stays drawn at the
levels that draw objects. A double click opens a folder in place, and **Collapse its folder** on a flow's menu closes
it. Folders are on by default when the scope holds more than 30 flows.

### Stacks

With **Stacks** on, three or more nodes of one kind with the same producers and the same consumers draw as one node:
twenty health checks that read one table, or the staging tables one flow writes and one procedure reads. The stack says how many
it holds and lists them in the details panel; a double click unstacks it. The selected node, the traced node, a node
whose lineage was not derived, and a node the walk stopped at are never stacked.

### Waves

Flows run in waves: a flow runs after everything it reads from
([the execution plan](../concepts/lineage-graph-and-plan.md)). The range has a stop per wave, with a bar as tall as the
wave's share of the flows. Dragging an end fades the flows that would leave, and releasing it applies the range. The
range keeps every wave between its ends, so a chain running through those waves stays whole; flows outside it are
dropped and nothing is bridged. Flows lineage gave no wave are the last stop.

### Kinds

**Kinds** lists every kind of node the drawing holds with its glyph and count. Hiding a kind takes its nodes out and
joins what they joined, so hiding `ing` flows leaves the landed table pointing at the table the flow filled.

### Trace

**Trace** on a selected node (or on its menu) draws only what is upstream and downstream of it. The bar then offers:

- **1, 2, 3, All**: how many hops from the node are drawn.
- **Upstream only**, **Both sides**, **Downstream only**: what the node is made from, or what a change to it reaches.

A producer or consumer the trace left out beside a drawn node is counted above or below that node (`+3`); clicking the count
draws exactly those. With folders on, the folders of the traced node and of the flows next to it are open, and folders
further away stay collapsed: the trace is exact where you are looking and coarse beyond.

## Selecting a node

A click selects a node: its producers light up in violet, its consumers in green, the rest fades, and the details
panel opens with what the node is, how many nodes are upstream and downstream of it, its delivery verdict from the data
streams board, and the lists of what it reads and writes (or, for a folder or a stack, what it holds). A double click
opens what the node stands for: a folder's flows, a stack's nodes, a flow's pipeline page, an object in the explorer.
The right-click menu offers the same, the node's YAML or SQL, and running a flow of the scope's own repository.

A node wears a mark only when something needs a look: a delivery verdict other than healthy, or lineage that could not
be derived (a dashed warning edge, with the reason on hover and in the panel).

## The line under the bar

The line states how many nodes are drawn of how many are in scope, the scope and the level, and every reduction in
force: folders collapsed, look-alikes stacked, the wave range, hidden kinds, the trace. Each reduction has a remove
button that undoes just that one, and **Reset** returns the drawing to how its scope opens. On the right it reports
what affects the drawing's truth: a scope cut short, flows whose lineage was not derived, and how many data streams
need a look (each one a jump to its flow).

## A drawing is a link

The whole drawing is in the address, so it can be shared and survives a reload.

| Parameter | Meaning |
| --- | --- |
| `repoId`, `project` | The scope. `repoId` alone is the whole repository. |
| `focus` | The node a link landed on. In a repository or a project it is traced; alone it seeds its own neighbourhood. |
| `show` | `flows`, `handovers`, `all` or `objects`. Left out, a repository or project opens at `flows` and a neighbourhood at `all`. |
| `group` | `on` or `off`. Left out, folders collapse when the scope holds more than 30 flows. |
| `open` | A folder the reader opened; repeated. |
| `stacks` | `off` draws look-alikes one by one. |
| `waves` | `2-4`: the first and last wave drawn. `u` is the unwaved stop. |
| `hide` | A hidden kind (`flow:ing`, `object:table`); repeated. |
| `trace`, `hops`, `dir` | The traced node, how many hops (left out: all), and `up` or `down` for one side. |
| `layout` | `right` draws left to right. Left out, the graph runs top to bottom. |
| `expand` | A node the walk was continued past; repeated. |

Links written for the earlier page still open: `view=objects` is `show=objects`, and `wave=3` is `waves=3-3`.

## Where the drawing comes from

One request, `GET /api/v1/lineage/project-graph`, returns the scope: `repoId` seeds every active flow of the repository,
`repoId` with `project` the flows of that root folder, and `expand` a node (an object key, or a pipeline id) with its
producers and consumers. Every reduction above is applied in the browser to that one answer, in a fixed order: level,
kinds, waves, folders, trace, stacks. The **Download** button writes the drawing as an SVG.

## See also

- [The lineage graph: report model, node identity, execution plan](../concepts/lineage-graph-and-plan.md)
- [Lineage tiers](../concepts/lineage-tiers.md)
- [The control plane](../concepts/control-plane.md)
