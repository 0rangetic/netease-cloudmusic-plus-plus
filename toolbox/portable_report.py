"""Generate self-contained listening reports without Codex, npm or network assets."""
import argparse
import base64
import html
import json
from pathlib import Path
import sys
import tempfile

sys.path.append(str(Path(__file__).resolve().parent.parent / "music-profile"))
import portrait_ai
import refresh_listening_report as data


def encoded(value):
    return base64.urlsafe_b64encode(str(value).encode()).decode().rstrip("=")


def esc(value):
    return html.escape(str(value), quote=True)


def render(snapshot, scope, ai):
    queries = snapshot["queries"]
    summary = queries["listening_summary"]["rows"][0]
    prefix = "listening_all_" if scope == "all" else "listening_recent_"
    title = "所有歌曲画像" if scope == "all" else "近30天新增红心画像"
    total = summary["allTracks" if scope == "all" else "recentLikedTracks"]
    genre_covered = summary["allGenreCovered" if scope == "all" else "recentGenreCovered"]
    mood_covered = summary["allMoodCovered" if scope == "all" else "recentMoodCovered"]
    cards = f'<div class="metrics"><article><strong>{total}</strong>歌曲数</article><article><strong>{genre_covered}</strong>有曲风标签</article><article><strong>{mood_covered}</strong>有情绪标签</article></div>'
    sections = []
    for kind, label in [("genres", "曲风分布"), ("moods", "情绪分布")]:
        query = queries[prefix + kind]
        rows = query["rows"]
        bars = "".join(f'<div class="bar"><span>{esc(row["label"])}</span><meter min="0" max="1" value="{max(0,min(1,float(row["share"])))}"></meter><b>{float(row["share"]):.1%}</b><small>{int(row["tracks"])} 首</small></div>' for row in rows)
        definition = query["source"]["metricDefinitions"][0]["definition"]
        sections.append(f'<section><h2>{label}</h2><p class="muted">{esc(definition)}</p>{bars or "<p>暂无标签数据。请先刷新曲风分析。</p>"}</section>')
    table_queries = [("listening_artists", "首位艺人累计播放", [("artist","艺人"),("plays","累计播放"),("tracks","歌曲数")])] if scope == "all" else [
        ("listening_genre_comparison", "近期与全局对比", [("label","曲风"),("recentShare","近期占比"),("allShare","全局占比"),("deltaPp","差值（百分点）")]),
        ("listening_recent_tracks", "近期新增红心曲目", [("title","歌曲"),("artist","艺人"),("likedAt","红心日期"),("genres","曲风"),("moods","情绪")])]
    for key, label, columns in table_queries:
        query = queries[key]
        header = "".join(f'<th>{esc(label)}</th>' for _, label in columns)
        body = []
        for row in query["rows"]:
            cells = []
            for field, _ in columns:
                value = row.get(field, "")
                text = f'{float(value):.1%}' if field.endswith("Share") else str(value)
                cells.append(f'<td>{esc(text)}</td>')
            body.append('<tr>' + ''.join(cells) + '</tr>')
        definition = query["source"]["metricDefinitions"][0]["definition"]
        sections.append(f'<section><h2>{label}</h2><p class="muted">{esc(definition)}</p><div class="table"><table><thead><tr>{header}</tr></thead><tbody>{"".join(body)}</tbody></table></div></section>')
    status = ai.get("status", "not_generated")
    status_text = {"disabled":"AI 已停用", "not_generated":"尚未生成 AI 解读", "ready":"AI 解读已生成", "unavailable":"AI 解读不可用", "error":"AI 解读失败"}.get(status, status)
    ai_text = ai.get("text") or ai.get("error") or "启用并配置 AI 后，刷新画像可生成解读。"
    sections.append(f'<section><h2>AI 解读</h2><p class="muted">{esc(status_text)} · {esc(ai.get("model", ""))}</p><div class="prose">{esc(ai_text)}</div></section>')
    css = 'body{margin:0;background:#f3f6f8;color:#172536;font:16px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}main{max-width:1100px;margin:36px auto;padding:0 24px}h1{font-size:30px}h2{font-size:21px;margin:0 0 14px}section,article{background:white;border:1px solid #dce4ea;border-radius:16px;padding:24px;margin:20px 0}.metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:16px}.metrics article{margin:0}.metrics strong{display:block;font-size:32px;color:#226d87}.muted{color:#647487;font-size:14px}.bar{display:grid;grid-template-columns:minmax(120px,1fr) 3fr 70px 65px;align-items:center;gap:14px;margin:12px 0}meter{width:100%;height:22px}table{width:100%;border-collapse:collapse;text-align:left}td,th{padding:12px;border-bottom:1px solid #e4e9ef}th{background:#f2f6f8;position:sticky;top:0}.table{overflow:auto;max-height:600px}.prose{white-space:pre-wrap;overflow-wrap:anywhere}a{color:#226d87}@media(max-width:650px){.metrics{grid-template-columns:1fr}.bar{grid-template-columns:110px 1fr 60px}.bar small{display:none}main{padding:0 14px}}'
    asof = summary["profileAt"]
    return f'<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{title}</title><style>{css}</style><main><nav><a href="music-profile-report-all.html">所有歌曲</a> · <a href="music-profile-report-recent.html">近30天新增红心</a></nav><h1>{title}</h1><p class="muted">档案时间：{esc(asof)} · 本机生成：{esc(snapshot["generatedAt"])}<br>近期范围：{esc(summary["recentStart"])} — {esc(summary["recentEnd"])}。近期表示新增红心时间，不表示最近播放。多标签比例可能合计超过 100%。</p>{cards}{"".join(sections)}<footer class="muted">来源：本机网易云听歌档案及曲风标签缓存。标签由网易云歌曲百科提供；无标签曲目不计入对应标签比例分母。报告不含登录凭证或 AI 密钥。</footer></main></html>'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scope", choices=("all", "recent", "both"), required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--ai", action="store_true")
    args = parser.parse_args()
    output = Path(args.output_dir) / ".data-app-offline/exports"
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="netease-report-") as scratch:
        snapshot = data.main(verbose=False, destination=Path(scratch)/"snapshot.json")
    scopes = ("all", "recent") if args.scope == "both" else (args.scope,)
    enabled = portrait_ai.config()["enabled"]
    if args.ai:
        for scope in scopes:
            entry = portrait_ai.generate(scope, data.PROFILE, data.GENRE_DB)
            print(f'AI_STATUS\t{scope}\t{entry["status"]}', flush=True)
            if entry.get("error"):
                print(f'AI_ERROR\t{scope}\t{encoded(entry["error"])}', flush=True)
    state = json.loads(portrait_ai.STATE_PATH.read_text(encoding="utf-8")) if portrait_ai.STATE_PATH.exists() else {}
    for scope in ("all", "recent"):
        ai = state.get(scope, {}) if enabled else {"status": "disabled"}
        content = render(snapshot, scope, ai)
        target = output / f"music-profile-report-{scope}.html"
        temporary = target.with_suffix(".html.tmp")
        temporary.write_text(content, encoding="utf-8")
        temporary.replace(target)
    requested = output / f'music-profile-report-{"all" if args.scope=="both" else args.scope}.html'
    print(f'REPORT\t{args.scope}\t{encoded(requested)}', flush=True)
    if not args.ai:
        for scope in scopes:
            print(f'AI_STATUS\t{scope}\t{state.get(scope, {}).get("status", "not_generated") if enabled else "disabled"}', flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("FATAL\t" + encoded(error), flush=True)
        sys.exit(1)
