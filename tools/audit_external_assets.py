"""只读盘点；可选隔离字节完全相同的 .url / 指定说明 ZIP，不处理素材包或下载文件。"""
import argparse
import collections
import datetime
import hashlib
import json
from pathlib import Path


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def audit(root, output, quarantine=False):
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("素材根目录必须是目录")
    rows, duplicates = [], collections.defaultdict(list)
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or not path.is_file():
            continue
        relative = path.relative_to(root)
        if any(p.startswith(".筛选隔离-") for p in relative.parts):
            continue
        stat = path.stat()
        downloading = any(x in path.name.lower() for x in ("downloading", ".part", ".crdownload"))
        helper = path.suffix.lower() == ".url" or path.name == "插件及使用方法图示.zip"
        row = dict(path=str(relative), bytes=stat.st_size, mtime_ns=stat.st_mtime_ns,
                   status="downloading_keep" if downloading else "keep_pending_or_previously_reviewed")
        # 大素材不因文件名或大小判重复；只核对明确的辅助文件，限制扫描范围。
        if helper and not downloading and stat.st_size < 4 * 1024 * 1024:
            row["sha256"] = digest(path)
            duplicates[(stat.st_size, row["sha256"])].append(row)
        rows.append(row)
    groups = []
    for members in duplicates.values():
        if len(members) < 2:
            continue
        canonical = members[0]
        groups.append(dict(canonical=canonical["path"], sha256=canonical["sha256"],
                           bytes_each=canonical["bytes"], duplicates=[r["path"] for r in members[1:]]))
    report = dict(schema=1, scanned_at=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  root=str(root), files=rows, duplicate_helpers=groups, quarantined=[],
                  policy="原始素材/下载中/用途或许可未明者保留；仅相同哈希的辅助文件可恢复隔离，未永久删除")
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError("拒绝覆盖既有审计：" + str(output))
    # 先落盘候选，再做精确且同卷可恢复的移动；失败时已有逐项记录。
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2))
    if quarantine:
        backup = root / (".筛选隔离-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
        for group in groups:
            canonical = root / group["canonical"]
            for relative in group["duplicates"]:
                source, target = root / relative, backup / relative
                if source.is_symlink() or canonical.is_symlink() or target.exists():
                    raise ValueError("隔离前路径状态变化")
                if digest(source) != group["sha256"] or digest(canonical) != group["sha256"]:
                    raise ValueError("隔离前文件内容变化")
                target.parent.mkdir(parents=True, exist_ok=True)
                source.rename(target)
                report["quarantined"].append(dict(original=relative, backup=str(target.relative_to(root)),
                                                  sha256=group["sha256"]))
                output.write_text(json.dumps(report, ensure_ascii=False, indent=2))
        if report["quarantined"]:
            (backup / "隔离清单.json").write_text(json.dumps(report["quarantined"], ensure_ascii=False, indent=2))
    print(json.dumps(dict(files=len(rows), downloading=sum(r["status"] == "downloading_keep" for r in rows),
                          duplicate_groups=len(groups), quarantined=len(report["quarantined"]),
                          report=str(output)), ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--quarantine-duplicate-helpers", action="store_true")
    args = parser.parse_args()
    audit(args.root, args.output, args.quarantine_duplicate_helpers)
