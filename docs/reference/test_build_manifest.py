"""Tests of build_manifest.py's page types: a reference corpus may hold `table` pages, one per table a
database holds, and the wiki corpus may not.

Usage: python -m unittest discover -s docs/reference -p "test_*.py"
"""

import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build_manifest  # noqa: E402  (imported from this folder, which the line above puts on the path)


def page(page_id, page_type):
    return "\n".join([
        "---",
        "id: {0}".format(page_id),
        'title: "A page"',
        "type: {0}".format(page_type),
        'summary: "What the page answers."',
        "keywords:",
        "  - one",
        "  - two",
        "---",
        "",
        "# A page",
        "",
    ])


class TableTypeTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="build-manifest-")
        self.addCleanup(shutil.rmtree, self.root)
        self.reference = os.path.join(self.root, "reference")
        self.wiki = os.path.join(self.root, "wiki")
        self.out = os.path.join(self.root, "manifest.json")
        os.makedirs(os.path.join(self.reference, "tables"))
        os.makedirs(os.path.join(self.reference, "concepts"))
        os.makedirs(os.path.join(self.wiki, "maps"))

    def write(self, relative, text):
        path = os.path.join(self.root, relative.replace("/", os.sep))
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)

    def build(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            status = build_manifest.main(["--reference", self.reference, "--wiki", self.wiki,
                                          "--product", "A product", "--out", self.out])
        return status, out.getvalue()

    def test_a_reference_corpus_indexes_a_table_page_as_a_table(self):
        self.write("reference/tables/ledger.md", page("product-table-ledger", "table"))
        self.write("reference/concepts/ledger.md", page("product-concept-ledger", "concept"))
        status, out = self.build()
        self.assertIsNone(status, out)
        self.assertIn("issues_count=0", out)
        with open(self.out, encoding="utf-8") as handle:
            docs = dict((d["id"], d) for d in json.load(handle)["docs"])
        self.assertEqual("table", docs["product-table-ledger"]["type"])
        self.assertEqual("reference", docs["product-table-ledger"]["corpus"])

    def test_a_table_page_in_the_wiki_is_reported(self):
        self.write("wiki/maps/ledger.md", page("product-wiki-ledger", "table"))
        _, out = self.build()
        self.assertIn("'table' is a reference type but the page lives in the wiki corpus", out)

    def test_a_type_that_is_not_one_of_the_corpus_types_is_reported(self):
        self.write("reference/tables/ledger.md", page("product-table-ledger", "tables"))
        _, out = self.build()
        self.assertIn("missing or invalid 'type' ('tables')", out)


if __name__ == "__main__":
    unittest.main()
