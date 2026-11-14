namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Reproduces the client generator used to preselect BnB items.</summary>
internal sealed class BnBRandomSource
{
    private readonly int[] _state = new int[56];
    private int _first;
    private int _second = 21;

    internal BnBRandomSource(int seed)
    {
        // kr-1256 sub_F99280 uses signed wrapping subtraction, including abs(INT_MIN).
        unchecked
        {
            var previous = 161803398 - (seed < 0 ? -seed : seed);
            _state[55] = previous;
            var next = 1;
            for (var index = 1; index < 55; index++)
            {
                var slot = 21 * index % 55;
                _state[slot] = next;
                next = previous - next;
                if (next < 0) next += int.MaxValue;
                previous = _state[slot];
            }
            for (var pass = 0; pass < 4; pass++)
                for (var index = 1; index < 56; index++)
                {
                    _state[index] -= _state[1 + (index + 30) % 55];
                    if (_state[index] < 0) _state[index] += int.MaxValue;
                }
        }
    }

    internal int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMaximum, 1);
        float sample;
        do
        {
            if (++_first == 56) _first = 1;
            if (++_second == 56) _second = 1;
            var value = unchecked(_state[_first] - _state[_second]);
            if (value < 0) value = unchecked(value + int.MaxValue);
            _state[_first] = value;
            // kr-1256 sub_F99400 rounds the double product to float, retries 1.0f, then multiplies as float.
            sample = (float)(value * 4.656612875245797E-10);
        } while (sample == 1.0f);
        return (int)((float)exclusiveMaximum * sample);
    }
}
