import { readFile, writeFile } from 'node:fs/promises';
import { version } from './version.mjs';
const destination = process.argv[2];
if (!destination) throw new Error('Provide the output HTML path');
const markdown = await readFile(new URL('./manual.md', import.meta.url), 'utf8');
const escape = x => x.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const inline = x => escape(x).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
const lines = markdown.split(/\r?\n/), out = [];
let list = '', table = false;
const closeList = () => { if (list) { out.push('</' + list + '>'); list = ''; } };
for (const line of lines) {
  if (line.startsWith('|')) {
    closeList();
    if (/^\|[\s:|-]+\|$/.test(line)) continue;
    if (!table) { out.push('<table>'); table = true; }
    out.push('<tr>' + line.split('|').slice(1, -1).map(x => '<td>' + inline(x.trim()) + '</td>').join('') + '</tr>');
    continue;
  }
  if (table) { out.push('</table>'); table = false; }
  const heading = line.match(/^(#{1,3}) (.+)$/);
  if (heading) { closeList(); const n = heading[1].length; out.push(`<h${n}>${inline(heading[2])}</h${n}>`); continue; }
  const item = line.match(/^(?:([-])|\d+\.) (.+)$/);
  if (item) {
    const wanted = item[1] ? 'ul' : 'ol';
    if (list !== wanted) { closeList(); list = wanted; out.push('<' + list + '>'); }
    out.push('<li>' + inline(item[2]) + '</li>'); continue;
  }
  closeList();
  if (line.trim()) out.push('<p>' + inline(line.trim()) + '</p>');
}
closeList(); if (table) out.push('</table>');
await writeFile(destination, `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>网易云多人一起听补丁 · 使用说明</title><style>
*{box-sizing:border-box}html{background:#eef2f7;color:#1c293c;font:16px/1.85 "Microsoft YaHei",system-ui,sans-serif}body{margin:0}main{max-width:920px;margin:42px auto;background:white;border:1px solid #dae2ec;border-radius:18px;box-shadow:0 14px 48px #2030500b;padding:44px 60px}h1{font-size:32px;line-height:1.4;letter-spacing:.03em;color:#153152;margin:0 0 10px}h2{font-size:22px;margin:38px 0 16px;padding-top:24px;border-top:1px solid #e3e8ef;color:#174a78}p{margin:10px 0}strong{color:#174a78}li{margin:10px 0}code{background:#edf2f8;color:#264965;padding:2px 5px;border-radius:4px;font-size:.9em;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;margin:18px 0;font-size:15px}td{border:1px solid #dce4ee;padding:9px 14px;vertical-align:top}tr:first-child{background:#eaf1f8;font-weight:700}td:first-child{min-width:160px}ol{padding-left:28px}footer{color:#687b91;border-top:1px solid #e3e8ef;margin-top:38px;padding-top:20px;font-size:13px}@media(max-width:680px){main{margin:0;border-radius:0;padding:28px 22px}h1{font-size:26px}table{font-size:13px}td{padding:7px}td:first-child{min-width:90px}}@media print{html{background:white}main{max-width:none;border:0;box-shadow:none;margin:0;padding:0}h2{break-after:avoid}tr,li{break-inside:avoid}}
</style><main>${out.join('\n')}<footer>离线说明书 · 包内不包含网易云安装程序或账号数据 · ${escape(version)}</footer></main></html>`, 'utf8');
