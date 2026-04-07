using System;

namespace MIR.Direct2D1ForAvalonia
{
    internal readonly record struct OptionalDispose<T> : IDisposable where T : IDisposable
    {
        private readonly bool _dispose;

        public OptionalDispose(T value, bool dispose)
        {
            Value = value;
            _dispose = dispose;
        }

        public T Value { get; }

        public void Dispose()
        {
            if (_dispose) Value?.Dispose();
        }
    }
}