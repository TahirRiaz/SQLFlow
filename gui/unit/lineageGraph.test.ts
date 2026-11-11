// The lineage graph's drawing rules: what each control leaves on the canvas. The estate here is the shape a real one
// has in small: a folder of flows that land, load and publish a table, a lookup flow in a folder of its own feeding the
// publishing flow, a dictionary only that flow reads, and a folder of checks that all read what it writes.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ProjectGraph, ProjectGraphPipeline } from "../src/api/types.ts";
import { layeredLayout, MAX_ROW_NODES, NODE_HEIGHT, NODE_SPACING } from "../src/features/lineage/graphLayout.ts";
import {
  buildGraphSource,
  closure,
  deriveView,
  kindCounts,
  levelSizes,
  reveal,
  stackIdOf,
  waveCounts,
  type View,
  type ViewOptions,
} from "../src/features/lineage/graphModel.ts";
import { clipMiddle } from "../src/features/lineage/graphText.ts";
import {
  groupedBy,
  levelOf,
  readGraphParams,
  scopeOf,
  tracedNode,
  wavesInRange,
  writeGraphParams,
} from "../src/features/lineage/graphUrl.ts";

const REPO = "r1";

const pipeline = (id: string, kind: string, wave: number, folder: string, extra: Partial<ProjectGraphPipeline> = {}): ProjectGraphPipeline => ({
  id,
  name: id,
  kind,
  wave,
  repoId: REPO,
  repoName: "estate",
  relativePath: `${folder}/${id}.yaml`,
  isSeed: true,
  depth: 0,
  ...extra,
});

const object = (key: string, kind: string, frontier = false) => ({ key, name: key, kind, location: kind === "file" ? null : "db.dbo", frontier });
const edge = (source: string, target: string, label = "", pipelineId: string | null = null) => ({ source, target, label, pipelineId });

function estate(change: (payload: ProjectGraph) => void = () => {}): ProjectGraph {
  const payload: ProjectGraph = {
    pipelines: [
      pipeline("pre", "file", 1, "src/flows"),
      pipeline("ing", "ing", 2, "src/flows"),
      pipeline("publish", "exp", 3, "src/flows"),
      pipeline("lookup", "cpy", 1, "lookups"),
      pipeline("check-1", "hc", 4, "checks"),
      pipeline("check-2", "hc", 4, "checks"),
      pipeline("check-3", "hc", 4, "checks"),
    ],
    edges: [],
    frontier: [],
    truncated: false,
    objects: [
      object("landed.csv", "file"),
      object("P", "table"),
      object("A", "table"),
      object("C", "message queue"),
      object("M", "dictionary"),
      object("T", "record type"),
    ],
    flowGraph: [
      edge("landed.csv", "pre"), edge("pre", "P"), edge("P", "ing"), edge("ing", "A"), edge("A", "publish"),
      edge("lookup", "C"), edge("C", "publish"), edge("M", "publish"), edge("publish", "T"),
      edge("T", "check-1"), edge("T", "check-2"), edge("T", "check-3"),
    ],
    objectGraph: [
      edge("landed.csv", "P", "pre", "pre"), edge("P", "A", "ing", "ing"), edge("A", "T", "publish", "publish"),
      edge("C", "T", "publish", "publish"), edge("M", "T", "publish", "publish"),
    ],
  };
  change(payload);
  return payload;
}

const plain: ViewOptions = {
  level: "flows",
  hiddenKinds: new Set(),
  waves: null,
  grouped: false,
  openGroups: new Set(),
  trace: null,
  revealUp: new Set(),
  revealDown: new Set(),
  stacked: false,
  openStacks: new Set(),
  pinned: new Set(),
};

const SRC = `group:${REPO}/src/flows`;
const CHECKS = `group:${REPO}/checks`;
const ids = (view: View) => view.nodes.map((node) => node.id).sort();
const edges = (view: View) => view.edges.map((item) => `${item.source}>${item.target}`).sort();
const draw = (options: Partial<ViewOptions>, payload = estate()) => deriveView(buildGraphSource(payload), { ...plain, ...options });

describe("the level", () => {
  it("draws flows alone, joined where one reads what another writes, and remembers what the arrow passes through", () => {
    const view = draw({ level: "flows" });
    assert.deepEqual(ids(view), ["check-1", "check-2", "check-3", "ing", "lookup", "pre", "publish"]);
    assert.deepEqual(edges(view), [
      "ing>publish", "lookup>publish", "pre>ing", "publish>check-1", "publish>check-2", "publish>check-3",
    ]);
    assert.deepEqual(view.edges.find((item) => item.id === "pre\u0000ing")?.via, ["P"]);
    assert.equal(view.total, 13);
  });

  it("counts on a flow the objects the level folded into it", () => {
    const publish = draw({ level: "flows" }).nodes.find((node) => node.id === "publish")!;
    assert.deepEqual([publish.foldedIn, publish.foldedOut], [3, 1]);
  });

  it("draws, at handovers, only the objects something writes and something reads", () => {
    const view = draw({ level: "handovers" });
    assert.deepEqual(ids(view).filter((id) => ["landed.csv", "P", "A", "C", "M", "T"].includes(id)), ["A", "C", "P", "T"]);
    const publish = view.nodes.find((node) => node.id === "publish")!;
    assert.deepEqual([publish.foldedIn, publish.foldedOut], [1, 0]);
  });

  it("keeps an object the server stopped at as a handover, since its readers exist", () => {
    const payload = estate((p) => {
      p.pipelines = p.pipelines.filter((item) => !item.id.startsWith("check"));
      p.flowGraph = p.flowGraph.filter((item) => !item.target.startsWith("check"));
      p.objects = p.objects.map((item) => (item.key === "T" ? { ...item, frontier: true } : item));
    });
    assert.ok(ids(draw({ level: "handovers" }, payload)).includes("T"));
    // With the object folded away, the flow writing it carries the key the server can be asked to walk past.
    assert.deepEqual(draw({ level: "flows" }, payload).nodes.find((node) => node.id === "publish")?.frontierKeys, ["T"]);
  });

  it("draws everything, and the data alone as objects joined by the flows between them", () => {
    assert.equal(draw({ level: "all" }).nodes.length, 13);
    const data = draw({ level: "objects" });
    assert.deepEqual(ids(data), ["A", "C", "M", "P", "T", "landed.csv"]);
    assert.deepEqual(data.edges.find((item) => item.id === "P\u0000A")?.labels, ["ing"]);
    assert.equal(data.total, 6);
  });
});

describe("hidden kinds and waves", () => {
  it("bridges a hidden kind, so the chain through it stays joined", () => {
    const view = draw({ level: "all", hiddenKinds: new Set(["flow:ing"]) });
    assert.ok(!ids(view).includes("ing"));
    assert.deepEqual(view.edges.find((item) => item.id === "P\u0000A")?.via, ["ing"]);
  });

  it("trims to the chosen waves without bridging, and drops the objects left joined to no drawn flow", () => {
    const view = draw({ level: "all", waves: new Set([3, 4]) });
    assert.deepEqual(ids(view), ["A", "C", "M", "T", "check-1", "check-2", "check-3", "publish"]);
    assert.ok(!edges(view).some((item) => item.startsWith("ing>")));
  });

  it("never hides the pinned node", () => {
    const view = draw({ level: "flows", waves: new Set([4]), hiddenKinds: new Set(["flow:file"]), pinned: new Set(["pre", "M"]) });
    assert.ok(ids(view).includes("pre"));
    assert.ok(ids(view).includes("M"));
  });
});

describe("folders", () => {
  it("draws a folder holding several flows as one node, and leaves a lone flow as itself", () => {
    const view = draw({ grouped: true });
    assert.deepEqual(ids(view), ["lookup", CHECKS, SRC].sort());
    assert.deepEqual(edges(view), [`${SRC}>${CHECKS}`, `lookup>${SRC}`].sort());
    const src = view.nodes.find((node) => node.id === SRC)!;
    assert.deepEqual([src.type, src.name, src.flowCount, src.waves], ["group", "src/flows", 3, [1, 2, 3]]);
    assert.ok(view.groupable);
  });

  it("takes into a folder the objects only that folder touches, and leaves what crosses folders drawn", () => {
    const view = draw({ grouped: true, level: "all" });
    assert.deepEqual(ids(view), ["C", "T", "lookup", CHECKS, SRC].sort());
    assert.equal(view.nodes.find((node) => node.id === SRC)?.objectCount, 4);
    assert.equal(view.locate.get("P"), SRC);
  });

  it("opens the folders the reader opened", () => {
    const view = draw({ grouped: true, openGroups: new Set([SRC]) });
    assert.deepEqual(ids(view), ["lookup", "publish", CHECKS, "ing", "pre"].sort());
  });

  it("says a scope has nothing to collapse when no folder holds two flows", () => {
    const payload = estate((p) => {
      p.pipelines = p.pipelines.filter((item) => item.id === "pre" || item.id === "lookup");
      p.flowGraph = [];
    });
    assert.equal(draw({ grouped: true }, payload).groupable, false);
  });
});

describe("a trace", () => {
  it("draws what is within the hops asked for, and counts on each node what it left out beside it", () => {
    const view = draw({ trace: { id: "ing", hops: 1, direction: "both" } });
    assert.deepEqual(ids(view), ["ing", "pre", "publish"]);
    const publish = view.nodes.find((node) => node.id === "publish")!;
    assert.deepEqual([publish.hiddenUp, publish.hiddenDown], [1, 3]);
    assert.ok(view.nodes.find((node) => node.id === "ing")?.anchor);
  });

  it("draws one side alone when asked", () => {
    assert.deepEqual(ids(draw({ trace: { id: "ing", hops: null, direction: "upstream" } })), ["ing", "pre"]);
    assert.deepEqual(
      ids(draw({ trace: { id: "ing", hops: null, direction: "downstream" } })),
      ["check-1", "check-2", "check-3", "ing", "publish"],
    );
  });

  it("brings back what the reader asks back beside a node", () => {
    const view = draw({ trace: { id: "ing", hops: 1, direction: "both" }, revealUp: new Set(["publish"]) });
    assert.ok(ids(view).includes("lookup"));
    assert.equal(view.nodes.find((node) => node.id === "publish")?.hiddenUp, 0);
  });

  it("opens the folder of the traced flow, and keeps a traced object drawn at any level", () => {
    assert.deepEqual(
      ids(draw({ grouped: true, trace: { id: "ing", hops: null, direction: "both" } })),
      [CHECKS, "publish", "ing", "pre"].sort(),
    );
    const view = draw({ level: "flows", trace: { id: "T", hops: 1, direction: "both" } });
    assert.deepEqual(ids(view), ["T", "check-1", "check-2", "check-3", "publish"]);
  });

  it("opens the folders of the flows next to the traced node, so a trace is exact where the reader is looking", () => {
    assert.deepEqual(
      ids(draw({ grouped: true, trace: { id: "publish", hops: null, direction: "both" } })),
      ["check-1", "check-2", "check-3", "ing", "lookup", "pre", "publish"],
    );
    // An object is next to the flows that write and read it, whatever the level folds away between them.
    assert.deepEqual(
      ids(draw({ grouped: true, level: "flows", trace: { id: "T", hops: 1, direction: "both" } })),
      ["T", "check-1", "check-2", "check-3", "publish"],
    );
  });

  it("traces a collapsed folder as one node", () => {
    const view = draw({ grouped: true, trace: { id: CHECKS, hops: 1, direction: "both" } });
    assert.deepEqual(ids(view), [CHECKS, SRC].sort());
  });

  it("says so, and draws the scope, when the traced node is not in it", () => {
    const view = draw({ trace: { id: "gone", hops: 1, direction: "both" } });
    assert.ok(view.traceMissing);
    assert.equal(view.nodes.length, 7);
  });
});

describe("stacks", () => {
  const STACK = stackIdOf(["check-1", "check-2", "check-3"]);

  it("draws nodes of one kind with the same producers and consumers as one", () => {
    const view = draw({ stacked: true });
    assert.deepEqual(ids(view), ["lookup", "publish", "ing", "pre", STACK].sort());
    const stack = view.nodes.find((node) => node.id === STACK)!;
    assert.deepEqual([stack.type, stack.kind, stack.members.length, stack.name], ["stack", "hc", 3, "check-1, check-2, check-3"]);
    assert.equal(view.locate.get("check-2"), STACK);
    assert.equal(view.represented, 7);
  });

  it("leaves an opened stack, and one the pinned node would leave too small, drawn one by one", () => {
    assert.equal(draw({ stacked: true, openStacks: new Set([STACK]) }).nodes.length, 7);
    assert.equal(draw({ stacked: true, pinned: new Set(["check-1"]) }).nodes.length, 7);
  });

  it("does not stack two nodes, or nodes with different consumers", () => {
    const payload = estate((p) => {
      p.pipelines.push(pipeline("after-check-3", "hc", 5, "late"));
      p.objects.push(object("R", "table"));
      p.flowGraph.push(edge("check-3", "R"), edge("R", "after-check-3"));
    });
    assert.ok(!draw({ stacked: true }, payload).nodes.some((node) => node.type === "stack"));
  });
});

describe("reveal", () => {
  it("opens the folder and the stack hiding a picked node", () => {
    const source = buildGraphSource(estate());
    const options = reveal(source, { ...plain, grouped: true, stacked: true }, "check-2");
    assert.deepEqual([...options.openGroups], [CHECKS]);
    assert.equal(deriveView(source, options).locate.get("check-2"), "check-2");
  });

  it("draws a picked object at a level that folds it away, and drops a trace that does not hold it", () => {
    const source = buildGraphSource(estate());
    const options = reveal(source, { ...plain, trace: { id: "pre", hops: 1, direction: "both" } }, "M");
    assert.equal(options.trace, null);
    assert.equal(deriveView(source, options).locate.get("M"), "M");
  });
});

describe("what the controls say", () => {
  const source = buildGraphSource(estate());

  it("sizes each level for the whole scope", () => {
    assert.deepEqual(levelSizes(source), { flows: 7, handovers: 11, all: 13, objects: 6 });
  });

  it("lists the waves in running order with their flows, unwaved last", () => {
    const withUnwaved = buildGraphSource(estate((p) => { p.pipelines.push(pipeline("loose", "hc", -1, "late")); }));
    assert.deepEqual(waveCounts(withUnwaved), [
      { wave: 1, count: 2 }, { wave: 2, count: 1 }, { wave: 3, count: 1 }, { wave: 4, count: 3 }, { wave: -1, count: 1 },
    ]);
  });

  it("lists the kinds a level draws, flows first", () => {
    assert.deepEqual(kindCounts(source, "flows").map((kind) => `${kind.key}=${kind.count}`), [
      "flow:hc=3", "flow:cpy=1", "flow:exp=1", "flow:file=1", "flow:ing=1",
    ]);
    assert.ok(kindCounts(source, "all").some((kind) => kind.key === "object:table" && kind.count === 2));
  });

  it("finds everything upstream and downstream of a drawn node", () => {
    const { upstream, downstream } = closure(draw({}), "publish");
    assert.deepEqual([...upstream].sort(), ["ing", "lookup", "pre"]);
    assert.deepEqual([...downstream].sort(), ["check-1", "check-2", "check-3"]);
  });

  it("says a flow whose lineage was not derived why", () => {
    const payload = estate((p) => {
      p.pipelines[1] = pipeline("ing", "ing", 2, "src/flows", { lineageComplete: false, incompleteReason: "The body was not harvested." });
    });
    assert.deepEqual(draw({}, payload).nodes.find((node) => node.id === "ing")?.incompleteReasons, ["The body was not harvested."]);
  });
});

describe("the layout", () => {
  it("lays a level too wide for one line in staggered lines, all above what they feed", () => {
    const inputs = Array.from({ length: 19 }, (_, i) => `in-${String(i).padStart(2, "0")}`);
    const at = layeredLayout([...inputs, "sink"], inputs.map((source) => ({ source, target: "sink" })));
    const lines = new Set(inputs.map((id) => at.get(id)!.y));
    assert.equal(lines.size, 3);
    assert.ok([...lines].every((y) => y + NODE_HEIGHT < at.get("sink")!.y));
    // No line holds more cards than a row has room for, no two cards share a place, and a line is shifted against
    // the one above it, so an edge from above passes between its cards.
    for (const y of lines) {
      assert.ok(inputs.filter((id) => at.get(id)!.y === y).length <= MAX_ROW_NODES);
    }
    assert.equal(new Set(inputs.map((id) => `${at.get(id)!.x}/${at.get(id)!.y}`)).size, 19);
    assert.notEqual(at.get("in-00")!.x, at.get("in-01")!.x);
    const xs = inputs.map((id) => at.get(id)!.x);
    assert.ok(Math.max(...xs) - Math.min(...xs) < MAX_ROW_NODES * NODE_SPACING);
  });

  it("puts a producer in a row above its consumers, and the nodes of a row side by side", () => {
    const view = draw({});
    const at = layeredLayout(view.nodes.map((node) => node.id), view.edges);
    assert.ok(at.get("pre")!.y < at.get("ing")!.y);
    assert.ok(at.get("ing")!.y < at.get("publish")!.y);
    assert.ok(at.get("publish")!.y < at.get("check-1")!.y);
    assert.equal(new Set(["check-1", "check-2", "check-3"].map((id) => at.get(id)!.x)).size, 3);
    assert.equal(new Set(["check-1", "check-2", "check-3"].map((id) => at.get(id)!.y)).size, 1);
    // A lone input slides down to the row above its only consumer instead of stranding in the top row.
    assert.equal(at.get("lookup")!.y, at.get("ing")!.y);
  });

  it("draws the same levels left to right when asked, one column a level", () => {
    const view = draw({});
    const at = layeredLayout(view.nodes.map((node) => node.id), view.edges, "right");
    assert.ok(at.get("pre")!.x < at.get("ing")!.x);
    assert.ok(at.get("ing")!.x < at.get("publish")!.x);
    assert.equal(new Set(["check-1", "check-2", "check-3"].map((id) => at.get(id)!.x)).size, 1);
    assert.equal(new Set(["check-1", "check-2", "check-3"].map((id) => at.get(id)!.y)).size, 3);
  });

  it("places every node of a cycle", () => {
    const at = layeredLayout(["a", "b", "c"], [{ source: "a", target: "b" }, { source: "b", target: "c" }, { source: "c", target: "a" }]);
    assert.equal(at.size, 3);
    assert.equal(new Set([...at.values()].map((position) => position.y)).size, 3);
  });
});

describe("the query string", () => {
  it("round-trips a drawing, leaving defaults out", () => {
    const search = new URLSearchParams("repoId=r1&show=handovers&waves=2-u&hide=flow%3Aing&group=off&open=g1&trace=x&hops=2&dir=up&stacks=off&layout=right&expand=k");
    const params = readGraphParams(search);
    assert.deepEqual(params.waves, { from: 2, to: -1 });
    assert.deepEqual([params.level, params.grouped, params.hops, params.direction, params.stacked, params.orientation], ["handovers", false, 2, "upstream", false, "right"]);
    assert.equal(writeGraphParams(params).toString(), "repoId=r1&expand=k&show=handovers&waves=2-u&hide=flow%3Aing&group=off&open=g1&trace=x&hops=2&dir=up&stacks=off&layout=right");
    assert.equal(writeGraphParams(readGraphParams(new URLSearchParams("repoId=r1"))).toString(), "repoId=r1");
  });

  it("opens links written for the earlier page", () => {
    const params = readGraphParams(new URLSearchParams("repoId=r1&project=p&view=objects&wave=3"));
    assert.equal(params.level, "objects");
    assert.deepEqual(params.waves, { from: 3, to: 3 });
  });

  it("falls back to the default on a value it does not know", () => {
    const params = readGraphParams(new URLSearchParams("show=nope&waves=x-y&hops=0&dir=sideways&group=maybe&layout=up"));
    assert.deepEqual([params.level, params.waves, params.hops, params.direction, params.grouped, params.orientation], [null, null, null, "both", null, "down"]);
  });

  it("scopes on a repository, a project, or a node with no repository", () => {
    const of = (text: string) => scopeOf(readGraphParams(new URLSearchParams(text)));
    assert.deepEqual([of(""), of("repoId=r"), of("repoId=r&project=p"), of("focus=k"), of("repoId=r&focus=k")], ["none", "repo", "project", "node", "repo"]);
  });

  it("opens a repository as its flows and a node's neighbourhood with everything", () => {
    assert.equal(levelOf(readGraphParams(new URLSearchParams("repoId=r"))), "flows");
    assert.equal(levelOf(readGraphParams(new URLSearchParams("focus=k"))), "all");
    assert.equal(levelOf(readGraphParams(new URLSearchParams("focus=k&show=flows"))), "flows");
  });

  it("traces the node a link landed on in a repository, until the reader traces another", () => {
    assert.equal(tracedNode(readGraphParams(new URLSearchParams("repoId=r&focus=k"))), "k");
    assert.equal(tracedNode(readGraphParams(new URLSearchParams("repoId=r&focus=k&trace=other"))), "other");
    assert.equal(tracedNode(readGraphParams(new URLSearchParams("focus=k"))), "");
  });

  it("collapses folders by the scope's size unless the reader chose", () => {
    assert.deepEqual([groupedBy({ grouped: null }, 30), groupedBy({ grouped: null }, 31), groupedBy({ grouped: false }, 500), groupedBy({ grouped: true }, 2)], [false, true, false, true]);
  });

  it("turns a wave range into the waves drawn, and draws every wave for a range that names none of them", () => {
    const stops = [1, 2, 3, 4, -1];
    assert.deepEqual([...wavesInRange({ from: 2, to: 3 }, stops)!], [2, 3]);
    assert.deepEqual([...wavesInRange({ from: 4, to: -1 }, stops)!], [4, -1]);
    assert.equal(wavesInRange({ from: 1, to: -1 }, stops), null);
    assert.equal(wavesInRange({ from: 7, to: 9 }, [1, 2, 3]), null);
    assert.equal(wavesInRange(null, stops), null);
  });
});

describe("clipMiddle", () => {
  it("keeps how a long name begins and how it ends", () => {
    assert.equal(clipMiddle("recall-welllog-03-v140-metadata-publish", 21), "recall-wel…ta-publish");
    assert.equal(clipMiddle("short", 21), "short");
  });
});
