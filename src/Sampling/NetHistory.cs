using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace task_monitor
{
    /// <summary>每月一条：所属月份（"yyyy-MM"）+ 上传/下载字节数。</summary>
    [DataContract]
    internal sealed class NetMonthRecord
    {
        [DataMember(Name = "period", Order = 1)]
        public string Period;

        [DataMember(Name = "up", Order = 2)]
        public long Up;

        [DataMember(Name = "down", Order = 3)]
        public long Down;
    }

    /// <summary>net_history.json 的根对象。带 version 是为了以后换格式时能识别旧文件。</summary>
    [DataContract]
    internal sealed class NetHistoryFile
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version = 1;

        [DataMember(Name = "months", Order = 2)]
        public List<NetMonthRecord> Months = new List<NetMonthRecord>();
    }

    /// <summary>
    /// 多月流量历史，存**独立的 `net_history.json`**（不塞进 `settings.yaml`）。
    ///
    /// <para>为什么独立文件：`settings.yaml` 的语义是「配置」，历史是「数据」——分开之后
    /// ① 数据坏了不会把配置带崩，反之亦然；② 配置的每次整体重写不必带上会增长的数组；
    /// ③ 用户想清掉历史时，删一个文件就行，不必动配置。</para>
    ///
    /// <para>保留策略（用户 2026-11-12 定）：**默认保留最近 3 个月**，写入时自动裁掉更早的。
    /// 上限放进 `settings.yaml` 的 `netHistoryKeepMonths`（1~120），不设开关 —— 历史本来
    /// 就只是一行行数字，没有必要为它单开一个界面。</para>
    ///
    /// <para>健壮性：文件缺失 / 损坏 / 权限不足一律**当成空历史**并继续跑，绝不因为历史
    /// 读不出来就让采样或界面出错；写入走「先写 .tmp 再替换」，避免断电留下半个文件。</para>
    /// </summary>
    internal static class NetHistory
    {
        /// <summary>
        /// 默认保留月数 = 3（用户 2026-11-12 定：「3 个月保底」）。
        /// 语义是「至少留住上一个季度」—— 只留当月的话，跨月就只剩一个孤零零的数字，
        /// 看不出趋势；3 个月够看出「这个月是不是比上个月多」，文件也只有三百来字节。
        /// </summary>
        public const int DefaultKeepMonths = 3;

        /// <summary>
        /// 允许的保留区间：1~**12**。上限压到 12 是用户 2026-11-12 定的 —— 详情弹窗的「往月」
        /// 一行最多也就展示这么多，存更多只会在 net_history.json 里吃灰，还会让下拉多一个
        /// 「24 个月」这种点开就把弹窗拉长的选项。
        /// </summary>
        public const int MinKeepMonths = 1;
        public const int MaxKeepMonths = 12;

        /// <summary>历史文件与 exe 同目录。</summary>
        public static string DefaultPath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "net_history.json");
                }
                catch { }
                return "net_history.json";
            }
        }

        /// <summary>把保留月数夹到合法区间。</summary>
        public static int NormalizeKeep(int months)
            => months < MinKeepMonths ? MinKeepMonths : (months > MaxKeepMonths ? MaxKeepMonths : months);

        /// <summary>
        /// 读取历史。任何异常都退化为空列表（调用方不需要 try/catch）。返回按月份升序、
        /// 并已按 <paramref name="keepMonths"/> 裁剪过。
        /// </summary>
        public static List<NetMonthRecord> Load(string path, int keepMonths)
        {
            var months = new List<NetMonthRecord>();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var ser = new DataContractJsonSerializer(typeof(NetHistoryFile));
                        var file = ser.ReadObject(stream) as NetHistoryFile;
                        if (file != null && file.Months != null)
                        {
                            foreach (var m in file.Months)
                            {
                                if (m == null || string.IsNullOrEmpty(m.Period)) continue;
                                months.Add(m);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"流量历史读取失败（当作空历史继续）: {ex.GetType().Name} {ex.Message}");
                months.Clear();
            }
            return Trim(months, keepMonths);
        }

        /// <summary>写回历史：先排序裁剪，再原子替换。失败只记日志，不影响调用方。</summary>
        public static void Save(string path, IEnumerable<NetMonthRecord> months, int keepMonths)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var file = new NetHistoryFile { Months = Trim(months, keepMonths) };
                string tmp = path + ".tmp";
                using (var stream = File.Create(tmp))
                {
                    var ser = new DataContractJsonSerializer(typeof(NetHistoryFile));
                    ser.WriteObject(stream, file);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"流量历史写入失败: {ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>
        /// 排序 + 按保留月数裁剪（保留最近 N 个），返回新列表。同月份重复项只留最后一条
        /// （月份是 "yyyy-MM" 定长字符串，用 Ordinal 比较即可当成时间序）。
        /// </summary>
        public static List<NetMonthRecord> Trim(IEnumerable<NetMonthRecord> months, int keepMonths)
        {
            var result = new List<NetMonthRecord>();
            if (months == null) return result;

            var byPeriod = new Dictionary<string, NetMonthRecord>(StringComparer.Ordinal);
            foreach (var m in months)
            {
                if (m == null || string.IsNullOrEmpty(m.Period)) continue;
                byPeriod[m.Period] = m;   // 后来的覆盖先来的
            }
            var periods = new List<string>(byPeriod.Keys);
            periods.Sort(StringComparer.Ordinal);

            int keep = NormalizeKeep(keepMonths);
            int start = periods.Count > keep ? periods.Count - keep : 0;
            for (int i = start; i < periods.Count; i++) result.Add(byPeriod[periods[i]]);
            return result;
        }

        /// <summary>
        /// 插入或更新某个月的数值（就地修改并返回同一个列表引用）。用于「本月的数字随采样
        /// 一直变」这个场景：每 30 秒落盘时把当前月的条目覆盖成本地最新值。
        /// </summary>
        public static List<NetMonthRecord> Upsert(List<NetMonthRecord> months, string period, long up, long down)
        {
            if (months == null) months = new List<NetMonthRecord>();
            if (string.IsNullOrEmpty(period)) return months;

            NetMonthRecord hit = null;
            foreach (var m in months)
            {
                if (m != null && string.Equals(m.Period, period, StringComparison.Ordinal)) { hit = m; break; }
            }
            if (hit == null)
            {
                hit = new NetMonthRecord { Period = period };
                months.Add(hit);
            }
            hit.Up = Math.Max(0, up);
            hit.Down = Math.Max(0, down);
            return months;
        }
    }
}
