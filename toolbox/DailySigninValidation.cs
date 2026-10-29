using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace NeteaseToolbox
{
    internal static class DailySigninValidation
    {
        private static int count;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        private static Dictionary<string, object> Ok(object data) { return new Dictionary<string, object> { { "code", 200 }, { "data", data } }; }
        private static Dictionary<string, object> Record(string day, long id)
        { return Ok(new object[] { new Dictionary<string, object> { { "timeStr", day }, { "today", true }, { "recordId", id }, { "time", id > 0 ? 1791944255000L : 0 } } }); }
        private sealed class Fake
        {
            internal string Day, User = "123";
            internal bool DetailFails, CloudFails, CloudDuplicate, AuthFails, Written;
            internal int CloudCalls, SignCalls, DetailCalls, Calls;
            internal Dictionary<string, object> Send(string path, Dictionary<string, object> data, bool encrypted)
            {
                Calls++;
                if (path == "/api/user/level") return AuthFails ? new Dictionary<string, object> { { "code", 301 } } : Ok(new Dictionary<string, object> { { "userId", User } });
                if (path == "/api/pointmall/user/sign") { CloudCalls++; return Ok(CloudFails ? new Dictionary<string, object>() : new Dictionary<string, object> { { "sign", !CloudDuplicate } }); }
                if (path == "/api/vipnewcenter/app/user/sign/info") return Record(Day, Written ? 456 : 0);
                if (path == "/api/vip-center-bff/task/sign") { SignCalls++; return Ok(true); }
                if (path == "/api/vipnewcenter/app/level/user/checkin/history/detail") {
                    DetailCalls++; Check(encrypted, "check-in details use EAPI");
                    if (DetailFails) return new Dictionary<string, object> { { "code", 500 } };
                    Written = true; return Ok(new Dictionary<string, object>());
                }
                throw new Exception("Unexpected API path");
            }
        }
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try {
                if (args.Contains("--live")) {
                    var result = DailySigninService.Run(false, item => Console.WriteLine(item.Task + " " + item.Phase + " " + item.Detail));
                    Check(result.YunbeiDone && result.VipDone, "live current account: both check-ins verified");
                    var api = new SigninApi(NeteaseAuth.Cookie());
                    var detail = api.Send("/api/vipnewcenter/app/level/user/checkin/history/detail", new Dictionary<string, object> {
                        { "signDayTime", (long)(DateTimeOffset.UtcNow - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds }, { "type", 1 } }, true);
                    Check(NeteaseAuth.Text(detail, "code") == "200", "native C# EAPI transport gets valid check-in details");
                    var records = api.Send("/api/vipnewcenter/app/user/sign/info", new Dictionary<string, object>(), false);
                    Check(DailySigninService.HasTodayRecord(records, DateTimeOffset.Now.ToOffset(DailySigninService.ChinaOffset).ToString("yyyy-MM-dd")), "native C# transport verifies the actual current-day VIP record");
                    return 0;
                }
                string root = Path.Combine(Path.GetTempPath(), "NeteaseSigninValidation-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string json = "{\"type\":0,\"csrf_token\":\"\"}";
                string weapi = SigninApi.EncryptWeapi(json, "0123456789abcdef");
                Check(WebUtility.UrlDecode(weapi.Split('&')[0].Substring(7)) == "5XU6N2rL263dxRhUX0TqM3ptAIHplOUoSlucTosplXuPEUFzrdBFJdA10NT6RH0L", "WEAPI AES matches independent Python fixture");
                Check(weapi.Split('&')[1].Substring(10) == "35701388baf89fed412e11269b9c76625d095ecaf17f03fa018abe19ea2d38b949debf242ee39a71ca1f6cda71b1b86a45aa909ee27f7e78e267d34e732f0de948206c3340a788d0003372183e2f753c1f78b66ac23d134ac1fc9b993156520ea826b8aa89a962d4491b4b8d7e08738e1da9b07aa39bf4a7ef0b1c210728cd52", "WEAPI RSA matches independent Python fixture");
                Check(SigninApi.EncryptEapi("/api/test", json) == "4DC723619A991588865191FD2F319BAD1DF86742A02D9801D4FC114D30A0E1FB98007EEFA3048CBA32F5B941256641BA6E4F03F3001C9EE80913265AC4DF90A86FF6A971FD5FBACDDE6CA6A202A8E05253ACFD622B37C6C006904F887C7683AB", "EAPI matches independent Python fixture");
                var now = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(8));
                string day = "2026-10-14", folder = Path.Combine(root, "123");
                var settings = new DailySigninSettings();
                Check(!DailySigninService.HasTodayRecord(Record(day, 0), day), "recordId=0 is never a successful VIP check-in");
                Check(!DailySigninService.HasTodayRecord(Record("2026-10-13", 45), day), "previous day record rejected");
                Check(DailySigninService.HasTodayRecord(Record(day, 45), day), "valid record with time accepted");
                Check(!DailySigninService.IsDue(settings, new SigninDayState(), now.AddHours(-2)), "automatic job waits until configured China time");
                Check(DailySigninService.IsDue(settings, new SigninDayState(), now.ToOffset(TimeSpan.Zero)), "schedule uses China time regardless of machine offset");
                var fake = new Fake { Day = day, DetailFails = true };
                var first = DailySigninService.RunForAccount("123", folder, settings, now, false, fake.Send, null);
                Check(first.Phase == "部分失败" && first.YunbeiDone && !first.VipDone, "VIP error yields partial failure, not completion");
                Check(DailySigninService.ReadState(folder).YunbeiDone && !DailySigninService.ReadState(folder).VipDone, "partial progress is durable");
                int previous = fake.Calls;
                var cooldown = DailySigninService.RunForAccount("123", folder, settings, now.AddMinutes(10), true, fake.Send, null);
                Check(cooldown.Skipped && fake.Calls == previous, "automatic failure retry respects 30-minute cooldown");
                fake.DetailFails = false;
                var retry = DailySigninService.RunForAccount("123", folder, settings, now.AddMinutes(31), true, fake.Send, null);
                Check(retry.Phase == "完成" && retry.YunbeiDone && retry.VipDone, "retry completes only failed VIP task");
                Check(fake.CloudCalls == 1, "successful Yunbei is not repeated on partial retry");
                previous = fake.Calls;
                var duplicate = DailySigninService.RunForAccount("123", folder, settings, now, false, fake.Send, null);
                Check(duplicate.Skipped && fake.Calls == previous, "same-day completed manual run avoids all network requests");
                fake.Day = "2026-10-15"; fake.Written = false;
                DailySigninService.RunForAccount("123", folder, settings, now.AddDays(1), true, fake.Send, null);
                Check(fake.CloudCalls == 2 && DailySigninService.ReadState(folder).Day == "2026-10-15", "next China day runs again");
                var cloudFailure = new Fake { Day = day, CloudFails = true, Written = true };
                var rejected = DailySigninService.RunForAccount("123", Path.Combine(root, "cloud-fail"), settings, now, false, cloudFailure.Send, null);
                Check(rejected.Phase == "部分失败" && !rejected.YunbeiDone && rejected.VipDone, "code=200 without sign confirmation is not treated as success");
                var cloudDuplicate = new Fake { Day = day, CloudDuplicate = true, Written = true };
                var already = DailySigninService.RunForAccount("123", Path.Combine(root, "duplicate"), settings, now, false, cloudDuplicate.Send, null);
                Check(already.Phase == "完成" && already.YunbeiDone, "sign=false is the documented already-signed state");
                var expired = new Fake { Day = day, AuthFails = true };
                var expiry = DailySigninService.RunForAccount("123", Path.Combine(root, "expired"), settings, now, false, expired.Send, null);
                Check(expiry.Phase == "失败" && expired.CloudCalls == 0 && expired.SignCalls == 0, "expired login stops mutations and reports failure");
                var wrong = new Fake { Day = day, User = "999" };
                var wrongAccount = DailySigninService.RunForAccount("123", Path.Combine(root, "wrong"), settings, now, false, wrong.Send, null);
                Check(wrongAccount.Phase == "失败" && wrong.CloudCalls == 0, "account mismatch cannot sign with another identity");
                Check(!DailySigninService.ReadState(Path.Combine(root, "other-account")).YunbeiDone, "state remains isolated between accounts");
                var events = DailySigninService.ReadHistory(folder);
                Check(events.Any(item => item.Phase == "开始") && events.Any(item => item.Phase == "部分失败") && events.Any(item => item.Phase == "完成"), "durable history retains start, partial failure, and completion");
                Check(!DailySigninService.SafeError(new InvalidOperationException("MUSIC_U=secret")).Contains("secret"), "unexpected exception text cannot leak credentials");
                Console.WriteLine("PASS TOTAL=" + count + " EVIDENCE=" + root);
                return 0;
            } catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
        }
    }
}
