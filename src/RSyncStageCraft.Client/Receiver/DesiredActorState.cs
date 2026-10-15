namespace RSyncStageCraft.Core.Receiver;

public readonly record struct ApplyGeneration(long Value)
{
    public bool IsValid => Value > 0;

    internal bool TryAdvance(out ApplyGeneration generation)
    {
        if (!IsValid || Value == long.MaxValue)
        {
            generation = default;
            return false;
        }

        generation = new(Value + 1);
        return true;
    }
}
