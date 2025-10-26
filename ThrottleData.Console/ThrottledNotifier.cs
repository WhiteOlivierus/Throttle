using System.ComponentModel;

namespace ThrottleData.Console
{
    public sealed class ThrottledValueEventArgs<T> : EventArgs
    {
        public ThrottledValueEventArgs(T value, DateTimeOffset timestamp)
        {
            Value = value;
            Timestamp = timestamp;
        }

        public T Value { get; }
        public DateTimeOffset Timestamp { get; }
    }

    public sealed class ThrottlerOptions<T>
    {
        /// <summary>Throttle period (minimum time between forwarded emissions).</summary>
        public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Predicate that causes an immediate emission when true for the incoming value.</summary>
        public Func<T, bool>? TriggerNow { get; set; }

        /// <summary>
        /// If true: when the source's previous update is older than Period, emit the new value immediately.
        /// In other words: treat sparse updates (less frequent than Period) as immediate.
        /// </summary>
        public bool ImmediateOnSparse { get; set; } = false;

        /// <summary>Synchronization context to Post events to (UI thread). Optional.</summary>
        public SynchronizationContext? SyncContext { get; set; }
    }

    /// <summary>
    /// Generic throttler that subscribes to a value-producing source and forwards values according to throttle rules.
    /// Use subscribe(sourceHandler => { subscribe to source and call sourceHandler(value) on updates; return IDisposable to unsubscribe; })
    /// </summary>
    public sealed class GenericThrottler<T> : IDisposable
    {
        private readonly Func<Action<T>, IDisposable> _subscribe;
        private readonly ThrottlerOptions<T> _options;

        // synchronization
        private readonly object _gate = new object();

        // state
        private T? _latest;
        private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSourceUpdate = DateTimeOffset.MinValue;
        private CancellationTokenSource? _scheduledCts;
        private IDisposable? _subscription;
        private bool _disposed;

        public GenericThrottler(Func<Action<T>, IDisposable> subscribe, ThrottlerOptions<T> options)
        {
            _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (_options.Period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.Period));

            // subscribe to source
            _subscription = _subscribe(HandleSourceValue);
        }

        /// <summary>
        /// Event raised for each forwarded value (after throttle/coalescing). Posts to SyncContext if provided.
        /// </summary>
        public event EventHandler<ThrottledValueEventArgs<T>>? ValueEmitted;

        private void HandleSourceValue(T value)
        {
            if (_disposed) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            T? toEmitNow = default;
            TimeSpan scheduleAfter = TimeSpan.Zero;
            bool schedule = false;

            lock (_gate)
            {
                // record last source update timestamp and latest value
                var prevSourceUpdate = _lastSourceUpdate;
                _lastSourceUpdate = now;
                _latest = value;

                // triggerNow bypass
                if (_options.TriggerNow != null)
                {
                    bool should;
                    try
                    {
                        should = _options.TriggerNow(value);
                    }
                    catch
                    {
                        should = false;
                    }

                    if (should)
                    {
                        toEmitNow = _latest;
                        // cancel scheduled
                        _scheduledCts?.Cancel();
                        _scheduledCts?.Dispose();
                        _scheduledCts = null;
                        _lastEmit = now;
                        _latest = default;
                        // fall through to emit below
                    }
                }

                if (toEmitNow == null)
                {
                    // ImmediateOnSparse: if previous source update was far in the past (or never), emit immediately
                    if (_options.ImmediateOnSparse && prevSourceUpdate != DateTimeOffset.MinValue)
                    {
                        var sincePrevSource = now - prevSourceUpdate;
                        if (sincePrevSource >= _options.Period)
                        {
                            toEmitNow = _latest;
                            _scheduledCts?.Cancel();
                            _scheduledCts?.Dispose();
                            _scheduledCts = null;
                            _lastEmit = now;
                            _latest = default;
                        }
                    }
                }

                if (toEmitNow == null)
                {
                    // Normal throttle: if enough time passed since last emit, emit immediately
                    var sinceLastEmit = now - _lastEmit;
                    if (sinceLastEmit >= _options.Period)
                    {
                        toEmitNow = _latest;
                        _scheduledCts?.Cancel();
                        _scheduledCts?.Dispose();
                        _scheduledCts = null;
                        _lastEmit = now;
                        _latest = default;
                    }
                    else
                    {
                        // need to schedule one emission at the end of the throttle window
                        var remaining = _options.Period - sinceLastEmit;

                        // create or replace scheduled task to emit latest after remaining
                        _scheduledCts?.Cancel();
                        _scheduledCts?.Dispose();
                        _scheduledCts = new CancellationTokenSource();
                        var token = _scheduledCts.Token;
                        schedule = true;
                        scheduleAfter = remaining;

                        // schedule outside of lock
                    }
                }
            } // lock

            if (toEmitNow != null)
            {
                RaiseValueEmitted(toEmitNow, DateTimeOffset.UtcNow);
                return;
            }

            if (schedule)
            {
                // schedule the emission of the latest value (coalesced)
                _ = Task.Run(async () =>
                {
                    CancellationTokenSource? localCts;
                    lock (_gate)
                    {
                        localCts = _scheduledCts;
                    }

                    try
                    {
                        await Task.Delay(scheduleAfter, localCts!.Token).ConfigureAwait(false);
                        T? toEmit = default;
                        lock (_gate)
                        {
                            if (localCts!.IsCancellationRequested) return;
                            toEmit = _latest;
                            if (toEmit is not null)
                            {
                                _latest = default;
                                _lastEmit = DateTimeOffset.UtcNow;
                            }
                            // else nothing to emit (shouldn't happen normally)
                            // scheduledCts remains (we do not automatically dispose here; it's handled on replace/cancel)
                        }

                        if (toEmit is not null)
                        {
                            RaiseValueEmitted(toEmit, DateTimeOffset.UtcNow);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        /* ignored */
                    }
                    catch
                    {
                        /* swallow other exceptions to avoid unobserved exceptions */
                    }
                }, CancellationToken.None);
            }
        }

        private void RaiseValueEmitted(T value, DateTimeOffset ts)
        {
            var h = ValueEmitted;
            if (h == null) return;

            void invoke()
            {
                try
                {
                    h.Invoke(this, new ThrottledValueEventArgs<T>(value, ts));
                }
                catch
                {
                    /* swallow */
                }
            }

            if (_options.SyncContext != null)
                _options.SyncContext.Post(_ => invoke(), null);
            else
                invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_gate)
            {
                _subscription?.Dispose();
                _subscription = null;
                _scheduledCts?.Cancel();
                _scheduledCts?.Dispose();
                _scheduledCts = null;
                _latest = default;
            }
        }
    }

    public sealed class DisposableAction : IDisposable
    {
        private Action? _action;
        public DisposableAction(Action action) => _action = action ?? throw new ArgumentNullException(nameof(action));

        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _action, null);
            a?.Invoke();
        }
    }

    public static class ThrottlerHelpers
    {
        /// <summary>
        /// Build a GenericThrottler that emits the source (T) whenever the source raises PropertyChanged.
        /// Optionally pass propertyFilter to only react to a single property name (null = any).
        /// </summary>
        public static GenericThrottler<T> Throttle<T>(
            this T source,
            ThrottlerOptions<T> options,
            string? propertyFilter = null)
            where T : INotifyPropertyChanged
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));

            IDisposable Subscribe(Action<T> handler)
            {
                PropertyChangedEventHandler h = (s, e) =>
                {
                    if (propertyFilter == null || e.PropertyName == propertyFilter)
                    {
                        handler(source);
                    }
                };
                source.PropertyChanged += h;
                return new DisposableAction(() => source.PropertyChanged -= h);
            }

            return new GenericThrottler<T>(h => Subscribe(h), options);
        }

        /// <summary>
        /// Build a GenericThrottler that emits a snapshot produced by selector whenever the source raises PropertyChanged.
        /// Useful when you want an immutable DTO instead of the live source reference.
        /// </summary>
        public static GenericThrottler<TOut> FromINotifySnapshot<TIn, TOut>(
            this TIn source,
            ThrottlerOptions<TOut> options,
            Func<TIn, TOut> snapshotSelector,
            string? propertyFilter = null)
            where TIn : INotifyPropertyChanged
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (snapshotSelector == null) throw new ArgumentNullException(nameof(snapshotSelector));

            IDisposable Subscribe(Action<TOut> handler)
            {
                PropertyChangedEventHandler h = (s, e) =>
                {
                    if (propertyFilter == null || e.PropertyName == propertyFilter)
                    {
                        handler(snapshotSelector(source));
                    }
                };
                source.PropertyChanged += h;
                return new DisposableAction(() => source.PropertyChanged -= h);
            }

            return new GenericThrottler<TOut>(h => Subscribe(h), options);
        }
    }
}