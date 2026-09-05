namespace Tessera.Sync;

public readonly record struct BoardId(string Value)
{
    public override string ToString() => Value;
}
