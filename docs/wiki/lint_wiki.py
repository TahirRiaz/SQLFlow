"""Health-check docs/wiki: frontmatter, cross-references, the index, the log, and page staleness.

This is the mechanical half of the wiki's lint operation. It cannot judge whether two pages
contradict each other or whether a claim has gone stale; that judgement is the agent's, and the
workflow for it lives in the "Internals Wiki" section of CLAUDE.md. What this script does is catch
everything decidable from the files themselves, so the agent's lint pass spends its attention on
the part that needs reading.

The staleness tripwire is `sourceRefs`. A wiki page names the source files its claims rest on; when
one of those files is renamed or deleted, the page's claims are suspect and this script fails.

Usage: python docs/wiki/lint_wiki.py
Exit code 0 when clean, 1 when any error is reported. Warnings alone do not fail the run.

A product that extends SQLFlow and keeps a wiki of its own in the same format lints it with the same
script, naming its wiki, the folder its sourceRefs and rawRefs are relative to, the manifest its pages
are indexed in, and the prefix its ids carry in place of `wiki-`. Its pages may name SQLFlow's pages as
well, so the manifests whose ids are known too are given with --known: a known manifest's reference
pages may be named in `referenceRefs` and its wiki pages in `related`, and no page of the wiki may take
an id a known manifest already holds:

  python docs/wiki/lint_wiki.py --wiki <dir> --root <dir> --manifest <manifest.json>
                                [--id-prefix <prefix>] [--known <manifest.json>]...
"""

import argparse
import datetime
import glob
import json
import os
import re
import sys

import yaml

SQLFLOW_WIKI_ROOT = os.path.dirname(os.path.abspath(__file__))
SQLFLOW_REPO_ROOT = os.path.abspath(os.path.join(SQLFLOW_WIKI_ROOT, "..", ".."))
SQLFLOW_REFERENCE_MANIFEST = os.path.join(SQLFLOW_REPO_ROOT, "docs", "reference", "manifest.json")

# The wiki linted, the folder its sourceRefs and rawRefs resolve from, the manifest its referenceRefs are
# checked against, and the prefix its ids carry. SQLFlow's own unless the command line names others (configure).
WIKI_ROOT = SQLFLOW_WIKI_ROOT
REPO_ROOT = SQLFLOW_REPO_ROOT
REFERENCE_MANIFEST = SQLFLOW_REFERENCE_MANIFEST
ID_PREFIX = "wiki-"
# The manifests given with --known, and the ids they hold: every id, the ids of their reference pages (which
# referenceRefs may name), and the ids of their wiki pages (which related may name).
KNOWN_MANIFESTS = []
KNOWN_IDS = {}
KNOWN_REFERENCE_IDS = set()
KNOWN_WIKI_IDS = set()

PAGE_TYPES = {"narrative", "decision", "incident", "map", "pattern"}
TYPE_DIRECTORIES = {
    "narrative": "narratives",
    "decision": "decisions",
    "incident": "incidents",
    "map": "maps",
    "pattern": "patterns",
}
REQUIRED_KEYS = ("id", "title", "type", "summary", "keywords", "updated")
LIST_KEYS = ("keywords", "sourceRefs", "rawRefs", "referenceRefs", "related")
SPECIAL_PAGES = {"index.md", "log.md"}

FM_RE = re.compile(r"^---\s*\n(.*?\n)---\s*\n", re.DOTALL)
LINK_RE = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
LOG_ENTRY_RE = re.compile(r"^## \[(\d{4}-\d{2}-\d{2})\] (\w+) \| (.+)$")
LOG_OPS = {"ingest", "query", "lint"}
FENCE_RE = re.compile(r"^\s*```")
EXTERNAL_PREFIXES = ("http://", "https://", "mailto:", "#")


class Report:
    """Collects errors and warnings, each keyed by the file it belongs to."""

    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, where, message):
        self.errors.append((where, message))

    def warn(self, where, message):
        self.warnings.append((where, message))

    def emit(self):
        for where, message in self.errors:
            print("ERROR  {0}: {1}".format(where, message))
        for where, message in self.warnings:
            print("WARN   {0}: {1}".format(where, message))
        return 1 if self.errors else 0


def rel(path):
    return os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")


def strip_code_fences(text):
    """Blank out fenced code blocks so example links inside them are not link-checked."""
    out = []
    in_fence = False
    for line in text.splitlines():
        if FENCE_RE.match(line):
            in_fence = not in_fence
            out.append("")
            continue
        out.append("" if in_fence else line)
    return "\n".join(out)


def link_targets(text):
    """Every local (non-external) link target in the text, with any anchor stripped."""
    for target in LINK_RE.findall(strip_code_fences(text)):
        if target.startswith(EXTERNAL_PREFIXES):
            continue
        clean = target.split("#", 1)[0]
        if clean:
            yield target, clean


def find_pages():
    pages = []
    for path in glob.glob(os.path.join(WIKI_ROOT, "**", "*.md"), recursive=True):
        is_special = os.path.basename(path) in SPECIAL_PAGES
        if is_special and os.path.dirname(os.path.abspath(path)) == WIKI_ROOT:
            continue
        pages.append(os.path.abspath(path))
    return sorted(pages, key=rel)


def parse_frontmatter(path, report):
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()
    match = FM_RE.match(text)
    if not match:
        report.error(rel(path), "no YAML frontmatter block at the top of the file")
        return None, text
    try:
        data = yaml.safe_load(match.group(1)) or {}
    except yaml.YAMLError as exc:
        report.error(rel(path), "frontmatter is not valid YAML: {0}".format(exc))
        return None, text
    if not isinstance(data, dict):
        report.error(rel(path), "frontmatter must be a mapping")
        return None, text
    return data, text


def load_reference_ids(report):
    if not os.path.exists(REFERENCE_MANIFEST):
        report.warn(
            rel(REFERENCE_MANIFEST),
            "reference manifest not found, so referenceRefs cannot be validated",
        )
        return None
    with open(REFERENCE_MANIFEST, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    ids = set(doc["id"] for doc in manifest.get("docs", []) if "id" in doc)
    return ids | KNOWN_REFERENCE_IDS


def reference_sources():
    """The manifests a referenceRefs id may come from, as a message names them."""
    names = rel(REFERENCE_MANIFEST)
    if KNOWN_MANIFESTS:
        names += " or the reference pages of " + ", ".join(rel(path) for path in KNOWN_MANIFESTS)
    return names


def check_frontmatter(path, data, report):
    where = rel(path)
    for key in REQUIRED_KEYS:
        if key not in data or data[key] in (None, "", []):
            report.error(where, "frontmatter is missing required key '{0}'".format(key))

    page_type = data.get("type")
    if page_type is not None and page_type not in PAGE_TYPES:
        report.error(
            where,
            "type '{0}' is not one of {1}".format(page_type, ", ".join(sorted(PAGE_TYPES))),
        )

    stem = os.path.splitext(os.path.basename(path))[0]
    expected_id = "{0}{1}".format(ID_PREFIX, stem)
    if data.get("id") is not None and data.get("id") != expected_id:
        report.error(
            where,
            "id '{0}' should be '{1}' to match the filename".format(data["id"], expected_id),
        )

    # YAML reads unquoted null, ~, true, false, yes, no and bare numbers as non-strings, and the MCP server
    # refuses to start on a manifest carrying one. sourceRefs and rawRefs entries are checked in check_refs_exist.
    for key in ("id", "title", "type", "summary"):
        if data.get(key) is not None and not isinstance(data[key], str):
            report.error(where, "frontmatter key '{0}' must be a string, got {1!r}".format(key, data[key]))

    for key in LIST_KEYS:
        if key not in data:
            continue
        if not isinstance(data[key], list):
            report.error(where, "frontmatter key '{0}' must be a list".format(key))
            continue
        if key in ("sourceRefs", "rawRefs"):
            continue
        for item in data[key]:
            if not isinstance(item, str):
                report.error(
                    where,
                    "frontmatter key '{0}' entry {1!r} is not a string; quote it".format(key, item),
                )

    updated = data.get("updated")
    if updated is not None and not isinstance(updated, datetime.date):
        try:
            datetime.date.fromisoformat(str(updated))
        except ValueError:
            report.error(
                where, "updated '{0}' is not an ISO date (YYYY-MM-DD)".format(updated)
            )

    directory = os.path.basename(os.path.dirname(path))
    expected_dir = TYPE_DIRECTORIES.get(page_type)
    if expected_dir and directory != expected_dir:
        report.error(
            where,
            "a '{0}' page belongs in {1}/, not {2}/".format(page_type, expected_dir, directory),
        )


def check_refs_exist(path, data, report):
    where = rel(path)
    for key in ("sourceRefs", "rawRefs"):
        value = data.get(key)
        if not isinstance(value, list):
            continue
        for ref in value:
            if not isinstance(ref, str):
                report.error(where, "{0} entry {1!r} is not a string".format(key, ref))
                continue
            target = os.path.join(REPO_ROOT, ref.replace("/", os.sep))
            if os.path.exists(target):
                continue
            if key == "sourceRefs":
                report.error(
                    where,
                    "sourceRefs points at '{0}', which no longer exists; this page's claims "
                    "rest on a file that moved or was deleted and must be re-verified".format(ref),
                )
            else:
                report.error(
                    where, "rawRefs points at '{0}', which does not exist".format(ref)
                )


def check_links(path, text, report):
    where = rel(path)
    base = os.path.dirname(path)
    for target, clean in link_targets(text):
        resolved = os.path.normpath(os.path.join(base, clean.replace("/", os.sep)))
        if not os.path.exists(resolved):
            report.error(where, "link target '{0}' does not resolve".format(target))


def check_index(pages, report):
    index_path = os.path.join(WIKI_ROOT, "index.md")
    if not os.path.exists(index_path):
        report.error(rel(index_path), "the wiki has no index")
        return
    with open(index_path, "r", encoding="utf-8") as handle:
        text = handle.read()
    check_links(index_path, text, report)

    linked = set()
    for _, clean in link_targets(text):
        resolved = os.path.normpath(os.path.join(WIKI_ROOT, clean.replace("/", os.sep)))
        if resolved.startswith(WIKI_ROOT) and resolved.endswith(".md"):
            linked.add(resolved)

    for page in pages:
        if page not in linked:
            report.error(
                rel(page),
                "page is not listed in {0}; the index is the only navigational "
                "surface, so an unlisted page is invisible to a query".format(rel(index_path)),
            )


def check_cross_refs(entries, reference_ids, report):
    known = set(data["id"] for _, data, _ in entries if data.get("id"))
    inbound = dict((page_id, 0) for page_id in known)

    for path, data, _ in entries:
        where = rel(path)
        related = data.get("related")
        if isinstance(related, list):
            for ref in related:
                if ref == data.get("id"):
                    report.error(where, "related lists the page's own id")
                elif ref in known:
                    inbound[ref] += 1
                elif ref not in KNOWN_WIKI_IDS:
                    report.error(
                        where, "related '{0}' is not the id of any wiki page".format(ref)
                    )
        if reference_ids is None:
            continue
        refs = data.get("referenceRefs")
        if isinstance(refs, list):
            for ref in refs:
                if ref not in reference_ids:
                    report.error(
                        where,
                        "referenceRefs '{0}' is not an id in {1}".format(ref, reference_sources()),
                    )

    for path, data, _ in entries:
        page_id = data.get("id")
        if page_id and inbound.get(page_id) == 0:
            report.warn(
                rel(path),
                "orphan page: no other wiki page lists it in 'related'. Either cross-reference "
                "it from a related page, or confirm it genuinely stands alone",
            )


def check_log(report):
    log_path = os.path.join(WIKI_ROOT, "log.md")
    if not os.path.exists(log_path):
        report.error(rel(log_path), "the wiki has no log")
        return
    with open(log_path, "r", encoding="utf-8") as handle:
        text = handle.read()
    check_links(log_path, text, report)

    where = rel(log_path)
    previous = None
    count = 0
    for line in strip_code_fences(text).splitlines():
        if not line.startswith("## ["):
            continue
        count += 1
        match = LOG_ENTRY_RE.match(line)
        if not match:
            report.error(
                where,
                "entry does not match '## [YYYY-MM-DD] <op> | <title>': {0}".format(line.strip()),
            )
            continue
        stamp, op, _ = match.groups()
        try:
            when = datetime.date.fromisoformat(stamp)
        except ValueError:
            report.error(where, "entry has an invalid date: {0}".format(line.strip()))
            continue
        if op not in LOG_OPS:
            report.error(
                where,
                "entry op '{0}' is not one of {1}".format(op, ", ".join(sorted(LOG_OPS))),
            )
        if previous is not None and when < previous:
            report.error(
                where,
                "entry dated {0} appears after {1}; the log is append-only and must stay "
                "chronological".format(stamp, previous.isoformat()),
            )
        previous = when

    if count == 0:
        report.warn(where, "the log has no entries")


def read_known_manifest(parser, path):
    """The ids a --known manifest holds, as (every id, reference page ids, wiki page ids)."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            other = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        parser.error("--known {0} could not be read as a manifest: {1}".format(path, exc))
    docs = other.get("docs") if isinstance(other, dict) else None
    if not isinstance(docs, list):
        parser.error("--known {0} has no 'docs' list".format(path))
    every, reference, wiki = set(), set(), set()
    for doc in docs:
        if not isinstance(doc, dict) or not isinstance(doc.get("id"), str):
            continue
        every.add(doc["id"])
        # An entry with no corpus field is a reference page, as build_manifest.py writes it.
        (wiki if doc.get("corpus") == "wiki" else reference).add(doc["id"])
    return every, reference, wiki


def configure(argv):
    """Points the run at the wiki, root, manifest and id prefix the command line names; SQLFlow's own without arguments."""
    global WIKI_ROOT, REPO_ROOT, REFERENCE_MANIFEST, ID_PREFIX
    global KNOWN_MANIFESTS, KNOWN_IDS, KNOWN_REFERENCE_IDS, KNOWN_WIKI_IDS
    parser = argparse.ArgumentParser(description="Health-check a documentation wiki.")
    parser.add_argument("--wiki", help="the wiki tree (default: this script's folder)")
    parser.add_argument("--root", help="the folder sourceRefs and rawRefs are relative to (default: SQLFlow's "
                                       "repository root; required when --wiki names another tree)")
    parser.add_argument("--manifest", help="the manifest the wiki is indexed in, whose ids referenceRefs may name "
                                           "(default: SQLFlow's docs/reference/manifest.json; required when --wiki "
                                           "names another tree)")
    parser.add_argument("--id-prefix", help="what every page id carries before its filename stem (default: wiki-)")
    parser.add_argument("--known", action="append", default=[],
                        help="another manifest whose reference pages referenceRefs may name and whose wiki pages "
                             "related may name; repeatable")
    args = parser.parse_args(argv)

    wiki = os.path.abspath(args.wiki) if args.wiki else SQLFLOW_WIKI_ROOT
    if not os.path.isdir(wiki):
        parser.error("--wiki {0} is not a folder".format(wiki))
    if wiki != SQLFLOW_WIKI_ROOT:
        if not args.root:
            parser.error("--root is required when --wiki names a tree other than SQLFlow's")
        if not args.manifest:
            parser.error("--manifest is required when --wiki names a tree other than SQLFlow's")
    root = os.path.abspath(args.root) if args.root else SQLFLOW_REPO_ROOT
    if not os.path.isdir(root):
        parser.error("--root {0} is not a folder".format(root))
    id_prefix = args.id_prefix if args.id_prefix is not None else "wiki-"
    if not id_prefix.strip():
        parser.error("--id-prefix must not be empty")

    known_manifests, known_ids, known_reference, known_wiki = [], {}, set(), set()
    for path in args.known:
        every, reference, wiki_ids = read_known_manifest(parser, path)
        absolute = os.path.abspath(path)
        known_manifests.append(absolute)
        for page_id in every:
            known_ids.setdefault(page_id, absolute)
        known_reference |= reference
        known_wiki |= wiki_ids

    WIKI_ROOT = wiki
    REPO_ROOT = root
    REFERENCE_MANIFEST = os.path.abspath(args.manifest) if args.manifest else SQLFLOW_REFERENCE_MANIFEST
    ID_PREFIX = id_prefix
    KNOWN_MANIFESTS = known_manifests
    KNOWN_IDS = known_ids
    KNOWN_REFERENCE_IDS = known_reference
    KNOWN_WIKI_IDS = known_wiki


def main(argv=None):
    configure(sys.argv[1:] if argv is None else argv)
    report = Report()
    pages = find_pages()
    reference_ids = load_reference_ids(report)

    entries = []
    for path in pages:
        data, text = parse_frontmatter(path, report)
        if data is None:
            continue
        check_frontmatter(path, data, report)
        check_refs_exist(path, data, report)
        check_links(path, text, report)
        entries.append((path, data, text))

    seen = {}
    for path, data, _ in entries:
        page_id = data.get("id")
        if not page_id:
            continue
        if page_id in seen:
            report.error(
                rel(path), "duplicate id '{0}', also used by {1}".format(page_id, seen[page_id])
            )
        else:
            seen[page_id] = rel(path)
        if page_id in KNOWN_IDS:
            report.error(
                rel(path),
                "id '{0}' is also an id in {1}; one MCP index holds both, so the id must be "
                "unique across them".format(page_id, rel(KNOWN_IDS[page_id])),
            )

    check_index(pages, report)
    check_cross_refs(entries, reference_ids, report)
    check_log(report)

    status = report.emit()
    print(
        "\n{0} page(s), {1} error(s), {2} warning(s).".format(
            len(pages), len(report.errors), len(report.warnings)
        )
    )
    if status == 0:
        print("Mechanical checks pass. The judgement half of lint is still the agent's job:")
        print("  contradictions between pages, stale claims, and concepts that lack a page.")
    return status


if __name__ == "__main__":
    sys.exit(main())
