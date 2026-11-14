# GraphQL with Hot Chocolate

A `ParquetDataset<T>` is an `IQueryable<T>`, so a GraphQL server that builds its filtering, sorting and field selection on `IQueryable`
can serve Parquet files without code of its own in this library. This article uses [Hot Chocolate](https://chillicream.com/docs/hotchocolate)
(version 16), which generates the GraphQL types, the `where` and `order` arguments and the field selection from your class.

## A server in a few lines

```bash
dotnet add package ParquetNeo.Linq
dotnet add package HotChocolate.AspNetCore
dotnet add package HotChocolate.Data
```

```csharp
using HotChocolate;
using HotChocolate.Data;
using ParquetNeo.Linq;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(ParquetDataset.Open<Person>("people/"));   // a file, or a directory
builder.Services
    .AddGraphQL()
    .AddQueryType<Query>()
    .AddFiltering()
    .AddSorting()
    .AddProjections();

var app = builder.Build();
app.MapGraphQL();
app.Run();

public sealed class Person
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int Age { get; set; }
    public string City { get; set; } = "";
}

public sealed class Query
{
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Person> GetPeople([Service] ParquetDataset<Person> people) => people;
}
```

Hot Chocolate makes a `people` field with a `where` argument (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `contains`, `and`, `or`, ...
for each property), an `order` argument, and returns only the fields a client asks for:

```graphql
{
  people(where: { and: [{ id: { gte: 1100 } }, { id: { lt: 1130 } }] }, order: { id: ASC }) {
    id
    name
  }
}
```

## What is read

Hot Chocolate turns that query into LINQ (`OrderBy`, then `Where`, then `Select`) and `ParquetNeo.Linq` plans it like any other query
(see [LINQ over files](linq.md)):

- **Field selection** reads only the columns the query selects, plus the ones its filter and sorting use. A query for `{ id name }` reads
  two columns.
- **Filters** become a `ParquetFilter` that skips files, row groups and pages (if the files have a page index) that cannot hold a
  matching row. A `Where` that comes after the ordering counts: it does not change the result to move it in front.
- **What cannot be turned into a filter** (`contains` on text, for example) is applied to the rows that are read, so the result is
  exact, and the rest of the condition still skips what it can.
- **Sorting** is done in memory on the rows that are read.

In a test with three files of a thousand rows each, in row groups of a hundred, the query above skipped 2 of the 3 files and 9 of
the 10 remaining row groups, and read two columns. `Explain()` shows the same for a query of your own:

```csharp
var plan = people.Where(p => p.Id >= 1100 && p.Id < 1130).Explain();
// files 3 skipped 2, row groups 10 skipped 9, filter (Id >= 1100 and Id < 1130)
```

How much is skipped depends on the files, as always: the statistics of a column that is in no order (a name, a city) rule out little.
Write the files with a page index and bloom filters for the columns that are searched (see [Skipping what is not needed](filtering.md)),
and sort the data by the columns that are filtered most.

## Things to keep in mind

- **Paging needs an order.** Files are read in parallel, so without a sort the rows come in no particular order, and a page of rows
  would not be the same twice. Sort by a unique column (give the `order` argument a default, or sort in the resolver) before you
  page, whichever paging attribute of Hot Chocolate you use.
- **A client can ask for anything.** A query with no filter reads whole files. Limit the page size and the cost of a query with the
  options of Hot Chocolate (`ModifyPagingOptions`, `ModifyCostOptions`), and put an authorization rule on the field if the data is not public.
- **It reads and filters; it does not write.** The dataset is read only.
- **No trimming or Native AOT.** `ParquetNeo.Linq` builds expression trees and types at run time, and so does Hot Chocolate. This is a
  scenario for a regular server.
- **Names:** Hot Chocolate makes the GraphQL names from the properties (`Id` becomes `id`). A property that is not a column
  (`[ParquetIgnore]`, or without a setter) is not read from the file; leave it out of the type with `[GraphQLIgnore]` as well.

## Other libraries

Anything that builds on `IQueryable` works the same way: the operators it generates are the ones of `System.Linq.Queryable`, and
`ParquetNeo.Linq` runs them. A library that needs more from the provider (such as Entity Framework Core) does not.
