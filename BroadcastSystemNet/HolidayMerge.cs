#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 统一的"连续日期合并"：四种订阅源（小米 / iCloud ICS / holiday-cn / 原生 JSON）转出来的记录，
    /// 在写入配置文件之前都先经过这里 —— 相接或重叠的日期合并成一条区间记录。
    ///
    /// 例：元旦 01-01、01-02、01-03 三条 → 一条 2026-01-01 ~ 2026-01-03；
    ///     再遇到 01-01 ~ 01-03 这种重叠区间，也会并成同一条。
    /// 合并只在「动作 + 标记」相同的前提下进行，避免把"放假"和"调休上班"混成一条。
    /// 备注取该区间内天数最多的那条（信息量最大）。
    /// </summary>
    public static class HolidayMerge
    {
        public static JsonArray MergeRanges(JsonArray items)
        {
            if (items == null) return null;

            var list = new List<Item>();
            foreach (var n in items)
            {
                if (n is not JsonObject o) continue;
                string sTxt = o["start_date"]?.ToString() ?? o["date"]?.ToString() ?? "";
                string eTxt = o["end_date"]?.ToString() ?? sTxt;
                DateTime s, e;
                if (!DateTime.TryParse(sTxt, CultureInfo.InvariantCulture, DateTimeStyles.None, out s)) continue;
                if (!DateTime.TryParse(eTxt, CultureInfo.InvariantCulture, DateTimeStyles.None, out e) || e < s) e = s;
                list.Add(new Item
                {
                    Start = s.Date,
                    End = e.Date,
                    Action = o["action"]?.ToString() ?? "disable",
                    Marker = o["marker"]?.ToString() ?? "0",
                    Note = o["note"]?.ToString() ?? ""
                });
            }
            if (list.Count == 0) return items;   // 没有可解析的日期就原样返回，交给上层报错

            var merged = new List<Item>();
            foreach (var group in list.GroupBy(x => x.Action + "|" + x.Marker))
            {
                var sorted = group.OrderBy(x => x.Start).ThenByDescending(x => x.End).ToList();
                DateTime curStart = sorted[0].Start, curEnd = sorted[0].End;
                string note = sorted[0].Note;
                int bestDays = (curEnd - curStart).Days + 1;
                string action = sorted[0].Action, marker = sorted[0].Marker;

                for (int i = 1; i < sorted.Count; i++)
                {
                    Item cur = sorted[i];
                    if (cur.Start <= curEnd.AddDays(1))               // 相接或重叠 → 合并
                    {
                        if (cur.End > curEnd) curEnd = cur.End;
                        int days = (cur.End - cur.Start).Days + 1;
                        if (days > bestDays)
                        {
                            bestDays = days;
                            if (!string.IsNullOrWhiteSpace(cur.Note)) note = cur.Note;
                        }
                    }
                    else
                    {
                        merged.Add(new Item { Start = curStart, End = curEnd, Action = action, Marker = marker, Note = note });
                        curStart = cur.Start; curEnd = cur.End; note = cur.Note;
                        bestDays = (curEnd - curStart).Days + 1;
                    }
                }
                merged.Add(new Item { Start = curStart, End = curEnd, Action = action, Marker = marker, Note = note });
            }

            // 按开始日期排序后再逐个新建节点写进结果数组
            // （注意：不能把已经挂在别的数组上的 JsonNode 再加一次，JsonNode 不允许有两个父节点）
            var result = new JsonArray();
            foreach (var it in merged.OrderBy(x => x.Start).ThenBy(x => x.Action))
            {
                result.Add(new JsonObject
                {
                    ["date"] = it.Start.ToString("yyyy-MM-dd"),
                    ["start_date"] = it.Start.ToString("yyyy-MM-dd"),
                    ["end_date"] = it.End.ToString("yyyy-MM-dd"),
                    ["action"] = it.Action,
                    ["marker"] = it.Marker,
                    ["note"] = it.Note ?? ""
                });
            }
            return result;
        }

        private class Item
        {
            public DateTime Start;
            public DateTime End;
            public string Action;
            public string Marker;
            public string Note;
        }
    }
}
