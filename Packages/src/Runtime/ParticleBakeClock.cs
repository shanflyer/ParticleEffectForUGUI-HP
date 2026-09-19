using System;

namespace Coffee.UIExtensions
{
    // Keep scheduling time separate from simulation time. A consumed delta must
    // never remain in the accumulator and be simulated a second time.
    internal struct ParticleBakeClock
    {
        private float _scaled;
        private float _unscaled;
        private float _schedule;
        private float _phase;
        private bool _started;
        private bool _limited;

        private long _lastTick;
        private bool _tickStarted;

        // Absolute ticks align all outputs, including late-enabled effects and replicas.
        public bool AdvanceAtTick(float scaled, float unscaled, long tick, bool force,
            out float scaledStep, out float unscaledStep)
        {
            _scaled += FiniteDelta(scaled);
            _unscaled += FiniteDelta(unscaled);
            scaledStep = unscaledStep = 0;
            if (_tickStarted && _lastTick == tick && !force) return false;
            _tickStarted = true;
            _lastTick = tick;
            scaledStep = _scaled; unscaledStep = _unscaled;
            _scaled = _unscaled = 0;
            return true;
        }

        public void Reset() { this = default; }

        public bool Advance(float scaled, float unscaled, int fps, bool force,
            out float scaledStep, out float unscaledStep, float phase = 0)
        {
            _scaled += FiniteDelta(scaled);
            var realDelta = FiniteDelta(unscaled);
            _unscaled += realDelta;
            scaledStep = unscaledStep = 0;
            // Preserve the project's reduced-work mode even below the requested
            // rate: at steady low FPS bake every other frame. Scheduling residue
            // and phase offsets must never be fed back into simulation time.
            var interval = fps > 0 ? Math.Max(1f / fps, 2f * realDelta) : 0;
            phase = Math.Min(1f, FiniteDelta(phase));
            if (fps > 0)
            {
                if (_limited && phase != _phase)
                    _schedule = Math.Max(0, Math.Min(interval, _schedule + (phase - _phase) * interval));
                _phase = phase;
                if (!_limited) _schedule = Math.Min(1f, FiniteDelta(phase)) * interval;
                _schedule += realDelta;
                if (_started && !force && _schedule + 0.000001f < interval)
                {
                    _limited = true;
                    return false;
                }
                _schedule = !_started || force
                    ? Math.Min(1f, FiniteDelta(phase)) * interval
                    : Math.Min(interval, Math.Max(0, _schedule - interval));
            }
            else _schedule = 0;
            scaledStep = _scaled;
            unscaledStep = _unscaled;
            _scaled = _unscaled = 0;
            _started = true;
            _limited = fps > 0;
            return true;
        }

        private static float FiniteDelta(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? 0 : value;
        }
    }
}
