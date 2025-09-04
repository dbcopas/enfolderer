namespace Enfolderer.App.Metadata;

public readonly struct FetchSpec
{
    public readonly string SetCode;
    public readonly string Number;
    public readonly string? NameOverride;
    public readonly int SpecIndex;
    public FetchSpec(string setCode, string number, string? nameOverride, int specIndex)
    { SetCode = setCode; Number = number; NameOverride = nameOverride; SpecIndex = specIndex; }
}
