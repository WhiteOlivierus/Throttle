using ThrottleData.Console;

namespace ThrottleData.Tests
{
    class PushSource
    {
        private event Action<int>? _onValue;
        public IDisposable Subscribe(Action<int> handler)
        {
            _onValue += handler;
            return new DisposableAction(() => _onValue -= handler);
        }

        public void Push(int v) => _onValue?.Invoke(v);
    }

    public class GenericThrottlerTests
    {
        private static GenericThrottler<int> Create(PushSource source, ThrottlerOptions<int> options) =>
            new(h => source.Subscribe(h), options);

        [Fact]
        public async Task Throttles_To_One_Per_Period_And_Coalesces()
        {
            var src = new PushSource();
            var options = new ThrottlerOptions<int> { Period = TimeSpan.FromMilliseconds(200) };
            using var thr = Create(src, options);

            int seen = 0;
            int first = -1, second = -1;
            thr.ValueEmitted += (s, ea) =>
            {
                var c = Interlocked.Increment(ref seen);
                if (c == 1) first = ea.Value;
                if (c == 2) second = ea.Value;
            };

            // burst of rapid updates
            for (int i = 0; i < 10; i++)
            {
                src.Push(i);
                await Task.Delay(10);
            }

            await Task.Delay(350); // period + slack

            Assert.Equal(2, seen);
            Assert.Equal(0, first);   // first was immediate
            Assert.Equal(9, second);  // latest coalesced
        }

        [Fact]
        public async Task TriggerNow_Bypasses_Throttle()
        {
            var src = new PushSource();
            var options = new ThrottlerOptions<int>
            {
                Period = TimeSpan.FromSeconds(10),
                TriggerNow = v => v >= 5
            };
            using var thr = Create(src, options);

            int seen = 0;
            thr.ValueEmitted += (s, e) => Interlocked.Increment(ref seen);

            src.Push(1);
            await Task.Delay(20);

            for (int i = 2; i <= 4; i++)
            {
                src.Push(i);
                await Task.Delay(15);
            }

            Assert.Equal(1, seen);

            src.Push(5);
            await Task.Delay(50);

            Assert.Equal(2, seen);
        }

        [Fact]
        public async Task ImmediateOnSparse_Emits_Immediately_When_Sparse()
        {
            var src = new PushSource();
            var options = new ThrottlerOptions<int>
            {
                Period = TimeSpan.FromMilliseconds(200),
                ImmediateOnSparse = true
            };
            using var thr = Create(src, options);

            int seen = 0;
            thr.ValueEmitted += (s, e) => Interlocked.Increment(ref seen);

            // push one value, wait longer than period, then push another
            src.Push(1);
            await Task.Delay(20);
            Assert.Equal(1, seen);

            // wait longer than Period to simulate sparse updates
            await Task.Delay(250);

            src.Push(2);
            await Task.Delay(50);

            // Because ImmediateOnSparse==true, the second should be emitted immediately (not scheduled)
            Assert.Equal(2, seen);
        }

        [Fact]
        public async Task Dispose_Stops_Forwarding()
        {
            var src = new PushSource();
            var options = new ThrottlerOptions<int> { Period = TimeSpan.FromMilliseconds(100) };
            var thr = Create(src, options);

            int seen = 0;
            thr.ValueEmitted += (s, e) => Interlocked.Increment(ref seen);

            src.Push(1);
            await Task.Delay(20);
            Assert.Equal(1, seen);

            thr.Dispose();

            src.Push(2);
            await Task.Delay(150);
            Assert.Equal(1, seen);
        }
    }
}
