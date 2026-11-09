#!/usr/bin/env python3
"""从离线计划研究记录确定性选取固定失败状态；不读密钥、不联网、不发送世界快照。"""
import hashlib
import json
from pathlib import Path
import sys


def build(source: Path):
    episodes = [json.loads(line) for line in (source / "episodes.jsonl").read_text().splitlines()]
    # request_id 仅在单局内唯一；跨局必须同时用 scope，不能把其他种子的标签接到这里。
    def scope(e):
        return f"{e['kind']}:{e['scenario']}:{e['option'] or ''}:seed={e['seed']}"
    truth = {(scope(e), x["request_id"]): x for e in episodes for x in e["replan_truth"]}
    rows = [json.loads(line) for line in (source / "plan-calls.jsonl").read_text().splitlines()]
    output, seen, counts = [], set(), {}
    for row in rows:
        trace = row["trace"]
        label = truth.get((row["scope"], trace["request_id"]))
        if label is None:
            continue
        before = row["before"]
        captor = before["whelp"]["captured_by"]
        dead_end = (captor is not None and captor != "osric") or (
            captor == "osric" and (before["day"] >= 4 or (before["day"] == 3 and before["phase"] == 3)))
        if dead_end != label["dead_end"]:
            raise ValueError("死局标签与本地快照不一致")
        key = label["failure"] + ("DeadEnd" if label["dead_end"] else "Recoverable")
        limit = 3 if label["dead_end"] else 10
        fingerprint = hashlib.sha256(json.dumps(row["before"], sort_keys=True).encode()).hexdigest()
        if counts.get(key, 0) >= limit or fingerprint in seen:
            continue
        seen.add(fingerprint)
        counts[key] = counts.get(key, 0) + 1
        output.append(dict(id=key + "-" + str(counts[key]), source=source.name, sourceScope=row["scope"],
                           sourceRequest=trace["request_id"], failure=label["failure"],
                           deadEnd=label["dead_end"], before=row["before"],
                           input=trace["input"], context=trace["plan_context"]))
    for failure in ("Precondition", "Dice", "Stale"):
        if counts.get(failure + "Recoverable", 0) != 10:
            raise ValueError(f"{failure} 可补救样本不足 10 个，不能开始此批探针")
    return json.dumps(output, ensure_ascii=False, indent=2), counts


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("用法：python3 build-recovery-fixtures.py 离线报告目录 输出.json")
    text, counts = build(Path(sys.argv[1]))
    target = Path(sys.argv[2])
    if target.exists() and target.read_text() != text:
        raise SystemExit("拒绝覆盖不同的冻结探针集；请另选新文件名并分开报告")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text)
    print(counts)
    print("SHA-256:", hashlib.sha256(target.read_bytes()).hexdigest())
