// The login page's picture: records on curved lanes into rings, one per label, never stacked where the lanes meet.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { LANES, RINGS, VANISH, VIEW_HEIGHT, VIEW_WIDTH, pointOnCurve, streamChips } from "../src/auth/loginStreamLanes.ts";

describe("streamChips", () => {
  it("draws nothing without labels, and ignores blank ones", () => {
    assert.deepEqual(streamChips([]), []);
    assert.deepEqual(streamChips(["", "  "]), []);
  });

  it("draws one record per label, each label once, while the lanes have room", () => {
    const labels = ["Wells", "Seismic", "Well logs", "Documents", "Trajectories", "Production", "Wellbores", "Markers"];
    const chips = streamChips(labels);
    assert.deepEqual(chips.map((chip) => chip.label).sort(), [...labels].sort());
  });

  it("puts a record on every lane, repeating labels when there are fewer than lanes", () => {
    const chips = streamChips(["CSV", "JSON"]);
    assert.equal(chips.length, LANES.length);
    assert.deepEqual(new Set(chips.map((chip) => chip.lane)), new Set(LANES.map((_, index) => index)));
    assert.deepEqual(new Set(chips.map((chip) => chip.label)), new Set(["CSV", "JSON"]));
  });

  it("leaves out the labels the lanes have no room for", () => {
    const labels = Array.from({ length: 40 }, (_, index) => `Kind ${index}`);
    const chips = streamChips(labels);
    assert.equal(chips.length, LANES.length * 2);
    assert.deepEqual(chips.map((chip) => chip.label), labels.slice(0, chips.length));
  });

  it("starts every trip in the past, within one trip, so the lanes are full from the first frame", () => {
    for (const chip of streamChips(Array.from({ length: 12 }, (_, index) => `Kind ${index}`))) {
      assert.ok(chip.beginSeconds <= 0, String(chip.beginSeconds));
      assert.ok(-chip.beginSeconds < LANES[chip.lane].seconds, String(chip.beginSeconds));
    }
  });

  it("spaces two records on one lane half a trip apart", () => {
    const chips = streamChips(Array.from({ length: 12 }, (_, index) => `Kind ${index}`));
    for (const [index, lane] of LANES.entries()) {
      const onLane = chips.filter((chip) => chip.lane === index);
      assert.equal(onLane.length, 2);
      const apart = Math.abs(onLane[0].beginSeconds - onLane[1].beginSeconds);
      assert.ok(Math.abs(apart - lane.seconds / 2) < 1e-9, `lane ${index}: ${apart}`);
    }
  });

  it("rests one record on each lane, inside the picture, when motion is reduced", () => {
    const chips = streamChips(Array.from({ length: 12 }, (_, index) => `Kind ${index}`));
    const resting = chips.filter((chip) => chip.rest !== null);
    assert.equal(resting.length, LANES.length);
    for (const chip of resting) {
      assert.ok(chip.rest!.x > 0 && chip.rest!.x < VIEW_WIDTH, JSON.stringify(chip.rest));
      assert.ok(chip.rest!.y > 0 && chip.rest!.y < VIEW_HEIGHT, JSON.stringify(chip.rest));
    }
  });
});

describe("LANES", () => {
  it("runs every lane from beyond the left edge to the outermost ring's stroke, then to where the rings recede", () => {
    const outer = RINGS[0];
    for (const lane of LANES) {
      const [start, , , entry] = lane.curve;
      assert.ok(start.x < 0, String(start.x));
      const onStroke = ((entry.x - outer.cx) / outer.rx) ** 2 + ((entry.y - outer.cy) / outer.ry) ** 2;
      assert.ok(Math.abs(onStroke - 1) < 1e-9, String(onStroke));
      assert.ok(lane.path.endsWith(`L ${VANISH.x} ${VANISH.y}`), lane.path);
    }
  });

  it("folds each label before its lane meets the rings", () => {
    for (const lane of LANES) {
      assert.ok(lane.foldFrom > 0 && lane.foldFrom < lane.mouthAt && lane.mouthAt < 1, JSON.stringify(lane));
    }
  });

  it("keeps the lanes apart where they meet the rings, so no two labels stack there", () => {
    const entries = LANES.map((lane) => lane.curve[3].y).sort((a, b) => a - b);
    for (let index = 1; index < entries.length; index++) {
      assert.ok(entries[index] - entries[index - 1] >= 30, `${entries[index - 1]} and ${entries[index]}`);
    }
  });
});

describe("pointOnCurve", () => {
  it("starts at the first point and ends at the last", () => {
    const curve = LANES[0].curve;
    assert.deepEqual(pointOnCurve(curve, 0), curve[0]);
    const end = pointOnCurve(curve, 1);
    assert.ok(Math.abs(end.x - curve[3].x) < 1e-9 && Math.abs(end.y - curve[3].y) < 1e-9, JSON.stringify(end));
  });
});

describe("RINGS", () => {
  it("recedes: each ring further right, smaller and fainter than the one before", () => {
    for (let index = 1; index < RINGS.length; index++) {
      const [before, ring] = [RINGS[index - 1], RINGS[index]];
      assert.ok(ring.cx > before.cx && ring.rx < before.rx && ring.ry < before.ry && ring.opacity < before.opacity, String(index));
    }
  });
});
