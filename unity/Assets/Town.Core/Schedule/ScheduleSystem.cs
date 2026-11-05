using System;
using System.Collections.Generic;
using System.Linq;
using Town.Core.World;

namespace Town.Core.Schedule
{
    public sealed class ScheduleEntry
    {
        public int StartMinute;   // 当天第几分钟开始
        public string LocationId;
        public string Activity;
        /// <summary>忙碌时段（例如午休）：请人帮忙会被拒绝。</summary>
        public bool Busy;

        public ScheduleEntry(int hour, int minute, string locationId, string activity, bool busy = false)
        {
            StartMinute = hour * 60 + minute;
            LocationId = locationId;
            Activity = activity;
            Busy = busy;
        }
    }

    /// <summary>一位居民一天的日程。每天循环；集市日只有一天，所以够用。</summary>
    public sealed class NpcSchedule
    {
        public string NpcId { get; }
        public IReadOnlyList<ScheduleEntry> Entries { get; }

        public NpcSchedule(string npcId, params ScheduleEntry[] entries)
        {
            NpcId = npcId;
            Entries = entries.OrderBy(e => e.StartMinute).ToArray();
            if (Entries.Count == 0 || Entries[0].StartMinute != 0) throw new ArgumentException("日程必须从 0:00 开始。");
        }

        public ScheduleEntry At(GameTime t)
        {
            var current = Entries[0];
            foreach (var e in Entries)
            {
                if (e.StartMinute <= t.MinuteOfDay) current = e;
                else break;
            }
            return current;
        }
    }

    /// <summary>地点之间的步行时间（游戏分钟）。与灰盒场景距离大致对应：现实 2 秒 = 游戏 1 分钟。</summary>
    public static class TravelTimes
    {
        public static int Minutes(string from, string to)
        {
            if (from == null || to == null || from == to) return from == to ? 0 : 4;
            return from == Ids.Loc.Plaza || to == Ids.Loc.Plaza ? 3 : 5;
        }
    }

    /// <summary>
    /// 居民移动由 Core 按游戏时间判定（出发、到达），Unity 只负责把人走过去。
    /// 这样无画面测试与实际游玩的到达时刻一致，可复现。
    /// 被任务接管的居民暂停日程，任务结束后自动回到日程。
    /// </summary>
    public sealed class ScheduleSystem
    {
        private readonly Dictionary<string, NpcSchedule> _schedules = new Dictionary<string, NpcSchedule>();
        private readonly Dictionary<string, string> _taskOwner = new Dictionary<string, string>();

        /// <summary>居民到达某地（npcId, locationId）。</summary>
        public event Action<string, string> Arrived;

        public void Add(NpcSchedule schedule) => _schedules[schedule.NpcId] = schedule;
        public NpcSchedule Get(string npcId) => _schedules.TryGetValue(npcId, out var s) ? s : null;

        public bool IsTaskControlled(string npcId) => _taskOwner.ContainsKey(npcId);

        public void TakeControl(string npcId, string taskId) => _taskOwner[npcId] = taskId;
        public void ReleaseControl(string npcId) => _taskOwner.Remove(npcId);

        /// <summary>日程上的忙碌时段，或正被任务占用。</summary>
        public bool IsBusy(string npcId, GameTime now)
        {
            if (IsTaskControlled(npcId)) return true;
            var s = Get(npcId);
            return s != null && s.At(now).Busy;
        }

        public void StartTravel(WorldState world, string npcId, string target, string activity)
        {
            var npc = world.Npcs[npcId];
            if (npc.LocationId == target && !npc.IsTravelling)
            {
                if (npc.Activity != activity) world.Commit($"npc:activity:{npcId}", _ => npc.Activity = activity);
                return;
            }
            if (npc.TravelTo == target) return;
            string from = npc.LocationId ?? npc.TravelTo;
            int minutes = Math.Max(1, TravelTimes.Minutes(from, target));
            world.Commit($"npc:depart:{npcId}:{target}", w =>
            {
                npc.LocationId = null;
                npc.TravelTo = target;
                npc.ArriveAt = w.Now.AddMinutes(minutes);
                npc.Activity = activity;
            });
        }

        public void Tick(WorldState world)
        {
            // 先处理到达，再按日程决定下一步；按 ID 排序保证结果可复现。
            foreach (var npc in world.Npcs.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList())
            {
                if (npc.IsTravelling && npc.ArriveAt.HasValue && world.Now >= npc.ArriveAt.Value)
                {
                    string loc = npc.TravelTo;
                    world.Commit($"npc:arrive:{npc.Id}:{loc}", _ =>
                    {
                        npc.LocationId = loc;
                        npc.TravelTo = null;
                        npc.ArriveAt = null;
                    });
                    Arrived?.Invoke(npc.Id, loc);
                }
            }

            foreach (var npc in world.Npcs.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList())
            {
                if (npc.IsTravelling || IsTaskControlled(npc.Id) || npc.CurrentTaskId != null) continue;
                var schedule = Get(npc.Id);
                if (schedule == null) continue;
                var entry = schedule.At(world.Now);
                if (npc.LocationId != entry.LocationId) StartTravel(world, npc.Id, entry.LocationId, entry.Activity);
                else if (npc.Activity != entry.Activity) world.Commit($"npc:activity:{npc.Id}", _ => npc.Activity = entry.Activity);
            }
        }
    }
}
