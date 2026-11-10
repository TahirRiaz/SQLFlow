/**
 * A left-to-right layered (Sugiyama) layout, which is what makes data flow readable: sources on the left, consumers
 * to the right, one column per level. A column of cards is far denser than a row of them (a card is four times as
 * wide as it is tall), so a level holding thirty nodes stays on one screen instead of running seven screens wide.
 *
 * Levels come from a longest-path layering over the edges, with cycles broken first (a depth-first walk drops the
 * edges that close a cycle from the level calculation, so a flow that reads a view derived from its own output
 * still gets a real level instead of collapsing onto the first column). Within each column, several barycenter
 * passes order nodes by the average position of their neighbours to cut edge crossings. Independent of any layout
 * library, so the level alignment is exact.
 */

export const NODE_WIDTH = 240;
export const NODE_HEIGHT = 54;
/** Horizontal distance between columns: a card plus the room an edge needs to bend. */
export const COLUMN_SPACING = NODE_WIDTH + 110;
/** Vertical distance between the cards of a column. */
export const ROW_SPACING = NODE_HEIGHT + 20;

export interface LayoutEdge {
  source: string;
  target: string;
}

export interface Position {
  x: number;
  y: number;
}

export function layeredLayout(ids: readonly string[], edges: readonly LayoutEdge[]): Map<string, Position> {
  const position = new Map<string, Position>();
  if (ids.length === 0) {
    return position;
  }

  const known = new Set(ids);
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  const levelInDegree = new Map<string, number>();
  for (const id of known) {
    outgoing.set(id, []);
    incoming.set(id, []);
    levelInDegree.set(id, 0);
  }
  const seenEdges = new Set<string>();
  for (const edge of edges) {
    const key = `${edge.source}\u0000${edge.target}`;
    if (known.has(edge.source) && known.has(edge.target) && edge.source !== edge.target && !seenEdges.has(key)) {
      seenEdges.add(key);
      outgoing.get(edge.source)!.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
      levelInDegree.set(edge.target, levelInDegree.get(edge.target)! + 1);
    }
  }

  // ---- Level assignment: longest path, with cycles broken first. ----------------------------------------------
  // A cyclic subgraph would leave Kahn's queue undrained and pin every node of the cycle to the first column. A
  // depth-first walk therefore marks each edge that closes back onto its own stack; those edges are left out of
  // the level calculation (they still draw, running backwards), so the rest is acyclic and every node gets a real
  // level. Nodes with the fewest inbound edges are visited first so cycles break in the natural flow direction.
  const backTargets = new Map<string, Set<string>>();
  const state = new Map<string, number>(); // 1 = on the walk's stack, 2 = finished
  const roots = [...known].sort((a, b) => levelInDegree.get(a)! - levelInDegree.get(b)!);
  for (const root of roots) {
    if (state.has(root)) {
      continue;
    }
    const stack: Array<{ id: string; next: number }> = [{ id: root, next: 0 }];
    state.set(root, 1);
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const children = outgoing.get(frame.id)!;
      if (frame.next < children.length) {
        const child = children[frame.next];
        frame.next += 1;
        const childState = state.get(child);
        if (childState === 1) {
          (backTargets.get(frame.id) ?? backTargets.set(frame.id, new Set()).get(frame.id)!).add(child);
          levelInDegree.set(child, levelInDegree.get(child)! - 1);
        } else if (childState === undefined) {
          state.set(child, 1);
          stack.push({ id: child, next: 0 });
        }
      } else {
        state.set(frame.id, 2);
        stack.pop();
      }
    }
  }

  // Longest-path layering over the acyclic remainder: a node sits one column after its deepest producer.
  const level = new Map<string, number>();
  const queue: string[] = [];
  for (const id of known) {
    if (levelInDegree.get(id) === 0) {
      level.set(id, 0);
      queue.push(id);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const id = queue[head];
    const current = level.get(id)!;
    for (const child of outgoing.get(id)!) {
      if (backTargets.get(id)?.has(child)) {
        continue;
      }
      level.set(child, Math.max(level.get(child) ?? 0, current + 1));
      const remaining = levelInDegree.get(child)! - 1;
      levelInDegree.set(child, remaining);
      if (remaining === 0) {
        queue.push(child);
      }
    }
  }

  // As-late-as-possible relaxation: slide every node to one column before its EARLIEST consumer. Longest-path
  // places each node as early as it can, which strands a slack chain (a hand-kept input, its loader, its table) at
  // the far left even when its only consumers sit columns away. Walking the topological order in reverse and only
  // ever moving a node later keeps every producer before its consumers; a sink or a lone node never moves.
  for (let i = queue.length - 1; i >= 0; i--) {
    const id = queue[i];
    const back = backTargets.get(id);
    let earliest = Number.POSITIVE_INFINITY;
    for (const child of outgoing.get(id)!) {
      if (!back?.has(child)) {
        earliest = Math.min(earliest, level.get(child)!);
      }
    }
    if (Number.isFinite(earliest) && earliest - 1 > level.get(id)!) {
      level.set(id, earliest - 1);
    }
  }

  const columns = new Map<number, string[]>();
  for (const id of known) {
    const column = level.get(id) ?? 0;
    (columns.get(column) ?? columns.set(column, []).get(column)!).push(id);
  }
  const levels = [...columns.keys()].sort((a, b) => a - b);

  // ---- Crossing reduction: order each column by the barycenter of its neighbours, both directions. --------------
  const order = new Map<string, number>();
  for (const column of levels) {
    const group = columns.get(column)!;
    group.sort((a, b) =>
      outgoing.get(b)!.length + incoming.get(b)!.length - (outgoing.get(a)!.length + incoming.get(a)!.length)
      || a.localeCompare(b));
    group.forEach((id, i) => order.set(id, i));
  }

  const barycenter = (id: string, neighbours: Map<string, string[]>, neighbourLevel: number): number => {
    const relevant = neighbours.get(id)!.filter((n) => level.get(n) === neighbourLevel);
    if (relevant.length === 0) {
      return order.get(id) ?? 0;
    }
    return relevant.reduce((sum, n) => sum + (order.get(n) ?? 0), 0) / relevant.length;
  };

  for (let pass = 0; pass < 6; pass++) {
    for (let i = 1; i < levels.length; i++) {
      const group = columns.get(levels[i])!;
      const weight = new Map(group.map((id) => [id, barycenter(id, incoming, levels[i - 1])]));
      group.sort((a, b) => weight.get(a)! - weight.get(b)!);
      group.forEach((id, j) => order.set(id, j));
    }
    for (let i = levels.length - 2; i >= 0; i--) {
      const group = columns.get(levels[i])!;
      const weight = new Map(group.map((id) => [id, barycenter(id, outgoing, levels[i + 1])]));
      group.sort((a, b) => weight.get(a)! - weight.get(b)!);
      group.forEach((id, j) => order.set(id, j));
    }
  }

  // ---- Positions: x by column, y by order within the column, each column centered on the same axis. -------------
  levels.forEach((column, index) => {
    const group = columns.get(column)!;
    const height = (group.length - 1) * ROW_SPACING;
    group.forEach((id, i) => {
      position.set(id, { x: index * COLUMN_SPACING, y: i * ROW_SPACING - height / 2 });
    });
  });

  return position;
}
