// The report name rule the upload dialog checks, which mirrors the server's (ReportSpecs.ReportFileProblem).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { MAX_REPORT_FILE_LENGTH, reportFileProblem } from "../src/features/semantic-layer/reportFile.ts";

describe("reportFileProblem", () => {
  it("accepts a relative, forward-slashed name of any script", () => {
    for (const name of ["Sales.pbix", "team/Sales Q3.pbix", "Salg og økonomi.pbix", "a".repeat(MAX_REPORT_FILE_LENGTH)]) {
      assert.equal(reportFileProblem(name), null, name);
    }
  });

  it("refuses what cannot key a report's rows, naming why", () => {
    const refused: [string, string][] = [
      ["", "required"],
      ["   ", "required"],
      [" Sales.pbix", "space"],
      ["Sales.pbix\u00a0", "space"],
      ["a".repeat(MAX_REPORT_FILE_LENGTH + 1), "longer"],
      ["Sales#2.pbix", "'#'"],
      ["team\\Sales.pbix", "backslash"],
      ["C:/Sales.pbix", "':'"],
      ["Sales.pbix:stream", "':'"],
      ["Sales\t.pbix", "control"],
      ["Sales\u007f.pbix", "control"],
      ["Sales\u0085.pbix", "control"],
      ["/Sales.pbix", "relative"],
      ["../Sales.pbix", "relative"],
      ["./Sales.pbix", "relative"],
      ["team//Sales.pbix", "relative"],
      ["Sales.pbix/", "relative"],
    ];
    for (const [name, why] of refused) {
      const problem = reportFileProblem(name);
      assert.ok(problem !== null && problem.includes(why), `${JSON.stringify(name)}: ${problem}`);
    }
  });
});
