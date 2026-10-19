#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 苹果 iCloud 中国大陆节假日日历（https://p10-calendars.icloud.com/holidays/CN_ZH.ics）解析。
    ///
    /// 关键规则（都是从真实内容里核对出来的）：
    ///   1) 只有带 X-APPLE-SPECIAL-DAY 的事件才是真节假日：
    ///        WORK-HOLIDAY      = 法定放假日 → 按"放假日动作/标记"写（默认 disable 不打铃）
    ///        ALTERNATE-WORKDAY = 调休上班日 → 按"上班日动作/标记"写（默认 enable 照常打铃）
    ///      像 小寒/大寒/立春/妇女节/元宵节/青年节 这类只有 SUMMARY 的事件必须忽略，
    ///      否则每个节气都会被当成"放假不打铃"。
    ///   2) DTEND 是"不含"的：DTSTART=20231230 + DTEND=20240102 表示 12-30 ~ 01-01。
    ///   3) iCalendar 规定长行折行（CRLF + 空格/制表符开头），解析前必须先还原。
    ///   4) 日历里实际包含 2023~2029 多年数据，只取当年与下一年，避免把几年数据全灌进调休表。
    /// </summary>
    public static class IcsHoliday
    {
        /// <summary>把 ICS 文本解析成程序内部的调休记录数组；失败时通过 error 返回人话原因。</summary>
        public static JsonArray Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text) ||
                text.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) < 0)
            {
                error = "ICS 内容里找不到 VEVENT 事件";
                return null;
            }

            // 1) 还原折行
            var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lines = new List<string>();
            foreach (var l in rawLines)
            {
                if (l.Length > 0 && (l[0] == ' ' || l[0] == '\t') && lines.Count > 0)
                    lines[lines.Count - 1] += l.Substring(1);
                else
                    lines.Add(l);
            }

            int thisYear = DateTime.Now.Year;
            var wantYears = new HashSet<int> { thisYear, thisYear + 1 };

            var result = new JsonArray();
            int accepted = 0;
            bool inEvent = false;
            string start = null, end = null, summary = null, special = null;

            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inEvent = true; start = end = summary = special = null;
                    continue;
                }
                if (s.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (inEvent && start != null && special != null && AddEvent(result, start, end, summary, special, wantYears))
                        accepted++;
                    inEvent = false;
                    start = end = summary = special = null;
                    continue;
                }
                if (!inEvent) continue;

                int colon = s.IndexOf(':');
                if (colon <= 0) continue;
                string keyName = s.Substring(0, colon).Split(';')[0].Trim().ToUpperInvariant();
                string val = s.Substring(colon + 1).Trim();
                if (keyName == "DTSTART") start = val.Length >= 8 ? val.Substring(0, 8) : val;
                else if (keyName == "DTEND") end = val.Length >= 8 ? val.Substring(0, 8) : val;
                else if (keyName == "SUMMARY") summary = val;
                else if (keyName == "X-APPLE-SPECIAL-DAY") special = val;
            }

            if (accepted == 0)
            {
                error = "ICS 里没有 " + thisYear + " / " + (thisYear + 1) + " 年的节假日条目（只有节日名的不算）";
                return null;
            }
            return result;
        }

        static bool AddEvent(JsonArray target, string start, string end, string summary, string special, HashSet<int> wantYears)
        {
            DateTime dStart;
            if (!DateTime.TryParseExact(start, "yyyyMMdd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out dStart)) return false;
            if (!wantYears.Contains(dStart.Year)) return false;

            DateTime dEnd = dStart;
            DateTime parsedEnd;
            if (end != null && DateTime.TryParseExact(end, "yyyyMMdd", CultureInfo.InvariantCulture,
                                                      DateTimeStyles.None, out parsedEnd) && parsedEnd > dStart)
                dEnd = parsedEnd.AddDays(-1);   // DTEND 不含当天

            bool isWorkday = special.IndexOf("ALTERNATE-WORKDAY", StringComparison.OrdinalIgnoreCase) >= 0;
            string action = isWorkday ? Program.GetSubscriptionOnAction() : Program.GetSubscriptionOffAction();
            string note = (summary ?? "").Replace("（休）", "").Replace("（班）", "").Trim();

            target.Add(new JsonObject
            {
                ["date"] = dStart.ToString("yyyy-MM-dd"),
                ["start_date"] = dStart.ToString("yyyy-MM-dd"),
                ["end_date"] = dEnd.ToString("yyyy-MM-dd"),
                ["action"] = action,
                ["marker"] = Program.GetSubscriptionMarker(action),
                ["note"] = note
            });
            return true;
        }
    }
}
