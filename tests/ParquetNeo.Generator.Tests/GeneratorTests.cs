namespace ParquetNeo.Generator.Tests;

public class GeneratorTests
{
    private const string Usings = "using System; using ParquetNeo; using ParquetNeo.DataModel; using System.Collections.Generic;\n";

    /// <param name="source">The code of the sample, which is in the namespace <c>Sample</c>; one line comes before it.</param>
    /// <param name="assemblyAttributes">Attributes of the assembly, which come before everything else.</param>
    private static GeneratorRun Run(string source, string assemblyAttributes = "") =>
        GeneratorRun.Of(Usings + assemblyAttributes + "namespace Sample;\n" + source);

    [Test]
    public async Task AFlatType_GetsAWriteAndAReadMapping_ThatCompile()
    {
        var run = Run("[ParquetSerializable] public class Order { public long Id { get; set; } public string? Name { get; set; } public int? Count { get; set; } }");

        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.CompileErrors).IsEmpty();
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Order>");
        await Assert.That(run.Generated!).Contains("RegisterRead<global::Sample.Order>");
        await Assert.That(run.Generated!).Contains(".Column<long>(\"Id\", \"Property 'Order.Id'\", optional: false)");
        await Assert.That(run.Generated!).Contains(".Column<string>(\"Name\", \"Property 'Order.Name'\", optional: true)");
        await Assert.That(run.Generated!).Contains(".Column<int>(\"Count\", \"Property 'Order.Count'\", optional: true)");
    }

    [Test]
    public async Task NothingIsGenerated_WhenNothingAsksForIt()
    {
        var run = Run("public class Order { public long Id { get; set; } }");

        await Assert.That(run.Generated).IsNull();
        await Assert.That(run.ReportedIds).IsEmpty();
    }

    [Test]
    public async Task ARequiredReferenceIsOneThatSaysItCannotBeNull_AndWithoutANullableContextItCan()
    {
        var annotated = Run("[ParquetSerializable] public class A { public string Required { get; set; } = \"\"; public string? Optional { get; set; } public byte[] Bytes { get; set; } = []; }");
        var oblivious = Run("#nullable disable\n[ParquetSerializable] public class B { public string Text { get; set; } }");

        await Assert.That(annotated.Generated!).Contains("\"Required\", \"Property 'A.Required'\", optional: false");
        await Assert.That(annotated.Generated!).Contains("\"Optional\", \"Property 'A.Optional'\", optional: true");
        await Assert.That(annotated.Generated!).Contains(".Column<byte[]>(\"Bytes\", \"Property 'A.Bytes'\", optional: false)");
        await Assert.That(oblivious.Generated!).Contains("\"Text\", \"Property 'B.Text'\", optional: true");
    }

    [Test]
    public async Task TheColumnsAreInTheOrderTheReflectionPathUses()
    {
        var run = Run(@"
public class Base { public int FromBase { get; set; } }
[ParquetSerializable] public class Derived : Base
{
    public int Third { get; set; }
    [ParquetColumn(Order = -5)] public int First { get; set; }
    [ParquetColumn(""second"", Order = 3)] public int Last { get; set; }
    [ParquetIgnore] public int Skipped { get; set; }
    public int Fourth { get; set; }
}");

        string code = run.Generated!;
        var names = new[] { "\"First\"", "\"FromBase\"", "\"Third\"", "\"Fourth\"", "\"second\"" };
        int[] positions = names.Select(n => code.IndexOf(".Column<int>(" + n, StringComparison.Ordinal)).ToArray();

        await Assert.That(positions.All(p => p >= 0)).IsTrue();
        await Assert.That(positions.SequenceEqual(positions.OrderBy(p => p))).IsTrue();
        await Assert.That(code).DoesNotContain("Skipped");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task OnlyPropertiesWithAPublicSetterAreRead_AndOthersAreOnlyWritten()
    {
        var run = Run("[ParquetSerializable] public class Row { public int Both { get; set; } public int WriteOnly { get; } = 1; public int Initialized { get; init; } public int Private { get; private set; } }");

        string code = run.Generated!;
        string read = code.Substring(code.IndexOf("CreateRead0()", StringComparison.Ordinal) + 1);
        string write = code.Substring(code.IndexOf("CreateWrite0()", StringComparison.Ordinal), code.IndexOf("CreateRead0()", StringComparison.Ordinal) - code.IndexOf("CreateWrite0()", StringComparison.Ordinal));

        await Assert.That(write).Contains("WriteOnly");
        await Assert.That(write).Contains("Private");
        await Assert.That(read).Contains("Both = ");
        await Assert.That(read).Contains("Initialized = ");
        await Assert.That(read).DoesNotContain("WriteOnly");
        await Assert.That(read).DoesNotContain("Private = ");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task EnumsAreRegisteredWithTheLibrary_SoThatTheyWorkWhereTypesCannotBeMadeAtRunTime()
    {
        var run = Run("public enum Color { Red } [ParquetSerializable] public class Row { public Color Main { get; set; } public Color? Other { get; set; } }");

        await Assert.That(run.Generated!).Contains("ColumnTypeRegistry.RegisterEnum<global::Sample.Color>()");
        await Assert.That(run.Generated!).Contains(".Column<global::Sample.Color>(\"Main\"");
        await Assert.That(run.Generated!).Contains(".Column<global::Sample.Color>(\"Other\"");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task TheAttributesThatAdjustAColumnBecomeItsSettings()
    {
        var run = Run(@"
[ParquetSerializable] public class Row
{
    [ParquetDecimal(18, 4)] public decimal Money { get; set; }
    [ParquetTime(TimeUnit.Nanos)] public TimeOnly Clock { get; set; }
    [ParquetTimestamp(TimeUnit.Millis, IsAdjustedToUtc = false)] public DateTime Moment { get; set; }
    [ParquetEncoding(ParquetEncoding.DeltaBinaryPacked)] public long Counter { get; set; }
    [ParquetDictionary(DictionaryMode.Adaptive, SizeLimit = 99)] public string Text { get; set; } = """";
}");

        string code = run.Generated!;
        await Assert.That(code).Contains("settings: new global::ParquetNeo.ColumnTypes.ColumnSettings { Precision = 18, Scale = 4 }");
        await Assert.That(code).Contains("settings: new global::ParquetNeo.ColumnTypes.ColumnSettings { Unit = (global::ParquetNeo.TimeUnit)3, IsAdjustedToUtc = false }");
        await Assert.That(code).Contains("settings: new global::ParquetNeo.ColumnTypes.ColumnSettings { Unit = (global::ParquetNeo.TimeUnit)1, IsAdjustedToUtc = false }");
        await Assert.That(code).Contains("encoding: (global::ParquetNeo.ParquetEncoding)");
        await Assert.That(code).Contains("Mode = (global::ParquetNeo.DictionaryMode)1, SizeLimit = 99");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task ATypeYouDoNotOwn_IsMappedByAnAttributeOnTheAssembly()
    {
        var run = Run("public class Theirs { public int Value { get; set; } }", "[assembly: ParquetSerializable(typeof(Sample.Theirs))]\n");

        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Theirs>");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task ATypeNamedTwice_IsMappedOnce()
    {
        var run = Run("[ParquetSerializable] public class Row { public int Value { get; set; } }", "[assembly: ParquetSerializable(typeof(Sample.Row))]\n");

        string code = run.Generated!;
        await Assert.That(code.Split("RegisterWrite<").Length - 1).IsEqualTo(1);
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task AStruct_IsMapped()
    {
        var run = Run("[ParquetSerializable] public struct Point { public int X { get; set; } public int Y { get; set; } }");

        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.Generated!).Contains("RegisterRead<global::Sample.Point>");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task ATypeWithoutAParameterlessConstructor_IsOnlyWritten_AndTheUserIsTold()
    {
        var run = Run("[ParquetSerializable] public class Row { public Row(int value) { Value = value; } public int Value { get; set; } }");

        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO002" });
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Row>");
        await Assert.That(run.Generated!).DoesNotContain("RegisterRead");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    [Arguments("public class Inner { public int X { get; set; } } [ParquetSerializable] public class Row { public Inner Child { get; set; } = new(); }", "objects and collections")]
    [Arguments("[ParquetSerializable] public class Row { public List<int> Values { get; set; } = new(); }", "objects and collections")]
    [Arguments("[ParquetSerializable] public class Row { public int[] Values { get; set; } = []; }", "objects and collections")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public int Count { get; set; } }", "ParquetDecimalAttribute")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetTimestamp] public TimeOnly Clock { get; set; } }", "ParquetTimestampAttribute")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(40, 2)] public decimal Money { get; set; } }", "not valid")]
    [Arguments("[ParquetSerializable] public class Row { public int Value { get; set; } [ParquetColumn(\"Value\")] public int Other { get; set; } }", "column named 'Value'")]
    [Arguments("[ParquetSerializable] public class Row { private int Hidden { get; set; } }", "no public properties")]
    [Arguments("[ParquetSerializable] public class Row<T> { public int Value { get; set; } }", "generic")]
    [Arguments("[ParquetSerializable] public class Row { public object Anything { get; set; } = new(); }", "objects and collections")]
    public async Task ATypeThatCannotBeMapped_IsLeftToReflection_WithAWarningThatSaysWhy(string source, string reason)
    {
        var run = Run(source);

        await Assert.That(run.Generated).IsNull();
        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO001" });
        await Assert.That(run.Reported[0].GetMessage()).Contains(reason);
        await Assert.That(run.Reported[0].Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task ATypeGeneratedCodeCannotName_IsLeftToReflection()
    {
        var run = Run("public class Outer { [ParquetSerializable] private class Row { public int Value { get; set; } } }");

        await Assert.That(run.Generated).IsNull();
        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO001" });
        await Assert.That(run.Reported[0].GetMessage()).Contains("not accessible");
    }

    [Test]
    public async Task OneBadTypeDoesNotStopTheOthers()
    {
        var run = Run("[ParquetSerializable] public class Good { public int Value { get; set; } } [ParquetSerializable] public class Bad { public List<int> Values { get; set; } = new(); }");

        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO001" });
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Good>");
        await Assert.That(run.Generated!).DoesNotContain("Bad");
    }

    [Test]
    public async Task TheWarningIsAtTheAttribute()
    {
        var run = Run("[ParquetSerializable] public class Row { public List<int> Values { get; set; } = new(); }");

        var place = run.Reported[0].Location.GetLineSpan();

        await Assert.That(place.Path).IsEqualTo("Sample.cs");
        await Assert.That(place.StartLinePosition.Line).IsEqualTo(2);
    }
}
