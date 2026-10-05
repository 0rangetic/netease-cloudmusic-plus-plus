const crypto = require("crypto");
const fs = require("fs");
const https = require("https");
const os = require("os");
const path = require("path");
const querystring = require("querystring");

const API_PATH = "/api/content/activity/music/first/listen/info";
const EAPI_PATH = "/eapi/content/activity/music/first/listen/info";
const KEY = Buffer.from("e82ckenh8dichen8");

function loadCookie() {
  const cookie = process.env.NETEASE_TOOLBOX_COOKIE;
  if (!cookie) throw new Error("请通过工具箱登录后刷新网易云数据");
  return cookie;
}

function encrypt(songId) {
  const payload = JSON.stringify({
    songId: String(songId),
    header: { os: "iPhone OS", appver: "9.0.90", osver: "16.2", channel: "distribution" },
  });
  const digest = crypto.createHash("md5").update(`nobody${API_PATH}use${payload}md5forencrypt`).digest("hex");
  const source = `${API_PATH}-36cd479b6b5-${payload}-36cd479b6b5-${digest}`;
  const cipher = crypto.createCipheriv("aes-128-ecb", KEY, null);
  return Buffer.concat([cipher.update(source, "utf8"), cipher.final()]).toString("hex").toUpperCase();
}

function request(songId, cookie) {
  return new Promise((resolve) => {
    const body = querystring.stringify({ params: encrypt(songId) });
    const req = https.request({
      hostname: "interface.music.163.com",
      path: EAPI_PATH,
      method: "POST",
      timeout: 20000,
      headers: {
        Cookie: cookie,
        "User-Agent": "NeteaseMusic/9.0.90",
        "Content-Type": "application/x-www-form-urlencoded",
        "Content-Length": Buffer.byteLength(body),
      },
    }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (chunk) => { text += chunk; });
      res.on("end", () => {
        try {
          const response = JSON.parse(text);
          resolve({ songId: String(songId), data: response.code === 200 ? response.data : null });
        } catch (_) { resolve({ songId: String(songId), data: null }); }
      });
    });
    req.on("timeout", () => req.destroy());
    req.on("error", () => resolve({ songId: String(songId), data: null }));
    req.end(body);
  });
}

async function main() {
  const input = await new Promise((resolve) => {
    let text = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => { text += chunk; });
    process.stdin.on("end", () => resolve(text));
  });
  const ids = JSON.parse(input);
  const cookie = loadCookie();
  let cursor = 0;
  async function worker() {
    while (cursor < ids.length) {
      const index = cursor++;
      const result = await request(ids[index], cookie);
      process.stdout.write(JSON.stringify(result) + os.EOL);
    }
  }
  await Promise.all(Array.from({ length: Math.min(6, ids.length) }, worker));
}

main().catch((error) => {
  process.stderr.write(String(error && error.message || error) + os.EOL);
  process.exitCode = 1;
});
