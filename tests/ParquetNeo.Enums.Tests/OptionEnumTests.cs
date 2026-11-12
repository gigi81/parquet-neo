namespace ParquetNeo.Enums.Tests;

/// <summary>The enumerations that are choices of the library rather than values of the format.</summary>
public class OptionEnumTests
{
    [Test]
    public async Task TheStandardDictionaryMode_IsTheDefault_SoAnUnsetOptionKeepsTheBehaviorOfBefore()
    {
        DictionaryMode unset = default;

        await Assert.That(unset).IsEqualTo(DictionaryMode.Standard);
        await Assert.That(Enum.GetValues<DictionaryMode>()).IsEquivalentTo([DictionaryMode.Standard, DictionaryMode.Adaptive, DictionaryMode.Disabled]);
    }

    [Test]
    [Arguments(DataPageVersion.V1, 1)]
    [Arguments(DataPageVersion.V2, 2)]
    public async Task TheDataPageVersions_AreNamedByTheirNumber(DataPageVersion version, int number)
    {
        await Assert.That((int)version).IsEqualTo(number);
    }

    [Test]
    public async Task TheColumnSettingKinds_AreFlags_ThatDoNotOverlap()
    {
        var kinds = new[] { ColumnSettingKinds.Decimal, ColumnSettingKinds.Time, ColumnSettingKinds.Timestamp };

        ColumnSettingKinds none = ColumnSettingKinds.None;

        await Assert.That((int)none).IsEqualTo(0);
        foreach (var kind in kinds)
        {
            await Assert.That(int.PopCount((int)kind)).IsEqualTo(1).Because(kind.ToString());
        }

        await Assert.That(kinds.Aggregate(0, (all, kind) => all | (int)kind)).IsEqualTo(7);
        await Assert.That(typeof(ColumnSettingKinds).IsDefined(typeof(FlagsAttribute), inherit: false)).IsTrue();
    }

    [Test]
    public async Task TheListStylesAndStructureLevelKinds_AreStable()
    {
        await Assert.That(Enum.GetNames<ListStyle>()).IsEquivalentTo(["ThreeLevel", "RepeatedPrimitive", "RepeatedStruct", "RepeatedTuple", "Implicit"]);
        await Assert.That(Enum.GetNames<StructureLevelKind>()).IsEquivalentTo(["Optional", "Repeated"]);
    }
}
