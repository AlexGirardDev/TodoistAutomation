using System;
using Newtonsoft.Json;
using TimeZoneConverter;
using Todoist.Net;
using Todoist.Net.Models;
using TimeZoneInfo = System.TimeZoneInfo;

namespace TodoistAutomation;

internal static class Program
{

    private static readonly TimeZoneInfo TimeZoneInfoEst = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

    static async Task Main(string[] args)
    {
        var apiKey = Environment.GetEnvironmentVariable("ApiKey");
        var  endOfDayOffset = TimeSpan.FromMinutes(int.Parse(Environment.GetEnvironmentVariable("EodOffset") ?? "240"));
        var  syncRate = TimeSpan.FromMinutes(int.Parse(Environment.GetEnvironmentVariable("SyncRate") ?? "30"));
        
        var client = new TodoistClient(apiKey);
        Console.WriteLine("Connected");
        

        var refreshTaskCache = TimeSpan.FromMinutes(15);
        var taskCacheAge = DateTime.Now;


        var lastRunTime = DateTime.UtcNow.AddMilliseconds(-5);
        var recurringTasks = (await client.Items.GetAsync())?
            .Where(x => x.DueDate is {IsRecurring: { }} && x.DueDate.IsRecurring.Value)
            .ToDictionary(x => x.Id);
        Console.WriteLine($"Loaded recurring tasks{recurringTasks?.Count}");
        while (true)
        {
            try
            {
                if (DateTime.Now - taskCacheAge > refreshTaskCache)
                {
                    recurringTasks = (await client.Items.GetAsync())?
                        .Where(x => x.DueDate?.IsRecurring != null && x.DueDate.IsRecurring.Value)
                        .ToDictionary(x => x.Id);
                    taskCacheAge = DateTime.Now;
                }

                var newCompletedTasks = await client.Items.GetCompletedAsync(new ItemFilter {Since = lastRunTime});
                lastRunTime = DateTime.UtcNow;

                if (!newCompletedTasks.Items.Any())
                    continue;
                var transaction = client.CreateTransaction();
                Console.WriteLine($"{newCompletedTasks.Items.Count} new completed tasks found");
                foreach (var change in newCompletedTasks.Items)
                {
                    var newTask = (await client.Items.GetAsync(change.TaskId))?.Item;
                    var oldTask = recurringTasks?[change.TaskId];
                    
                    if (newTask?.DueDate.IsRecurring == null || !newTask.DueDate.IsRecurring.Value || !newTask.DueDate.IsFullDay || oldTask == null || newTask.DueDate?.Date == null || oldTask.DueDate?.Date == null)
                        continue;

                    var completedOn = TimeZoneInfo.ConvertTimeFromUtc(change.CompletedDate, TimeZoneInfoEst);
                    var etcNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfoEst);
                    var window = completedOn - etcNow.Date;
                    
                    if (window > endOfDayOffset)
                        continue;

                    var oldDueDate = oldTask.DueDate.Date.Value;
                    var newDueDate = newTask.DueDate.Date.Value;
                    var daysOverdue = (etcNow.Date - oldDueDate.Date).Days;

                    if ((newDueDate.Date - etcNow.Date).Days != 1 || daysOverdue < 1) continue;
                    newTask.DueDate = new DueDate(etcNow.Date.ToUniversalTime(), true);
                    Console.WriteLine($"Fixing duedate of {newTask.Content}");
                    await transaction.Items.UpdateAsync(newTask);
                }

                await transaction.CommitAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                continue;
            }
            finally
            {
                await Task.Delay(syncRate * 1000);
            }
        }
    }
}