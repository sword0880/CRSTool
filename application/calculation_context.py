"""记录计算版本和文件摘要，避免配置变化后复用旧结果。"""
import json
from hashlib import sha256
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def calculation_context():
    # 仅记录业务代码和汇率配置；下载令牌及联网配置不进入快照。
    paths = [ROOT / "config" / name for name in ("exchange_rate.json", "exchange_rate_sources.json")]
    for folder in ("application", "domain", "infrastructure"):
        paths.extend(sorted((ROOT / folder).rglob("*.py")))
    paths.append(ROOT / "app.py")
    files = {str(p.relative_to(ROOT)).replace("\\", "/"):
             sha256(p.read_bytes()).hexdigest() if p.exists() else "missing" for p in paths}
    return {"files": files, "fingerprint": sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()}


def source_fingerprint(source, role):
    """读取后恢复流位置，来源底稿只保留名称和摘要。"""
    if isinstance(source, (str, Path)):
        name, raw = Path(source).name, Path(source).read_bytes()
    else:
        pos = source.tell()
        source.seek(0)
        raw = source.read()
        source.seek(pos)
        name = Path(getattr(source, "name", "uploaded")).name
    return {"filename": name, "role": role, "sha256": sha256(raw).hexdigest()}
