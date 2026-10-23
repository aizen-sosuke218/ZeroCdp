using System.Globalization;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="Clock.InstallAsync"/>.</summary>
public sealed class ClockInstallOptions
{
    /// <summary>Initial fake time. Defaults to the current UTC time.</summary>
    public DateTimeOffset? Time { get; init; }
}

/// <summary>
/// Playwright-compatible fake clock for a page. Installing replaces the page's Date, timer,
/// animation-frame and performance implementations with deterministic versions.
/// </summary>
/// <remarks>
/// The implementation runs entirely in page JavaScript and is injected through the page's document
/// transformer, so it survives navigations without <c>Page.addScriptToEvaluateOnNewDocument</c>.
/// It is opt-in; pages that never use <see cref="Clock"/> are not modified.
/// </remarks>
public sealed class Clock
{
    internal const string ScriptMarker = "/*__ZERO_CDP_CLOCK__*/";

    private readonly CdpPage _page;
    private ClockState _state = new();

    internal Clock(CdpPage page) => _page = page;

    /// <summary>Installs fake time starting at <paramref name="options"/>' time (or now).</summary>
    public async ValueTask InstallAsync(
        ClockInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_page.IsClosed, _page);
        DateTimeOffset time = options?.Time ?? DateTimeOffset.UtcNow;
        _state = new ClockState
        {
            TimeMilliseconds = time.ToUnixTimeMilliseconds(),
            Paused = false,
            Fixed = false,
        };

        string script = ClockScript.Build(_state);
        await _page.SetClockInitScriptAsync(script, cancellationToken).ConfigureAwait(false);
        await EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Jumps forward and fires due timers at most once.</summary>
    public ValueTask FastForwardAsync(double milliseconds, CancellationToken cancellationToken = default)
    {
        ValidateMilliseconds(milliseconds);
        return RunCommandAsync("fastForward", milliseconds, cancellationToken);
    }

    /// <summary>Jumps forward and fires due timers at most once.</summary>
    public ValueTask FastForwardAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => FastForwardAsync(duration.TotalMilliseconds, cancellationToken);

    /// <summary>Advances the clock, firing every due timer in order.</summary>
    public ValueTask RunForAsync(double milliseconds, CancellationToken cancellationToken = default)
    {
        ValidateMilliseconds(milliseconds);
        return RunCommandAsync("runFor", milliseconds, cancellationToken);
    }

    /// <summary>Advances the clock, firing every due timer in order.</summary>
    public ValueTask RunForAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => RunForAsync(duration.TotalMilliseconds, cancellationToken);

    /// <summary>Jumps to <paramref name="time"/> and pauses the clock.</summary>
    public ValueTask PauseAtAsync(DateTimeOffset time, CancellationToken cancellationToken = default)
    {
        _state.Paused = true;
        _state.TimeMilliseconds = time.ToUnixTimeMilliseconds();
        return RunCommandAsync("pauseAt", time.ToUnixTimeMilliseconds(), cancellationToken);
    }

    /// <summary>Resumes real-time progression of the fake clock.</summary>
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        _state.Paused = false;
        return RunCommandAsync("resume", null, cancellationToken);
    }

    /// <summary>Pins Date to <paramref name="time"/> while leaving timers running.</summary>
    public ValueTask SetFixedTimeAsync(
        DateTimeOffset time,
        CancellationToken cancellationToken = default)
    {
        _state.Fixed = true;
        _state.TimeMilliseconds = time.ToUnixTimeMilliseconds();
        return RunCommandAsync("setFixedTime", time.ToUnixTimeMilliseconds(), cancellationToken);
    }

    /// <summary>Changes the clock's system time without firing timers.</summary>
    public ValueTask SetSystemTimeAsync(
        DateTimeOffset time,
        CancellationToken cancellationToken = default)
    {
        _state.Fixed = false;
        _state.TimeMilliseconds = time.ToUnixTimeMilliseconds();
        return RunCommandAsync("setSystemTime", time.ToUnixTimeMilliseconds(), cancellationToken);
    }

    private async ValueTask RunCommandAsync(
        string command,
        double? argument,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_page.IsClosed, _page);
        string expression = "globalThis.__zeroCdpClock && globalThis.__zeroCdpClock."
            + command
            + "("
            + (argument is { } value
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : string.Empty)
            + ")";

        EvaluationResult result = await _page
            .EvaluateAsync(expression, isolatedContext: false, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(
                result.ExceptionSummary ?? $"Clock.{command} failed in the page.");
        }

        if (result.Value.TryGetDouble(out double current))
        {
            _state.TimeMilliseconds = current;
        }

        await _page
            .SetClockInitScriptAsync(ClockScript.Build(_state), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        EvaluationResult result = await _page
            .EvaluateAsync(script, isolatedContext: false, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(
                result.ExceptionSummary ?? "Clock installation failed in the page.");
        }

        if (result.Value.TryGetDouble(out double current))
        {
            _state.TimeMilliseconds = current;
        }
    }

    private static void ValidateMilliseconds(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }
    }

    private sealed class ClockState
    {
        public double TimeMilliseconds { get; set; }

        public bool Paused { get; set; }

        public bool Fixed { get; set; }
    }

    private static class ClockScript
    {
        private const string Template = """
            /*__ZERO_CDP_CLOCK__*/(() => {
              const config = __CONFIG__;
              if (globalThis.__zeroCdpClock) {
                globalThis.__zeroCdpClock.configure(config);
                return globalThis.__zeroCdpClock.now();
              }

              const RealDate = globalThis.Date;
              const realPerformanceNow = globalThis.performance.now.bind(globalThis.performance);
              const realSetTimeout = globalThis.setTimeout.bind(globalThis);
              const realClearTimeout = globalThis.clearTimeout.bind(globalThis);

              let paused = !!config.paused;
              let fixed = !!config.fixed;
              let base = Number(config.time) || 0;
              let fixedTime = base;
              let realStart = realPerformanceNow();
              let epoch = base;
              let nextId = 1;
              const timers = new Map();
              let wakeHandle = 0;
              let wakeDue = Infinity;

              function timerNow() {
                return paused ? base : base + (realPerformanceNow() - realStart);
              }

              function currentTime() {
                return fixed ? fixedTime : timerNow();
              }

              function clearWake() {
                if (wakeHandle) {
                  realClearTimeout(wakeHandle);
                  wakeHandle = 0;
                }
                wakeDue = Infinity;
              }

              function scheduleWake() {
                if (paused) {
                  return;
                }

                let next = Infinity;
                for (const timer of timers.values()) {
                  if (timer.due < next) {
                    next = timer.due;
                  }
                }

                if (next === Infinity) {
                  clearWake();
                  return;
                }

                if (wakeHandle && wakeDue <= next) {
                  return;
                }

                if (wakeHandle) {
                  realClearTimeout(wakeHandle);
                  wakeHandle = 0;
                }

                wakeDue = next;
                const delay = Math.max(0, next - timerNow());
                wakeHandle = realSetTimeout(() => {
                  wakeHandle = 0;
                  wakeDue = Infinity;
                  if (paused) {
                    return;
                  }

                  fireDue(timerNow());
                  scheduleWake();
                }, delay);
              }

              function fireDue(limit) {
                let guard = 0;
                while (guard++ < 100000) {
                  let selected = null;
                  for (const timer of timers.values()) {
                    if (timer.due <= limit
                        && (!selected
                          || timer.due < selected.due
                          || (timer.due === selected.due && timer.id < selected.id))) {
                      selected = timer;
                    }
                  }

                  if (!selected) {
                    return;
                  }

                  const due = selected.due;
                  if (selected.interval > 0) {
                    selected.due = due + selected.interval;
                  } else {
                    timers.delete(selected.id);
                  }

                  if (paused) {
                    base = due;
                  }

                  try {
                    selected.fn.apply(undefined, selected.args);
                  } catch (error) {
                    realSetTimeout(() => { throw error; }, 0);
                  }
                }
              }

              function advanceTo(target, once) {
                const start = timerNow();
                const destination = Math.max(start, target);
                const wasPaused = paused;
                paused = true;
                base = start;

                if (once) {
                  const candidates = [];
                  for (const timer of timers.values()) {
                    if (timer.due <= destination) {
                      candidates.push(timer);
                    }
                  }

                  candidates.sort((left, right) => left.due === right.due
                    ? left.id - right.id
                    : left.due - right.due);

                  for (const timer of candidates) {
                    if (!timers.has(timer.id)) {
                      continue;
                    }

                    const due = timer.due;
                    if (timer.interval > 0) {
                      timer.due = destination + timer.interval;
                    } else {
                      timers.delete(timer.id);
                    }

                    base = due;
                    try {
                      timer.fn.apply(undefined, timer.args);
                    } catch (error) {
                      realSetTimeout(() => { throw error; }, 0);
                    }
                  }
                } else {
                  fireDue(destination);
                }

                base = destination;
                paused = wasPaused;
                if (!paused) {
                  realStart = realPerformanceNow();
                  scheduleWake();
                }

                return currentTime();
              }

              function schedule(fn, delay, args, interval) {
                if (typeof fn !== 'function') {
                  fn = () => {};
                }

                let milliseconds = Number(delay);
                if (!Number.isFinite(milliseconds) || milliseconds < 0) {
                  milliseconds = 0;
                }

                const id = nextId++;
                timers.set(id, {
                  id: id,
                  fn: fn,
                  args: args || [],
                  due: timerNow() + milliseconds,
                  interval: interval ? Math.max(1, milliseconds) : 0
                });
                scheduleWake();
                return id;
              }

              const api = {
                configure(next) {
                  paused = !!next.paused;
                  fixed = !!next.fixed;
                  base = Number(next.time) || 0;
                  fixedTime = base;
                  epoch = base;
                  realStart = realPerformanceNow();
                  timers.clear();
                  clearWake();
                  if (!paused) {
                    scheduleWake();
                  }
                  return currentTime();
                },
                now: currentTime,
                fastForward(milliseconds) {
                  return advanceTo(timerNow() + Math.max(0, Number(milliseconds) || 0), true);
                },
                runFor(milliseconds) {
                  return advanceTo(timerNow() + Math.max(0, Number(milliseconds) || 0), false);
                },
                pauseAt(milliseconds) {
                  advanceTo(Number(milliseconds), true);
                  paused = true;
                  clearWake();
                  return currentTime();
                },
                resume() {
                  paused = false;
                  fixed = false;
                  realStart = realPerformanceNow();
                  scheduleWake();
                  return currentTime();
                },
                setFixedTime(milliseconds) {
                  fixed = true;
                  fixedTime = Number(milliseconds);
                  return currentTime();
                },
                setSystemTime(milliseconds) {
                  const target = Number(milliseconds);
                  const delta = target - timerNow();
                  fixed = false;
                  fixedTime = target;
                  base = target;
                  realStart = realPerformanceNow();
                  for (const timer of timers.values()) {
                    timer.due += delta;
                  }
                  if (!paused) {
                    scheduleWake();
                  }
                  return currentTime();
                }
              };

              globalThis.__zeroCdpClock = api;

              function FakeDate(...args) {
                if (!new.target) {
                  return new RealDate(currentTime()).toString();
                }
                if (args.length === 0) {
                  return new RealDate(currentTime());
                }
                return new RealDate(...args);
              }

              FakeDate.prototype = RealDate.prototype;
              FakeDate.now = () => currentTime();
              FakeDate.parse = RealDate.parse;
              FakeDate.UTC = RealDate.UTC;
              globalThis.Date = FakeDate;

              globalThis.setTimeout = (fn, delay, ...args) => schedule(fn, delay, args, false);
              globalThis.clearTimeout = id => { timers.delete(id); scheduleWake(); };
              globalThis.setInterval = (fn, delay, ...args) => schedule(fn, delay, args, true);
              globalThis.clearInterval = id => { timers.delete(id); scheduleWake(); };
              globalThis.requestAnimationFrame = fn => schedule(fn, 16, [], false);
              globalThis.cancelAnimationFrame = id => { timers.delete(id); scheduleWake(); };
              globalThis.requestIdleCallback = fn => schedule(fn, 1, [], false);
              globalThis.cancelRequestIdleCallback = id => { timers.delete(id); scheduleWake(); };
              try {
                globalThis.performance.now = () => currentTime() - epoch;
              } catch (error) {
              }

              if (!paused) {
                scheduleWake();
              }
              return currentTime();
            })();
            """;

        public static string Build(ClockState state)
        {
            string config = "{\"time\":"
                + state.TimeMilliseconds.ToString("R", CultureInfo.InvariantCulture)
                + ",\"paused\":"
                + (state.Paused ? "true" : "false")
                + ",\"fixed\":"
                + (state.Fixed ? "true" : "false")
                + "}";

            return Template.Replace("__CONFIG__", config, StringComparison.Ordinal);
        }
    }
}
