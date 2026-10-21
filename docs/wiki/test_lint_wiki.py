"""Tests of lint_wiki.py's options for a product's own wiki: its root, its manifest, its id prefix and the
manifests it may name with --known. SQLFlow's own run, without arguments, is the script's ordinary use.

Usage: python -m unittest discover -s docs/wiki -p "test_*.py"
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

import lint_wiki  # noqa: E402  (imported from this folder, which the line above puts on the path)


def page(page_id, page_type, related=None, reference_refs=None, source_refs=None):
    """A wiki page's text: frontmatter with the keys the lint requires, and a one-line body."""
    lines = [
        "---",
        "id: {0}".format(page_id),
        'title: "A page"',
        "type: {0}".format(page_type),
        'summary: "What the page answers."',
        "keywords:",
        "  - one",
        "  - two",
        "  - three",
    ]
    for key, values in (("sourceRefs", source_refs), ("referenceRefs", reference_refs), ("related", related)):
        if values:
            lines.append("{0}:".format(key))
            lines.extend("  - {0}".format(value) for value in values)
    lines += ["updated: 2026-10-21", "---", "", "# A page", ""]
    return "\n".join(lines)


class ProductWikiTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="lint-wiki-")
        self.addCleanup(shutil.rmtree, self.root)
        self.wiki = os.path.join(self.root, "product", "docs", "wiki")
        os.makedirs(os.path.join(self.wiki, "decisions"))
        os.makedirs(os.path.join(self.wiki, "patterns"))
        os.makedirs(os.path.join(self.root, "product", "src"))
        self.write("product/src/engine.cs", "// the code the pages rest on\n")
        self.manifest = self.write_manifest("product/docs/reference/manifest.json", [
            {"id": "product-concept-ledger", "corpus": "reference"},
            {"id": "product-wiki-grain", "corpus": "wiki"},
            {"id": "product-wiki-keys", "corpus": "wiki"},
        ])
        self.known = self.write_manifest("platform/docs/reference/manifest.json", [
            {"id": "concept-lineage"},
            {"id": "wiki-pattern-catalog", "corpus": "wiki"},
        ])
        self.write("product/docs/wiki/index.md",
                   "# Index\n\n- [Grain](decisions/grain.md)\n- [Keys](patterns/keys.md)\n")
        self.write("product/docs/wiki/log.md", "# Log\n\n## [2026-10-21] ingest | Instantiated\n")
        self.write_pages(grain_id="product-wiki-grain")

    def write(self, relative, text):
        path = os.path.join(self.root, relative.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        return path

    def write_manifest(self, relative, docs):
        return self.write(relative, json.dumps({"docs": docs}))

    def write_pages(self, grain_id, grain_related=None, grain_reference_refs=None, grain_sources=None):
        self.write("product/docs/wiki/decisions/grain.md", page(
            grain_id, "decision",
            related=grain_related if grain_related is not None else ["product-wiki-keys", "wiki-pattern-catalog"],
            reference_refs=grain_reference_refs if grain_reference_refs is not None
            else ["product-concept-ledger", "concept-lineage"],
            source_refs=grain_sources if grain_sources is not None else ["product/src/engine.cs"]))
        self.write("product/docs/wiki/patterns/keys.md", page(
            "product-wiki-keys", "pattern", related=["product-wiki-grain"]))

    def lint(self, *extra):
        argv = ["--wiki", self.wiki, "--root", self.root, "--manifest", self.manifest,
                "--id-prefix", "product-wiki-", "--known", self.known] + list(extra)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            status = lint_wiki.main(argv)
        return status, out.getvalue()

    def test_a_product_wiki_that_names_its_own_and_the_platforms_pages_is_clean(self):
        status, out = self.lint()
        self.assertEqual(0, status, out)
        self.assertIn("2 page(s), 0 error(s), 0 warning(s).", out)

    def test_a_page_id_carries_the_products_prefix(self):
        self.write_pages(grain_id="wiki-grain", grain_related=["product-wiki-keys"])
        status, out = self.lint()
        self.assertEqual(1, status, out)
        self.assertIn("id 'wiki-grain' should be 'product-wiki-grain' to match the filename", out)

    def test_source_refs_resolve_from_the_root_the_command_line_names(self):
        self.write_pages(grain_id="product-wiki-grain", grain_sources=["product/src/engine.cs", "src/engine.cs"])
        status, out = self.lint()
        self.assertEqual(1, status, out)
        self.assertIn("sourceRefs points at 'src/engine.cs', which no longer exists", out)
        self.assertNotIn("'product/src/engine.cs'", out)

    def test_a_known_manifest_lends_its_reference_pages_to_reference_refs_and_its_wiki_pages_to_related(self):
        self.write_pages(grain_id="product-wiki-grain",
                         grain_related=["product-wiki-keys", "concept-lineage"],
                         grain_reference_refs=["wiki-pattern-catalog"])
        status, out = self.lint()
        self.assertEqual(1, status, out)
        self.assertIn("related 'concept-lineage' is not the id of any wiki page", out)
        self.assertIn("referenceRefs 'wiki-pattern-catalog' is not an id in product/docs/reference/manifest.json "
                      "or the reference pages of platform/docs/reference/manifest.json", out)

    def test_an_id_a_known_manifest_holds_is_refused(self):
        os.makedirs(os.path.join(self.wiki, "maps"))
        self.write("product/docs/wiki/maps/pattern-catalog.md",
                   page("wiki-pattern-catalog", "map", related=["product-wiki-keys"]))
        self.write("product/docs/wiki/index.md", "# Index\n\n- [Grain](decisions/grain.md)\n"
                   "- [Keys](patterns/keys.md)\n- [Catalog](maps/pattern-catalog.md)\n")
        status, out = self.lint("--id-prefix", "wiki-")
        self.assertEqual(1, status, out)
        self.assertIn("id 'wiki-pattern-catalog' is also an id in platform/docs/reference/manifest.json", out)

    def test_a_product_wiki_needs_its_root_and_its_manifest(self):
        for argv in (["--wiki", self.wiki, "--manifest", self.manifest], ["--wiki", self.wiki, "--root", self.root]):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    lint_wiki.main(argv)
                self.assertEqual(2, raised.exception.code)

    def test_a_known_manifest_that_is_not_a_manifest_is_refused(self):
        broken = self.write("platform/broken.json", "[]")
        with contextlib.redirect_stderr(io.StringIO()) as err, self.assertRaises(SystemExit) as raised:
            lint_wiki.main(["--wiki", self.wiki, "--root", self.root, "--manifest", self.manifest, "--known", broken])
        self.assertEqual(2, raised.exception.code)
        self.assertIn("has no 'docs' list", err.getvalue())


class SqlFlowWikiTests(unittest.TestCase):
    def test_without_arguments_the_run_lints_sqlflows_own_wiki_with_its_own_prefix(self):
        lint_wiki.configure([])
        self.assertEqual(lint_wiki.SQLFLOW_WIKI_ROOT, lint_wiki.WIKI_ROOT)
        self.assertEqual(lint_wiki.SQLFLOW_REPO_ROOT, lint_wiki.REPO_ROOT)
        self.assertEqual(lint_wiki.SQLFLOW_REFERENCE_MANIFEST, lint_wiki.REFERENCE_MANIFEST)
        self.assertEqual("wiki-", lint_wiki.ID_PREFIX)
        self.assertEqual({}, lint_wiki.KNOWN_IDS)


if __name__ == "__main__":
    unittest.main()
