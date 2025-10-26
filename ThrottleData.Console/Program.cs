using CommunityToolkit.Mvvm.ComponentModel;
using ThrottleData.Console;

var data = new Data();

// Configure throttler: 1s period, ImmediateOnSparse true, TriggerNow bypass when Value >= 250
var options = new ThrottlerOptions<Data>
{
    Period = TimeSpan.FromSeconds(1),
    ImmediateOnSparse = true,
    TriggerNow = d => d.Value >= 250,
    SyncContext = null // set to SynchronizationContext.Current in UI apps if needed
};

// Create throttler that emits the Data instance
using var throttler = data.Throttle(options);

// Subscribe and print emissions with timestamps
throttler.ValueEmitted += (s, ea) =>
{
    var v = ea.Value.Value; // same Data instance
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  EMIT  value={v}  (emitted at {ea.Timestamp:HH:mm:ss.fff})");
};

Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Starting rapid burst (300 updates @10ms)");

// 1) Start a rapid burst of updates (changes every 10ms)
_ = Task.Run(async () =>
{
    for (var i = 0; i < 300; i++)
    {
        data.Value = i;
        await Task.Delay(10);
    }
});

// 2) While the burst is still running, inject a triggerNow value after 500ms
//    This should bypass the throttle and be emitted immediately.
_ = Task.Run(async () =>
{
    await Task.Delay(500);
    data.Value = 260; // >=250 => TriggerNow should cause immediate emission
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  (injected triggerNow value=260)");
});

// Let the burst run and the scheduled emissions happen
await Task.Delay(3500);

Console.WriteLine(
    $"{DateTime.Now:HH:mm:ss.fff} Burst finished; now waiting longer than Period to demonstrate ImmediateOnSparse...");

// Wait longer than the throttle period so next update is 'sparse' relative to previous source update
await Task.Delay(TimeSpan.FromSeconds(1.5)); // > Period (1s)

// This update happens after a long gap -> ImmediateOnSparse should make it emit immediately
Console.WriteLine(
    $"{DateTime.Now:HH:mm:ss.fff} Pushing sparse update value=777 (should emit immediately because ImmediateOnSparse=true)");
data.Value = 777;

// small wait to ensure emission printed
await Task.Delay(500);

Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Demo completed.");

public partial class Data : ObservableObject
{
    [ObservableProperty] private int _value;
}