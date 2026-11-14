namespace ParquetNeo.Generator.Tests;

public class GeneratorTests
{
    private const string Usings = "using System; using ParquetNeo; using ParquetNeo.DataModel; using System.Collections.Generic;\n";

    /// <param name="source">The code of the sample, which is in the namespace <c>Sample</c>; one line comes before it.</param>
    /// <param name="assemblyAttributes">Attributes of the assembly, which come before everything else.</param>
    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        return text.Substring(start, text.IndexOf(to, start + from.Length, StringComparison.Ordinal) - start);
    }

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
        await Assert.That(run.Generated!).Contains(".Leaf<long>(\"Id\", \"Property 'Order.Id'\", optional: false)");
        await Assert.That(run.Generated!).Contains(".Leaf<string>(\"Name\", \"Property 'Order.Name'\", optional: true)");
        await Assert.That(run.Generated!).Contains(".Leaf<int>(\"Count\", \"Property 'Order.Count'\", optional: true)");
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
        await Assert.That(annotated.Generated!).Contains(".Leaf<byte[]>(\"Bytes\", \"Property 'A.Bytes'\", optional: false)");
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
        int[] positions = names.Select(n => code.IndexOf(".Leaf<int>(" + n, StringComparison.Ordinal)).ToArray();

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
        string write = Between(code, "Properties0<TRow>", "Shred0(");
        string read = Between(code, "ReadProperties0<TRow>", "Assemble0(") + Between(code, "Assemble0(", "}");

        await Assert.That(write).Contains("WriteOnly");
        await Assert.That(write).Contains("Private");
        await Assert.That(read).Contains("Both");
        await Assert.That(read).Contains("Initialized");
        await Assert.That(read).DoesNotContain("WriteOnly");
        await Assert.That(read).DoesNotContain("Private");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task EnumsAreRegisteredWithTheLibrary_SoThatTheyWorkWhereTypesCannotBeMadeAtRunTime()
    {
        var run = Run("public enum Color { Red } [ParquetSerializable] public class Row { public Color Main { get; set; } public Color? Other { get; set; } }");

        await Assert.That(run.Generated!).Contains("ColumnTypeRegistry.RegisterEnum<global::Sample.Color>()");
        await Assert.That(run.Generated!).Contains(".Leaf<global::Sample.Color>(\"Main\"");
        await Assert.That(run.Generated!).Contains(".Leaf<global::Sample.Color>(\"Other\"");
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
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public int Count { get; set; } }", "ParquetDecimalAttribute")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetTimestamp] public TimeOnly Clock { get; set; } }", "ParquetTimestampAttribute")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(40, 2)] public decimal Money { get; set; } }", "not valid")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public List<int> Values { get; set; } = new(); }", "ParquetDecimalAttribute")]
    [Arguments("public class Inner { public int X { get; set; } } [ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public Inner Child { get; set; } = new(); }", "ParquetDecimalAttribute")]
    [Arguments("[ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public Dictionary<string, int> Values { get; set; } = new(); }", "none of what it is made of")]
    [Arguments("[ParquetSerializable] public class Row { public int Value { get; set; } [ParquetColumn(\"Value\")] public int Other { get; set; } }", "column named 'Value'")]
    [Arguments("[ParquetSerializable] public class Row { private int Hidden { get; set; } }", "no public properties")]
    [Arguments("[ParquetSerializable] public class Row<T> { public int Value { get; set; } }", "generic")]
    [Arguments("[ParquetSerializable] public class Row { public object Anything { get; set; } = new(); }", "cannot be written as a column")]
    [Arguments("[ParquetSerializable] public class Row { public TimeSpan Span { get; set; } }", "cannot be written as a column")]
    [Arguments("[ParquetSerializable] public class Row { public int[,] Grid { get; set; } = new int[0, 0]; }", "cannot be written as a column")]
    [Arguments("[ParquetSerializable] public class Row { public Dictionary<int?, int> Values { get; set; } = new(); }", "keys of type")]
    [Arguments("[ParquetSerializable] public class Row { public System.Collections.Immutable.ImmutableArray<int> Values { get; set; } }", "a collection that is a struct")]
    [Arguments("public class Node { public Node? Next { get; set; } public int Value { get; set; } } [ParquetSerializable] public class Row { public Node Head { get; set; } = new(); }", "contains itself")]
    [Arguments("public class Empty { } [ParquetSerializable] public class Row { public Empty Nothing { get; set; } = new(); }", "no public properties")]
    [Arguments("public class Inner { public object Hidden { get; set; } = new(); } [ParquetSerializable] public class Row { public List<Inner> Items { get; set; } = new(); }", "cannot be written as a column")]
    public async Task ATypeThatCannotBeMapped_IsLeftToReflection_WithAWarningThatSaysWhy(string source, string reason)
    {
        var run = Run(source);

        await Assert.That(run.Generated).IsNull();
        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO001" });
        await Assert.That(run.Reported[0].GetMessage()).Contains(reason);
        await Assert.That(run.Reported[0].Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task ObjectsInsideARow_AreDescribedOnceEach_AndRegisteredWithTheirColumns()
    {
        var run = Run(@"
public class Place { public string Street { get; set; } = """"; public int? Zip { get; set; } }
public struct Point { public int X { get; set; } }
[ParquetSerializable] public class Row
{
    public Place Home { get; set; } = new();
    public Place? Work { get; set; }
    public Point Where { get; set; }
    public Point? Spot { get; set; }
}");

        string code = run.Generated!;
        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.CompileErrors).IsEmpty();
        await Assert.That(code.Split("private static void Describe").Length - 1).IsEqualTo(6);
        await Assert.That(code).Contains("optional: false);");
        await Assert.That(code).Contains(", \"Work\", \"Property 'Row.Work'\", optional: true)");
        await Assert.That(code).Contains("sink.Child<global::Sample.Point>(");
        await Assert.That(code).Contains(".GetValueOrDefault(), row.Spot.HasValue)");
        await Assert.That(code).Contains("source.TryReadChild<global::Sample.Place>(");
    }

    [Test]
    public async Task Collections_AreDescribedByTheirElements_WhateverTheKindOfCollection()
    {
        var run = Run(@"
public class Place { public int Zip { get; set; } }
public class Names : List<string> { }
[ParquetSerializable] public class Row
{
    public List<string> Tags { get; set; } = new();
    public string[] Codes { get; set; } = new string[0];
    public List<int?> Scores { get; set; } = new();
    public HashSet<string> Labels { get; set; } = new();
    public ISet<int> Numbers { get; set; } = new HashSet<int>();
    public IReadOnlyList<Place> Places { get; set; } = new List<Place>();
    public Names Team { get; set; } = new();
    public List<List<int>> Matrix { get; set; } = new();
}");

        string code = run.Generated!;
        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.CompileErrors).IsEmpty();
        await Assert.That(code).Contains("BeginList<global::System.Collections.Generic.List<string>, string>(\"Tags\"");
        await Assert.That(code).Contains("BeginList<string[], string>(\"Codes\"");
        await Assert.That(code).Contains("BeginNullableList<global::System.Collections.Generic.List<int?>, int>(\"Scores\"");
        await Assert.That(code).Contains("BeginList<global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>, global::System.Collections.Generic.List<int>>(\"Matrix\"");

        // Only what is not made from the elements read needs a function that creates it.
        await Assert.That(code).Contains("create: static () => new global::System.Collections.Generic.HashSet<string>()");
        await Assert.That(code).Contains("create: static () => new global::System.Collections.Generic.HashSet<int>()");
        await Assert.That(code).Contains("create: static () => new global::Sample.Names()");
        await Assert.That(code.Split("create: ").Length - 1).IsEqualTo(3);
    }

    [Test]
    public async Task Dictionaries_AreDescribedByTheirKeysAndValues()
    {
        var run = Run(@"
[ParquetSerializable] public class Row
{
    public Dictionary<string, int> Counts { get; set; } = new();
    public IDictionary<int, List<string>> Groups { get; set; } = new Dictionary<int, List<string>>();
    public IReadOnlyDictionary<string, int?> Optionals { get; set; } = new Dictionary<string, int?>();
}");

        string code = run.Generated!;
        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(run.CompileErrors).IsEmpty();
        await Assert.That(code).Contains("BeginMap<global::System.Collections.Generic.Dictionary<string, int>, string, int>(\"Counts\"");
        await Assert.That(code).Contains("BeginMapOfNullable<global::System.Collections.Generic.IReadOnlyDictionary<string, int?>, string, int>(\"Optionals\"");
        await Assert.That(code).Contains("create: static () => new global::System.Collections.Generic.Dictionary<string, int?>()");
    }

    [Test]
    public async Task AnAttributeOnACollection_AdjustsTheColumnsOfItsElements()
    {
        var run = Run("[ParquetSerializable] public class Row { [ParquetDecimal(10, 2)] public List<decimal> Amounts { get; set; } = new(); [ParquetDecimal(8, 1)] public Dictionary<string, decimal> Prices { get; set; } = new(); }");

        string code = run.Generated!;
        await Assert.That(run.ReportedIds).IsEmpty();
        await Assert.That(code).Contains("Leaf<decimal>(null, null, optional: false, settings: new global::ParquetNeo.ColumnTypes.ColumnSettings { Precision = 10, Scale = 2 })");
        await Assert.That(code).Contains("Leaf<decimal>(null, null, optional: false, settings: new global::ParquetNeo.ColumnTypes.ColumnSettings { Precision = 8, Scale = 1 })");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task AnObjectThatCannotBeCreated_MakesTheRowWriteOnly()
    {
        var run = Run("public class Place { public Place(int zip) { Zip = zip; } public int Zip { get; set; } } [ParquetSerializable] public class Row { public Place Home { get; set; } = new(1); }");

        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO002" });
        await Assert.That(run.Reported[0].GetMessage()).Contains("Place has no public constructor without parameters");
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Row>");
        await Assert.That(run.Generated!).DoesNotContain("RegisterRead");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task ACollectionThatCannotBeCreated_MakesTheRowWriteOnly()
    {
        var run = Run("public interface IMine : IEnumerable<int> { } [ParquetSerializable] public class Row { public IMine Values { get; set; } = null!; }");

        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO002" });
        await Assert.That(run.Reported[0].GetMessage()).Contains("cannot be created");
        await Assert.That(run.Generated!).DoesNotContain("RegisterRead");
        await Assert.That(run.CompileErrors).IsEmpty();
    }

    [Test]
    public async Task AnObjectUsedByTwoRows_IsDescribedOnce()
    {
        var run = Run("public class Place { public int Zip { get; set; } } [ParquetSerializable] public class A { public Place P { get; set; } = new(); } [ParquetSerializable] public class B { public List<Place> Ps { get; set; } = new(); }");

        await Assert.That(run.CompileErrors).IsEmpty();
        await Assert.That(run.Generated!.Split("BeginObject<global::Sample.Place>").Length - 1).IsEqualTo(2);
        await Assert.That(run.Generated!.Split("private static void Shred").Length - 1).IsEqualTo(3);
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
        var run = Run("[ParquetSerializable] public class Good { public int Value { get; set; } } [ParquetSerializable] public class Bad { public object Values { get; set; } = new(); }");

        await Assert.That(run.ReportedIds).IsEquivalentTo(new[] { "PNEO001" });
        await Assert.That(run.Generated!).Contains("RegisterWrite<global::Sample.Good>");
        await Assert.That(run.Generated!).DoesNotContain("Bad");
    }

    [Test]
    public async Task TheWarningIsAtTheAttribute()
    {
        var run = Run("[ParquetSerializable] public class Row { public object Values { get; set; } = new(); }");

        var place = run.Reported[0].Location.GetLineSpan();

        await Assert.That(place.Path).IsEqualTo("Sample.cs");
        await Assert.That(place.StartLinePosition.Line).IsEqualTo(2);
    }
}
