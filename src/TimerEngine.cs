using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace Orbit
{
    [DataContract]
    public enum OrbitTaskStatus
    {
        [EnumMember] Pending,
        [EnumMember] Running,
        [EnumMember] Paused,
        [EnumMember] Completed
    }

    [DataContract]
    public sealed class OrbitTask
    {
        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public double DurationSeconds { get; set; }
        [DataMember(Order = 3)] public double RemainingSeconds { get; set; }
        [DataMember(Order = 4)] public DateTimeOffset? DueAtUtc { get; set; }
        [DataMember(Order = 5)] public DateTimeOffset? CompletedAtUtc { get; set; }
        [DataMember(Order = 6)] public OrbitTaskStatus Status { get; set; }
        [DataMember(Order = 7)] public bool IsChecklist { get; set; }
    }

    [DataContract]
    public sealed class OrbitState
    {
        public OrbitState()
        {
            Tasks = new List<OrbitTask>();
            DailyTaskNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        [DataMember(Order = 0)] public List<OrbitTask> Tasks { get; set; }
        [DataMember(Order = 1)] public string ActiveTaskId { get; set; }
        [DataMember(Order = 2)] public string AlertTaskId { get; set; }
        [DataMember(Order = 3)] public double? DotLeft { get; set; }
        [DataMember(Order = 4)] public double? DotTop { get; set; }
        [DataMember(Order = 5)] public Dictionary<string, int> DailyTaskNumbers { get; set; }
        // null means this installation has not yet asked the user about startup.
        // Once chosen, the preference remains stable across launches.
        [DataMember(Order = 6)] public bool? StartWithWindows { get; set; }
        [IgnoreDataMember] public bool WasRepaired { get; set; }
    }

    /// <summary>
    /// One running task, with explicit UTC deadlines. The UI may tick at any rate;
    /// time spent asleep or between frames is still counted correctly.
    /// </summary>
    public sealed class TimerEngine
    {
        public const double MaximumDurationSeconds = 24 * 60 * 60;
        private static readonly Regex NumberPattern = new Regex(@"^(?:\d+(?:\.\d+)?|\.\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex DefaultNamePattern = new Regex(@"^(?<day>\d{4}-\d{2}-\d{2}) · 任务 (?<number>[1-9]\d*)$", RegexOptions.CultureInvariant);
        private static readonly Regex UnitPattern = new Regex(
            @"(?<number>(?:\d+(?:\.\d+)?|\.\d+))\s*(?<unit>hours?|hrs?|h|小时|时|minutes?|mins?|m|分钟|分|seconds?|secs?|s|秒钟|秒)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public TimerEngine(OrbitState state)
        {
            State = state ?? new OrbitState();
            RepairState();
        }

        public OrbitState State { get; private set; }
        public OrbitTask ActiveTask { get { return Find(State.ActiveTaskId); } }
        public OrbitTask AlertTask { get { return Find(State.AlertTaskId); } }

        public static bool TryParseDuration(string input, out double seconds, out string error)
        {
            seconds = 0;
            error = null;
            string value = (input ?? string.Empty).Trim().Replace('：', ':');
            if (value.Length == 0)
            {
                error = "请输入时长，例如 25 分钟或 10s。";
                return false;
            }

            double total = 0;
            if (NumberPattern.IsMatch(value))
            {
                double minutes;
                if (!TryNumber(value, out minutes)) return InvalidDuration(out seconds, out error);
                total = minutes * 60;
            }
            else if (Regex.IsMatch(value, @"^\d+(?::\d{1,2}){1,2}$", RegexOptions.CultureInvariant))
            {
                string[] parts = value.Split(':');
                double first;
                if (!TryNumber(parts[0], out first)) return InvalidDuration(out seconds, out error);
                int last = int.Parse(parts[parts.Length - 1], CultureInfo.InvariantCulture);
                if (last >= 60) return InvalidDuration(out seconds, out error);
                if (parts.Length == 2)
                {
                    total = first * 60 + last;
                }
                else
                {
                    int middle = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (middle >= 60) return InvalidDuration(out seconds, out error);
                    total = first * 3600 + middle * 60 + last;
                }
            }
            else
            {
                MatchCollection matches = UnitPattern.Matches(value);
                int previousEnd = 0;
                if (matches.Count == 0) return InvalidDuration(out seconds, out error);
                foreach (Match match in matches)
                {
                    if (value.Substring(previousEnd, match.Index - previousEnd).Trim().Length != 0)
                        return InvalidDuration(out seconds, out error);
                    double amount;
                    if (!TryNumber(match.Groups["number"].Value, out amount))
                        return InvalidDuration(out seconds, out error);
                    string unit = match.Groups["unit"].Value.ToLowerInvariant();
                    double multiplier = unit.StartsWith("h", StringComparison.Ordinal) || unit == "小时" || unit == "时"
                        ? 3600 : unit.StartsWith("m", StringComparison.Ordinal) || unit == "分钟" || unit == "分" ? 60 : 1;
                    total += amount * multiplier;
                    previousEnd = match.Index + match.Length;
                }
                if (value.Substring(previousEnd).Trim().Length != 0)
                    return InvalidDuration(out seconds, out error);
            }

            if (!IsValidDuration(total))
            {
                error = "时长需要在 1 秒到 24 小时之间。";
                return false;
            }
            seconds = total;
            return true;
        }

        public OrbitTask AddTask(string name, double seconds, bool start, DateTimeOffset now)
        {
            string trimmedName = (name ?? string.Empty).Trim();
            bool checklist = seconds == 0;
            if (!checklist && !IsValidDuration(seconds)) throw new ArgumentException("时长需要在 1 秒到 24 小时之间；无时长任务请留空。", "seconds");
            if (checklist && start) throw new InvalidOperationException("无时长任务只能勾选完成，不能开始计时。 ");
            if (start)
            {
                Tick(now);
                RequireAcknowledgedAlert();
                // Validate the deadline before adding a new task to the collection.
                now.ToUniversalTime().AddSeconds(seconds);
            }
            if (trimmedName.Length == 0) trimmedName = AllocateDefaultName(now);

            OrbitTask task = new OrbitTask
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmedName,
                DurationSeconds = seconds,
                RemainingSeconds = seconds,
                Status = OrbitTaskStatus.Pending,
                IsChecklist = checklist
            };
            State.Tasks.Add(task);
            ObserveTaskNumber(task.Name);
            if (start) Start(task.Id, now);
            return task;
        }

        /// <returns>True when the task changes status, rather than on every time update.</returns>
        public bool Tick(DateTimeOffset now)
        {
            OrbitTask current = ActiveTask;
            if (current == null || current.Status != OrbitTaskStatus.Running) return false;
            if (!current.DueAtUtc.HasValue)
            {
                current.Status = OrbitTaskStatus.Paused;
                return true;
            }

            current.RemainingSeconds = Math.Max(0, Math.Min(current.DurationSeconds, (current.DueAtUtc.Value - now).TotalSeconds));
            if (current.RemainingSeconds > 0) return false;
            Finish(current, current.DueAtUtc.Value);
            return true;
        }

        public void Start(string id, DateTimeOffset now)
        {
            OrbitTask task = Find(id);
            if (task == null) throw new ArgumentException("找不到这项任务。", "id");
            if (task.IsChecklist) throw new InvalidOperationException("无时长任务只能勾选完成，不能开始计时。 ");
            if (task.Status == OrbitTaskStatus.Completed) throw new InvalidOperationException("这项任务已完成，请创建新任务。 ");
            Tick(now);
            RequireAcknowledgedAlert();

            OrbitTask current = ActiveTask;
            if (current == task && task.Status == OrbitTaskStatus.Running) return;
            double remaining = task.Status == OrbitTaskStatus.Pending ? task.DurationSeconds : task.RemainingSeconds;
            DateTimeOffset deadline = now.ToUniversalTime().AddSeconds(remaining);
            if (current != null && current.Status == OrbitTaskStatus.Running)
            {
                current.Status = OrbitTaskStatus.Paused;
                current.DueAtUtc = null;
            }
            task.RemainingSeconds = remaining;
            task.Status = OrbitTaskStatus.Running;
            task.DueAtUtc = deadline;
            task.CompletedAtUtc = null;
            State.ActiveTaskId = task.Id;
        }

        public void Pause(DateTimeOffset now)
        {
            Tick(now);
            OrbitTask current = ActiveTask;
            if (current == null || current.Status != OrbitTaskStatus.Running) return;
            current.Status = OrbitTaskStatus.Paused;
            current.DueAtUtc = null;
        }

        public void Resume(DateTimeOffset now)
        {
            OrbitTask current = ActiveTask;
            if (current == null) return;
            Start(current.Id, now);
        }

        public void Delete(string id)
        {
            OrbitTask task = Find(id);
            if (task == null) return;
            State.Tasks.Remove(task);
            if (State.ActiveTaskId == id) State.ActiveTaskId = null;
            if (State.AlertTaskId == id) State.AlertTaskId = null;
        }

        public void ToggleChecklist(string id, DateTimeOffset now)
        {
            OrbitTask task = Find(id);
            if (task == null) throw new ArgumentException("找不到这项任务。", "id");
            if (!task.IsChecklist) throw new InvalidOperationException("计时任务请使用计时控制。 ");
            if (task.Status != OrbitTaskStatus.Pending && task.Status != OrbitTaskStatus.Completed)
                throw new InvalidOperationException("这项清单任务的状态不正确。 ");
            bool complete = task.Status == OrbitTaskStatus.Pending;
            task.Status = complete ? OrbitTaskStatus.Completed : OrbitTaskStatus.Pending;
            task.CompletedAtUtc = complete ? (DateTimeOffset?)now.ToUniversalTime() : null;
            // Checklist changes never affect another task's timer or pending alarm.
        }

        public void ReorderTask(string movedId, string targetId, bool after)
        {
            OrbitTask moved = Find(movedId);
            OrbitTask target = Find(targetId);
            if (moved == null || target == null || moved == target) return;
            State.Tasks.Remove(moved);
            int targetIndex = State.Tasks.IndexOf(target);
            State.Tasks.Insert(targetIndex + (after ? 1 : 0), moved);
        }

        public void Acknowledge()
        {
            State.AlertTaskId = null;
        }

        public void Restart(DateTimeOffset now)
        {
            Tick(now);
            RequireAcknowledgedAlert();
            OrbitTask current = ActiveTask;
            if (current == null) return;
            DateTimeOffset deadline = now.ToUniversalTime().AddSeconds(current.DurationSeconds);
            current.RemainingSeconds = current.DurationSeconds;
            current.DueAtUtc = deadline;
            current.CompletedAtUtc = null;
            current.Status = OrbitTaskStatus.Running;
        }

        public void Complete(DateTimeOffset now)
        {
            Tick(now);
            // An already expired task is already complete; leave its alert alone.
            OrbitTask current = ActiveTask;
            if (current == null) return;
            RequireAcknowledgedAlert();
            Finish(current, now.ToUniversalTime());
        }

        private void Finish(OrbitTask task, DateTimeOffset completedAt)
        {
            task.Status = OrbitTaskStatus.Completed;
            task.RemainingSeconds = 0;
            task.DueAtUtc = null;
            task.CompletedAtUtc = completedAt.ToUniversalTime();
            State.ActiveTaskId = null;
            State.AlertTaskId = task.Id;
        }

        private void RequireAcknowledgedAlert()
        {
            if (AlertTask != null) throw new InvalidOperationException("请先确认上一项任务的完成提醒。 ");
        }

        private OrbitTask Find(string id)
        {
            return string.IsNullOrEmpty(id) ? null : State.Tasks.FirstOrDefault(task => task.Id == id);
        }

        private string AllocateDefaultName(DateTimeOffset now)
        {
            string day = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int previous;
            State.DailyTaskNumbers.TryGetValue(day, out previous);
            if (previous == int.MaxValue) throw new InvalidOperationException("今天的任务编号已达到上限，请手动填写任务名称。 ");
            int next = previous + 1;
            State.DailyTaskNumbers[day] = next;
            return day + " · 任务 " + next.ToString(CultureInfo.InvariantCulture);
        }

        private bool ObserveTaskNumber(string name)
        {
            Match match = DefaultNamePattern.Match((name ?? string.Empty).Trim());
            int number;
            DateTime day;
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                !DateTime.TryParseExact(match.Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return false;
            string key = match.Groups["day"].Value;
            int previous;
            if (State.DailyTaskNumbers.TryGetValue(key, out previous) && previous >= number) return false;
            State.DailyTaskNumbers[key] = number;
            return true;
        }

        private void RepairState()
        {
            bool repaired = false;
            if (State.Tasks == null)
            {
                State.Tasks = new List<OrbitTask>();
                repaired = true;
            }
            if (State.Tasks.Any(task => task == null))
            {
                State.Tasks = State.Tasks.Where(task => task != null).ToList();
                repaired = true;
            }
            // This dictionary did not exist in the first release. Its absence is
            // a normal migration, and existing default names seed their high-water marks.
            bool legacyNumbers = State.DailyTaskNumbers == null;
            if (legacyNumbers) State.DailyTaskNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in State.DailyTaskNumbers.Keys.ToList())
            {
                DateTime day;
                if (State.DailyTaskNumbers[key] < 0 || !DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                {
                    State.DailyTaskNumbers.Remove(key);
                    repaired = true;
                }
            }
            foreach (OrbitTask task in State.Tasks)
            {
                if (ObserveTaskNumber(task.Name) && !legacyNumbers) repaired = true;
            }
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (OrbitTask task in State.Tasks)
            {
                if (task.IsChecklist)
                {
                    if (task.DurationSeconds != 0 || task.RemainingSeconds != 0 || task.DueAtUtc.HasValue) repaired = true;
                    task.DurationSeconds = 0;
                    task.RemainingSeconds = 0;
                    task.DueAtUtc = null;
                    if (task.Status != OrbitTaskStatus.Pending && task.Status != OrbitTaskStatus.Completed)
                    {
                        task.Status = OrbitTaskStatus.Pending;
                        repaired = true;
                    }
                }
                else if (!IsValidDuration(task.DurationSeconds))
                {
                    // Preserve the record even when one numeric field is damaged.
                    // A repaired running task requires the user to resume it.
                    task.DurationSeconds = IsValidDuration(task.RemainingSeconds) ? task.RemainingSeconds : 25 * 60;
                    if (task.Status == OrbitTaskStatus.Running)
                    {
                        task.Status = OrbitTaskStatus.Paused;
                        task.DueAtUtc = null;
                    }
                    repaired = true;
                }
                if (string.IsNullOrEmpty(task.Id) || !ids.Add(task.Id))
                {
                    task.Id = Guid.NewGuid().ToString("N");
                    ids.Add(task.Id);
                    repaired = true;
                }
                string name = string.IsNullOrWhiteSpace(task.Name) ? "未命名任务" : task.Name.Trim();
                if (name != task.Name) { task.Name = name; repaired = true; }
                if (!Enum.IsDefined(typeof(OrbitTaskStatus), task.Status))
                {
                    task.Status = OrbitTaskStatus.Pending;
                    repaired = true;
                }
                if (!IsFinite(task.RemainingSeconds) || task.RemainingSeconds < 0 || task.RemainingSeconds > task.DurationSeconds)
                {
                    task.RemainingSeconds = task.DurationSeconds;
                    repaired = true;
                }
                if (task.Status == OrbitTaskStatus.Completed)
                {
                    if (task.RemainingSeconds != 0 || task.DueAtUtc.HasValue) repaired = true;
                    task.RemainingSeconds = 0;
                    task.DueAtUtc = null;
                }
                else
                {
                    if (task.CompletedAtUtc.HasValue) repaired = true;
                    task.CompletedAtUtc = null;
                    if (task.Status == OrbitTaskStatus.Pending)
                    {
                        if (task.RemainingSeconds != task.DurationSeconds || task.DueAtUtc.HasValue) repaired = true;
                        task.RemainingSeconds = task.DurationSeconds;
                        task.DueAtUtc = null;
                    }
                    else if (task.Status == OrbitTaskStatus.Paused || !task.DueAtUtc.HasValue)
                    {
                        if (task.Status != OrbitTaskStatus.Paused || task.DueAtUtc.HasValue || task.RemainingSeconds == 0) repaired = true;
                        task.Status = OrbitTaskStatus.Paused;
                        task.DueAtUtc = null;
                        if (task.RemainingSeconds == 0) task.RemainingSeconds = task.DurationSeconds;
                    }
                }
            }

            OrbitTask active = ActiveTask;
            if (active == null || active.IsChecklist || (active.Status != OrbitTaskStatus.Running && active.Status != OrbitTaskStatus.Paused))
            {
                active = State.Tasks.FirstOrDefault(task => !task.IsChecklist && task.Status == OrbitTaskStatus.Running);
                string activeId = active == null ? null : active.Id;
                if (State.ActiveTaskId != activeId) repaired = true;
                State.ActiveTaskId = activeId;
            }
            OrbitTask alert = AlertTask;
            if (alert == null || alert.IsChecklist || alert.Status != OrbitTaskStatus.Completed)
            {
                if (State.AlertTaskId != null) repaired = true;
                State.AlertTaskId = null;
            }
            foreach (OrbitTask task in State.Tasks)
            {
                if (task.Status == OrbitTaskStatus.Running && (task != active || AlertTask != null))
                {
                    repaired = true;
                    task.Status = OrbitTaskStatus.Paused;
                    task.DueAtUtc = null;
                    if (task.RemainingSeconds == 0) task.RemainingSeconds = task.DurationSeconds;
                }
            }
            if (State.DotLeft.HasValue && !IsFinite(State.DotLeft.Value)) { State.DotLeft = null; repaired = true; }
            if (State.DotTop.HasValue && !IsFinite(State.DotTop.Value)) { State.DotTop = null; repaired = true; }
            State.WasRepaired = repaired;
        }

        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) && IsFinite(number);
        }

        private static bool IsFinite(double number)
        {
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private static bool IsValidDuration(double seconds)
        {
            return IsFinite(seconds) && seconds >= 1 && seconds <= MaximumDurationSeconds;
        }

        private static bool InvalidDuration(out double seconds, out string error)
        {
            seconds = 0;
            error = "时长格式不正确。可输入 25、25m、10s、1h 30m 或 25:00。";
            return false;
        }
    }
}
