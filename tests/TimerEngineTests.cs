using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using Orbit;

internal static class TimerEngineTests
{
    private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);
    private static int failures;

    private static int Main()
    {
        Run("duration formats and limits", DurationFormats);
        Run("deadline timing and acknowledgement", DeadlineTiming);
        Run("pause/resume and task switching", PauseResumeAndSwitch);
        Run("pending, delete, restart and early completion", TaskOperations);
        Run("unacknowledged alerts cannot be overwritten", AlertProtection);
        Run("serialized recovery and overdue completion", SerializationRecovery);
        Run("corrupt state repair", CorruptStateRepair);
        Run("damaged durations preserve task records", DamagedDurationRecovery);
        Run("automatic names persist across dates and deletion", AutomaticTaskNames);
        Run("legacy naming counters migrate safely", LegacyNamingCounters);
        Run("checklist completion is silent and reversible", ChecklistTasks);
        Run("checklist recovery preserves zero durations", ChecklistRecovery);
        Run("reordering preserves active timers and task identity", ReorderPreservesTasks);
        Run("invalid additions do not mutate state", InvalidAdditions);
        Run("editing and restarting timed tasks", EditingAndRestarting);
        Run("manual completion is silent for every unfinished task state", ManualCompletionStates);
        Run("manual completion preserves other timers and pending alerts", ManualCompletionIsolation);
        Run("clearing completed tasks preserves active work and daily numbering", ClearCompletedTasks);
        Run("restoring completed tasks preserves identity and appends pending work", RestoreCompletedTasks);
        Console.WriteLine(failures == 0 ? "All 19 timer test groups passed." : failures + " timer test group(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + ": " + exception); }
    }

    private static void DurationFormats()
    {
        Dictionary<string, double> valid = new Dictionary<string, double>
        {
            { "25", 1500 }, { " 25m ", 1500 }, { "10s", 10 }, { "1h 30m", 5400 },
            { "1小时30分钟", 5400 }, { "20秒", 20 }, { "2分5秒钟", 125 }, { "25:00", 1500 },
            { "1:02:03", 3723 }, { "00：10", 10 }, { ".5", 30 }, { "1.5h", 5400 },
            { "24h", 86400 }, { "1 second", 1 }, { "1 HR 30 MIN", 5400 }, { "0.1m", 6 }
        };
        foreach (KeyValuePair<string, double> pair in valid)
        {
            double parsed;
            string error;
            Assert(TimerEngine.TryParseDuration(pair.Key, out parsed, out error), "Rejected " + pair.Key + ": " + error);
            Equal(pair.Value, parsed, pair.Key);
            Assert(error == null, "Valid format should not report an error.");
        }
        string[] invalid = { null, "", " ", "0", "-1", "1m -2s", "-1m", "-1:00", "25hours", "0s", "0.5s", "24h1s", "1:60", "1:60:00", "1e3", "NaN", "Infinity", "1m rubbish", "1month", "1m+2s", "1,5m", "1:00:00:00", new string('9', 400) };
        foreach (string input in invalid)
        {
            double parsed;
            string error;
            Assert(!TimerEngine.TryParseDuration(input, out parsed, out error), "Accepted invalid " + input);
            Assert(parsed == 0 && !string.IsNullOrWhiteSpace(error), "Invalid parse must provide an error and zero duration.");
        }
    }

    private static void DeadlineTiming()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask task = engine.AddTask(" Write ", 90, true, Epoch);
        Assert(task.Name == "Write", "Names should be trimmed.");
        Assert(!engine.Tick(Epoch.AddSeconds(37.25)), "Normal tick is not a structural change.");
        Equal(52.75, task.RemainingSeconds, "Sparse tick uses absolute deadline.");
        Assert(engine.Tick(Epoch.AddHours(1)), "Wake after deadline should complete.");
        Assert(task.Status == OrbitTaskStatus.Completed && engine.ActiveTask == null, "Completion should clear active task.");
        Assert(engine.AlertTask == task, "Completion should alert once.");
        Assert(task.CompletedAtUtc == Epoch.AddSeconds(90), "Overdue completion records the actual deadline.");
        Assert(!engine.Tick(Epoch.AddHours(2)), "Completed task must not complete a second time.");
        engine.Acknowledge();
        Assert(!engine.Tick(Epoch.AddHours(3)) && engine.AlertTask == null, "Acknowledged alarm must stay stopped.");
        Assert(engine.State.Tasks.Count == 1, "Acknowledging should preserve completed history.");
    }

    private static void PauseResumeAndSwitch()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask first = engine.AddTask("First", 60, true, Epoch);
        engine.Pause(Epoch.AddSeconds(12.5));
        Equal(47.5, first.RemainingSeconds, "Pause should capture exact remaining time.");
        engine.Tick(Epoch.AddDays(1));
        Equal(47.5, first.RemainingSeconds, "Paused time must not elapse.");
        engine.Resume(Epoch.AddDays(1));
        engine.Tick(Epoch.AddDays(1).AddSeconds(7.5));
        Equal(40, first.RemainingSeconds, "Resume should set a fresh deadline.");
        OrbitTask second = engine.AddTask("Second", 120, true, Epoch.AddDays(1).AddSeconds(10));
        Equal(37.5, first.RemainingSeconds, "Switch must update old task before pausing.");
        Assert(first.Status == OrbitTaskStatus.Paused && second.Status == OrbitTaskStatus.Running, "Switch should pause the old task.");
        engine.Start(first.Id, Epoch.AddDays(1).AddSeconds(20));
        Equal(110, second.RemainingSeconds, "Switch back preserves second remaining time.");
        Equal(37.5, first.RemainingSeconds, "Switch back preserves first remaining time.");
        Assert(engine.State.Tasks.Count(task => task.Status == OrbitTaskStatus.Running) == 1, "Only one timer can run.");
        DateTimeOffset? due = first.DueAtUtc;
        engine.Start(first.Id, Epoch.AddDays(1).AddSeconds(22));
        Assert(first.DueAtUtc == due, "Starting the already active task must not reset its deadline.");
    }

    private static void TaskOperations()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask pending = engine.AddTask("Pending", 60, false, Epoch);
        Assert(engine.ActiveTask == null && pending.Status == OrbitTaskStatus.Pending, "Queued task must not run.");
        engine.Start(pending.Id, Epoch);
        engine.Tick(Epoch.AddSeconds(10));
        engine.Restart(Epoch.AddSeconds(10));
        Equal(60, pending.RemainingSeconds, "Restart restores duration.");
        Assert(pending.DueAtUtc == Epoch.AddSeconds(70), "Restart resets deadline.");
        engine.Complete(Epoch.AddSeconds(15));
        Assert(engine.AlertTask == pending && pending.CompletedAtUtc == Epoch.AddSeconds(15), "Early completion should alert at completion time.");
        engine.Delete(pending.Id);
        Assert(engine.AlertTask == null && engine.State.Tasks.Count == 0, "Deleting alerted task clears stale alert.");
        OrbitTask active = engine.AddTask("Delete", 30, true, Epoch);
        engine.Delete(active.Id);
        Assert(engine.ActiveTask == null && !engine.Tick(Epoch.AddHours(1)), "Deleting active task cancels its timer.");
        engine.Delete("missing");
        engine.Pause(Epoch);
        engine.Resume(Epoch);
        engine.Complete(Epoch);
    }

    private static void EditingAndRestarting()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask task = engine.AddTask("Draft", 120, true, Epoch);
        engine.Tick(Epoch.AddSeconds(30));
        engine.EditTask(task.Id, "Revised", 60, Epoch.AddSeconds(30));
        Assert(task.Name == "Revised" && task.Status == OrbitTaskStatus.Running, "Editing should keep an active task running.");
        Equal(45, task.RemainingSeconds, "Editing duration keeps the current completion ratio.");
        Assert(task.DueAtUtc == Epoch.AddSeconds(75), "Editing an active task moves its deadline from now.");
        engine.Pause(Epoch.AddSeconds(35));
        engine.EditTask(task.Id, "Revised again", 100, Epoch.AddSeconds(35));
        Assert(task.Status == OrbitTaskStatus.Paused && task.Name == "Revised again", "Editing a paused task should not restart it.");
        Equal(100.0 * 40.0 / 60.0, task.RemainingSeconds, "Paused task editing keeps its current completion ratio.");
        engine.Restart(task.Id, Epoch.AddSeconds(40));
        Assert(task.Status == OrbitTaskStatus.Running && engine.ActiveTask == task, "Restarting a task makes it active.");
        Equal(100, task.RemainingSeconds, "Restarting restores the edited full duration.");
        Assert(task.DueAtUtc == Epoch.AddSeconds(140), "Restarting sets a new deadline from now.");
        Expect<ArgumentException>(() => engine.EditTask(task.Id, "", 100, Epoch.AddSeconds(40)));
        Expect<ArgumentException>(() => engine.EditTask(task.Id, "Still revised", 0, Epoch.AddSeconds(40)));
    }

    private static void ManualCompletionStates()
    {
        foreach (string initial in new[] { "pending", "running", "paused", "checklist" })
        {
            TimerEngine engine = new TimerEngine(null);
            bool checklist = initial == "checklist";
            OrbitTask task = engine.AddTask(initial, checklist ? 0 : 60, initial == "running" || initial == "paused", Epoch);
            if (initial == "paused") engine.Pause(Epoch.AddSeconds(5));
            DateTimeOffset completedAt = Epoch.AddSeconds(20).ToOffset(TimeSpan.FromHours(5.5));
            engine.MarkCompleted(task.Id, completedAt);
            Assert(task.Status == OrbitTaskStatus.Completed && task.CompletedAtUtc.HasValue &&
                task.CompletedAtUtc.Value == completedAt && task.CompletedAtUtc.Value.Offset == TimeSpan.Zero,
                "Manual completion records a UTC completion time for " + initial + ".");
            Assert(task.RemainingSeconds == 0 && task.DueAtUtc == null && engine.ActiveTask == null && engine.AlertTask == null,
                "Manual completion stops the target without creating an alert for " + initial + ".");
            Assert(task.Name == initial && task.DurationSeconds == (checklist ? 0 : 60) && task.IsChecklist == checklist,
                "Manual completion retains task identity, original duration and kind.");
            string snapshot = TaskSnapshot(task);
            engine.MarkCompleted(task.Id, Epoch.AddHours(1));
            Assert(TaskSnapshot(task) == snapshot, "Repeated completion must preserve the original completed record.");
            Assert(!engine.Tick(Epoch.AddHours(2)) && engine.AlertTask == null, "A manually completed task must never ring on a later tick.");
            TimerEngine restored = RoundTrip(engine);
            Assert(!restored.State.WasRepaired && restored.AlertTask == null && TaskSnapshot(restored.State.Tasks.Single()) == snapshot,
                "Manual completion must survive serialization without repair or a new alarm.");
        }

        TimerEngine overdue = new TimerEngine(null);
        OrbitTask late = overdue.AddTask("Unticked deadline", 1, true, Epoch);
        overdue.MarkCompleted(late.Id, Epoch.AddSeconds(5));
        Assert(overdue.AlertTask == null && late.CompletedAtUtc == Epoch.AddSeconds(5),
            "A manual checkmark before the next tick remains silent even when the deadline has just passed.");
    }

    private static void ManualCompletionIsolation()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask paused = engine.AddTask("Paused", 60, true, Epoch);
        OrbitTask active = engine.AddTask("Active", 120, true, Epoch.AddSeconds(10));
        OrbitTask pending = engine.AddTask("Pending", 90, false, Epoch);
        OrbitTask checklist = engine.AddTask("Checklist", 0, false, Epoch);
        string activeSnapshot = TaskSnapshot(active);
        foreach (OrbitTask target in new[] { paused, pending, checklist })
        {
            engine.MarkCompleted(target.Id, Epoch.AddSeconds(200));
            Assert(engine.ActiveTask == active && TaskSnapshot(active) == activeSnapshot && engine.AlertTask == null,
                "Completing another task must not tick, pause, restart or clear the active timer.");
        }
        Assert(engine.Tick(Epoch.AddSeconds(200)) && engine.AlertTask == active,
            "The untouched active timer must still expire through its normal ringing path.");
        string alertSnapshot = TaskSnapshot(active);
        OrbitTask next = engine.AddTask("Finish while alarm waits", 30, false, Epoch);
        engine.MarkCompleted(next.Id, Epoch.AddSeconds(210));
        engine.MarkCompleted(active.Id, Epoch.AddSeconds(220));
        Assert(engine.AlertTask == active && TaskSnapshot(active) == alertSnapshot,
            "Manual completion must preserve an existing reminder and its original completion time.");
        int count = engine.State.Tasks.Count;
        Expect<ArgumentException>(() => engine.MarkCompleted("missing", Epoch));
        Expect<ArgumentException>(() => engine.MarkCompleted(null, Epoch));
        Assert(engine.State.Tasks.Count == count && engine.AlertTask == active,
            "An unknown completion target must not mutate tasks or reminders.");
    }

    private static void ClearCompletedTasks()
    {
        TimerEngine engine = new TimerEngine(null);
        string day = Epoch.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        OrbitTask done = engine.AddTask("", 60, false, Epoch);
        engine.MarkCompleted(done.Id, Epoch.AddSeconds(1));
        OrbitTask paused = engine.AddTask("Paused work", 60, true, Epoch);
        OrbitTask active = engine.AddTask("Active work", 120, true, Epoch.AddSeconds(10));
        OrbitTask pending = engine.AddTask("Pending work", 90, false, Epoch);
        OrbitTask checklist = engine.AddTask("Checklist", 0, false, Epoch);
        OrbitTask checkedItem = engine.AddTask("", 0, false, Epoch);
        engine.MarkCompleted(checkedItem.Id, Epoch.AddSeconds(11));
        var retained = new[] { paused, active, pending, checklist };
        var snapshots = retained.Select(TaskSnapshot).ToArray();
        List<OrbitTask> originalList = engine.State.Tasks;
        var originalNumbers = engine.State.DailyTaskNumbers;
        engine.ClearCompleted();
        Assert(engine.State.Tasks == originalList && engine.State.Tasks.SequenceEqual(retained),
            "Clearing completion history preserves the task list and unfinished objects in their existing order.");
        for (int index = 0; index < retained.Length; index++)
            Assert(TaskSnapshot(retained[index]) == snapshots[index], "Clearing history must preserve every unfinished task field.");
        Assert(engine.ActiveTask == active && engine.AlertTask == null && engine.State.DailyTaskNumbers == originalNumbers && originalNumbers[day] == 2,
            "Clearing completion history must preserve active identity and the daily numbering high-water mark.");
        engine.ClearCompleted();
        Assert(engine.State.Tasks.SequenceEqual(retained) && TaskSnapshot(active) == snapshots[1], "Clearing an empty completion history must be a no-op.");
        engine = RoundTrip(engine);
        Assert(engine.AddTask("", 0, false, Epoch).Name == day + " · 任务 3", "Cleared task numbers must not be reused after relaunch.");
        Assert(engine.Tick(Epoch.AddSeconds(130)) && engine.AlertTask.Id == active.Id,
            "The protected running timer keeps its original deadline and still rings normally.");
        engine.ClearCompleted();
        Assert(engine.AlertTask == null && engine.State.Tasks.All(task => task.Id != active.Id) && !engine.Tick(Epoch.AddDays(1)),
            "Deleting alerted completion history must clear its reminder and prevent it from returning.");
        engine.Start(paused.Id, Epoch.AddSeconds(140));
        engine.Pause(Epoch.AddSeconds(145));
        string pausedSnapshot = TaskSnapshot(engine.ActiveTask);
        engine.ClearCompleted();
        Assert(engine.ActiveTask.Id == paused.Id && TaskSnapshot(engine.ActiveTask) == pausedSnapshot,
            "A paused active task must also survive clearing completed history.");

        TimerEngine stale = new TimerEngine(null);
        OrbitTask staleDone = stale.AddTask("Old completed reference", 10, false, Epoch);
        stale.MarkCompleted(staleDone.Id, Epoch);
        stale.State.ActiveTaskId = staleDone.Id;
        stale.State.AlertTaskId = staleDone.Id;
        stale.ClearCompleted();
        Assert(stale.State.Tasks.Count == 0 && stale.State.ActiveTaskId == null && stale.State.AlertTaskId == null,
            "References to removed completed records must not be left dangling.");
    }

    private static void RestoreCompletedTasks()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask timed = engine.AddTask("", 60, false, Epoch);
        OrbitTask checklist = engine.AddTask("", 0, false, Epoch);
        engine.MarkCompleted(timed.Id, Epoch);
        engine.ToggleChecklist(checklist.Id, Epoch);
        string originalName = timed.Name;
        OrbitTask active = engine.AddTask("Other active work", 120, true, Epoch);
        OrbitTask pending = engine.AddTask("Other pending work", 30, false, Epoch);
        string activeSnapshot = TaskSnapshot(active);
        engine.RestoreCompleted(timed.Id);
        Assert(engine.State.Tasks.SequenceEqual(new[] { checklist, active, pending, timed }) &&
            timed.Name == originalName && timed.DurationSeconds == 60 && timed.RemainingSeconds == 60,
            "Restoring a timer preserves its original object, identity and duration, appending it after current pending work.");
        Assert(timed.Status == OrbitTaskStatus.Pending && timed.CompletedAtUtc == null && timed.DueAtUtc == null,
            "A restored timer must be pending with its completion and deadline cleared.");
        engine.RestoreCompleted(checklist.Id);
        Assert(engine.State.Tasks.SequenceEqual(new[] { active, pending, timed, checklist }) && checklist.IsChecklist &&
            checklist.Status == OrbitTaskStatus.Pending && checklist.DurationSeconds == 0 && checklist.RemainingSeconds == 0 &&
            checklist.CompletedAtUtc == null && checklist.DueAtUtc == null, "A restored checklist remains untimed at the end of pending work.");
        Assert(engine.ActiveTask == active && TaskSnapshot(active) == activeSnapshot && engine.AlertTask == null,
            "Restoring either task kind must not start it or modify another active timer.");
        engine.RestoreCompleted(timed.Id);
        engine.RestoreCompleted(active.Id);
        Assert(engine.State.Tasks.SequenceEqual(new[] { active, pending, timed, checklist }) && TaskSnapshot(active) == activeSnapshot,
            "Restoring an already unfinished task must not reorder it or reset its progress.");
        Expect<ArgumentException>(() => engine.RestoreCompleted("missing"));
        TimerEngine restored = RoundTrip(engine);
        Assert(!restored.State.WasRepaired && restored.State.Tasks.Select(task => task.Id).SequenceEqual(engine.State.Tasks.Select(task => task.Id)),
            "Restored state and ordering must survive serialization without repair.");
        string day = Epoch.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert(restored.AddTask("", 0, false, Epoch).Name == day + " · 任务 3", "Restoring completed records must not allocate or reuse task numbers.");

        engine.Tick(Epoch.AddSeconds(121));
        Assert(engine.AlertTask == active, "Fixture timer should complete with a normal alarm.");
        engine.MarkCompleted(pending.Id, Epoch.AddSeconds(122));
        engine.RestoreCompleted(pending.Id);
        Assert(engine.AlertTask == active, "Restoring another completed task must preserve the existing alarm.");
        engine.State.ActiveTaskId = active.Id;
        engine.RestoreCompleted(active.Id);
        Assert(engine.ActiveTask == null && engine.AlertTask == null && active.Status == OrbitTaskStatus.Pending &&
            active.RemainingSeconds == 120 && active.CompletedAtUtc == null && active.DueAtUtc == null && engine.State.Tasks.Last() == active,
            "Restoring the alerted task clears only its own alarm and stale active reference, without starting it.");
    }

    private static void AlertProtection()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask first = engine.AddTask("First", 1, true, Epoch);
        OrbitTask next = engine.AddTask("Next", 20, false, Epoch);
        Expect<InvalidOperationException>(() => engine.Start(next.Id, Epoch.AddSeconds(2)));
        Assert(engine.AlertTask == first && next.Status == OrbitTaskStatus.Pending, "Starting after an unticked deadline must preserve alert.");
        int beforeCount = engine.State.Tasks.Count;
        Expect<InvalidOperationException>(() => engine.AddTask("", 30, true, Epoch.AddSeconds(3)));
        Assert(engine.State.Tasks.Count == beforeCount, "Blocked start must not add a half-created task.");
        Assert(engine.State.DailyTaskNumbers.Count == 0, "A start blocked by an unacknowledged alert must not consume a generated name number.");
        engine.AddTask("Queue allowed", 10, false, Epoch);
        Assert(engine.AlertTask == first, "Adding to queue must not clear alert.");
        engine.Acknowledge();
        engine.Start(next.Id, Epoch.AddSeconds(4));
        Assert(engine.ActiveTask == next, "Acknowledgement should allow next task.");
    }

    private static void SerializationRecovery()
    {
        TimerEngine original = new TimerEngine(null);
        OrbitTask first = original.AddTask("恢复任务", 60, true, Epoch);
        original.State.DotLeft = -1800;
        original.State.DotTop = 100;
        original.Tick(Epoch.AddSeconds(10));
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
        OrbitState restored;
        using (MemoryStream stream = new MemoryStream())
        {
            serializer.WriteObject(stream, original.State);
            stream.Position = 0;
            restored = (OrbitState)serializer.ReadObject(stream);
        }
        TimerEngine engine = new TimerEngine(restored);
        Assert(!engine.State.WasRepaired, "Healthy serialized data should not trigger a recovery notice.");
        Assert(engine.ActiveTask.Name == "恢复任务" && engine.ActiveTask.Id == first.Id, "JSON should preserve task identity and Chinese names.");
        engine.Tick(Epoch.AddSeconds(40));
        Equal(20, engine.ActiveTask.RemainingSeconds, "Relaunch must use original deadline, not saved remaining snapshot.");
        Assert(engine.Tick(Epoch.AddSeconds(70)) && engine.AlertTask != null, "Overdue restored timer should alert.");
        Assert(engine.State.DotLeft == -1800 && engine.State.DotTop == 100, "Negative monitor coordinates are valid.");
        TimerEngine restoredAlarm = new TimerEngine(engine.State);
        Assert(restoredAlarm.AlertTask != null && restoredAlarm.ActiveTask == null, "Unacknowledged alert survives relaunch.");
        restoredAlarm.Acknowledge();
        Assert(new TimerEngine(restoredAlarm.State).AlertTask == null, "Acknowledged alert stays cleared after relaunch.");
    }

    private static void CorruptStateRepair()
    {
        OrbitState state = new OrbitState { ActiveTaskId = "first", AlertTaskId = "missing", DotLeft = double.NaN, DotTop = double.PositiveInfinity };
        state.Tasks.Add(null);
        state.Tasks.Add(new OrbitTask { Id = "bad", Name = "bad", DurationSeconds = double.NaN });
        state.Tasks.Add(new OrbitTask { Id = "first", Name = "First", DurationSeconds = 60, RemainingSeconds = 50, DueAtUtc = Epoch.AddSeconds(60), Status = OrbitTaskStatus.Running });
        state.Tasks.Add(new OrbitTask { Id = "second", Name = "Second", DurationSeconds = 100, RemainingSeconds = 80, DueAtUtc = Epoch.AddSeconds(100), Status = OrbitTaskStatus.Running });
        state.Tasks.Add(new OrbitTask { Id = "second", Name = "", DurationSeconds = 30, RemainingSeconds = double.NaN, Status = (OrbitTaskStatus)99 });
        TimerEngine engine = new TimerEngine(state);
        Assert(state.Tasks.Count == 4, "Only null records should be discarded; damaged tasks must be preserved.");
        Assert(state.Tasks.Select(task => task.Id).Distinct().Count() == 4, "Duplicate IDs should be repaired.");
        Assert(state.Tasks.Count(task => task.Status == OrbitTaskStatus.Running) == 1, "Restoration must enforce a single running timer.");
        Assert(state.Tasks[2].Status == OrbitTaskStatus.Paused && state.Tasks[2].DueAtUtc == null, "Additional running tasks should become paused.");
        Assert(state.AlertTaskId == null && state.DotLeft == null && state.DotTop == null, "Invalid references and coordinates should be cleared.");
        Assert(state.Tasks[3].Status == OrbitTaskStatus.Pending && state.Tasks[3].RemainingSeconds == 30, "Invalid status and remaining time should recover.");
        Assert(state.Tasks[0].Id == "bad" && state.Tasks[0].DurationSeconds == 1500, "Invalid duration should recover without dropping the task.");
        Assert(state.WasRepaired, "Semantic corrections should trigger a recovery notice.");
        Assert(engine.ActiveTask.Id == "first", "Valid active identity should remain stable.");
        TimerEngine empty = new TimerEngine(new OrbitState { Tasks = null, ActiveTaskId = "unknown" });
        Assert(empty.State.Tasks.Count == 0 && empty.ActiveTask == null, "Missing persisted properties should be safe.");
    }

    private static void DamagedDurationRecovery()
    {
        OrbitState state = new OrbitState { ActiveTaskId = "running", AlertTaskId = "history" };
        OrbitTask running = new OrbitTask { Id = "running", Name = "保留我的任务", DurationSeconds = double.NaN, RemainingSeconds = 90, Status = OrbitTaskStatus.Running, DueAtUtc = Epoch.AddSeconds(90) };
        OrbitTask fallback = new OrbitTask { Id = "fallback", Name = "保留待办", DurationSeconds = 0, RemainingSeconds = -1, Status = OrbitTaskStatus.Pending };
        OrbitTask history = new OrbitTask { Id = "history", Name = "保留完成记录", DurationSeconds = double.PositiveInfinity, RemainingSeconds = 0, Status = OrbitTaskStatus.Completed, CompletedAtUtc = Epoch.AddDays(-1) };
        OrbitTask longDuration = new OrbitTask { Id = "too-long", Name = "保留暂停任务", DurationSeconds = 90000, RemainingSeconds = 20, Status = OrbitTaskStatus.Paused };
        state.Tasks.Add(running);
        state.Tasks.Add(fallback);
        state.Tasks.Add(history);
        state.Tasks.Add(longDuration);
        TimerEngine engine = new TimerEngine(state);
        Assert(state.Tasks.Count == 4 && state.Tasks[0] == running && state.Tasks[2] == history, "Recovery must retain all non-null records and identities.");
        Equal(90, running.DurationSeconds, "Valid remaining time is the preferred duration recovery.");
        Equal(90, running.RemainingSeconds, "Remaining time should be preserved.");
        Assert(running.Status == OrbitTaskStatus.Paused && running.DueAtUtc == null, "A repaired running timer must require explicit resume.");
        Assert(running.Name == "保留我的任务" && running.Id == "running" && engine.ActiveTask == running, "Recovery must preserve the active task's name and ID.");
        engine.Tick(Epoch.AddDays(10));
        Equal(90, running.RemainingSeconds, "A repaired timer must not advance automatically.");
        Equal(1500, fallback.DurationSeconds, "Use 25 minutes when neither duration is usable.");
        Equal(1500, fallback.RemainingSeconds, "Pending fallback task starts with the recovered duration.");
        Assert(history.Status == OrbitTaskStatus.Completed && history.CompletedAtUtc == Epoch.AddDays(-1) && history.RemainingSeconds == 0, "Completed status and timestamp must survive recovery.");
        Assert(engine.AlertTask == history, "An existing completion reminder must survive recovery.");
        Equal(20, longDuration.DurationSeconds, "Out-of-range duration can be recovered from valid remaining time.");
        Assert(state.WasRepaired, "UI must be able to detect semantic repair.");

        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
        using (MemoryStream stream = new MemoryStream())
        {
            serializer.WriteObject(stream, state);
            string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            Assert(!json.Contains("WasRepaired"), "Recovery notice must not be persisted.");
            stream.Position = 0;
            TimerEngine reloaded = new TimerEngine((OrbitState)serializer.ReadObject(stream));
            Assert(!reloaded.State.WasRepaired && reloaded.State.Tasks.Count == 4, "Normalized state must reload cleanly with all task records.");
        }
    }

    private static void InvalidAdditions()
    {
        TimerEngine engine = new TimerEngine(null);
        Expect<ArgumentException>(() => engine.AddTask(" ", -10, true, Epoch));
        Expect<InvalidOperationException>(() => engine.AddTask("Bad", 0, true, Epoch));
        Expect<ArgumentException>(() => engine.AddTask("Bad", double.NaN, true, Epoch));
        Expect<ArgumentException>(() => engine.AddTask("Bad", 86401, false, Epoch));
        Assert(engine.State.Tasks.Count == 0, "Invalid input must not modify the task list.");
        Assert(engine.State.DailyTaskNumbers.Count == 0, "Rejected additions must not consume a default name number.");
    }

    private static void AutomaticTaskNames()
    {
        TimerEngine engine = new TimerEngine(null);
        string day = Epoch.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        OrbitTask first = engine.AddTask(" ", 60, true, Epoch);
        Assert(first.Name == day + " · 任务 1", "A blank name should use the local date and first daily number.");
        engine.Restart(Epoch.AddSeconds(2));
        OrbitTask second = engine.AddTask(null, 0, false, Epoch);
        Assert(second.Name == day + " · 任务 2", "Restart must not consume a new number; checklist tasks share the counter.");
        OrbitTask named = engine.AddTask("  My task  ", 60, false, Epoch);
        Assert(named.Name == "My task", "An explicit name is trimmed and preserved.");
        engine.ReorderTask(second.Id, first.Id, false);
        engine.Delete(first.Id);
        engine.Delete(second.Id);
        OrbitTask third = engine.AddTask("", 60, false, Epoch);
        Assert(third.Name == day + " · 任务 3", "Deleted or reordered tasks must not make daily numbers reusable.");

        engine = RoundTrip(engine);
        foreach (OrbitTask task in engine.State.Tasks.ToList()) engine.Delete(task.Id);
        OrbitTask fourth = engine.AddTask("", 0, false, Epoch);
        Assert(fourth.Name == day + " · 任务 4", "Counters must survive serialization even after all earlier tasks are deleted.");
        DateTimeOffset tomorrow = Epoch.AddDays(1);
        string nextDay = tomorrow.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert(engine.AddTask("", 0, false, tomorrow).Name == nextDay + " · 任务 1", "A different local date has its own counter.");
        Assert(engine.AddTask("", 0, false, Epoch).Name == day + " · 任务 5", "Returning to a previous date must continue its high-water mark.");
        Assert(engine.AddTask("", 0, false, Epoch.ToOffset(TimeSpan.FromHours(14))).Name == day + " · 任务 6", "Name dates must use local time, not the input's arbitrary offset.");
        engine.AddTask(day + " · 任务 40", 0, false, Epoch);
        Assert(engine.AddTask("", 0, false, Epoch).Name == day + " · 任务 41", "Explicit names using the generated pattern reserve their number too.");
    }

    private static void LegacyNamingCounters()
    {
        string day = Epoch.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // This is the first release's JSON shape: neither new property exists.
        string json = "{\"Tasks\":[{\"Id\":\"legacy\",\"Name\":\"" + day + " · 任务 7\",\"DurationSeconds\":60,\"RemainingSeconds\":60,\"Status\":0}],\"ActiveTaskId\":null,\"AlertTaskId\":null}";
        OrbitState state;
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
        using (MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))) state = (OrbitState)serializer.ReadObject(stream);
        TimerEngine engine = new TimerEngine(state);
        Assert(!engine.State.Tasks[0].IsChecklist && engine.State.Tasks[0].DurationSeconds == 60, "Legacy timed records must retain their meaning.");
        Assert(!state.WasRepaired, "Adding versioned counter state is a normal migration, not a corrupt-data warning.");
        Assert(engine.AddTask("", 0, false, Epoch).Name == day + " · 任务 8", "Existing generated names must seed missing counters.");
        engine.State.DailyTaskNumbers[day] = 1;
        engine = new TimerEngine(engine.State);
        Assert(engine.State.WasRepaired && engine.AddTask("", 0, false, Epoch).Name == day + " · 任务 9", "An outdated saved counter must be raised to existing task numbers.");
        engine.State.DailyTaskNumbers[day] = int.MaxValue;
        int count = engine.State.Tasks.Count;
        Expect<InvalidOperationException>(() => engine.AddTask("", 0, false, Epoch));
        Assert(engine.State.Tasks.Count == count && engine.State.DailyTaskNumbers[day] == int.MaxValue, "Counter exhaustion must not overflow or add a partial task.");
    }

    private static void ChecklistTasks()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask timed = engine.AddTask("Focus", 20, true, Epoch);
        OrbitTask item = engine.AddTask("Buy paper", 0, false, Epoch);
        DateTimeOffset? due = timed.DueAtUtc;
        Assert(item.IsChecklist && item.DurationSeconds == 0 && item.RemainingSeconds == 0 && item.Status == OrbitTaskStatus.Pending, "Zero seconds creates an untimed checklist item.");
        Expect<InvalidOperationException>(() => engine.Start(item.Id, Epoch.AddSeconds(1)));
        engine.ToggleChecklist(item.Id, Epoch.AddSeconds(2));
        Assert(item.Status == OrbitTaskStatus.Completed && item.CompletedAtUtc == Epoch.AddSeconds(2), "Checking an item records completion.");
        Assert(engine.AlertTask == null && engine.ActiveTask == timed && timed.DueAtUtc == due && timed.RemainingSeconds == 20, "Checking must not ring, tick, pause, or replace the active timer.");
        Expect<InvalidOperationException>(() => engine.Start(item.Id, Epoch.AddSeconds(3)));
        engine.ToggleChecklist(item.Id, Epoch.AddSeconds(4));
        Assert(item.Status == OrbitTaskStatus.Pending && item.CompletedAtUtc == null, "Unchecking returns the item to pending without a timestamp.");
        Expect<InvalidOperationException>(() => engine.ToggleChecklist(timed.Id, Epoch));
        Expect<ArgumentException>(() => engine.ToggleChecklist("missing", Epoch));
        engine.Tick(Epoch.AddSeconds(21));
        Assert(engine.AlertTask == timed, "The actual timed task still completes normally.");
        engine.ToggleChecklist(item.Id, Epoch.AddSeconds(22));
        Assert(engine.AlertTask == timed, "Checklist changes must not dismiss another task's pending alarm.");
        engine.Acknowledge();
        Assert(!engine.Tick(Epoch.AddDays(1)) && engine.AlertTask == null, "A completed checklist must never start an alarm later.");
    }

    private static void ChecklistRecovery()
    {
        TimerEngine original = new TimerEngine(null);
        OrbitTask pending = original.AddTask("Checklist", 0, false, Epoch);
        OrbitTask done = original.AddTask("Done", 0, false, Epoch);
        original.ToggleChecklist(done.Id, Epoch.AddSeconds(1));
        TimerEngine restored = RoundTrip(original);
        Assert(!restored.State.WasRepaired, "Valid checklist state should not be treated as damaged timer data.");
        Assert(restored.State.Tasks.All(task => task.IsChecklist && task.DurationSeconds == 0 && task.RemainingSeconds == 0), "Serialized checklist tasks must remain untimed.");
        Assert(restored.State.Tasks[0].Id == pending.Id && restored.State.Tasks[0].Status == OrbitTaskStatus.Pending, "Pending checklist identity survives reload.");
        Assert(restored.State.Tasks[1].Id == done.Id && restored.State.Tasks[1].Status == OrbitTaskStatus.Completed && restored.State.Tasks[1].CompletedAtUtc == Epoch.AddSeconds(1), "Completed checklist history survives reload.");

        OrbitState damaged = new OrbitState { ActiveTaskId = "bad-check", AlertTaskId = "bad-alert" };
        damaged.Tasks.Add(new OrbitTask { Id = "bad-check", Name = "Malformed", IsChecklist = true, DurationSeconds = 60, RemainingSeconds = 50, Status = OrbitTaskStatus.Running, DueAtUtc = Epoch.AddSeconds(50) });
        damaged.Tasks.Add(new OrbitTask { Id = "bad-alert", Name = "Malformed done", IsChecklist = true, DurationSeconds = double.NaN, Status = OrbitTaskStatus.Completed, CompletedAtUtc = Epoch });
        TimerEngine repaired = new TimerEngine(damaged);
        Assert(damaged.WasRepaired && repaired.ActiveTask == null && repaired.AlertTask == null, "Checklist records must not remain attached to timers or alarms after recovery.");
        Assert(damaged.Tasks[0].Status == OrbitTaskStatus.Pending && damaged.Tasks[0].DueAtUtc == null && damaged.Tasks[0].DurationSeconds == 0, "Damaged running checklist recovers as an untimed pending item.");
        Assert(damaged.Tasks[1].Status == OrbitTaskStatus.Completed && damaged.Tasks[1].CompletedAtUtc == Epoch, "Completed checklist history should be retained during repair.");
    }

    private static void ReorderPreservesTasks()
    {
        TimerEngine engine = new TimerEngine(null);
        OrbitTask first = engine.AddTask("First", 60, true, Epoch);
        OrbitTask second = engine.AddTask("Second", 90, true, Epoch.AddSeconds(10));
        OrbitTask third = engine.AddTask("Third", 0, false, Epoch);
        OrbitTask completed = engine.AddTask("Done", 0, false, Epoch);
        engine.ToggleChecklist(completed.Id, Epoch.AddSeconds(12));
        DateTimeOffset? deadline = second.DueAtUtc;
        var objects = engine.State.Tasks.ToList();
        var snapshots = objects.Select(TaskSnapshot).ToList();
        string activeId = engine.State.ActiveTaskId;
        engine.ReorderTask(second.Id, third.Id, true);
        Assert(engine.State.Tasks.SequenceEqual(new[] { first, third, second, completed }), "Moving forward after a target must account for removal index changes.");
        engine.ReorderTask(completed.Id, first.Id, false);
        Assert(engine.State.Tasks.SequenceEqual(new[] { completed, first, third, second }), "Moving backward before a target must preserve relative order of other tasks.");
        engine.ReorderTask(completed.Id, second.Id, true);
        Assert(engine.State.Tasks.SequenceEqual(new[] { first, third, second, completed }), "Moving the first item after the last target must work.");
        engine.ReorderTask(first.Id, "missing", false);
        engine.ReorderTask("missing", first.Id, true);
        engine.ReorderTask(first.Id, first.Id, true);
        Assert(engine.State.Tasks.SequenceEqual(new[] { first, third, second, completed }), "Missing IDs and self-reordering must be no-ops.");
        for (int index = 0; index < objects.Count; index++) Assert(snapshots[index] == TaskSnapshot(objects[index]), "Reordering must preserve every business field on the same task objects.");
        Assert(engine.State.ActiveTaskId == activeId && engine.ActiveTask == second && second.DueAtUtc == deadline, "Reordering must not select or restart a timer.");
        TimerEngine restored = RoundTrip(engine);
        Assert(restored.State.Tasks.Select(task => task.Id).SequenceEqual(engine.State.Tasks.Select(task => task.Id)), "Task order must persist across relaunches.");
        engine.Tick(Epoch.AddSeconds(25));
        Equal(75, second.RemainingSeconds, "Active timer must continue from its original deadline after reordering.");
    }

    private static string TaskSnapshot(OrbitTask task)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(OrbitTask)).WriteObject(stream, task);
            return Convert.ToBase64String(stream.ToArray());
        }
    }

    private static TimerEngine RoundTrip(TimerEngine engine)
    {
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
        using (MemoryStream stream = new MemoryStream())
        {
            serializer.WriteObject(stream, engine.State);
            stream.Position = 0;
            return new TimerEngine((OrbitState)serializer.ReadObject(stream));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal(double expected, double actual, string message)
    {
        Assert(Math.Abs(expected - actual) < 0.00001, message + " Expected " + expected + ", actual " + actual);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception("Expected " + typeof(TException).Name);
    }
}
