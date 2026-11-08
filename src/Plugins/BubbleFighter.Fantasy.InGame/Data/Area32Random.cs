namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Reproduces the Area32 seeded draws used by native supply-box contents.</summary>
internal sealed class Area32Random
{
    private readonly int[] _state = new int[56];
    private int _first;
    private int _second = 21;

    internal Area32Random(int seed)
    {
        // kr-1256 sub_F99280 fills 55 subtractive states and mixes them four times.
        var previous = unchecked(161803398 - (seed < 0 ? -seed : seed));
        _state[55] = previous;
        var next = 1;
        for (var index = 1; index < 55; index++)
        {
            _state[21 * index % 55] = next;
            var difference = unchecked(previous - next);
            previous = next;
            next = difference < 0 ? unchecked(difference + int.MaxValue) : difference;
        }
        for (var pass = 0; pass < 4; pass++)
            for (var index = 1; index <= 55; index++)
            {
                var difference = unchecked(_state[index] - _state[1 + (index + 30) % 55]);
                _state[index] = difference < 0 ? unchecked(difference + int.MaxValue) : difference;
            }
    }

    internal int Next(int maximum) => Next(0, maximum);

    internal int Next(int minimum, int maximum)
    {
        if (minimum < 0 || maximum < minimum) throw new ArgumentOutOfRangeException(nameof(maximum));
        // kr-1256 sub_F99330 and kr-1256 sub_F99400 round the sample and scaled range to float, rejecting 1.0f.
        float sample;
        do
        {
            if (++_first == 56) _first = 1;
            if (++_second == 56) _second = 1;
            var difference = unchecked(_state[_first] - _state[_second]);
            if (difference < 0) difference = unchecked(difference + int.MaxValue);
            _state[_first] = difference;
            sample = (float)(difference * 4.656612875245797e-10);
        } while (sample == 1.0f);
        return minimum + (int)((float)(maximum - minimum) * sample);
    }
}
