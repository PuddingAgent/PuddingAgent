"""从 biyingapi.com 的文档页抽取接口清单，生成可被运行时消费的端点注册表。

为什么要有这一步（2026-10-25 决策）：
必盈 API 有约 200 个接口，不可能一接口一工具；工具层只暴露一个
`biying_query(endpoint, code, params)`，端点清单必须是**数据**而不是 schema。
本脚本把官方文档里的 `data-api-*` 属性机械抽出来，避免手抄出错。

用法：
    py -3 Tools/Biying/extract_endpoints.py            # 抓取并写入 endpoints.json
    py -3 Tools/Biying/extract_endpoints.py --check    # 只校验已有文件，不联网

输出字段：
    id / name / category / subcategory / host / path / query / needsCode / testUrl

注意：docs 页面是 Django 服务端渲染的静态 HTML，`data-api-*` 直接可解析；
若某天改成 JS 渲染，本脚本会抽到 0 条并**报错退出**（不会静默产出空清单）。
"""

from __future__ import annotations

import html
import io
import json
import re
import sys
import urllib.request
from dataclasses import dataclass, asdict
from datetime import datetime, timezone

DOC_PAGES = [
    ("doc_hs", "沪深A股"),
    ("doc_zs", "沪深指数"),
    ("doc_jg", "沪深数据中心"),
    ("doc_jj", "基金数据"),
    ("doc_jjhq", "基金行情档案"),
    ("doc_kc", "科创板"),
    ("doc_fx", "北京交易所"),
]

BASE = "https://biyingapi.com/"
OUT_PATH = "Tools/Biying/endpoints.json"
UA = {"User-Agent": "PuddingAgent/1.0 (+endpoint-catalog-extractor)"}

# 模板里的中文占位符（如「股票代码(如000001)」「您的licence」）不是真实路径段
_PLACEHOLDER = re.compile(r"[\u4e00-\u9fff]")
# 返回字段表行：<td><code>jzrq</code></td><td>string</td><td>截止日期yyyy-MM-dd</td>
_FIELD_ROW = re.compile(
    r"<code>([A-Za-z0-9_]+)</code>\s*</td>\s*<td>([^<]*)</td>\s*<td>(.*?)</td>", re.S
)
_TAG = re.compile(r"<[^>]+>")


@dataclass
class Endpoint:
    id: str
    name: str
    category: str
    subcategory: str
    host: str
    path: str
    template: str
    segments: list[dict]
    query: list[str]
    needsCode: bool
    testUrl: str
    fields: list[dict]


def fetch(page: str) -> str:
    url = BASE + page
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read().decode("utf-8", "replace")


def parse_page(markup: str, category: str) -> list[Endpoint]:
    # 先把锚点位置找出：category 标题、subcategory 标题、每个 api 条目
    events: list[tuple[int, str, str]] = []
    for m in re.finditer(r'<span>([^<]+)</span>\s*<span class="toggle-icon">', markup):
        # 两类标题都用这个结构，靠外层类名区分
        head = markup[max(0, m.start() - 400) : m.start()]
        kind = "subcategory" if "doc-nav-subcategory-title" in head else "category"
        events.append((m.start(), kind, html.unescape(m.group(1)).strip()))

    for m in re.finditer(r'<a class="doc-nav-item[^"]*"\s*([^>]+?)>', markup, re.S):
        attrs = dict(re.findall(r'data-([a-z\-]+)="([^"]*)"', m.group(1)))
        events.append((m.start(), "endpoint", json.dumps(attrs)))

    events.sort(key=lambda e: e[0])

    current_cat = category
    current_sub = ""
    result: list[Endpoint] = []
    for _, kind, payload in events:
        if kind == "category":
            current_cat = payload
            current_sub = ""  # 换大类时子类也重置，否则会串到上一个子类
            continue
        if kind == "subcategory":
            current_sub = payload
            continue
        if kind != "endpoint":
            continue
        attrs = json.loads(payload)
        raw_url = html.unescape(attrs.get("api-url", ""))
        if "/" not in raw_url:
            continue
        result.append(build(attrs, raw_url, current_cat, current_sub))
    return result


def build(attrs: dict, raw_url: str, category: str, subcategory: str) -> Endpoint:
    # 去掉 query 后再按段解析
    without_query, _, query = raw_url.partition("?")
    query_keys = [kv.split("=")[0] for kv in query.split("&") if kv and "=" in kv]

    scheme_host, _, rest = without_query.partition("://")
    host, _, tail = rest.partition("/")
    segments = [s for s in tail.split("/") if s]

    # 末段是 licence；其余含中文的段是代码/参数占位符
    code_segments = [s for s in segments if not _PLACEHOLDER.search(s)]
    needs_code = any(_PLACEHOLDER.search(s) and "licence" not in s.lower() for s in segments)

    # path 只保留纯 ASCII 的固定段（组/接口名），动态段交给调用方填
    fixed = [s for s in segments[: len(segments) - 1] if not _PLACEHOLDER.search(s)]
    path = "/".join(fixed) if fixed else "/".join(code_segments)

    return Endpoint(
        id=attrs.get("api-id", ""),
        name=html.unescape(attrs.get("api-name", "")).strip(),
        category=category,
        subcategory=subcategory,
        host=host,
        path=path,
        # template 逐字保留：它是唯一权威，降级方案（分段发牌）都和它对齐
        template=without_query,
        segments=[
            {"kind": "placeholder", "value": s} if _PLACEHOLDER.search(s) or "{" in s
            else {"kind": "literal", "value": s}
            for s in segments
        ],
        query=query_keys,
        needsCode=needs_code,
        testUrl=html.unescape(attrs.get("test-url", "")),
        # 字段词典由 parse_fields 另行填，不在导航属性里
        fields=[],
    )


def parse_fields(markup: str) -> dict[str, list[dict]]:
    """抽取每个接口的「返回字段说明」表。

    文档是服务端渲染的单页详情：每个接口一个 `div.doc-api-detail[data-api-id]`，
    里面先有「请求参数」表、再有「返回字段说明」表。两表 markup 同构，所以必须先切到
    「返回字段说明」之后才开始解析，否则会把请求参数当成返回字段（这是个真实陷阱）。
    """
    result: dict[str, list[dict]] = {}
    starts = [(m.start(), m.group(1)) for m in re.finditer(r'class="doc-api-detail" data-api-id="(\d+)"', markup)]
    for idx, (pos, api_id) in enumerate(starts):
        end = starts[idx + 1][0] if idx + 1 < len(starts) else len(markup)
        block = markup[pos:end]
        marker = block.find("返回字段说明")
        if marker < 0:
            result[api_id] = []
            continue
        tail = block[marker:]
        nxt = tail.find("<h2", 10)  # 下一节（如「请求示例」）不属于字段表
        section = tail[:nxt] if nxt > 0 else tail
        rows = []
        for key, typ, desc in _FIELD_ROW.findall(section):
            desc_clean = html.unescape(_TAG.sub("", desc)).strip()
            rows.append({"key": key.strip(), "type": typ.strip(), "desc": desc_clean})
        result[api_id] = rows
    return result


def main(argv: list[str]) -> int:
    if "--check" in argv:
        return check()

    endpoints: list[Endpoint] = []
    field_map: dict[str, list[dict]] = {}
    for page, category in DOC_PAGES:
        markup = fetch(page)
        got = parse_page(markup, category)
        # 只有详情页带字段表；导航页（无 doc-api-detail）返回空字典，不影响上一个来源
        for api_id, rows in parse_fields(markup).items():
            if rows:
                field_map.setdefault(api_id, rows)
        print(f"{page:<10} {category:<12} {len(got):>4} 条")
        if not got:
            print(f"ERROR: {page} 解析出 0 条——文档结构可能已改（JS 渲染？）", file=sys.stderr)
            return 2
        endpoints.extend(got)

    # 去重：同一 id 只保留第一次出现（跨页可能重复挂同一接口）
    seen: dict[str, Endpoint] = {}
    for e in endpoints:
        seen.setdefault(e.id or e.testUrl, e)
    final = list(seen.values())
    for e in final:
        e.fields = field_map.get(e.id, [])

    payload = {
        "schema": "pudding.biying.endpoints.v2",
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source": [BASE + page for page, _ in DOC_PAGES],
        "count": len(final),
        "fieldCoverage": {
            "withFields": sum(1 for e in final if e.fields),
            "totalFields": sum(len(e.fields) for e in final),
        },
        "endpoints": [asdict(e) for e in final],
    }
    io.open(OUT_PATH, "w", encoding="utf-8", newline="\n").write(
        json.dumps(payload, ensure_ascii=False, indent=1)
    )
    print(f"written {OUT_PATH}  count={len(final)}")
    return 0


def check() -> int:
    data = json.load(io.open(OUT_PATH, encoding="utf-8"))
    eps = data.get("endpoints") or []
    if not eps:
        print("FAIL: endpoints 为空", file=sys.stderr)
        return 2
    missing = [e for e in eps if not e.get("name") or not e.get("path")]
    if missing:
        print(f"FAIL: {len(missing)} 条缺 name/path", file=sys.stderr)
        return 2
    # 字段词典是 v2 的核心产物：没有它，拼音短键对模型不可解释（猜错比不给更糟）。
    # 所以把它当成硬门禁：任一端点抽不到字段就失败，而不是静默降级。
    no_fields = [e for e in eps if not e.get("fields")]
    if no_fields:
        print(f"FAIL: {len(no_fields)} 条端点没有字段词典，示例 "
              f"{[e.get('id') for e in no_fields[:5]]}", file=sys.stderr)
        return 2
    total_fields = sum(len(e["fields"]) for e in eps)
    hosts = sorted({e["host"] for e in eps})
    cats = sorted({e["category"] for e in eps})
    print(f"OK count={len(eps)} fields={total_fields} hosts={hosts}")
    print(f"categories={cats}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
