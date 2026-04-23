namespace BlockParam.SimaticML;

/// <summary>
/// String constants for SimaticML element and attribute names. Centralised so
/// typos surface at compile time and find-usages works across the parser,
/// writer and UDT resolvers.
/// </summary>
internal static class SimaticMLElements
{
    // Element names
    public const string Member = "Member";
    public const string AttributeList = "AttributeList";
    public const string StartValue = "StartValue";
    public const string Section = "Section";
    public const string Sections = "Sections";
    public const string Subelement = "Subelement";
    public const string Interface = "Interface";
    public const string Comment = "Comment";
    public const string MultiLanguageText = "MultiLanguageText";
    public const string BooleanAttribute = "BooleanAttribute";
    public const string Number = "Number";
    public const string MemoryLayout = "MemoryLayout";
    public const string PlcStruct = "SW.Types.PlcStruct";

    // Attribute names
    public const string Name = "Name";
    public const string Datatype = "Datatype";
    public const string Lang = "Lang";
    public const string Path = "Path";
    public const string ConstantName = "ConstantName";

    // Well-known values of the Name attribute on BooleanAttribute
    public const string SetPoint = "SetPoint";
}
