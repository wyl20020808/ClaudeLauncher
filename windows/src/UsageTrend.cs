using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace ClaudeLauncher {
    internal sealed class TrendPoint { public int Account; public DateTime At; public double? Five, Week; }
    internal static class UsageTrend {
        internal static List<TrendPoint> Clean(IEnumerable<TrendPoint> points, DateTime now) {
            return points.Where(p => p != null && p.Account >= 1 && p.Account <= 10 && p.At <= now && p.At >= now.AddDays(-8) && Valid(p.Five) && Valid(p.Week) && (p.Five.HasValue || p.Week.HasValue)).OrderBy(p => p.At).Reverse().Take(4000).Reverse().ToList();
        }
        static bool Valid(double? value) { return !value.HasValue || (!Double.IsNaN(value.Value) && value >= 0 && value <= 100); }
        internal static List<TrendPoint> Load(string path, DateTime now) {
            if (!File.Exists(path)) return new List<TrendPoint>();
            return Clean(new JavaScriptSerializer().Deserialize<List<TrendPoint>>(LocalFiles.Read(path, 1024 * 1024)) ?? new List<TrendPoint>(), now);
        }
        internal static void Observe(List<TrendPoint> points, int account, UsageRecord record, DateTime now) {
            var point = new TrendPoint { Account = account, At = now,
                // Call with freshly observed values only, never carried-over values or estimates.
                Five = record.FiveHour != null && record.FiveHour.Reset != UsageInterface.FiveHourEstimate ? (double?)record.FiveHour.Percent : null,
                Week = record.Weekly != null ? (double?)record.Weekly.Percent : null };
            var last = points.LastOrDefault(p => p.Account == account);
            if ((point.Five.HasValue || point.Week.HasValue) && (last == null || last.Five != point.Five || last.Week != point.Week || now - last.At >= TimeSpan.FromMinutes(15))) points.Add(point);
            var clean = Clean(points,now); points.Clear(); points.AddRange(clean);
        }
        internal static double? HoursLeft(IEnumerable<TrendPoint> points, bool week, DateTime now) {
            var rows = points.Where(p => (week ? p.Week : p.Five).HasValue).OrderBy(p => p.At).ToList();
            if (rows.Count < 2 || now - rows.Last().At > TimeSpan.FromHours(6)) return null;
            int start = 0;
            for (int i=1;i<rows.Count;i++) if ((week ? rows[i].Week : rows[i].Five) < (week ? rows[i-1].Week : rows[i-1].Five)) start=i;
            var first = rows[start]; var last = rows.Last(); double hours=(last.At-first.At).TotalHours;
            double a=(week?first.Week:first.Five).Value,b=(week?last.Week:last.Five).Value;
            return hours >= 1.0/3 && b>a ? (double?)((100-b)*hours/(b-a)) : null;
        }
        // 折线分段（SPEC 用量趋势）：数值回落是限额重置，相邻两点间隔超过该额度窗口（5 小时线 5 小时、周线 7 天）
        // 中间可能有没看到的重置，两种情况都新起一段，不画跨重置或跨天的斜线。
        internal static List<List<TrendPoint>> Runs(IEnumerable<TrendPoint> points, bool week, DateTime begin) {
            var runs = new List<List<TrendPoint>>(); double previous = -1; DateTime previousAt = DateTime.MinValue;
            TimeSpan window = week ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);
            foreach (var point in points.OrderBy(p => p.At)) {
                double? value = week ? point.Week : point.Five;
                if (!value.HasValue || point.At < begin) continue;
                if (runs.Count == 0 || value.Value < previous || point.At - previousAt > window) runs.Add(new List<TrendPoint>());
                runs[runs.Count - 1].Add(point); previous = value.Value; previousAt = point.At;
            }
            return runs;
        }
        internal static int Recommend(IEnumerable<TrendPoint> points, IEnumerable<int> accounts) {
            return points.Where(p => accounts.Contains(p.Account) && p.Week.HasValue).GroupBy(p => p.Account).Select(g => g.OrderBy(p => p.At).Last()).OrderBy(p => p.Week).ThenBy(p => p.Five ?? 100).Select(p => p.Account).FirstOrDefault();
        }
        internal static void Save(string path, IEnumerable<TrendPoint> points) { LocalFiles.Write(path,new JavaScriptSerializer().Serialize(points)); }
    }
}
