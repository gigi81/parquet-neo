#!/usr/bin/env bash
# Checks the packages the way a user gets them: a new project that installs only ParquetNeo.All from the folder of packages
# must build, get the source generator, and run as a Native AOT program.
#   build/verify-packages.sh <folder with the .nupkg files> <version of the packages>
set -euo pipefail

packages=$(realpath "$1")
version=$2
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cat > "$work/nuget.config" <<CONFIG
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="ParquetNeo*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
CONFIG

cat > "$work/Consumer.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ParquetNeo.All" Version="$version" />
  </ItemGroup>
</Project>
PROJECT

cat > "$work/Program.cs" <<'PROGRAM'
using ParquetNeo;
using ParquetNeo.DataModel;
using ParquetNeo.Serialization;

var rows = Enumerable.Range(0, 1000).Select(i => new Order { Id = i, Name = i % 3 == 0 ? null : "order-" + i, Total = i * 1.5 }).ToList();
var stream = new MemoryStream();
ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = CompressionCodec.Uncompressed, WritePageIndex = true } });
stream.Position = 0;
var back = ParquetSerializer.Deserialize<Order>(stream, new ParquetDeserializerOptions { RowGroupFilter = ParquetFilter.Column("Id").Equal(500L) });
if (!back.Any(order => order.Id == 500 && order.Name == "order-500" && order.Total == 750.0))
{
    Console.Error.WriteLine("The row was not read back.");
    return 1;
}

Console.WriteLine($"ParquetNeo packages work: {back.Count} rows read back.");
return 0;

[ParquetSerializable]
public sealed class Order
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public double Total { get; set; }
}
PROGRAM

cd "$work"
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet publish -c Release -r "$rid" -o out
"./out/Consumer"
