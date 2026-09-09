"""Regenerate docs/reference/manifest.json from the frontmatter of every indexed page.

Two corpora feed one manifest, because one manifest is what the MCP server embeds and searches:

  reference  docs/reference/**   WHAT the surface does, verified against code (cli-command,
                                 flow-reference, source-type, concept, guide).
  wiki       docs/wiki/**        WHY it is that way and HOW the pieces fit, plus the production
                                 pattern catalog (narrative, decision, incident, map, pattern).

Each entry carries a "corpus" field naming which root its "path" is relative to; tools/sqlflow-mcp
resolves the two roots when it embeds the bodies. Consumers that predate the field can ignore it and
still resolve reference pages, which are emitted with corpus "reference".

Run after adding, removing, or renaming a page in either tree, or after editing any page's
frontmatter (id, title, type, summary, keywords, yamlPath, cliCommand, related, sourceRefs).
Usage: python docs/reference/build_manifest.py
"""

import glob
import json
import os
import re
import sys

import yaml

DOCS_ROOT = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(DOCS_ROOT, "..", ".."))
WIKI_ROOT = os.path.join(REPO_ROOT, "docs", "wiki")

# corpus name -> root directory holding that corpus's pages.
CORPORA = (("reference", DOCS_ROOT), ("wiki", WIKI_ROOT))

REFERENCE_TYPES = {"cli-command", "flow-reference", "source-type", "concept", "guide"}
WIKI_TYPES = {"narrative", "decision", "incident", "map", "pattern"}
DOC_TYPES = REFERENCE_TYPES | WIKI_TYPES

# Files in an indexed tree that are deliberately not manifest pages: navigational or log surfaces
# that carry no frontmatter. Skipped silently rather than reported as missing frontmatter.
NOT_PAGES = {"README.md", "index.md", "log.md"}

FM_RE = re.compile(r"^---\s*\n(.*?\n)---\s*\n", re.DOTALL)


def find_pages():
    """Every candidate page across both corpora, as (abspath, rel, corpus), stable-sorted."""
    found = []
    for corpus, root in CORPORA:
        if not os.path.isdir(root):
            continue
        for p in glob.glob(os.path.join(root, "**", "*.md"), recursive=True):
            rel = os.path.relpath(p, root).replace(os.sep, "/")
            if os.path.basename(rel) in NOT_PAGES:
                continue
            found.append((p, rel, corpus))
    return sorted(found, key=lambda t: (t[2], t[1]))


def parse_frontmatter(path):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    m = FM_RE.match(text)
    if not m:
        return None, text
    try:
        fm = yaml.safe_load(m.group(1)) or {}
    except yaml.YAMLError as e:
        return {"__parse_error__": str(e)}, text
    return fm, text


def derive_summary(body_text):
    after_fm = FM_RE.sub("", body_text, count=1)
    lines = [l.strip() for l in after_fm.splitlines()]
    for l in lines:
        if not l or l.startswith("#"):
            continue
        return (l[:157] + "...") if len(l) > 160 else l
    return ""


def main():
    pages = find_pages()
    docs = []
    issues = []
    seen_ids = {}

    parsed = []
    for abspath, rel, corpus in pages:
        fm, text = parse_frontmatter(abspath)
        label = "{0}:{1}".format(corpus, rel)
        if fm is None:
            issues.append(f"{label}: no frontmatter block found; skipped from manifest")
            continue
        if "__parse_error__" in fm:
            issues.append(f"{label}: frontmatter YAML parse error: {fm['__parse_error__']}; skipped from manifest")
            continue
        parsed.append((rel, corpus, label, fm, text))

    all_ids = set()
    for rel, corpus, label, fm, _ in parsed:
        doc_id = fm.get("id")
        if not doc_id:
            issues.append(f"{label}: missing 'id' in frontmatter; skipped from manifest")
            continue
        if doc_id in seen_ids:
            issues.append(f"{label}: duplicate id '{doc_id}' (also used by {seen_ids[doc_id]}); kept first occurrence only")
            continue
        seen_ids[doc_id] = label
        all_ids.add(doc_id)

    for rel, corpus, label, fm, text in parsed:
        doc_id = fm.get("id")
        if not doc_id or seen_ids.get(doc_id) != label:
            continue

        title = fm.get("title")
        doc_type = fm.get("type")
        summary = fm.get("summary")
        keywords = fm.get("keywords") or []

        if not title:
            issues.append(f"{label}: missing 'title'; used filename as fallback")
            title = os.path.splitext(os.path.basename(rel))[0]
        if doc_type not in DOC_TYPES:
            issues.append(f"{label}: missing or invalid 'type' ({doc_type!r}); left as-is, needs manual fix")
        elif corpus == "wiki" and doc_type not in WIKI_TYPES:
            issues.append(f"{label}: '{doc_type}' is a reference type but the page lives in the wiki corpus")
        elif corpus == "reference" and doc_type not in REFERENCE_TYPES:
            issues.append(f"{label}: '{doc_type}' is a wiki type but the page lives in the reference corpus")
        if not summary:
            derived = derive_summary(text)
            issues.append(f"{label}: missing 'summary'; derived one from body text")
            summary = derived
        elif len(summary) > 160:
            issues.append(f"{label}: 'summary' exceeds 160 chars; truncated for manifest only (source file left untouched)")
            summary = summary[:157] + "..."
        if not keywords:
            issues.append(f"{label}: missing/empty 'keywords'")

        related = fm.get("related") or []
        pruned_related = [r for r in related if r in all_ids]
        dropped = [r for r in related if r not in all_ids]
        if dropped:
            issues.append(f"{label}: pruned {len(dropped)} dangling related id(s) from manifest entry: {dropped}")

        entry = {
            "id": doc_id,
            "corpus": corpus,
            "path": rel,
            "title": title,
            "type": doc_type,
            "summary": summary,
            "keywords": keywords,
        }
        if fm.get("yamlPath"):
            entry["yamlPath"] = fm["yamlPath"]
        if fm.get("cliCommand"):
            entry["cliCommand"] = fm["cliCommand"]
        entry["related"] = pruned_related
        entry["sourceRefs"] = fm.get("sourceRefs") or []

        docs.append(entry)

    docs.sort(key=lambda d: d["id"])

    manifest = {
        "version": 1,
        "product": "SQLFlow V3",
        "docs": docs,
    }

    out_path = os.path.join(DOCS_ROOT, "manifest.json")
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    by_corpus = {}
    for d in docs:
        by_corpus[d["corpus"]] = by_corpus.get(d["corpus"], 0) + 1

    print(f"pages_found={len(pages)}")
    print(f"docs_indexed={len(docs)} ({', '.join(f'{k}={v}' for k, v in sorted(by_corpus.items()))})")
    print(f"issues_count={len(issues)}")
    for i in issues:
        print(f"ISSUE: {i}")
    print(f"written={out_path}")


if __name__ == "__main__":
    sys.exit(main())
